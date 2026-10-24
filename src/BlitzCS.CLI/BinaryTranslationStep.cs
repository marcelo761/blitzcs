using BlitzCS.BinaryCompiler;

namespace BlitzCS.CLI;

/// <summary>Translates catalog 'binaryLibraries' (PE32 x86) into x64 artifacts next to the output. A no-op for programs without them.</summary>
internal static class BinaryTranslationStep
{
    internal static void Run(ExternalCatalogResult catalog, string outputPath, TextWriter output)
    {
        if (catalog.BinaryLibraries is not { Count: > 0 } libraries) return;
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
            var target = Path.Combine(outputDirectory, Path.GetFileName(library) + ".bcx");
            var bytes = new ArtifactSerializer().Serialize(result.Artifact);
            var temporary = target + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
            output.WriteLine($"binary: {Path.GetFileName(library)} -> {Path.GetFileName(target)} ({(result.CacheHit ? "cache hit" : "translated")}, {result.Artifact.HostImage.Length} bytes, {result.Artifact.Diagnostics.Count} diagnostic(s))");
        }
    }
}
