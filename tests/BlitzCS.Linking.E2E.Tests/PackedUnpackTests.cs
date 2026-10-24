using BlitzCS.BinaryCompiler.Unpack.Tests;
using Xunit;

namespace BlitzCS.Linking.E2E.Tests;

[Collection("LinkingE2E")]
public sealed class PackedUnpackTests : E2EBase
{
    private static string Catalog(string library) =>
        "{\"binaryLibraries\":[" + library + "],\"functions\":[" + Entry("Add2", ["Integer", "Integer"], "Integer", "packed.dll", "add2")
        + "," + Entry("CallAbs", ["Integer"], "Integer", "packed.dll", "callabs") + "]}";

    [Fact]
    public void UnpackTrueDerivesTranslatesAndRunsWhileTheOriginalIsCopiedUnchanged()
    {
        var packed = SyntheticPacker.Build();
        File.WriteAllBytes(Path.Combine(Root, "packed.dll"), packed);
        var stdout = Build("Print Add2(20, 22)\nPrint CallAbs(-7)\n", Catalog("{\"path\":\"packed.dll\",\"unpack\":true}"), out var exit, out var err);
        Assert.True(exit == 0, err);
        Assert.Contains("preprocessed (unpack:", stdout);
        Assert.Equal(packed, File.ReadAllBytes(Path.Combine(Root, "out", "packed.dll")));
        Assert.True(File.Exists(Path.Combine(Root, "out", "packed.dll.derived")));
        Assert.True(File.Exists(Path.Combine(Root, "out", "packed.dll.lineage.json")));
        Assert.Equal("42\n7\n", Run());
    }

    [Fact]
    public void WithoutUnpackTheBuildFailsAndSuggestsIt()
    {
        File.WriteAllBytes(Path.Combine(Root, "packed.dll"), SyntheticPacker.Build());
        Build("Print Add2(1, 2)\n", Catalog("\"packed.dll\""), out var exit, out var err);
        Assert.Equal(1, exit);
        Assert.Contains("BC_ROOT_NOT_FILE_BACKED", err);
        Assert.Contains("\"unpack\": true", err);
    }

    [Fact]
    public void AnUnpackRefusalFailsTheBuildWithTheUnpackersCode()
    {
        File.WriteAllBytes(Path.Combine(Root, "packed.dll"), SyntheticPacker.Build(StubVariant.Rdtsc));
        Build("Print Add2(1, 2)\n", Catalog("{\"path\":\"packed.dll\",\"unpack\":true}"), out var exit, out var err);
        Assert.Equal(1, exit);
        Assert.Contains("Unpacking failed", err);
    }
}
