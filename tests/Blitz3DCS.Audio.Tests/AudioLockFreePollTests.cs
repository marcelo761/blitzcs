using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioLockFreePollTests(ITestOutputHelper output)
{
    [Fact]
    public void IsPlaying_polls_never_block_on_the_mixer_and_never_tear()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 8, 0));
        var asset = runtime.SampleLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));
        Assert.Equal(1, runtime.SampleLoop(asset, true));
        var looping = runtime.PlaySample(asset);
        Assert.True(looping > 0);
        Assert.Equal(1, runtime.SampleLoop(asset, false));
        var stopped = runtime.PlaySample(asset);
        Assert.Equal(1, runtime.StopSound(stopped));

        using var stop = new CancellationTokenSource();
        var renders = 0L;
        var mixer = new Thread(() =>
        {
            var buffer = new float[2048];
            while (!stop.IsCancellationRequested) { runtime.Render(buffer); Interlocked.Increment(ref renders); }
        }) { IsBackground = true };
        mixer.Start();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000_000; i++)
        {
            Assert.Equal(1, runtime.IsPlaying(looping));
            if ((i & 1023) == 0) Assert.Equal(0, runtime.IsPlaying(stopped));
        }
        var lockFreeMs = sw.Elapsed.TotalMilliseconds;
        var rendersDuring = Interlocked.Read(ref renders);

        sw.Restart();
        for (var i = 0; i < 300; i++) Assert.Equal(1, runtime.SetVolume(looping, 100 + (i & 15)));
        var lockedPer1M = sw.Elapsed.TotalMilliseconds * (1_000_000 / 300.0);
        stop.Cancel();
        Assert.True(mixer.Join(TimeSpan.FromSeconds(10)), "mixer thread deadlocked");
        var report = $"IsPlaying x1M with concurrent Render: {lockFreeMs:F0} ms ({rendersDuring} renders); locked Control path extrapolated to 1M: {lockedPer1M:F0} ms";
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("BLITZCS_TEST_REPORT") is { Length: > 0 } reportPath) File.AppendAllText(reportPath, report + "\n");
    }

    [Fact]
    public void Snapshot_tracks_play_stop_and_close()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(0, runtime.IsPlaying(1));
        Assert.Equal(2, runtime.GetError());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 4, 0));
        var asset = runtime.SampleLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));
        var channel = runtime.PlaySample(asset);
        Assert.Equal(1, runtime.IsPlaying(channel));
        Assert.Equal(0, runtime.GetError());
        Assert.Equal(1, runtime.StopSound(channel));
        Assert.Equal(0, runtime.IsPlaying(channel));
        Assert.Equal(14, runtime.GetError());
        var again = runtime.PlaySample(asset);
        Assert.Equal(1, runtime.IsPlaying(again));
        runtime.FSOUND_Close();
        Assert.Equal(0, runtime.IsPlaying(again));
    }

    private sealed class FakeSdl : ISdlAudio
    {
        public bool InitAudio() => true;
        public uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec) => 1;
        public string GetError() => "fixture";
        public void CloseAudioDevice(uint device) { }
        public void QuitAudio() { }
    }
}
