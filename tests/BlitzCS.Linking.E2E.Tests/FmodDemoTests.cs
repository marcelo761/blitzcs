using System.Diagnostics;
using System.Text.Json.Nodes;
using BlitzCS.BinaryCompiler;
using BlitzCS.CLI;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.Linking.E2E.Tests;

/// <summary>
/// samples/FmodAudio: an unmodified BlitzBasic program drives the original FMOD 3.75 (the local packed fmod.dll named by BLITZCS_PACKED_SAMPLE, never copied into
/// the repository): version, output selection, Init, loading a WAV that THIS test generates, playing/stopping/finishing a channel, Close. The sample's committed
/// catalog uses "fmod.dll" next to it; the test writes a catalog with the absolute path of the local DLL into a temporary directory.
/// Output type NOSOUND: FMOD's silent mixer runs in real time (its mixer thread consumes the sample), but no sound device is touched.
/// </summary>
[Collection("LinkingE2E")]
public sealed class FmodDemoTests : E2EBase
{
    /// <summary>The exact stdout of samples/FmodAudio/main.bb with FSOUND_OUTPUT_NOSOUND (mixer values other than "before init 4" are CPU dependent and are only printed as a boolean).</summary>
    private const string ExpectedNoSound =
        "version 3.75\n" +
        "mixer before init 4\n" +
        "set output 1\n" +
        "output 0\n" +
        "drivers 1\n" +
        "set driver 1\n" +
        "init 1\n" +
        "error 0\n" +
        "mixer resolved 1\n" +
        "sample 1\n" +
        "samples 44100\n" +
        "mode 305\n" +
        "channel 1\n" +
        "playing 1\n" +
        "volume 1\n" +
        "playing 1\n" +
        "stop 1\n" +
        "playing 0\n" +
        "finished 0\n" +
        "error 0\n" +
        "closed\n";

