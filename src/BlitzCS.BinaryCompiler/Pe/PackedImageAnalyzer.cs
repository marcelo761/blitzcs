using System.Globalization;

namespace BlitzCS.BinaryCompiler;

public enum PackedVerdict { None, Suspected, Likely }

/// <summary>Shannon entropy (bits per byte, 0..8) of the file-backed bytes of one section (first 1 MiB at most).</summary>
public sealed record SectionEntropy(string Name, double Entropy, uint RawSize);

/// <summary>
/// Result of <see cref="PackedImageAnalyzer"/>. Diagnostics use the codes PACK_*; they are Info/Warning only (the analyzer never fails a build:
/// the only Error on a packed image remains BC_ROOT_NOT_FILE_BACKED, raised by the lifter). Section names never count as proof.
/// </summary>
public sealed record PackedImageReport(
    PackedVerdict Verdict, int Score, IReadOnlyList<string> Reasons, IReadOnlyList<BinaryDiagnostic> Diagnostics,
    IReadOnlyList<SectionEntropy> Entropies, string? NameHint)
{
    /// <summary>One line suitable for an error message, empty when the verdict is None.</summary>
    public string Summary => Verdict == PackedVerdict.None ? "" :
        $"this image appears to be packed ({Verdict.ToString().ToLowerInvariant()}: {string.Join("; ", Reasons)}); "
        + "AOT recompilation cannot see code produced at run time";
}

