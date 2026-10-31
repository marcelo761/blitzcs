using System.Diagnostics;
using Blitz3DCS.Audio;
using BlitzCS.CLI;
using BlitzCS.Compiler;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class PlaybackFunctionalTests
{
    [Fact]
    public void Sample_playback_renders_pcm_and_applies_pause_pan_volume_and_frequency_controls()
    {
        var path = WriteWave([16384, 8192, 0, -8192], channels: 1, sampleRate: 8000);
        try
        {
            using var runtime = InitializedRuntime(outputRate: 8000);
            var first = runtime.SampleLoad(path);
            var reloaded = runtime.SampleLoad(path);
            Assert.True(first > 0);
            Assert.True(reloaded > first);
            Assert.Equal(2, runtime.Statistics.Assets);

            Assert.Equal(1, runtime.SampleFree(first));
            Assert.Equal(0, runtime.SampleFree(first));
            Assert.Equal(14, runtime.GetError());
            Assert.Equal(1, runtime.Statistics.Assets);

            Assert.Equal(1, runtime.SampleLoop(reloaded, false));
            Assert.Equal(1, runtime.SampleVolume(reloaded, 1f));
            Assert.Equal(1, runtime.SamplePan(reloaded, -1f));
            Assert.Equal(1, runtime.SampleFrequency(reloaded, 4000));
            var channel = runtime.PlaySample(reloaded);
            Assert.True(channel > 0);
            Assert.Equal(1, runtime.IsPlaying(channel));
            Assert.Equal(1, runtime.SetPaused(channel, 1));

            var output = new float[8];
            runtime.Render(output);
            Assert.All(output, sample => Assert.Equal(0f, sample));
            Assert.Equal(0, runtime.GetPosition(channel));
            Assert.Equal(1, runtime.GetPaused(channel));

            Assert.Equal(1, runtime.SetPaused(channel, 0));
            Assert.Equal(1, runtime.SetVolume(channel, 255));
            Assert.Equal(1, runtime.SetPan(channel, 0));
            Assert.Equal(1, runtime.SetFrequency(channel, 4000));
            runtime.Render(output);
            Assert.Equal(new[] { 0.5f, 0f, 0.375f, 0f, 0.25f, 0f, 0.125f, 0f }, output);
            Assert.Equal(4000, runtime.GetFrequency(channel));
            Assert.Equal(0, runtime.GetPan(channel));
            Assert.Equal(255, runtime.GetVolume(channel));
            Assert.Equal(2, runtime.GetPosition(channel));

            Assert.Equal(1, runtime.SetPan(channel, 128));
            Assert.Equal(1, runtime.SetVolume(channel, 128));
            Assert.Equal(1, runtime.SetFrequency(channel, 8000));
            Assert.Equal(8000, runtime.GetFrequency(channel));
            Assert.Equal(128, runtime.GetPan(channel));
            Assert.Equal(128, runtime.GetVolume(channel));
            Assert.Equal(2, runtime.GetPosition(channel));
            Assert.Equal(1, runtime.SetPosition(channel, 0));
            runtime.Render(output.AsSpan(0, 2));
            Assert.InRange(output[0], 0.2505f, 0.2515f);
            Assert.InRange(output[1], 0.2505f, 0.2515f);

            Assert.Equal(1, runtime.SampleFree(reloaded));
            Assert.Equal(0, runtime.IsPlaying(channel));
            Assert.Equal(14, runtime.GetError());
            Assert.Equal(new AudioStatistics(0, 0, 0, 0, 0), runtime.Statistics);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ogg_stream_loops_and_can_pause_stop_close_and_reject_stale_handles()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg");
        var decoded = AudioDecoder.Decode(path);
        using var runtime = InitializedRuntime(decoded.SampleRate, channels: 2);
        var stream = runtime.StreamOpen(path, mode: 2);
        Assert.True(stream > 0);
        var channel = runtime.StreamPlay(-1, stream);
        Assert.True(channel > 0);
        Assert.Equal(1, runtime.IsPlaying(channel));
        Assert.Equal(1, runtime.Statistics.Assets);
        Assert.Equal(1, runtime.Statistics.Channels);

        Assert.Equal(1, runtime.SetVolume(channel, 64));
        Assert.Equal(1, runtime.SetPan(channel, 0));
        Assert.Equal(1, runtime.SetFrequency(channel, decoded.SampleRate));
        Assert.Equal(64, runtime.GetVolume(channel));
        Assert.Equal(0, runtime.GetPan(channel));
        Assert.Equal(decoded.SampleRate, runtime.GetFrequency(channel));

        var output = new float[decoded.Samples.Length * 6];
        runtime.Render(output);
        Assert.Contains(output, sample => sample != 0);
        Assert.Equal(1, runtime.IsPlaying(channel));
        Assert.InRange(runtime.GetPosition(channel), 0, decoded.Samples.Length / decoded.Channels - 1);
        var manualRenderStats = runtime.Statistics;
        Assert.Equal(1, manualRenderStats.Assets);
        Assert.Equal(1, manualRenderStats.Channels);
        Assert.Equal(0, manualRenderStats.SubmittedFrames);
        Assert.Equal(0, manualRenderStats.NonSilentFrames);
        Assert.Equal(0, manualRenderStats.PcmEnergy);

        Assert.Equal(1, runtime.SetPaused(channel, 1));
        var pausedAt = runtime.GetPosition(channel);
        runtime.Render(new float[64]);
        Assert.Equal(pausedAt, runtime.GetPosition(channel));
        Assert.Equal(1, runtime.GetPaused(channel));
        Assert.Equal(1, runtime.SetPaused(channel, 0));
        runtime.Render(new float[64]);
        Assert.NotEqual(pausedAt, runtime.GetPosition(channel));

        Assert.Equal(1, runtime.StreamStop(stream));
        Assert.Equal(0, runtime.IsPlaying(channel));
        Assert.Equal(1, runtime.StreamClose(stream));
        Assert.Equal(0, runtime.StreamClose(stream));
        Assert.Equal(14, runtime.GetError());
        Assert.Equal(new AudioStatistics(0, 0, 0, 0, 0), runtime.Statistics);
    }

    [Fact]
    public async Task Concurrent_play_and_close_leaves_no_handles_and_reinitialization_rejects_stale_tokens()
    {
        var path = WriteWave(Enumerable.Repeat((short)8192, 16000).ToArray(), channels: 1, sampleRate: 8000);
        try
        {
            using var runtime = InitializedRuntime(outputRate: 8000);
            var sample = runtime.SampleLoad(path);
            Assert.True(sample > 0);
            using var start = new Barrier(3);
            var play = Task.Run(() => { start.SignalAndWait(); return runtime.PlaySample(sample); });
            var close = Task.Run(() => { start.SignalAndWait(); runtime.FSOUND_Close(); });
            start.SignalAndWait();
            await Task.WhenAll(play, close).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(runtime.IsInitialized);
            Assert.Equal(new AudioStatistics(0, 0, 0, 0, 0), runtime.Statistics);
            Assert.Equal(1, runtime.FSOUND_Init(8000, 2, 0));
            Assert.Equal(0, runtime.SampleFree(sample));
            Assert.Equal(14, runtime.GetError());
            var next = runtime.SampleLoad(path);
            Assert.True(next > sample);
            Assert.Equal(1, runtime.SampleFree(next));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Basic_program_calls_published_audio_functions_in_isolated_dummy_driver_process()
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-audio-playback-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            WriteWave(Path.Combine(directory, "tone.wav"), CreateTone(sampleRate: 44100, frames: 44100), channels: 1, sampleRate: 44100);
            var methods = new (string Name, string Method)[]
            {
                ("FSOUND_Init", nameof(FSOUND.Init)), ("FSOUND_Close", nameof(FSOUND.Close)),
                ("FSOUND_Sample_Load", nameof(FSOUND.SampleLoad)), ("FSOUND_Sample_Free", nameof(FSOUND.SampleFree)),
                ("FSOUND_PlaySample", nameof(FSOUND.PlaySample)), ("FSOUND_SetVolume", nameof(FSOUND.SetVolume)),
                ("FSOUND_GetVolume", nameof(FSOUND.GetVolume)), ("FSOUND_GetPosition", nameof(FSOUND.GetPosition)),
                ("FSOUND_SetPan", nameof(FSOUND.SetPan)),
                ("FSOUND_GetPan", nameof(FSOUND.GetPan)), ("FSOUND_SetFrequency", nameof(FSOUND.SetFrequency)),
                ("FSOUND_GetFrequency", nameof(FSOUND.GetFrequency)), ("FSOUND_SetPaused", nameof(FSOUND.SetPaused)),
                ("FSOUND_GetPaused", nameof(FSOUND.GetPaused)), ("FSOUND_StopSound", nameof(FSOUND.StopSound)),
                ("FSOUND_SetListener3D", nameof(FSOUND.SetListener3D)),
                ("FSOUND_SetSpatialSettings", nameof(FSOUND.SetSpatialSettings)),
                ("FSOUND_PlaySample3D", nameof(FSOUND.PlaySample3D)),
                ("FSOUND_SetChannel3D", nameof(FSOUND.SetChannel3D)),
                ("FSOUND_SetChannel2D", nameof(FSOUND.SetChannel2D))
            };
            var registry = new ExternalFunctionRegistry(methods.Select(item =>
                new ExternalFunction(item.Name, typeof(FSOUND).GetMethod(item.Method)!)));
            var source = """
                Global sample%
                Global channel%
                Global spatial%
                Print FSOUND_Init(44100,8,0)
                Print FSOUND_SetSpatialSettings(1,1,1,343.3)
                Print FSOUND_SetListener3D(0,0,0,0,0,0,0,0,1,0,1,0)
                sample%=FSOUND_Sample_Load("tone.wav",0)
                Print sample%>0
                spatial%=FSOUND_PlaySample3D(sample%,10,0,0,0,0,0)
                Print spatial%>0
                Print FSOUND_SetChannel3D(spatial%,10,0,0,0,0,0,0.5,50)
                Print FSOUND_SetChannel2D(spatial%)
                Print FSOUND_StopSound(spatial%)
                channel%=FSOUND_PlaySample(sample%)
                Print channel%>0
                Print FSOUND_GetPosition(channel%)
                Print FSOUND_SetVolume(channel%,64)
                Print FSOUND_GetVolume(channel%)
                Print FSOUND_SetPan(channel%,0)
                Print FSOUND_GetPan(channel%)
                Print FSOUND_SetFrequency(channel%,22050)
                Print FSOUND_GetFrequency(channel%)
                Print FSOUND_SetPaused(channel%,1)
                Print FSOUND_GetPaused(channel%)
                Print FSOUND_StopSound(channel%)
                Print FSOUND_Sample_Free(sample%)
                FSOUND_Close()
                """;

            var code = BasicCompiler.Compile(source, Path.Combine(directory, "playback.bb"), registry);
            var nativeLibrary = new[] { "/usr/lib64/libSDL3.so.0", "/usr/lib/x86_64-linux-gnu/libSDL3.so.0" }
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("SDL3 Linux runtime is required by this integration test.");
            var output = Path.Combine(directory, "playback.dll");
            var decoderLibrary = new[] { "/usr/lib64/libsndfile.so.1", "/usr/lib/x86_64-linux-gnu/libsndfile.so.1" }
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("libsndfile Linux runtime is required by this integration test.");
            DotNetEmitter.Emit(code, output, null, registry.ReferencePaths, [nativeLibrary, decoderLibrary]);
            Assert.True(File.Exists(Path.Combine(directory, "libsndfile.so.1")));
            Assert.True(File.Exists(Path.Combine(directory, "libSDL3.so.0")));

            var startInfo = new ProcessStartInfo("dotnet", output)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.Environment["SDL_AUDIODRIVER"] = "dummy";
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw; }

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("1\n1\n1\n1\n1\n1\n1\n1\n1\n0\n1\n64\n1\n0\n1\n22050\n1\n1\n1\n1\n", (await stdout).Replace("\r\n", "\n"));
            Assert.Equal("", await stderr);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AudioRuntime InitializedRuntime(int outputRate, int channels = 8)
    {
        var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(outputRate, channels, 0));
        return runtime;
    }

    private static string WriteWave(short[] samples, int channels, int sampleRate)
    {
        var path = Path.Combine(Path.GetTempPath(), "blitz-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        WriteWave(path, samples, channels, sampleRate);
        return path;
    }

    private static void WriteWave(string path, short[] samples, int channels, int sampleRate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * sizeof(short));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * sizeof(short));
        foreach (var sample in samples) writer.Write(sample);
    }

    private static short[] CreateTone(int sampleRate, int frames)
    {
        var samples = new short[frames];
        for (var frame = 0; frame < frames; frame++)
            samples[frame] = (short)(Math.Sin(2 * Math.PI * 440 * frame / sampleRate) * short.MaxValue * 0.5);
        return samples;
    }

    private sealed class FakeSdl : ISdlAudio
    {
        public bool InitAudio() => true;
        public uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec) => 17;
        public string GetError() => "fake SDL error";
        public void CloseAudioDevice(uint device) { }
        public void QuitAudio() { }
    }
}
