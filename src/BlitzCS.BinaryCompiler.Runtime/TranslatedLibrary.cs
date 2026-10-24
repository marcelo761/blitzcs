using BlitzCS.BinaryCompiler;

namespace BlitzCS.BinaryCompiler.Runtime;

public sealed class TranslatedLibraryException(string message) : Exception(message);

/// <summary>Loads a PE32 library through the translation artifact the BlitzCS build placed next to it (<c>name.dll.bcx</c>).</summary>
public static class TranslatedLibrary
{
    public const string ArtifactExtension = ".bcx";

    public static GuestModule Open(string dllPath, IGuestImportResolver imports, GuestModuleOptions? options = null)
    {
        var full = Path.GetFullPath(dllPath);
        var artifactPath = full + ArtifactExtension;
        if (!File.Exists(full)) throw new TranslatedLibraryException($"Original library '{full}' does not exist.");
        if (!File.Exists(artifactPath)) throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' is missing; build the program with the library listed under 'binaryLibraries'.");
        var dll = File.ReadAllBytes(full);
        TranslationArtifact artifact;
        try { artifact = new ArtifactSerializer().Deserialize(File.ReadAllBytes(artifactPath)); }
        catch (ArtifactFormatException error) { throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' is corrupt: {error.Message}"); }
        if (!string.Equals(artifact.SourceSha256, BinaryBuild.Sha256(dll), StringComparison.OrdinalIgnoreCase))
            throw new TranslatedLibraryException($"Translation artifact '{artifactPath}' does not match '{full}'; rebuild the program.");
        return GuestModule.Load(artifact, PeReader.Read(dll), imports, options);
    }
}
