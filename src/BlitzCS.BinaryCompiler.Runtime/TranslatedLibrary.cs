using BlitzCS.BinaryCompiler;

namespace BlitzCS.BinaryCompiler.Runtime;

public sealed class TranslatedLibraryException(string message) : Exception(message);

/// <summary>Loads a PE32 library through the translation artifact the BlitzCS build placed next to it (<c>name.dll.bcx</c>).</summary>
public static class TranslatedLibrary
{
    public const string ArtifactExtension = ".bcx";

    public static GuestModule Open(string dllPath, IGuestImportResolver imports, GuestModuleOptions? options = null)
    {
        var (artifact, image) = Read(dllPath);
        return GuestModule.Load(artifact, image, imports, options);
    }

    /// <summary>Extension of the derived PE that a build with an unpack/preprocess step publishes beside the (unchanged) original DLL.</summary>
    public const string DerivedExtension = ".derived";

    /// <summary>Extension of the JSON lineage record published beside the derived PE.</summary>
    public const string LineageExtension = ".lineage.json";

    /// <summary>
    /// Reads and validates the artifact and the image it was translated from: the DLL itself, or, for a library preprocessed at build time
    /// (<c>"unpack": true</c>), the derived PE published as <c>name.dll.derived</c> after checking its lineage against the original DLL.
    /// </summary>
    public static (TranslationArtifact Artifact, PeImage Image) Read(string dllPath)
    {
        var full = Path.GetFullPath(dllPath);
        var artifactPath = full + ArtifactExtension;
        if (!File.Exists(full)) throw new TranslatedLibraryException($"Original library '{full}' does not exist.");
        if (!File.Exists(artifactPath)) throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' is missing; build the program with the library listed under 'binaryLibraries'.");
        var dll = File.ReadAllBytes(full);
        TranslationArtifact artifact;
        try { artifact = new ArtifactSerializer().Deserialize(File.ReadAllBytes(artifactPath)); }
        catch (ArtifactFormatException error) { throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' is corrupt: {error.Message}"); }
        var source = dll;
        if (!string.Equals(artifact.SourceSha256, BinaryBuild.Sha256(dll), StringComparison.OrdinalIgnoreCase))
        {
            var derivedPath = full + DerivedExtension;
            if (!File.Exists(derivedPath))
                throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' does not match '{full}'; rebuild the program.");
            source = File.ReadAllBytes(derivedPath);
            if (!string.Equals(artifact.SourceSha256, BinaryBuild.Sha256(source), StringComparison.OrdinalIgnoreCase))
                throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' does not match '{derivedPath}'; rebuild the program.");
            CheckLineage(full + LineageExtension, full, dll, source);
        }
        var image = PeReader.Read(source);
        if (artifact.RebasedFromImageBase is { } from && from == image.ImageBase && artifact.GuestImageBase != image.ImageBase)
            image = image.Rebase(artifact.GuestImageBase); // the artifact was translated for a rebased copy of this file
        return (artifact, image);
    }

    private static void CheckLineage(string lineagePath, string full, byte[] original, byte[] derived)
    {
        if (!File.Exists(lineagePath))
            throw new TranslatedLibraryException($"The derived image of '{full}' has no lineage record '{lineagePath}'; rebuild the program.");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(lineagePath));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("DerivedFromSha256").GetString(), BinaryBuild.Sha256(original), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(root.GetProperty("DerivedSha256").GetString(), BinaryBuild.Sha256(derived), StringComparison.OrdinalIgnoreCase))
                throw new TranslatedLibraryException($"The lineage record '{lineagePath}' does not match '{full}' and its derived image; rebuild the program.");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TranslatedLibraryException($"The lineage record '{lineagePath}' is unreadable: {error.Message}");
        }
    }
}
