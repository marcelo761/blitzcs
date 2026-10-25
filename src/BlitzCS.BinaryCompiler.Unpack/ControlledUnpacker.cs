using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BlitzCS.BinaryCompiler.Unpack;

public enum OepConfidence { None, Low, Medium, High }

/// <summary>Bounds and policy of one unpack run. Everything here is part of the derived artifact's lineage digest.</summary>
public sealed record UnpackOptions
{
    /// <summary>Hard instruction budget (deterministic). rep-string iterations count one each.</summary>
    public long MaxInstructions { get; init; } = 300_000_000;
    /// <summary>Wall-clock safety net, checked every million instructions. Exceeding it refuses; it never changes a successful result.</summary>
    public TimeSpan MaxTime { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>High confidence needs at least this many bytes written into the image before control enters generated code.</summary>
    public int MinWrittenBytes { get; init; } = 256;
    /// <summary>Also stop at a jump out of the stub's section into a never-written executable section (Medium confidence). Default: record it but keep running.</summary>
    public bool AcceptEntrySectionExit { get; init; }
    /// <summary>Accept an OEP that is only Low confidence. Default false.</summary>
    public bool AcceptLowConfidence { get; init; }
    /// <summary>
    /// Address at which the packed image is mapped while the stub runs (64 KiB aligned, image plus its footprint below 4 GiB). Null = the image's preferred base.
    /// With another base the sandbox applies the packed file's OWN base relocations exactly as the Windows loader would, and passes the base as the module handle
    /// (DllMain's hinstDLL, GetModuleHandle(NULL), PEB.ImageBaseAddress). Packers process their packed relocation table themselves when they are loaded away from the
    /// preferred base, so the dump is correct for this base and the derived image carries it as its ImageBase. It is part of the lineage digest: a different base is a different derived image.
    /// </summary>
    public uint? LoadBase { get; init; }

    public string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"max={MaxInstructions};min={MinWrittenBytes};exit={AcceptEntrySectionExit};low={AcceptLowConfidence};base={(LoadBase is { } b ? b.ToString("X8") : "preferred")}"))).ToLowerInvariant()[..16];
}

/// <summary>Where a derived image came from. Stored beside the derived file and mixed into its cache key.</summary>
public sealed record UnpackLineage(string DerivedFromSha256, string UnpackerVersion, string OptionsDigest, string DerivedSha256)
{
    public string Token => $"unpack:{DerivedFromSha256}:{UnpackerVersion}:{OptionsDigest}";
}

public sealed record UnpackReport(
    bool Reached,
    uint? OepRva,
    OepConfidence Confidence,
    IReadOnlyList<string> Evidence,
    long Instructions,
    TimeSpan Elapsed,
    long BytesWrittenToImage,
    IReadOnlyList<ResolvedImport> ResolvedImports,
    IReadOnlyList<string> LoaderCalls,
    int ExportsPopulated,
    int ExportsTotal,
    int RebuiltImports,
    int RebuiltImportDescriptors,
    int CodePages,
    int DataPages,
    string? RefusalCode);

/// <summary>DerivedPe is null unless the run reached a trustworthy OEP and the derived image could be built.</summary>
public sealed record UnpackResult(byte[]? DerivedPe, UnpackReport Report, IReadOnlyList<BinaryDiagnostic> Diagnostics, UnpackLineage? Lineage)
{
    public bool Success => DerivedPe is not null;
}

/// <summary>
/// Generic unpacking by controlled execution: the packer stub is interpreted (never natively executed) in a managed sandbox until control first enters
/// code the stub itself generated, then the in-memory image is dumped into a NEW derived PE32. The input bytes are never modified. See docs/binary-unpack.md.
/// </summary>
public static class ControlledUnpacker
{
    public const string Version = "1.1.0";

    private const uint StackBase = 0x7A000000, StackSize = 0x100000, TebBase = 0x7FFDE000;