    /// <summary>440 Hz sine, 16-bit mono, 44.1 kHz, exactly one second, as a canonical RIFF/WAVE file.</summary>
    internal static byte[] ToneWav()
    {
        const int rate = 44100;
        var data = new byte[rate * 2];
        for (var i = 0; i < rate; i++)
            BitConverter.GetBytes((short)Math.Round(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / rate))).CopyTo(data, 2 * i);
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(data.Length); w.Write(data);
        }
        return stream.ToArray();
    }

    /// <summary>The committed catalog with the library path replaced by the absolute path of the local DLL (the only machine-specific value).</summary>
    private static string CatalogFor(string dll)
    {
        var catalog = JsonNode.Parse(File.ReadAllText(RepoFile("samples/FmodAudio/fmod.json")))!;
        catalog["binaryLibraries"]![0]!["path"] = dll;
        foreach (var function in catalog["functions"]!.AsArray()) function!["binary"]!["library"] = dll;
        return catalog.ToJsonString();
    }

    private static string Source(string? outputType = null)
    {
        var source = File.ReadAllText(RepoFile("samples/FmodAudio/main.bb"));
        return outputType is null ? source : source.Replace("Const OUTPUT_TYPE = FSOUND_OUTPUT_NOSOUND", "Const OUTPUT_TYPE = " + outputType, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSampleSourceAndCatalogAreCommittedWithoutTheLibrary()
    {
        Assert.False(File.Exists(RepoFile("samples/FmodAudio/fmod.dll")), "the proprietary library must never be committed");
        var catalog = File.ReadAllText(RepoFile("samples/FmodAudio/fmod.json"));
        Assert.Contains("\"path\": \"fmod.dll\"", catalog);
        Assert.DoesNotContain("/home/", catalog);
        Assert.DoesNotContain(":\\\\", catalog);
        Assert.Contains("Const OUTPUT_TYPE = FSOUND_OUTPUT_NOSOUND", Source());
    }

    /// <summary>
    /// Builds the demo with the CLI and runs it as this host would: a Linux x86-64 translation on Linux (child dotnet process), a Windows x64 translation on Windows
    /// and under Wine (child WinTestRunner process), so the guest, the unpacked FMOD and its Win32 services execute for the host's own ABI.
    /// </summary>
    [PackedSampleFact]
    public void BlitzBasicDrivesTheOriginalFmodWithNoSoundOutput()
    {
        var dll = RealLibraries.PackedSample!;
        File.WriteAllBytes(Path.Combine(Root, "tone.wav"), ToneWav());
        var stopwatch = Stopwatch.StartNew();
        var stdout = Build(Source(), CatalogFor(dll), out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        Assert.Contains("preprocessed (unpack:", stdout);
        Assert.Equal(File.ReadAllBytes(dll), File.ReadAllBytes(Path.Combine(Root, "out", "fmod.dll")));     // the original, byte for byte
        var artifact = new ArtifactSerializer().Deserialize(File.ReadAllBytes(Path.Combine(Root, "out", "fmod.dll.bcx")));
        Assert.Equal(OperatingSystem.IsWindows() ? BinaryTargets.WindowsX64 : BinaryTargets.LinuxSysV, artifact.TargetOs);
        File.Copy(Path.Combine(Root, "tone.wav"), Path.Combine(Root, "out", "tone.wav"));                      // fileRoot "app": the library reads beside itself
        var built = stopwatch.Elapsed;

        Assert.Equal(ExpectedNoSound, Run());
        // The program plays 1.0 s of audio twice (0.3 s, then to the end) in FMOD's real-time mixer: a run that took no time did not play anything.
        Assert.True(stopwatch.Elapsed - built >= TimeSpan.FromSeconds(1.5), $"the demo ran for {stopwatch.Elapsed - built}");
    }

    /// <summary>Programs routinely end without FSOUND_Close; FMOD's mixer thread is then still inside the library. The process must end, not wait for it.</summary>
    [PackedSampleFact]
    public async Task AProgramThatNeverClosesFmodStillEnds()
    {
        File.WriteAllBytes(Path.Combine(Root, "tone.wav"), ToneWav());
        const string program = "Print FSOUND_SetOutput(0) And 255\nPrint FSOUND_Init(44100, 32, 0) And 255\ns = FSOUND_Sample_Load(-1, \"tone.wav\", 304, 0, 0)\n"
            + "c = FSOUND_PlaySound(-1, s)\nPrint FSOUND_IsPlaying(c) And 255\n";
        var stdout = Build(program, CatalogFor(RealLibraries.PackedSample!), out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        File.Copy(Path.Combine(Root, "tone.wav"), Path.Combine(Root, "out", "tone.wav"));
        using var process = Process.Start(BlitzCS.BinaryCompiler.Tests.Support.GeneratedProgramRunner.Create(Path.Combine(Root, "out", "main.dll")))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the program did not end while FMOD's mixer thread was running");
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("1\n1\n1\n", (await output).Replace("\r\n", "\n"));
        Assert.DoesNotContain("Unhandled", await error);
    }

    [PackedSampleFact]
    public void ARelativeFileNameOutsideTheApplicationDirectoryIsNotFound()
    {
        var dll = RealLibraries.PackedSample!;
        const string source = "Print FSOUND_SetOutput(0) And 255\nPrint FSOUND_Init(44100, 32, 0) And 255\n" +
            "s = FSOUND_Sample_Load(-1, \"..\\..\\missing.wav\", 304, 0, 0)\nPrint s\nPrint FSOUND_GetError() <> 0\nFSOUND_Close()\n";
        var stdout = Build(source, CatalogFor(dll), out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        Assert.Equal("1\n1\n0\n1\n", Run());     // the sandbox clamps ".." at its root; the file does not exist there: FMOD reports an error, the program goes on
    }

    /// <summary>
    /// The one-line switch of the sample: output type WINMM (waveOut) instead of NOSOUND. The catalog sends the library's waveOut to the in-process virtual sink and records what it
    /// plays, so no sound device is touched on any host. The program runs to FSOUND_Close: closing a WINMM output reaches a function that sits right after a tail jump,
    /// which the translator did not recognise as a function start before (docs/windows-integration-audit.md, F-12).
    /// </summary>
    [PackedSampleFact]
    public void TheSameProgramDrivesFmodsWinmmOutputThroughTheVirtualSink()
    {
        var previous = Environment.GetEnvironmentVariable("BLITZCS_AUDIO");
        Environment.SetEnvironmentVariable("BLITZCS_AUDIO", null);
        try
        {
            var catalog = JsonNode.Parse(CatalogFor(RealLibraries.PackedSample!))!;
            catalog["binaryLibraries"]![0]!["audio"] = "virtual";
            catalog["binaryLibraries"]![0]!["audioRecord"] = "fmod-output.wav";
            var source = Source("FSOUND_OUTPUT_WINMM");
            File.WriteAllBytes(Path.Combine(Root, "tone.wav"), ToneWav());
            var stdout = Build(source, catalog.ToJsonString(), out var exit, out var err);
            Assert.True(exit == 0, err + stdout);
            File.Copy(Path.Combine(Root, "tone.wav"), Path.Combine(Root, "out", "tone.wav"));

            var stopwatch = Stopwatch.StartNew();
            var expected = ExpectedNoSound.Replace("output 0\n", "output 1\n");
            Assert.Equal(expected, Run());
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5), $"the demo ran for {stopwatch.Elapsed}");

            // FMOD opened a 44.1 kHz 16-bit stereo waveOut device and kept it fed for as long as the program ran.
            var wav = File.ReadAllBytes(Path.Combine(Root, "out", "fmod-output.wav"));
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal((short)2, BitConverter.ToInt16(wav, 22));     // channels
            Assert.Equal(44100, BitConverter.ToInt32(wav, 24));        // sample rate
            Assert.Equal((short)16, BitConverter.ToInt16(wav, 34));    // bits
            Assert.InRange(BitConverter.ToInt32(wav, 40) / (44100.0 * 4), 1.0, 5.0);   // seconds of audio consumed in real time
        }
        finally { Environment.SetEnvironmentVariable("BLITZCS_AUDIO", previous); }
    }

    /// <summary>
    /// FMOD streams the mix into one looped WAVEHDR buffer that it keeps rewriting in place. The virtual sink reads the guest buffer when each slice is consumed (F-11), so the
    /// recording of the original library's WINMM output carries the 440 Hz tone: dominant frequency, level and about one second of non-silent audio.
    /// </summary>
    [PackedSampleFact]
    public void TheRecordedWinmmOutputIsTheTone()
    {
        var previous = Environment.GetEnvironmentVariable("BLITZCS_AUDIO");
        Environment.SetEnvironmentVariable("BLITZCS_AUDIO", null);
        try
        {
            var catalog = JsonNode.Parse(CatalogFor(RealLibraries.PackedSample!))!;
            catalog["binaryLibraries"]![0]!["audio"] = "virtual";
            catalog["binaryLibraries"]![0]!["audioRecord"] = "tone-output.wav";
            File.WriteAllBytes(Path.Combine(Root, "tone.wav"), ToneWav());
            var stdout = Build(Source("FSOUND_OUTPUT_WINMM"), catalog.ToJsonString(), out var exit, out var err);
            Assert.True(exit == 0, err + stdout);
            File.Copy(Path.Combine(Root, "tone.wav"), Path.Combine(Root, "out", "tone.wav"));
            Run();

            var wav = File.ReadAllBytes(Path.Combine(Root, "out", "tone-output.wav"));
            var n = BitConverter.ToInt32(wav, 40) / 4;            // stereo 16-bit frames
            var left = new double[n];
            for (var i = 0; i < n; i++) left[i] = BitConverter.ToInt16(wav, 44 + 4 * i) / 32768.0;
            static double Goertzel(double[] x, double hz, int rate)
            {
                double k = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
                foreach (var v in x) { var s0 = v + k * s1 - s2; s2 = s1; s1 = s0; }
                return Math.Sqrt(s1 * s1 + s2 * s2 - k * s1 * s2) * 2 / x.Length;
            }
            // The loudest second-long stretch is the tone: scan 4096-frame windows.
            var best = 0.0; var bestStart = 0;
            for (var start = 0; start + 4096 <= n; start += 2048)
            {
                var level = Goertzel(left[start..(start + 4096)], 440, 44100);
                if (level > best) { best = level; bestStart = start; }
            }
            var window = left[bestStart..(bestStart + 4096)];
            Assert.True(best > 0.2, $"the 440 Hz level in the loudest window is {best}");
            foreach (var other in new[] { 300.0, 380.0, 520.0, 700.0 })
                Assert.True(Goertzel(window, other, 44100) < best / 10, $"{other} Hz is as strong as the tone");
            var active = 0;
            for (var i = 0; i + 441 <= n; i += 441) if (left[i..(i + 441)].Max(Math.Abs) > 0.05) active++;
            Assert.InRange(active / 100.0, 0.8, 1.6);               // about one second of audible tone (the sample is 1.0 s long)
        }
        finally { Environment.SetEnvironmentVariable("BLITZCS_AUDIO", previous); }
    }

    /// <summary>
    /// FSOUND_OUTPUT_DSOUND (2): the original FMOD loads dsound.dll with LoadLibraryA, takes DirectSoundCreate / DirectSoundEnumerateA with GetProcAddress (dynamic stubs of the translated module),
    /// enumerates devices through a callback into guest code, creates a primary and a streaming secondary buffer and mixes into it with Lock/Unlock - all against the HOST's native dsound
    /// (Windows, or Wine's built-in). Windows/Wine only; the audible proof (440 Hz in a null sink) is scripts/wine/audio-capture.sh, see docs/binary-com.md.
    /// </summary>
    [PackedSampleFact]
    public void TheSameProgramDrivesFmodsDirectSoundOutputThroughTheHostDsound()
    {
        if (!OperatingSystem.IsWindows()) return;   // no host dsound elsewhere: DirectSoundCreate answers DSERR_NODRIVER (covered by the Win32 tests)
        File.WriteAllBytes(Path.Combine(Root, "tone.wav"), ToneWav());
        var stdout = Build(Source("FSOUND_OUTPUT_DSOUND"), CatalogFor(RealLibraries.PackedSample!), out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        File.Copy(Path.Combine(Root, "tone.wav"), Path.Combine(Root, "out", "tone.wav"));
        var artifact = new ArtifactSerializer().Deserialize(File.ReadAllBytes(Path.Combine(Root, "out", "fmod.dll.bcx")));
        Assert.True(artifact.DynamicStubs > 0, "the CLI gives a library that imports LoadLibrary/GetProcAddress a dynamic stub pool");

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(ExpectedNoSound.Replace("output 0\n", "output 2\n"), Run());
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5), $"the demo ran for {stopwatch.Elapsed}");
    }

    /// <summary>
    /// The product path: `blitzcs build main.bb --externals fmod.json --assets assets` writes a self-contained Windows x64 application (dist/Main/Main.exe), which this test
    /// runs on Windows or under Wine. Needs the SDK and the win-x64 runtime packs (NuGet), hence the explicit opt-in used by the other package tests.
    /// </summary>
    [FmodPackageFact]
    public void TheDefaultWindowsApplicationPackageRunsTheDemo()
    {
        var dll = RealLibraries.PackedSample!;
        var source = Path.Combine(Root, "Main.bb");
        File.WriteAllText(source, Source());
        var catalog = Path.Combine(Root, "fmod.json");
        File.WriteAllText(catalog, CatalogFor(dll));
        var assets = Path.Combine(Root, "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, "tone.wav"), ToneWav());

        var output = new StringWriter(); var error = new StringWriter();
        var exit = CliApplication.Run(["build", source, "--externals", catalog, "--assets", assets, "-o", Path.Combine(Root, "dist", "Main.exe")], output, error);
        Assert.True(exit == 0, error.ToString() + output);

        var package = Path.Combine(Root, "dist", "Main");
        Assert.True(File.Exists(Path.Combine(package, "Main.exe")));
        Assert.Equal(File.ReadAllBytes(dll), File.ReadAllBytes(Path.Combine(package, "fmod.dll")));
        Assert.Equal(BinaryTargets.WindowsX64, new ArtifactSerializer().Deserialize(File.ReadAllBytes(Path.Combine(package, "fmod.dll.bcx"))).TargetOs);
        foreach (var required in new[] { "fmod.dll.derived", "fmod.dll.lineage.json", "tone.wav", "BlitzCS.BinaryBindings.dll", "BlitzCS.BinaryCompiler.dll",
                     "BlitzCS.BinaryCompiler.Runtime.dll", "BlitzCS.BinaryCompiler.Win32.dll", "Iced.dll", "coreclr.dll" })
            Assert.True(File.Exists(Path.Combine(package, required)), $"the package lacks {required}");

        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? Path.Combine(package, "Main.exe") : "wine")
        {
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(Path.Combine(package, "Main.exe"));
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "the demo did not finish");
        Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
        Assert.Equal(ExpectedNoSound, stdout.Result.Replace("\r\n", "\n"));
    }

    private sealed class FmodPackageFactAttribute : FactAttribute
    {
        public FmodPackageFactAttribute()
        {
            Skip = RealLibraries.PackedSkipReason()
                ?? (Environment.GetEnvironmentVariable("BLITZCS_TEST_PUBLISH") != "1" ? "Set BLITZCS_TEST_PUBLISH=1 (SDK package coverage); on Linux also BLITZCS_TEST_WINE=1 with Wine on PATH (scripts/wine/env.sh)."
                : OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BLITZCS_TEST_WINE") == "1" ? null
                : "Set BLITZCS_TEST_WINE=1 to execute the Windows package under Wine (scripts/wine/env.sh).");
        }
    }
}
