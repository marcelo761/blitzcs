using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class TargetCapabilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-target-cap-" + Guid.NewGuid().ToString("N"));
    private string SourcePath => Path.Combine(directory, "Main.bb");

    public TargetCapabilityTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SourcePath, "Print 42\n");
    }

    [Theory]
    [InlineData("Win32", CompilationTarget.Win32, "win-x86", false, false)]
    [InlineData("win-x86", CompilationTarget.Win32, "win-x86", false, false)]
    [InlineData("Linux32", CompilationTarget.Linux32, "linux-x86", false, false)]
    [InlineData("linux-x86", CompilationTarget.Linux32, "linux-x86", false, false)]
    [InlineData("Win64", CompilationTarget.Win64, "win-x64", true, true)]
    [InlineData("windows-x64", CompilationTarget.Win64, "win-x64", true, true)]
    [InlineData("Linux64", CompilationTarget.Linux64, "linux-x64", true, false)]
    [InlineData("linux-x64", CompilationTarget.Linux64, "linux-x64", true, false)]
    public void TargetCapabilitiesDistinguish32BitAnd64BitTargets(
        string rawTarget,
        CompilationTarget expected,
        string expectedRid,
        bool expectedSupportsBcsbre,
        bool expectedValidatedTarget)
    {
        Assert.True(TargetCapabilities.TryParseTarget(rawTarget, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(expectedRid, parsed.RuntimeIdentifier());
        Assert.Equal(expectedSupportsBcsbre, parsed.SupportsBinaryRecompilation());
        Assert.Equal(expectedValidatedTarget, parsed.IsValidatedBinaryRecompilationTarget());
    }

    [Theory]
    [InlineData("Win32")]
    [InlineData("Linux32")]
    public void ThirtyTwoBitTargetsRejectExplicitBcsbreBinaryLibraries(string target)
    {
        var pe32 = Path.Combine(directory, "legacy.dll");
        File.WriteAllBytes(pe32, MinimalPe(is64Bit: false));
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            functions = Array.Empty<object>(),
            binaryLibraries = new[] { "legacy.dll" }
        }));
        var output = Path.Combine(directory, "out", "app.dll");
        var errors = new StringWriter();

        var exit = CliApplication.Run(["build", SourcePath, "-o", output, "--externals", catalog, "--target", target], TextWriter.Null, errors);

        Assert.Equal(1, exit);
        Assert.Contains("32-bit target; BCSBRE binary recompilation is only integrated in 64-bit targets", errors.ToString());
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Win32UserLibWithPe32UsesDirectNativePathWithoutInvokingBcsbre()
    {
        var pe32 = Path.Combine(directory, "native32.dll");
        File.WriteAllBytes(pe32, MinimalPe(is64Bit: false));
        var decls = Path.Combine(directory, "native32.decls");
        File.WriteAllText(decls, ".lib \"native32.dll\"\nNativeAdd%(a%, b%) : \"_NativeAdd@8\"\n");
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            functions = Array.Empty<object>(),
            userlibs = new[] { "native32.decls" }
        }));
        File.WriteAllText(SourcePath, "Print NativeAdd(20, 22)\n");
        var output = Path.Combine(directory, "out", "app.dll");
        var stdout = new StringWriter();
        var errors = new StringWriter();

        var exit = CliApplication.Run(["build", SourcePath, "-o", output, "--externals", catalog, "--target", "Win32"], stdout, errors);

        Assert.Equal(0, exit);
        Assert.Empty(errors.ToString());
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(directory, "out", "BlitzCS.NativeUserLibBindings.dll")));
        Assert.False(File.Exists(Path.Combine(directory, "out", "BlitzCS.BinaryBindings.dll")));
        Assert.False(File.Exists(Path.Combine(directory, "out", "native32.dll.bcx")));
    }

    [Fact]
    public void Win64UserLibWithPe32PlusUsesDirectNativePathWithoutInvokingBcsbre()
    {
        var pe64 = Path.Combine(directory, "native64.dll");
        File.WriteAllBytes(pe64, MinimalPe(is64Bit: true));
        var decls = Path.Combine(directory, "native64.decls");
        File.WriteAllText(decls, ".lib \"native64.dll\"\nNativeAdd64%(a%, b%) : \"NativeAdd64\"\n");
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            functions = Array.Empty<object>(),
            userlibs = new[] { "native64.decls" }
        }));
        File.WriteAllText(SourcePath, "Print NativeAdd64(20, 22)\n");
        var output = Path.Combine(directory, "out", "app.dll");
        var stdout = new StringWriter();
        var errors = new StringWriter();

        var exit = CliApplication.Run(["build", SourcePath, "-o", output, "--externals", catalog, "--target", "Win64"], stdout, errors);

        Assert.Equal(0, exit);
        Assert.Empty(errors.ToString());
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(directory, "out", "BlitzCS.NativeUserLibBindings.dll")));
        Assert.False(File.Exists(Path.Combine(directory, "out", "BlitzCS.BinaryBindings.dll")));
        Assert.False(File.Exists(Path.Combine(directory, "out", "native64.dll.bcx")));
    }

    [Fact]
    public void Win64RejectsPe32PlusInBinaryLibrariesInstructingDirectNativePath()
    {
        var pe64 = Path.Combine(directory, "native64.dll");
        File.WriteAllBytes(pe64, MinimalPe(is64Bit: true));
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            functions = Array.Empty<object>(),
            binaryLibraries = new[] { "native64.dll" }
        }));
        var output = Path.Combine(directory, "out", "app.dll");
        var errors = new StringWriter();

        var exit = CliApplication.Run(["build", SourcePath, "-o", output, "--externals", catalog, "--target", "Win64"], TextWriter.Null, errors);

        Assert.Equal(1, exit);
        Assert.Contains("already a native 64-bit PE32+ x64 image", errors.ToString());
        Assert.Contains("nativeLibraries", errors.ToString());
    }

    [Fact]
    public void Linux32RejectsPe32UserLibWithExplicitDiagnostic()
    {
        var pe32 = Path.Combine(directory, "legacy.dll");
        File.WriteAllBytes(pe32, MinimalPe(is64Bit: false));
        var decls = Path.Combine(directory, "legacy.decls");
        File.WriteAllText(decls, ".lib \"legacy.dll\"\nLegacyFn%(a%) : \"_LegacyFn@4\"\n");
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            functions = Array.Empty<object>(),
            userlibs = new[] { "legacy.decls" }
        }));
        var output = Path.Combine(directory, "out", "app.dll");
        var errors = new StringWriter();

        var exit = CliApplication.Run(["build", SourcePath, "-o", output, "--externals", catalog, "--target", "Linux32"], TextWriter.Null, errors);

        Assert.Equal(1, exit);
        Assert.Contains("Linux32", errors.ToString());
        Assert.Contains("BCSBRE binary recompilation is only integrated in 64-bit targets", errors.ToString());
    }

    [Fact]
    public void Linux64PackageBuildRejectsLegacyPe32BinaryLibrariesWithoutClaimingSupport()
    {
        var pe32 = Path.Combine(directory, "legacy.dll");
        File.WriteAllBytes(pe32, MinimalPe(is64Bit: false));
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            functions = Array.Empty<object>(),
            binaryLibraries = new[] { "legacy.dll" }
        }));
        var destination = Path.Combine(directory, "dist", "app");
        var errors = new StringWriter();

        var exit = CliApplication.Run(["build", SourcePath, "-o", destination, "--externals", catalog, "--target", "Linux64"], TextWriter.Null, errors);

        Assert.Equal(1, exit);
        Assert.Contains("not a validated application packaging target for legacy PE32 binary libraries", errors.ToString());
        Assert.Contains("Win64", errors.ToString());
        Assert.False(Directory.Exists(destination));
    }

    private static byte[] MinimalPe(bool is64Bit)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[0x3c] = 0x80;
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        // COFF Machine
        var machine = is64Bit ? (ushort)0x8664 : (ushort)0x014c;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        // NumberOfSections = 1
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x86);
        // SizeOfOptionalHeader
        BitConverter.GetBytes((ushort)(is64Bit ? 0xF0 : 0xE0)).CopyTo(bytes, 0x94);
        // Characteristics = IMAGE_FILE_EXECUTABLE_IMAGE | IMAGE_FILE_DLL
        BitConverter.GetBytes((ushort)0x2002).CopyTo(bytes, 0x96);
        // OptionalHeader Magic
        var magic = is64Bit ? (ushort)0x020b : (ushort)0x010b;
        BitConverter.GetBytes(magic).CopyTo(bytes, 0x98);
        return bytes;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