/// <summary>
/// Generic, structure-based detection of packed/obfuscated PE32 images (no packer-specific knowledge). Evidence is additive:
/// unbacked executable sections, entry point anomalies in high-entropy data, minimal loader-only imports, W+X sections, exports into
/// memory that does not exist in the file. Section names only produce an Info hint.
/// </summary>
public static class PackedImageAnalyzer
{
    public const double HighEntropy = 7.1;
    private const int MaxEntropySample = 1 << 20, MinEntropySample = 256;
    private static readonly string[] NameHints = ["UPX", ".aspack", ".adata", ".themida", ".winlice", ".vmp", ".petite", ".mpress", ".nsp", ".enigma", ".perplex", ".yP", ".packed"];
    private static readonly HashSet<string> LoaderApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "LoadLibraryA", "LoadLibraryW", "LoadLibraryExA", "LoadLibraryExW", "GetProcAddress", "GetModuleHandleA", "GetModuleHandleW",
        "VirtualAlloc", "VirtualProtect", "VirtualFree", "ExitProcess", "FreeLibrary",
    };

    public static PackedImageReport Analyze(ReadOnlyMemory<byte> file) => Analyze(PeReader.Read(file));

    public static PackedImageReport Analyze(PeImage image)
    {
        var diags = new List<BinaryDiagnostic>(); var reasons = new List<string>(); var score = 0;
        void Add(DiagnosticSeverity sev, string code, string message, int weight, uint? rva = null, string? reason = null)
        {
            diags.Add(new BinaryDiagnostic(sev, code, message, rva)); score += weight;
            if (reason is not null) reasons.Add(reason);
        }

        static string F(double d) => d.ToString("F2", CultureInfo.InvariantCulture);
        var entropies = image.Sections.Select(s => new SectionEntropy(s.Name, Entropy(image.Bytes, s), s.RawSize)).ToList();
        double EntropyOf(PeSection s) => entropies[image.Sections.ToList().IndexOf(s)].Entropy;
        static bool Unbacked(PeSection s) => s.RawSize == 0 && s.VirtualSize > 0;
        static uint Extent(PeSection s) => Math.Max(s.VirtualSize, s.RawSize);
        PeSection? SectionOf(uint rva) => image.Sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < (ulong)s.VirtualAddress + Extent(s));
        bool Backed(uint rva) => SectionOf(rva) is { } s ? rva - s.VirtualAddress < s.RawSize : rva < 0x1000;

        foreach (var s in image.Sections.Where(s => Unbacked(s) && s.IsExecutable))
            Add(DiagnosticSeverity.Warning, "PACK_UNBACKED_EXEC_SECTION",
                $"Executable section '{s.Name}' has VirtualSize 0x{s.VirtualSize:X} but no data in the file; its code is created at run time.", 3, s.VirtualAddress,
                $"executable section '{s.Name}' is empty in the file (0x{s.VirtualSize:X} bytes created at run time)");

        var entrySection = image.EntryPointRva == 0 ? null : SectionOf(image.EntryPointRva);
        if (entrySection is { } es)
        {
            var firstExec = image.Sections.FirstOrDefault(s => s.IsExecutable && !Unbacked(s));
            var high = es.RawSize >= MinEntropySample && EntropyOf(es) >= HighEntropy;
            if (!Backed(image.EntryPointRva) && !image.Sections.Any(s => Unbacked(s) && s.IsExecutable && s == es))
                Add(DiagnosticSeverity.Warning, "PACK_ENTRY_UNBACKED", $"Entry point 0x{image.EntryPointRva:X} lies in memory that is not in the file.", 3, image.EntryPointRva,
                    "the entry point is in memory that is not stored in the file");
            else if (Unbacked(es))
                Add(DiagnosticSeverity.Warning, "PACK_ENTRY_UNBACKED", $"Entry point 0x{image.EntryPointRva:X} lies in empty section '{es.Name}'.", 1, image.EntryPointRva);
            else if (firstExec is not null && es != firstExec && high)
                Add(DiagnosticSeverity.Warning, "PACK_ENTRY_IN_HIGH_ENTROPY", $"Entry point 0x{image.EntryPointRva:X} is in '{es.Name}' (entropy {F(EntropyOf(es))}), not in the first executable section '{firstExec.Name}'.", 2,
                    image.EntryPointRva, $"the entry point is in '{es.Name}' (entropy {F(EntropyOf(es))}), not in the first code section");
        }

        foreach (var s in image.Sections)
        {
            if (s.RawSize < MinEntropySample) continue;
            var e = EntropyOf(s);
            if (e >= HighEntropy && s.IsExecutable)
                Add(DiagnosticSeverity.Warning, "PACK_HIGH_ENTROPY", $"Executable section '{s.Name}' has entropy {F(e)} bits/byte (compressed or encrypted data).", 2, s.VirtualAddress,
                    $"executable section '{s.Name}' looks compressed (entropy {F(e)})");
            else if (e >= HighEntropy)
                Add(DiagnosticSeverity.Info, "PACK_HIGH_ENTROPY_DATA", $"Section '{s.Name}' has entropy {F(e)} bits/byte.", 0, s.VirtualAddress);
        }

        foreach (var s in image.Sections.Where(s => s.IsExecutable && s.IsWritable && !Unbacked(s)))
            Add(DiagnosticSeverity.Info, "PACK_WX_SECTION", $"Section '{s.Name}' is writable and executable.", 1, s.VirtualAddress, $"section '{s.Name}' is writable and executable");

        var named = image.Imports.Where(i => i.Name is not null).ToList();
        var hasLoader = named.Any(i => i.Name!.StartsWith("LoadLibrary", StringComparison.OrdinalIgnoreCase)) && named.Any(i => i.Name == "GetProcAddress");
        var others = image.Imports.Where(i => i.Name is null || !LoaderApis.Contains(i.Name)).GroupBy(i => i.Dll, StringComparer.OrdinalIgnoreCase).ToList();
        if (hasLoader && image.Imports.Count is > 0 and <= 6 && others.Count == 0)
            Add(DiagnosticSeverity.Warning, "PACK_MINIMAL_IMPORTS",
                $"The import table has only {image.Imports.Count} loader-class function(s) ({string.Join(", ", image.Imports.Select(i => i.Name))}); the real imports are resolved at run time.", 1,
                reason: "the import table only has loader functions (LoadLibrary/GetProcAddress class)");
        else if (hasLoader && image.Imports.Count <= 16 && others.Count >= 3 && others.All(g => g.Count() == 1))
            Add(DiagnosticSeverity.Warning, "PACK_MINIMAL_IMPORTS",
                $"The import table has {image.Imports.Count} function(s): LoadLibrary/GetProcAddress plus exactly one function from each of {others.Count} other DLL(s), the shape of a loader stub that only keeps the DLLs referenced.", 1,
                reason: "the import table is a loader stub (LoadLibrary/GetProcAddress plus one function per DLL)");

        var unbackedExports = image.Exports.Where(x => x.ForwarderTarget is null && !Backed(x.Rva)).ToList();
        if (unbackedExports.Count > 0)
            Add(DiagnosticSeverity.Warning, "PACK_EXPORT_UNBACKED",
                $"{unbackedExports.Count} export(s) point into memory that is not stored in the file (first: {unbackedExports[0].Name ?? "#" + unbackedExports[0].Ordinal} at RVA 0x{unbackedExports[0].Rva:X}).", 3,
                unbackedExports[0].Rva, $"{unbackedExports.Count} export(s) point into memory that is not stored in the file");

        string? hint = image.Sections.Select(s => s.Name).FirstOrDefault(n => NameHints.Any(h => n.StartsWith(h, StringComparison.OrdinalIgnoreCase)));
        if (hint is not null)
            Add(DiagnosticSeverity.Info, "PACK_NAME_HINT", $"Section name '{hint}' is commonly used by packers; this is a hint only and is not used as proof.", 0);

        var verdict = score >= 4 ? PackedVerdict.Likely : score >= 2 ? PackedVerdict.Suspected : PackedVerdict.None;
        if (verdict != PackedVerdict.None)
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "PACK_VERDICT", $"Packed image verdict: {verdict} (score {score}).", null));
        return new PackedImageReport(verdict, score, reasons, diags, entropies, hint);
    }

    private static double Entropy(byte[] file, PeSection s)
    {
        if (s.RawSize < MinEntropySample || (ulong)s.RawOffset + s.RawSize > (ulong)file.Length) return 0;
        var n = (int)Math.Min(s.RawSize, MaxEntropySample);
        Span<int> counts = stackalloc int[256];
        foreach (var b in file.AsSpan((int)s.RawOffset, n)) counts[b]++;
        double h = 0;
        foreach (var c in counts) if (c != 0) { var p = (double)c / n; h -= p * Math.Log2(p); }
        return h;
    }
}
