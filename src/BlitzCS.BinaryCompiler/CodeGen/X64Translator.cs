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
public sealed class X64Translator
{
    public static TranslationArtifact Translate(PeImage image, BinaryCompilerOptions options, string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        var mapped = image.MapImage();
        var cfg = ControlFlowGraph.Build(image, mapped);
        var release = options.Profile != OptimizationProfile.Debug;

        var exitRva = (image.SizeOfImage + 15u) & ~15u;
        var stubBase = exitRva + 16;
        var importCount = (uint)image.Imports.Count;
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

        (X64CodeGen, CodeEmitter.Result) Generate(ulong dataBase, Func<ulong, uint>? previousOffsets, CodeEmitter? seed)
        {
            var g = new X64CodeGen(image, cfg, release, dataBase, thunkRel, boundRel, tableRel, tableEntries, exitRva, stubBase) { PreviousOffsets = previousOffsets };
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
        for (var k = 0; k < image.Imports.Count; k++) SetEntry(stubBase + 16u * (uint)k, enc.OffsetOf(gen.ImportBodyLabels[k]));
        // Clamp vector: every lane = last valid index; that entry stays 0 (trap).
        for (var lane = 0; lane < 4; lane++)
            BinaryPrimitives.WriteUInt32LittleEndian(host.AsSpan(codeSize + (int)boundRel + 4 * lane), tableEntries - 1);

        var diagnostics = new List<BinaryDiagnostic>(image.Diagnostics);
        diagnostics.AddRange(cfg.Diagnostics);
        diagnostics.AddRange(gen.Diagnostics);
        if (image.Tls is not null)
            diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "BC_TLS_UNSUPPORTED", "The image has a TLS directory; thread-local storage and TLS callbacks are not translated."));
        diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "BC_FIXED_BASE", $"Translated code is bound to the fixed guest base 0x{image.ImageBase:X8}."));

        var exports = new List<TranslatedExport>();
        foreach (var e in image.Exports.OrderBy(e => e.Ordinal))
        {
            if (e.ForwarderTarget is not null)
            {
                diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "BC_FORWARDED_EXPORT", $"Forwarded export '{e.Name ?? "#" + e.Ordinal}' -> {e.ForwarderTarget} is not callable.", e.Rva));
                continue;
            }
            if (!gen.GuestLabels.TryGetValue(e.Rva, out var label) || !cfg.Instructions.ContainsKey(e.Rva))
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
            exitRva, stubBase, addressMap, exports, image.Imports, diagnostics);
    }
}
