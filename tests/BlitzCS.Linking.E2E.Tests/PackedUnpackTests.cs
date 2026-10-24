using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Unpack.Tests;
using BlitzCS.CLI;
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

    private static string RelocatableCatalog() =>
        "{\"binaryLibraries\":[{\"path\":\"packed.dll\",\"unpack\":true}],\"functions\":[" + Entry("Add2", ["Integer", "Integer"], "Integer", "packed.dll", "add2")
        + "," + Entry("CallAbs", ["Integer"], "Integer", "packed.dll", "callabs") + "," + Entry("GetPtr", ["Integer"], "Integer", "packed.dll", "getptr") + "]}";

    /// <summary>
    /// The build policy chooses the load base for the target BEFORE the packer stub runs. The packed library prefers 0x10000000 (unusable on windows-x64) and carries its own
    /// relocation list; for windows-x64 it must be unpacked at a base inside the image window, and the stub's pointer table must hold addresses of THAT base.
    /// This build-only check runs on every host (translation is pure computation).
    /// </summary>
    [Fact]
    public void ForWindowsX64TheLibraryIsUnpackedAtABaseOfTheWindowsImageWindow()
    {
        File.WriteAllBytes(Path.Combine(Root, "packed.dll"), SyntheticPacker.BuildRelocatable(0x10000000));
        File.WriteAllText(Path.Combine(Root, "main.bb"), "Print GetPtr(0)\n");
        File.WriteAllText(Path.Combine(Root, "catalog.json"), RelocatableCatalog());
        var stdout = new StringWriter(); var error = new StringWriter();
        var exit = CliApplication.Run(["build", Path.Combine(Root, "main.bb"), "-o", Path.Combine(Root, "out", "main.dll"), "--externals", Path.Combine(Root, "catalog.json"), "--target", "windows-x64"], stdout, error);
        Assert.True(exit == 0, error + stdout.ToString());
        var derived = PeReader.Read(File.ReadAllBytes(Path.Combine(Root, "out", "packed.dll.derived")));
        Assert.InRange(derived.ImageBase, ModuleBaseAllocator.WindowsMinimumBase, ModuleBaseAllocator.WindowsImageWindowEnd);
        Assert.Equal(0u, derived.ImageBase % 0x10000);
        Assert.Equal(derived.ImageBase + SyntheticPacker.AddRva, BitConverter.ToUInt32(derived.MapImage(), (int)SyntheticPacker.TableRva));
        var artifact = new ArtifactSerializer().Deserialize(File.ReadAllBytes(Path.Combine(Root, "out", "packed.dll.bcx")));
        Assert.Equal(derived.ImageBase, artifact.GuestImageBase);
        Assert.Equal(BinaryTargets.WindowsX64, artifact.TargetOs);
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>The same library built for the host and run: Linux keeps the preferred base, Windows (and Wine) uses the planned one; the program sees addresses of the actual base.</summary>
    [Fact]
    public void ARelocatablePackedLibraryRunsAtTheBaseThePolicyChoseForTheHost()
    {
        File.WriteAllBytes(Path.Combine(Root, "packed.dll"), SyntheticPacker.BuildRelocatable(0x10000000));
        var stdout = Build("Print Add2(20, 22)\nPrint CallAbs(-7)\nPrint GetPtr(0)\nPrint GetPtr(1) - GetPtr(0)\n", RelocatableCatalog(), out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        var derived = PeReader.Read(File.ReadAllBytes(Path.Combine(Root, "out", "packed.dll.derived")));
        if (OperatingSystem.IsWindows()) Assert.True(derived.ImageBase >= ModuleBaseAllocator.WindowsMinimumBase, $"derived base 0x{derived.ImageBase:X8} is unusable on windows-x64");
        else Assert.Equal(0x10000000u, derived.ImageBase);   // Linux: preferred base, nothing to relocate
        Assert.Equal($"42\n7\n{(int)(derived.ImageBase + SyntheticPacker.AddRva)}\n{SyntheticPacker.CallAbsRva - SyntheticPacker.AddRva}\n", Run());
    }

    /// <summary>A packed library fixed below 0x20000000 without relocations cannot be placed for windows-x64: the build fails BEFORE the stub runs, naming the cause.</summary>
    [Fact]
    public void ForWindowsX64APackedLibraryFixedAtALowBaseIsRefusedWithTheRelocationHint()
    {
        File.WriteAllBytes(Path.Combine(Root, "packed.dll"), SyntheticPacker.Build(imageBase: 0x10000000));
        File.WriteAllText(Path.Combine(Root, "main.bb"), "Print Add2(1, 2)\n");
        File.WriteAllText(Path.Combine(Root, "catalog.json"), Catalog("{\"path\":\"packed.dll\",\"unpack\":true}"));
        var stdout = new StringWriter(); var error = new StringWriter();
        var exit = CliApplication.Run(["build", Path.Combine(Root, "main.bb"), "-o", Path.Combine(Root, "out", "main.dll"), "--externals", Path.Combine(Root, "catalog.json"), "--target", "windows-x64"], stdout, error);
        Assert.Equal(1, exit);
        Assert.Contains("no relocations", error.ToString());
        Assert.Contains("0x10000000", error.ToString());
        Assert.False(File.Exists(Path.Combine(Root, "out", "packed.dll.derived")));   // nothing was unpacked: the policy decided first
    }
}
