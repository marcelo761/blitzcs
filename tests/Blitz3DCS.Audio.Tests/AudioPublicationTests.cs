using System.Diagnostics;
using BlitzCS.CLI;
using BlitzCS.Compiler;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioPublicationTests
{
    [Theory]
    [InlineData("dummy", "1\n1\n")]
    [InlineData("blitzcs_nonexistent_driver", "0\n0\n")]
    public async Task BasicAssemblyCallsPublishedAudioFacadeAndReportsRealDriverFailure(string driver, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-audio-publish-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var registry = new ExternalFunctionRegistry([
                new ExternalFunction("FSOUND_Init", typeof(FSOUND).GetMethod(nameof(FSOUND.Init))!),
                new ExternalFunction("FSOUND_Close", typeof(FSOUND).GetMethod(nameof(FSOUND.Close))!)
            ]);
            var source = """
                Global initialized%
                initialized%=FSOUND_Init(44100,64,0)
                Print initialized%
                <?cs
                System.Console.WriteLine(BB.initialized);
                ?>
                FSOUND_Close()
                """;
            var code = BasicCompiler.Compile(source, Path.Combine(directory, "audio.bb"), registry);
            Assert.Contains("@Blitz3DCS.@Audio.@FSOUND.@Init", code);
            var output = Path.Combine(directory, "audio.dll");
            var nativeLibrary = new[] { "/usr/lib64/libSDL3.so.0", "/usr/lib/x86_64-linux-gnu/libSDL3.so.0" }
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("SDL3 Linux runtime is required by this integration test.");
            DotNetEmitter.Emit(code, output, null, registry.ReferencePaths, [nativeLibrary]);
            Assert.True(File.Exists(Path.Combine(directory, "libSDL3.so.0")));
            Assert.True(File.Exists(Path.Combine(directory, "Blitz3DCS.Audio.dll")));
            var start = new ProcessStartInfo("dotnet", output)
            {
                WorkingDirectory = directory, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            };
            start.Environment["SDL_AUDIODRIVER"] = driver;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw; }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expected, (await stdout).Replace("\r\n", "\n"));
            Assert.Equal("", await stderr);
        }
        finally { Directory.Delete(directory, true); }
    }
}
