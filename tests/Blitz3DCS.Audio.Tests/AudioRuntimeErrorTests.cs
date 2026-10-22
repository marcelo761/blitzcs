using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioRuntimeErrorTests
{
    [Fact]
    public void Malformed_audio_returns_failure_sentinel_and_records_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-invalid-{Guid.NewGuid():N}.ogg");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);
        var runtime = new AudioRuntime(new FakeSdl());
        try
        {
            Assert.Equal((sbyte)1, runtime.FSOUND_Init(44100, 2, 0));

            Assert.Equal(0, runtime.SampleLoad(path));

            Assert.Equal(10, runtime.GetError());
            Assert.Equal(10, runtime.GetError());
            Assert.Contains("Could not decode", runtime.LastError);
        }
        finally
        {
            runtime.FSOUND_Close();
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_audio_returns_failure_sentinel_and_records_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-missing-{Guid.NewGuid():N}.wav");
        var runtime = new AudioRuntime(new FakeSdl());
        try
        {
            Assert.Equal((sbyte)1, runtime.FSOUND_Init(44100, 2, 0));

            Assert.Equal(0, runtime.SampleLoad(path));

            Assert.Equal(9, runtime.GetError());
            Assert.Contains("Audio file was not found", runtime.LastError);
        }
        finally
        {
            runtime.FSOUND_Close();
        }
    }

    [Fact]
    public void Stream_open_and_stop_preserve_error_while_close_and_channel_operations_clear_it()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(44100, 4, 0));
        Assert.Equal(0, runtime.IsPlaying(int.MaxValue));
        var stream = runtime.StreamOpen(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"), 2);
        Assert.True(stream > 0);
        Assert.Equal(14, runtime.GetError());
        var channel = runtime.StreamPlay(-1, stream);
        Assert.True(channel > 0);
        Assert.Equal(0, runtime.GetError());
        Assert.Equal(0, runtime.IsPlaying(int.MaxValue));
        Assert.Equal(1, runtime.StreamStop(stream));
        Assert.Equal(14, runtime.GetError());
        Assert.Equal(1, runtime.StreamClose(stream));
        Assert.Equal(0, runtime.GetError());
    }

    [Theory]
    [InlineData(44100, int.MaxValue)]
    [InlineData(int.MaxValue, 64)]
    public void Extreme_configuration_is_rejected_before_acquiring_native_resources(int rate, int channels)
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(0, runtime.FSOUND_Init(rate, channels, 0));
        Assert.Equal(14, runtime.GetError());
        Assert.False(runtime.IsInitialized);
        Assert.Equal(1, runtime.FSOUND_Init(44100, 4, 0));
    }

    [Fact]
    public void Closed_device_reports_original_uninitialized_error_and_can_reinitialize()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 2, 0));
        var asset = runtime.SampleLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));
        var channel = runtime.PlaySample(asset);
        Assert.True(channel > 0);
        runtime.FSOUND_Close();
        Assert.Equal(0, runtime.IsPlaying(channel));
        Assert.Equal(2, runtime.GetError());
        Assert.Equal(2, runtime.GetError());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 2, 0));
        Assert.Equal(0, runtime.GetError());
        Assert.Equal(0, runtime.Statistics.Assets);
        Assert.Equal(0, runtime.Statistics.Channels);
    }

    [Fact]
    public void Full_channel_pool_fails_explicitly_and_reuses_slots_without_reviving_old_tokens()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 2, 0));
        var asset = runtime.SampleLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));
        var first = runtime.PlaySample(asset);
        var second = runtime.PlaySample(asset);
        Assert.True(first > 0 && second > first);
        Assert.Equal(-1, runtime.PlaySample(asset));
        Assert.Equal(16, runtime.GetError());
        Assert.Equal(1, runtime.StopSound(first));
        var replacement = runtime.PlaySample(asset);
        Assert.True(replacement > second);
        Assert.Equal(0, runtime.StopSound(first));
        Assert.Equal(1, runtime.IsPlaying(replacement));
        Assert.Equal(1, runtime.IsPlaying(second));
    }

    [Fact]
    public void Exhausted_token_space_cannot_start_untracked_playback_or_reuse_an_old_identity()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 2, 0));
        typeof(AudioRuntime).GetField("_nextToken", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(runtime, (long)int.MaxValue);
        var asset = runtime.SampleLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));
        Assert.Equal(int.MaxValue, asset);
        Assert.Equal(-1, runtime.PlaySample(asset));
        Assert.Equal(12, runtime.GetError());
        Assert.Equal(0, runtime.Statistics.Channels);
        var pcm = new float[32];
        runtime.Render(pcm);
        Assert.All(pcm, value => Assert.Equal(0, value));
        Assert.Equal(1, runtime.SampleFree(asset));
        Assert.Equal(0, runtime.Statistics.Assets);
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
