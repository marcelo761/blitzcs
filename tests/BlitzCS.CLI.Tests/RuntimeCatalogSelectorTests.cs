using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class RuntimeCatalogSelectorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-runtime-catalog-" + Guid.NewGuid());

    public RuntimeCatalogSelectorTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("win-x86", "Win32")]
    [InlineData("linux-x86", "Linux32")]
    [InlineData("windows", "Win64")]
    [InlineData("linux", "Linux64")]
    public void SelectsCanonicalRuntimeTargetAndTracksManifestAndLeaf(string alias, string canonical)
    {
        var root = Path.Combine(directory, "runtime.json");
        var leaf = Path.Combine(directory, canonical.ToLowerInvariant() + ".json");
        var dependency = Path.Combine(directory, canonical.ToLowerInvariant() + ".so");
        File.WriteAllBytes(dependency, []);
        File.WriteAllText(leaf, JsonSerializer.Serialize(new { functions = Array.Empty<object>(), nativeLibraries = new[] { Path.GetFileName(dependency) } }));
        File.WriteAllText(root, JsonSerializer.Serialize(new { runtimeTargets = new Dictionary<string, string> { [canonical] = Path.GetFileName(leaf) } }));

        using var result = ExternalCatalog.Load(root, alias);

        Assert.Equal(Path.GetFullPath(dependency), Assert.Single(result.NativeLibraries));
        Assert.Equal(new[] { Path.GetFullPath(root), Path.GetFullPath(leaf) }, result.CatalogInputs);
    }

    [Fact]
    public void MissingTargetHasNoFallbackAndDefaultIsWin64()
    {
        var root = Path.Combine(directory, "runtime.json");
        var win64 = Path.Combine(directory, "win64.json");
        File.WriteAllText(win64, "{\"functions\":[]}");
        File.WriteAllText(root, "{\"runtimeTargets\":{\"Win64\":\"win64.json\"}}");

        using var defaultResult = ExternalCatalog.Load(root);
        Assert.Equal(new[] { Path.GetFullPath(root), Path.GetFullPath(win64) }, defaultResult.CatalogInputs);

        var error = Assert.Throws<ArgumentException>(() => ExternalCatalog.Load(root, "Linux64"));
        Assert.Contains("Linux64", error.Message);
        Assert.Contains("no runtime catalog", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyCatalogRemainsLeafAndIsTrackedAsSingleInput()
    {
        var leaf = Path.Combine(directory, "legacy.json");
        File.WriteAllText(leaf, "{\"functions\":[]}");

        using var result = ExternalCatalog.Load(leaf, "Win64");

        Assert.Equal(new[] { Path.GetFullPath(leaf) }, result.CatalogInputs);
    }

    [Theory]
    [InlineData("{\"runtimeTargets\":[]}", "must be an object")]
    [InlineData("{\"runtimeTargets\":{\"Win64\":42}}", "non-empty catalog path string")]
    [InlineData("{\"functions\":[],\"runtimeTargets\":{\"Win64\":\"leaf.json\"}}", "cannot mix")]
    public void InvalidManifestShapeAndMixedCatalogFieldsAreRejected(string json, string expectedMessage)
    {
        var manifest = Path.Combine(directory, "runtime.json");
        File.WriteAllText(manifest, json);

        var error = Assert.Throws<ArgumentException>(() => ExternalCatalog.Load(manifest));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestedManifestResolvesRelativeToEachManifestAndDetectsCycles()
    {
        var subdirectory = Directory.CreateDirectory(Path.Combine(directory, "nested")).FullName;
        var root = Path.Combine(directory, "runtime.json");
        var nested = Path.Combine(subdirectory, "runtime.json");
        var leaf = Path.Combine(subdirectory, "leaf.json");
        File.WriteAllText(leaf, "{\"functions\":[]}");
        File.WriteAllText(nested, "{\"runtimeTargets\":{\"Win64\":\"leaf.json\"}}");
        File.WriteAllText(root, "{\"runtimeTargets\":{\"Win64\":\"nested/runtime.json\"}}");

        using var result = ExternalCatalog.Load(root);
        Assert.Equal(new[] { Path.GetFullPath(root), Path.GetFullPath(nested), Path.GetFullPath(leaf) }, result.CatalogInputs);

        File.WriteAllText(nested, "{\"runtimeTargets\":{\"Win64\":\"../runtime.json\"}}");
        var error = Assert.Throws<ArgumentException>(() => ExternalCatalog.Load(root));
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
