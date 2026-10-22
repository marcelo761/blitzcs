using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioResourceStressTests
{
    [Fact]
    public void Ten_thousand_reused_channel_slots_leave_no_stale_handles_or_retained_assets()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 4, 0));
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg");
        var asset = runtime.SampleLoad(path);
        var previous = 0;
        for (var index = 0; index < 10000; index++)
        {
            var channel = runtime.PlaySample(asset);
            Assert.True(channel > previous);
            if (previous != 0) Assert.Equal(0, runtime.SetVolume(previous, 1));
            Assert.Equal(1, runtime.SetVolume(channel, 128));
            Assert.Equal(1, runtime.StopSound(channel));
            Assert.Equal(0, runtime.Statistics.Channels);
            previous = channel;
            if (index % 100 == 99)
            {
                Assert.Equal(1, runtime.SampleFree(asset));
                Assert.Equal(0, runtime.Statistics.Assets);
                asset = runtime.SampleLoad(path);
                Assert.True(asset > channel);
            }
        }
        Assert.Equal(1, runtime.SampleFree(asset));
        runtime.FSOUND_Close();
        Assert.Equal(new AudioStatistics(0, 0, 0, 0, 0), runtime.Statistics);
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
