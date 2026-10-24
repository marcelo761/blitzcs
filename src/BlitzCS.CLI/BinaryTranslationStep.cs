using BlitzCS.BinaryCompiler;

namespace BlitzCS.CLI;

/// <summary>Translates catalog 'binaryLibraries' (PE32 x86) into x64 artifacts next to the output. A no-op for programs without them.</summary>
internal static class BinaryTranslationStep
{
    internal static void Run(ExternalCatalogResult catalog, string outputPath, TextWriter output) =>
        Prepare(catalog, outputPath).Publish(output);

    /// <summary>Translates (or loads from cache) and validates every library and binding without writing next to the output.</summary>
    internal static PreparedBinaries Prepare(ExternalCatalogResult catalog, string outputPath)
    {
        var pending = new List<(string Library, string Target, byte[] Bytes, BinaryBuildResult Result)>();
        if (catalog.BinaryLibraries is not { Count: > 0 } libraries) return new(pending);
        var profile = catalog.BinaryProfile is null ? OptimizationProfile.Release
            : Enum.TryParse<OptimizationProfile>(catalog.BinaryProfile, ignoreCase: true, out var parsed) ? parsed
            : throw new ArgumentException($"Unknown binaryProfile '{catalog.BinaryProfile}'; expected Debug, Release or ReleasePgo.");
        var options = new BinaryCompilerOptions(profile);
        var cacheDirectory = Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE")
            ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "blitzcs", "binary");
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        foreach (var library in libraries)
        {
            if (!BinaryBuild.IsPe32X86(library))
                throw new ArgumentException($"Binary library '{library}' is not a PE32 x86 image.");
            var result = BinaryBuild.Translate(library, options, cacheDirectory);
            var errors = result.Artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
                throw new InvalidOperationException($"Binary library '{Path.GetFileName(library)}' contains unsupported constructs:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, errors.Take(20).Select(d => $"  {d.Code} at RVA 0x{d.GuestRva ?? 0:X}: {d.Message}"))
                    + (errors.Length > 20 ? $"{Environment.NewLine}  ... {errors.Length - 20} more" : ""));
            foreach (var binding in catalog.BinaryBindings?.Where(b => b.LibraryPath == library) ?? [])
                CheckBinding(binding, result.Artifact);
            pending.Add((library, Path.Combine(outputDirectory, Path.GetFileName(library) + ".bcx"), new ArtifactSerializer().Serialize(result.Artifact), result));
        }
        return new(pending);
    }

    private static void CheckBinding(BinaryBinding binding, TranslationArtifact artifact)
    {
        var label = $"'{binding.BasicName}' ({Path.GetFileName(binding.LibraryPath)}!{binding.Export})";
        var export = artifact.Exports.FirstOrDefault(e => e.Name == binding.Export)
            ?? throw new ArgumentException($"External function {label}: the translation has no such export.");
        if (export.Convention is GuestCallingConvention.Thiscall or GuestCallingConvention.Fastcall)
            throw new ArgumentException($"External function {label} is {export.Convention}, which passes arguments in registers and is unsupported.");
        var effective = binding.Convention != GuestCallingConvention.Unknown ? binding.Convention : export.Convention;
        if (effective == GuestCallingConvention.Unknown)
            throw new ArgumentException($"External function {label}: the calling convention cannot be inferred from the export; set binary.convention to 'cdecl' or 'stdcall'.");
        if (export.Convention != GuestCallingConvention.Unknown && binding.Convention != GuestCallingConvention.Unknown && export.Convention != binding.Convention)
            throw new ArgumentException($"External function {label}: the catalog says {binding.Convention} but the export decoration says {export.Convention}.");
        if (effective == GuestCallingConvention.Stdcall && export.ArgBytes is { } bytes && bytes != 4 * binding.Parameters.Count)
            throw new ArgumentException($"External function {label}: stdcall export pops {bytes} byte(s) but the declaration passes {4 * binding.Parameters.Count}.");
    }
}

internal sealed class PreparedBinaries(List<(string Library, string Target, byte[] Bytes, BinaryBuildResult Result)> pending)
{
    internal void Publish(TextWriter output)
    {
        foreach (var (library, target, bytes, result) in pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
            output.WriteLine($"binary: {Path.GetFileName(library)} -> {Path.GetFileName(target)} ({(result.CacheHit ? "cache hit" : "translated")}, {result.Artifact.HostImage.Length} bytes, {result.Artifact.Diagnostics.Count} diagnostic(s))");
        }
    }
}