    public static UnpackResult TryUnpack(PeImage packed, UnpackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(packed);
        options ??= new UnpackOptions();
        var diags = new List<BinaryDiagnostic>();
        var evidence = new List<string>();
        var sw = Stopwatch.StartNew();
        IA32Emulator? cpu = null;
        GuestLoaderSandbox? loader = null;
        SandboxRegion? image = null;

        UnpackResult Fail(string code, string message, uint? at = null, OepConfidence confidence = OepConfidence.None)
        {
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Error, code, message, at is { } a && image is not null && a >= image.Base ? a - image.Base : null));
            return new UnpackResult(null, MakeReport(false, null, confidence, 0, 0, 0, 0, 0, 0, code), diags, null);
        }

        UnpackReport MakeReport(bool reached, uint? oep, OepConfidence confidence, int expPop, int expTot, int rebuilt, int descriptors, int codePages, int dataPages, string? refusal) =>
            new(reached, oep, confidence, evidence, cpu?.Steps ?? 0, sw.Elapsed, image?.BytesWritten ?? 0, loader?.Imports ?? [], loader?.Notes ?? [],
                expPop, expTot, rebuilt, descriptors, codePages, dataPages, refusal);

        if (packed.Machine != 0x14C || packed.SizeOfImage == 0 || packed.EntryPointRva == 0 || packed.Sections.Count == 0)
            return Fail("UNPACK_NOT_APPLICABLE", "The image is not a PE32 (x86) with sections and a non-zero entry point; there is no stub to run.");

        // The base the stub runs at. A base other than the preferred one needs the packed file's own relocations, applied like the Windows loader does.
        var loadBase = options.LoadBase ?? packed.ImageBase;
        if (loadBase == 0 || loadBase % 0x10000 != 0 || (ulong)loadBase + packed.SizeOfImage > 0x1_0000_0000UL)
            return Fail("UNPACK_BAD_LOAD_BASE", $"LoadBase 0x{loadBase:X8} must be non-zero, 64 KiB aligned and leave room for the image (0x{packed.SizeOfImage:X} bytes) below 4 GiB.");
        if (loadBase != packed.ImageBase && packed.Relocations.Count == 0)
            return Fail("UNPACK_LOAD_BASE_NO_RELOCS", $"LoadBase 0x{loadBase:X8} differs from the preferred base 0x{packed.ImageBase:X8} but the packed file has no base relocations, so the Windows loader could not map it there either. A stripped image is fixed at its preferred base.");

        SandboxMemory mem = new();
        try
        {
            var mapped = packed.MapImage();
            var size = (uint)((mapped.Length + 4095) & ~4095);
            Array.Resize(ref mapped, (int)size);
            if (loadBase != packed.ImageBase)
            {
                PeRelocator.Apply(packed, mapped, loadBase);
                // The loader also stores the actual base in the in-memory optional header; a stub that reads ImageBase from its own header sees the load address.
                BinaryPrimitives.WriteUInt32LittleEndian(mapped.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(0x3C)) + 24 + 28), loadBase);
            }
            image = mem.Add(new SandboxRegion("image", loadBase, mapped, true));
            var stack = mem.Add(new SandboxRegion("stack", StackBase, new byte[StackSize], false));
            var teb = new byte[0x2000];
            const uint pebOffset = 0x1000;
            var pebAddress = TebBase + pebOffset;
            mem.Add(new SandboxRegion("teb+peb", TebBase, teb, false) { ReadWatch = o => o == pebOffset + 2 ? "PEB.BeingDebugged" : o == pebOffset + 0x68 ? "PEB.NtGlobalFlag" : null });
            // Fixed zones that must not collide with the image: thunks, module handles, exit address and the heap start.
            foreach (var (lo, hi, what) in new[] { (0x7C000000u, 0x7E000000u, "sandbox loader zone"), (0x40000000u, 0x41000000u, "sandbox heap start") })
                if (image.Base < hi && lo < image.End) return Fail("UNPACK_ADDRESS_CONFLICT", $"The image range 0x{image.Base:X8}-0x{image.End:X8} overlaps the {what}.");

            loader = new GuestLoaderSandbox(mem, loadBase);
            cpu = new IA32Emulator(mem, TebBase);
            // TEB/PEB: only the fields stubs legitimately use are populated (SEH list head, stack bounds, self pointer, PEB pointer).
            mem.WriteBytes(TebBase, new byte[4]);
            mem.Write(TebBase + 0, 4, 0xFFFFFFFF);
            mem.Write(TebBase + 4, 4, StackBase + StackSize);
            mem.Write(TebBase + 8, 4, StackBase);
            mem.Write(TebBase + 0x18, 4, TebBase);
            mem.Write(TebBase + 0x20, 4, 0x1000);
            mem.Write(TebBase + 0x24, 4, 0x1004);
            mem.Write(TebBase + 0x30, 4, pebAddress);
            mem.Write(pebAddress + 8, 4, loadBase);
            Array.Clear(image.Written); // sandbox setup is not an unpacker write

            // The stub's own import table: bound exactly like the loader binds it.
            var stubSlots = new HashSet<uint>();
            foreach (var imp in packed.Imports)
            {
                var t = loader.Thunk(imp.Dll, imp.Name ?? "#" + imp.Ordinal, false);
                mem.Write(loadBase + imp.IatRva, 4, t);
                stubSlots.Add(imp.IatRva);
            }
            Array.Clear(image.Written);
            image.BytesWritten = 0;

            cpu.Esp = StackBase + StackSize - 0x200;
            if (packed.IsDll) { cpu.Push(0); cpu.Push(1); cpu.Push(loadBase); }
            cpu.Push(GuestLoaderSandbox.ExitAddress);
            cpu.Eip = loadBase + packed.EntryPointRva;

            int SectionOf(uint rva)
            {
                for (var k = 0; k < packed.Sections.Count; k++)
                {
                    var s = packed.Sections[k];
                    if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize)) return k;
                }
                return -1;
            }

            var entrySection = SectionOf(packed.EntryPointRva);
            if (entrySection < 0) return Fail("UNPACK_NOT_APPLICABLE", "The entry point does not lie inside any section.");
            evidence.Add($"stub entry 0x{packed.EntryPointRva:X} in section '{packed.Sections[entrySection].Name}'");

            uint? oep = null;
            var confidence = OepConfidence.None;
            SandboxRegion? lastRegion = null;
            var lastPage = -1;
            var warnedSelfModify = false;
            var mediumNoted = false;
            var done = false;

            while (!done)
            {
                if (cpu.Steps >= options.MaxInstructions)
                    return Fail("UNPACK_BUDGET_EXCEEDED", $"The stub did not reach an original entry point within {options.MaxInstructions} instructions (last EIP 0x{cpu.Eip:X8}).", cpu.Eip);
                if ((cpu.Steps & 0xFFFFF) == 0 && sw.Elapsed > options.MaxTime)
                    return Fail("UNPACK_TIMEOUT", $"The stub exceeded the {options.MaxTime.TotalSeconds:0}s wall-clock bound (last EIP 0x{cpu.Eip:X8}, {cpu.Steps} instructions).", cpu.Eip);
                var eip = cpu.Eip;
                if (eip == GuestLoaderSandbox.ExitAddress)
                    return Fail("UNPACK_NO_OEP", $"The stub returned from its entry point without ever transferring control to generated code ({cpu.Steps} instructions); there is no original entry point to dump.");
                if (GuestLoaderSandbox.IsThunk(eip)) { cpu.Steps++; loader.CallHost(cpu); continue; }

                var region = mem.Find(eip);
                if (region is not null && region.Track)
                {
                    var page = (int)((eip - region.Base) >> 12);
                    if (region != lastRegion || page != lastPage)
                    {
                        lastRegion = region; lastPage = page;
                        if (!region.Executed[page])
                        {
                            region.Executed[page] = true;
                            if (region == image)
                            {
                                var rva = eip - image.Base;
                                var sec = SectionOf(rva);
                                var fromSec = SectionOf(cpu.LastEip - image.Base);
                                var transfer = cpu.LastTransfer;
                                if (region.Written[page])
                                {
                                    if (sec == entrySection)
                                    {
                                        if (!warnedSelfModify)
                                        {
                                            warnedSelfModify = true;
                                            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "UNPACK_STUB_SELF_MODIFIES", $"The stub executes bytes it wrote inside its own section '{packed.Sections[entrySection].Name}' (first at 0x{rva:X}); this is interpreted correctly but the OEP cannot be told from stub layers.", rva));
                                        }
                                    }
                                    else if (transfer == Transfer.Call)
                                    {
                                        diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "UNPACK_CALL_INTO_GENERATED", $"The stub calls generated code at 0x{rva:X}; treated as a helper, not as the OEP.", rva));
                                    }
                                    else
                                    {
                                        var written = image.BytesWritten;
                                        confidence = written >= options.MinWrittenBytes ? OepConfidence.High : OepConfidence.Low;
                                        evidence.Add($"first {transfer.ToString().ToLowerInvariant()} (0x{cpu.LastEip:X8} in '{(fromSec >= 0 ? packed.Sections[fromSec].Name : "?")}') into a page the stub wrote and never executed before: 0x{rva:X} in section '{(sec >= 0 ? packed.Sections[sec].Name : "headers")}'");
                                        evidence.Add($"{written} byte(s) written to the image before the transfer, after {cpu.Steps} instructions");
                                        oep = rva; done = true;
                                    }
                                }
                                else if (sec != entrySection && sec >= 0 && fromSec == entrySection && packed.Sections[sec].IsExecutable && transfer is Transfer.Jump or Transfer.Ret)
                                {
                                    if (options.AcceptEntrySectionExit)
                                    {
                                        confidence = OepConfidence.Medium; oep = rva; done = true;
                                        evidence.Add($"jump out of the stub section into untouched executable section '{packed.Sections[sec].Name}' at 0x{rva:X} (no write evidence)");
                                    }
                                    else if (!mediumNoted)
                                    {
                                        mediumNoted = true;
                                        diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "UNPACK_ENTRY_SECTION_EXIT", $"The stub left its section for '{packed.Sections[sec].Name}' at 0x{rva:X} without write evidence; not treated as OEP (UnpackOptions.AcceptEntrySectionExit).", rva));
                                    }
                                }
                            }
                            else
                                diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "UNPACK_EXEC_IN_ALLOCATION", $"The stub executes code in an allocated buffer ({region.Name}) at 0x{eip:X8}; interpreted, but it can never be the OEP."));
                        }
                    }
                    if (done) break;
                }

                var ins = cpu.Fetch();
                cpu.Execute(ins);
            }

            if (oep is null) return Fail("UNPACK_NO_OEP", "No original entry point was identified.");
            if (confidence == OepConfidence.Low && !options.AcceptLowConfidence)
                return Fail("UNPACK_LOW_CONFIDENCE_OEP", $"Control entered generated code at 0x{oep:X} but only {image.BytesWritten} byte(s) had been written (< {options.MinWrittenBytes}); refusing a Low-confidence OEP.", image.Base + oep, OepConfidence.Low);

            // Evidence that the dump is meaningful: exported code must now exist in memory.
            var exportable = packed.Exports.Where(e => e.ForwarderTarget is null).ToList();
            var populated = exportable.Count(e => e.Rva < image.Data.Length && image.Data[e.Rva] != 0 && (SectionOf(e.Rva) is var si && si >= 0 && (packed.Sections[si].RawSize == 0 || image.Written[(int)(e.Rva >> 12)])));
            evidence.Add($"{populated}/{exportable.Count} export target(s) contain bytes produced by the run");
            if (exportable.Count > 0 && populated == 0)
                return Fail("UNPACK_EXPORTS_NOT_POPULATED", "None of the exports points into memory the stub produced; the transfer was not the original entry point.", image.Base + oep, OepConfidence.Low);

            var built = DerivedPeWriter.Build(packed, image.Data, oep.Value, loadBase, loader, stubSlots, entrySection, diags);
            if (built is null)
                return new UnpackResult(null, MakeReport(true, oep, confidence, populated, exportable.Count, 0, 0, 0, 0, "UNPACK_REBUILD_FAILED"), diags, null);

            var derivedSha = Convert.ToHexString(SHA256.HashData(built.Bytes)).ToLowerInvariant();
            var lineage = new UnpackLineage(packed.Sha256, Version, options.Digest(), derivedSha);
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "UNPACK_DERIVED", $"Derived image {derivedSha[..16]} from {packed.Sha256[..16]} (OEP 0x{oep:X}, {confidence} confidence, {cpu.Steps} instructions)."));
            return new UnpackResult(built.Bytes, MakeReport(true, oep, confidence, populated, exportable.Count, built.ImportCount, built.DescriptorCount, built.CodePages, built.DataPages, null), diags, lineage);
        }
        catch (UnpackRefusalException refusal)
        {
            return Fail(refusal.Code, refusal.Message, refusal.Address);
        }
    }
}
