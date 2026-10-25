using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Lifting;

namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>
/// Ahead-of-time translator from PE32 (IA-32) to x64 host code using address-space-preserving widening.
///
/// HostImage layout: [code][0xCC padding to a 4096 multiple][RW data]. RW data = GuestContext (64 bytes) at ContextOffset,
/// uint64 import thunk table, a 16-byte aligned clamp vector, and the uint32 dispatch table.
/// GuestContext: [0] saved native RSP, [8] reserved, [16] trap kind (0 none, 1 untranslated branch target,
/// 2 unsupported/undecodable instruction), [24] guest address of the trap (VA). When a trap fires EnterGuest returns 0 and the
/// loader should inspect these fields. Host offset 0 is the trap routine, so zero dispatch entries trap.
/// Translated code embeds guest addresses as constants: the guest image must be mapped at GuestImageBase.
/// </summary>
/// <summary>Result of <see cref="X64Translator.TranslateDynamic"/>: the artifact, the SHA-256 of the snapshot it was made from, which snapshot bytes belong to a translated instruction, and how many instructions were translated.</summary>
public sealed record DynamicTranslation(TranslationArtifact Artifact, string SnapshotSha256, bool[] InstructionBytes, int InstructionCount);

public sealed class X64Translator
{
    /// <summary>Returns null when the TLS directory has a form the runtime models, else the reason it cannot.</summary>
    private static string? ValidateTls(PeImage image, PeTls tls, byte[] mapped, ControlFlowGraph cfg)
    {
        bool InImage(uint va, uint length) => va >= image.ImageBase && (ulong)(va - image.ImageBase) + length <= (ulong)mapped.Length;
        if (tls.EndRaw < tls.StartRaw || (tls.EndRaw != tls.StartRaw && !InImage(tls.StartRaw, tls.EndRaw - tls.StartRaw)))
            return $"TLS data template 0x{tls.StartRaw:X8}..0x{tls.EndRaw:X8} does not lie inside the image.";
        if (!InImage(tls.IndexAddress, 4) || tls.IndexAddress % 4 != 0)
            return $"TLS index address 0x{tls.IndexAddress:X8} is not a 4-byte aligned location inside the image.";
        if (tls.SizeOfZeroFill > 0x100000 || tls.EndRaw - tls.StartRaw > 0x100000)
            return "TLS data larger than 1 MiB per thread is not supported.";
        if (tls.CallbacksVa != 0)
        {
            if (!InImage(tls.CallbacksVa, 4)) return $"TLS callback array 0x{tls.CallbacksVa:X8} does not lie inside the image.";
            var callbacks = 0;
            for (var at = tls.CallbacksVa - image.ImageBase; ; at += 4)
            {
                if ((ulong)at + 4 > (ulong)mapped.Length || callbacks > 64) return "The TLS callback array is not zero-terminated inside the image.";
                var va = BitConverter.ToUInt32(mapped, (int)at);
                if (va == 0) break;
                callbacks++;
                if (!cfg.Instructions.ContainsKey(va - image.ImageBase)) return $"TLS callback 0x{va:X8} is not translatable code.";
            }
        }
        return null;
    }

