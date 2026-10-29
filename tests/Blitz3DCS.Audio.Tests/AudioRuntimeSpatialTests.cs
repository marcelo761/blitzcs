using System.Numerics;
using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioRuntimeSpatialTests
{
    [Fact]
    public void Runtime_maps_managed_channel_token_to_spatial_mixer_and_back_to_2d()
    {
        var path = WriteWave();
        using var runtime = new AudioRuntime(new FakeSdl());
        try
        {
            Assert.Equal(1, runtime.FSOUND_Init(8000, 4, 0));
            Assert.Equal(1, runtime.SetListener(AudioListenerState.Default));
            Assert.Equal(1, runtime.SetSpatialSettings(AudioSpatialSettings.Default));
            var sample = runtime.SampleLoad(path);
            Assert.True(sample > 0);
            var channel = runtime.PlaySample3D(sample, new AudioEmitterState(new Vector3(10, 0, 0), Vector3.Zero, 1, 100));
            Assert.True(channel > 0);

            var pcm3D = new float[2];
            runtime.Render(pcm3D);
            Assert.Equal(0f, pcm3D[0], 5);
            Assert.InRange(pcm3D[1], 0.02f, 0.04f); // inverse-distance attenuation with hard-right equal-power pan
            Assert.Equal(0, runtime.SetPan(channel, 128)); // manual pan is rejected while 3D is active
            Assert.Equal(14, runtime.GetError());

            Assert.Equal(1, runtime.SetChannel2D(channel));
            Assert.Equal(1, runtime.SetPan(channel, 0));
            var pcm2D = new float[2];
            runtime.Render(pcm2D);
            Assert.InRange(pcm2D[0], 0.25f, 0.4f);
            Assert.Equal(0f, pcm2D[1], 5);
        }
        finally
        {
            runtime.FSOUND_Close();
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalid_spatial_start_does_not_leave_an_untracked_playing_channel()
    {
        var path = WriteWave();
        using var runtime = new AudioRuntime(new FakeSdl());
        try
        {
            Assert.Equal(1, runtime.FSOUND_Init(8000, 2, 0));
            var sample = runtime.SampleLoad(path);
            Assert.True(sample > 0);
            var invalid = new AudioEmitterState(new Vector3(float.NaN, 0, 0), Vector3.Zero);
            Assert.Equal(-1, runtime.PlaySample3D(sample, invalid));
            Assert.Equal(14, runtime.GetError());
            Assert.Equal(0, runtime.Statistics.Channels);
        }
        finally
        {
            runtime.FSOUND_Close();
            File.Delete(path);
        }
    }

    [Fact]
    public void Spatial_configuration_requires_initialized_audio_and_rejects_invalid_listener()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal(0, runtime.SetListener(AudioListenerState.Default));
        Assert.Equal(2, runtime.GetError());
        Assert.Equal(1, runtime.FSOUND_Init(8000, 2, 0));
        var invalid = new AudioListenerState(Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        Assert.Equal(0, runtime.SetListener(invalid));
        Assert.Equal(14, runtime.GetError());
    }

    private static string WriteWave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-spatial-{Guid.NewGuid():N}.wav");
        const int frames = 800;
        const int sampleRate = 8000;
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8); writer.Write(36 + frames * sizeof(short)); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * sizeof(short)); writer.Write((short)sizeof(short)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * sizeof(short));
        for (var i = 0; i < frames; i++) writer.Write((short)10000);
        return path;
    }

    private sealed class FakeSdl : ISdlAudio
    {
        public bool InitAudio() => true;
        public uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec) => 1;
        public string GetError() => "fake SDL error";
        public void CloseAudioDevice(uint device) { }
        public void QuitAudio() { }
    }
}
