using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.Linking.E2E.Tests;

/// <summary>
/// The per-library catalog options that configure the generated Win32 environment: <c>fileRoot</c> (what the guest's file APIs may see) and <c>audio</c>
/// (the waveOut backend). A tiny PE32 library calls CreateFileA and waveOutGetNumDevs so the effect of each option is observed through the real build and run path.
/// </summary>
[Collection("LinkingE2E")]
public sealed class Win32EnvironmentOptionsTests : E2EBase, IDisposable
{
    private readonly string? previousAudio = Environment.GetEnvironmentVariable("BLITZCS_AUDIO");

    public Win32EnvironmentOptionsTests()
    {
        // BLITZCS_AUDIO overrides the catalog at run time; a developer's value must not change what these tests observe.
        Environment.SetEnvironmentVariable("BLITZCS_AUDIO", null);
    }

    void IDisposable.Dispose()
    {
        Environment.SetEnvironmentVariable("BLITZCS_AUDIO", previousAudio);
        base.Dispose();
    }

    private static byte[] Library()
    {
        var b = new PeFixtureBuilder();
        var create = PeFixtureBuilder.ImageBase + b.Import("kernel32.dll", "CreateFileA");
        var devices = PeFixtureBuilder.ImageBase + b.Import("winmm.dll", "waveOutGetNumDevs");
        // cdecl int open_file(const char *name): return CreateFileA(name, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        var open = TranslatorHarness.Asm(a =>
        {
            a.push(0); a.push(0); a.push(3); a.push(0); a.push(1); a.push(unchecked((int)0x80000000));
            a.push(__dword_ptr[esp + 28]);
            a.call(__dword_ptr[(int)create]);
            a.ret();
        });
        // cdecl int num_devices(): return waveOutGetNumDevs();
        var count = TranslatorHarness.Asm(a => { a.call(__dword_ptr[(int)devices]); a.ret(); });
        var pad = Enumerable.Repeat((byte)0x90, 0x80 - open.Length);
        return b.WithCode(open.Concat(pad).Concat(count).ToArray()).Export("open_file", 0).Export("num_devices", 0x80).Build();
    }

    private string Catalog(string options) =>
        "{\"binaryLibraries\":[{\"path\":\"lib.dll\",\"win32\":true" + options + "}],\"functions\":["
        + Entry("OpenFile", ["String"], "Integer", "lib.dll", "open_file", "cdecl") + ","
        + Entry("NumDevices", [], "Integer", "lib.dll", "num_devices", "cdecl") + "]}";

    private string BuildAndRun(string source, string options, bool withDataFile = true)
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), Library());
        var stdout = Build(source, Catalog(options), out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        if (withDataFile)
        {
            File.WriteAllText(Path.Combine(Root, "out", "data.bin"), "x");
            Directory.CreateDirectory(Path.Combine(Root, "out", "sub"));
            File.WriteAllText(Path.Combine(Root, "out", "sub", "inner.bin"), "y");
        }
        return Run();
    }

    [Fact]
    public void WithoutFileRootTheLibraryCannotOpenAnyFile()
    {
        Assert.Equal("-1\n", BuildAndRun("Print OpenFile(\"data.bin\")\n", ""));
        Assert.Equal("-1\n", BuildAndRun("Print OpenFile(\"data.bin\")\n", ",\"fileRoot\":\"none\""));
    }

    [Fact]
    public void FileRootAppLetsTheLibraryReadBesideItselfWithWindowsPaths()
    {
        const string source = "Print OpenFile(\"data.bin\") <> -1\nPrint OpenFile(\"C:\\data.bin\") <> -1\nPrint OpenFile(\"DATA.BIN\") <> -1\nPrint OpenFile(\"sub\\inner.bin\") <> -1\n"
            + "Print OpenFile(\"missing.bin\") <> -1\nPrint OpenFile(\"..\\..\\..\\data.bin\") <> -1\nPrint OpenFile(\"\\\\server\\share\\data.bin\") <> -1\n";
        // existing files (relative, drive-qualified, any case, nested) open; a missing one does not; ".." cannot climb above the root (so it finds the same data.bin);
        // a UNC path is refused rather than reaching the network
        Assert.Equal("1\n1\n1\n1\n0\n1\n0\n", BuildAndRun(source, ",\"fileRoot\":\"app\""));
    }

    [Fact]
    public void FileRootAppDoesNotExposeTheParentDirectory()
    {
        File.WriteAllText(Path.Combine(Root, "outside.bin"), "secret");
        Assert.Equal("0\n0\n", BuildAndRun("Print OpenFile(\"..\\outside.bin\") <> -1\nPrint OpenFile(\"out\\..\\..\\outside.bin\") <> -1\n", ",\"fileRoot\":\"app\""));
    }

    [Fact]
    public void TheAudioOptionSelectsTheWaveOutBackend()
    {
        Assert.Equal("0\n", BuildAndRun("Print NumDevices()\n", ",\"audio\":\"none\"", withDataFile: false));
        Assert.Equal("1\n", BuildAndRun("Print NumDevices()\n", ",\"audio\":\"virtual\"", withDataFile: false));
    }

    [Fact]
    public void WithoutTheAudioOptionTheWin32ComponentChoosesTheHostDefault()
    {
        // Default: the host device on Windows (the number depends on the machine), no devices elsewhere.
        var devices = int.Parse(BuildAndRun("Print NumDevices()\n", "", withDataFile: false).Trim());
        if (!OperatingSystem.IsWindows()) Assert.Equal(0, devices);
        else Assert.True(devices >= 0);
    }

    [Theory]
    [InlineData(",\"audio\":\"loud\"", "audio must be")]
    [InlineData(",\"audio\":1", "audio must be")]
    [InlineData(",\"fileRoot\":\"everything\"", "fileRoot must be")]
    [InlineData(",\"fileRoot\":true", "fileRoot must be")]
    [InlineData(",\"audioRecord\":\"../x.wav\"", "audioRecord must be")]
    [InlineData(",\"audioRecord\":\"/tmp/x.wav\"", "audioRecord must be")]
    [InlineData(",\"audioRecord\":\"C:x.wav\"", "audioRecord must be")]
    [InlineData(",\"audioRecord\":\"\"", "audioRecord must be")]
    [InlineData(",\"audioRecord\":3", "audioRecord must be")]
    public void InvalidOptionValuesAreRejectedAtBuildTime(string options, string message)
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), Library());
        Build("Print 1\n", Catalog(options), out var exit, out var err);
        Assert.Equal(1, exit);
        Assert.Contains(message, err);
    }

    [Theory]
    [InlineData(",\"fileRoot\":\"app\"")]
    [InlineData(",\"audio\":\"virtual\"")]
    [InlineData(",\"audioRecord\":\"a.wav\"")]
    public void OptionsThatConfigureTheWin32EnvironmentConflictWithDisablingIt(string options)
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), Library());
        Build("Print 1\n", Catalog(options).Replace("\"win32\":true", "\"win32\":false"), out var exit, out var err);
        Assert.Equal(1, exit);
        Assert.Contains("Win32 environment", err);
    }
}