    /// <summary>
    /// Translates guest code that exists only at run time (docs/binary-dynamic-code.md): <paramref name="code"/> is a snapshot of the guest bytes at
    /// <paramref name="guestBase"/> (page aligned, the executable run of pages the guest allocated), and <paramref name="entries"/> are the guest addresses control
    /// reached there. The snapshot becomes a synthetic one-section image (no headers, imports or relocations; the snapshot's SHA-256 is its identity) and goes through
    /// the unchanged translator: CFG from the entries plus the speculative pointer/immediate roots of an image without relocations, bounded by the snapshot.
    /// Targets outside it become dispatch misses, which reach other modules through the registry. The caller loads the artifact itself
    /// (it has no PE image to validate against); the fixed-base and target-base diagnostics of a mapped image do not apply and are left out.
    /// </summary>
    public static DynamicTranslation TranslateDynamic(byte[] code, uint guestBase, IReadOnlyCollection<uint> entries, BinaryCompilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(entries);
        if (code.Length == 0 || code.Length % 4096 != 0) throw new ArgumentException("The snapshot must be a non-empty whole number of 4 KiB pages.", nameof(code));
        if (guestBase % 4096 != 0 || (ulong)guestBase + (ulong)code.Length > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(guestBase), $"0x{guestBase:X8} is not a page-aligned base whose run fits below 4 GiB.");
        if (entries.Count == 0) throw new ArgumentException("At least one entry address is needed.", nameof(entries));
        foreach (var e in entries)
            if (e < guestBase || (ulong)e >= (ulong)guestBase + (ulong)code.Length) throw new ArgumentOutOfRangeException(nameof(entries), $"Entry 0x{e:X8} lies outside the snapshot 0x{guestBase:X8}+0x{code.Length:X}.");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(code)).ToLowerInvariant();
        var image = new PeImage
        {
            Bytes = code, Sha256 = sha, Machine = 0x14C, IsDll = true, ImageBase = guestBase, SizeOfImage = (uint)code.Length, EntryPointRva = 0,
            Sections = [new PeSection(".dyncode", 0, (uint)code.Length, 0, (uint)code.Length, 0x60000020)],
            Imports = [], Relocations = [], Diagnostics = [],
            Exports = [.. entries.Order().Select((e, i) => new PeExport($"dyn_{e:X8}", (ushort)(i + 1), e - guestBase, null))],
        };
        var artifact = TranslateCore(image, options with { LoadBase = null, DynamicStubs = 0 }, $"dynamic:{sha}", out var cfg);
        var codeBytes = new bool[code.Length];
        foreach (var (rva, g) in cfg.Instructions)
            for (var k = 0; k < g.Instr.Length && rva + k < code.Length; k++) codeBytes[rva + k] = true;
        return new DynamicTranslation(artifact with { Diagnostics = [.. artifact.Diagnostics.Where(d => d.Code is not ("BC_BASE_UNUSABLE_ON_TARGET" or "BC_FIXED_BASE"))] },
            sha, codeBytes, cfg.Instructions.Count);
    }

    public static TranslationArtifact Translate(PeImage image, BinaryCompilerOptions options, string cacheKey) => TranslateCore(image, options, cacheKey, out _);

    private static TranslationArtifact TranslateCore(PeImage image, BinaryCompilerOptions options, string cacheKey, out ControlFlowGraph cfg)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        var targetOs = BinaryTargets.Normalize(options.TargetOs);
        var abi = HostAbis.For(targetOs);
        if (options.DynamicStubs is < 0 or > BinaryCompilerOptions.MaxDynamicStubs)
            throw new ArgumentOutOfRangeException(nameof(options), options.DynamicStubs, $"DynamicStubs must be between 0 and {BinaryCompilerOptions.MaxDynamicStubs}.");
        if (options.LoadBase is { } loadBase && loadBase != image.ImageBase) image = image.Rebase(loadBase);
        var mapped = image.MapImage();
        var graph = ControlFlowGraph.Build(image, mapped);
        cfg = graph;
        var release = options.Profile != OptimizationProfile.Debug;

        var exitRva = (image.SizeOfImage + 15u) & ~15u;
        var stubBase = exitRva + 16;
        var importCount = (uint)image.Imports.Count + (uint)options.DynamicStubs + ImportStubLayout.RuntimeStubCount;  // real imports, the dynamic stub pool (0 = none), then the runtime stubs (guest SEH)
        var tableEntries = stubBase + 16 * importCount + 16;
        var ctxRel = 0u;
        var thunkRel = 64u;
        var boundRel = (thunkRel + 8 * importCount + 15u) & ~15u;
        var tableRel = boundRel + 16;
        var dataLength = tableRel + 4 * tableEntries;

        // Layout passes: the first sizes the code, later ones embed the previous pass's label offsets (host-offset jump tables) and
        // repeat until the layout is a fixed point, so those tables are exact by construction.
        var (gen, enc) = Generate(0x10000000UL, null, null);
        var codeSize = (enc.Code.Length + 4095) & ~4095;
        for (var attempt = 0; ; attempt++)
        {
            var (nextGen, nextEnc) = Generate((ulong)codeSize, enc.OffsetOf, gen.E);
            var stable = nextGen.ShadowTablesMatch(nextEnc.OffsetOf, nextEnc.Code);
            (gen, enc) = (nextGen, nextEnc);
            if (enc.Code.Length > codeSize) { codeSize = (enc.Code.Length + 4095) & ~4095; stable = false; }
            if (stable) break;
            if (attempt == 6) throw new InvalidOperationException("Host code layout did not reach a fixed point.");
        }

        int ImportBodyCount() => image.Imports.Count + options.DynamicStubs + ImportStubLayout.RuntimeStubCount;

        (X64CodeGen, CodeEmitter.Result) Generate(ulong dataBase, Func<ulong, uint>? previousOffsets, CodeEmitter? seed)
        {
            var g = new X64CodeGen(image, graph, release, dataBase, thunkRel, boundRel, tableRel, tableEntries, exitRva, stubBase, abi, options.DynamicStubs) { PreviousOffsets = previousOffsets };
            g.EmitRuntime();
            g.EmitGuestCode();
            return (g, g.E.Encode(seed));
        }

        var host = new byte[codeSize + dataLength];
        Array.Fill(host, (byte)0xCC, 0, codeSize);
        enc.Code.CopyTo(host, 0);
        var tableAbs = codeSize + (int)tableRel;
        void SetEntry(uint rva, uint offset) => BinaryPrimitives.WriteUInt32LittleEndian(host.AsSpan(tableAbs + 4 * (int)rva), offset);

        var addressMap = new List<AddressMapEntry>();
        foreach (var (rva, label) in gen.GuestEntries())
        {
            var off = enc.OffsetOf(label);
            SetEntry(rva, off);
            addressMap.Add(new AddressMapEntry(rva, off));
        }
        addressMap.Sort((a, b) => a.GuestRva.CompareTo(b.GuestRva));
        SetEntry(exitRva, enc.OffsetOf(gen.ExitCommonLabel));
        for (var k = 0; k < ImportBodyCount(); k++) SetEntry(stubBase + 16u * (uint)k, enc.OffsetOf(gen.ImportBodyLabels[k]));
        // Clamp vector: every lane = last valid index; that entry stays 0 (trap).
        for (var lane = 0; lane < 4; lane++)
            BinaryPrimitives.WriteUInt32LittleEndian(host.AsSpan(codeSize + (int)boundRel + 4 * lane), tableEntries - 1);

        var diagnostics = new List<BinaryDiagnostic>(image.Diagnostics);
        diagnostics.AddRange(graph.Diagnostics);
        diagnostics.AddRange(gen.Diagnostics);
        if (image.Tls is { } tls)
        {
            var problem = ValidateTls(image, tls, mapped, graph);
            diagnostics.Add(problem is null
                ? new BinaryDiagnostic(DiagnosticSeverity.Info, "TLS_HANDLED", "Implicit TLS is modelled: per-thread TLS data, the TLS index and TLS callbacks are provided by the runtime.")
                : new BinaryDiagnostic(DiagnosticSeverity.Error, "BC_TLS_UNSUPPORTED", problem));
        }
        if (targetOs == BinaryTargets.WindowsX64 && image.ImageBase < ModuleBaseAllocator.WindowsMinimumBase)
            diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Error, "BC_BASE_UNUSABLE_ON_TARGET",
                $"The guest base 0x{image.ImageBase:X8} lies below 0x{ModuleBaseAllocator.WindowsMinimumBase:X8}, which cannot be reserved on windows-x64 (the range holds the host's own mappings and is unavailable under Wine). Rebase the library (PeImage.Rebase / ModuleBaseAllocator.RequiredBase / BinaryCompilerOptions.LoadBase); images without base relocations cannot run on windows-x64."));
        diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "BC_FIXED_BASE", $"Translated code is bound to the fixed guest base 0x{image.ImageBase:X8}" + (image.RebasedFrom is { } rf ? $" (rebased from 0x{rf.OriginalImageBase:X8})." : ".")));

        var exports = new List<TranslatedExport>();
        foreach (var e in image.Exports.OrderBy(e => e.Ordinal))
        {
            if (e.ForwarderTarget is not null)
            {
                diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "BC_FORWARDED_EXPORT", $"Forwarded export '{e.Name ?? "#" + e.Ordinal}' -> {e.ForwarderTarget} is not callable.", e.Rva));
                continue;
            }
            if (!gen.GuestLabels.TryGetValue(e.Rva, out var label) || !graph.Instructions.ContainsKey(e.Rva))
            {
                diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "BC_EXPORT_NOT_CODE", $"Export '{e.Name ?? "#" + e.Ordinal}' does not point at translated code.", e.Rva));
                continue;
            }
            var (conv, args) = ExportAbiHints.Infer(e.Name);
            exports.Add(new TranslatedExport(e.Name, e.Ordinal, e.Rva, enc.OffsetOf(label), conv, args));
        }

        return new TranslationArtifact(
            ArtifactSerializer.CurrentVersion, image.Sha256, cacheKey, image.ImageBase, image.SizeOfImage, host, codeSize,
            (uint)(codeSize + tableRel), (uint)(codeSize + thunkRel), (uint)(codeSize + ctxRel), enc.OffsetOf(gen.EnterLabel),
            exitRva, stubBase, addressMap, exports, image.Imports, diagnostics, image.RebasedFrom?.OriginalImageBase, targetOs, options.DynamicStubs, ImportStubLayout.RuntimeStubCount);
    }
}
