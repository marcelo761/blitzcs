using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class NativeFloatSemanticsTests
{
    [Fact]
    public void Sound_defaults_follow_original_Cpp_truncation_while_Fsound_controls_keep_integer_values()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(1, runtime.FSOUND_Init(22050, 4, 0));
        var sound = runtime.SampleLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));
        Assert.True(sound > 0);
        runtime.SampleVolume(sound, .5f);
        runtime.SamplePan(sound, 0);
        var channel = runtime.PlaySample(sound);
        // gxSound::setVolume/SetPan assign float expressions to C++ int fields.
        Assert.Equal(127, runtime.GetVolume(channel));
        Assert.Equal(127, runtime.GetPan(channel));
        runtime.SetVolume(channel, 128);
        runtime.SetPan(channel, 128);
        Assert.Equal(128, runtime.GetVolume(channel));
        Assert.Equal(128, runtime.GetPan(channel));
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
