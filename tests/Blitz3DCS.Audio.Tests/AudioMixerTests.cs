using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioMixerTests
{
    [Fact]
    public void Render_applies_volume_and_pan_to_stereo_pcm()
    {
        var mixer = new AudioMixer(4, 1);
        mixer.Play(Audio([1f, 1f], 4, 1), volume: 128, pan: 0);
        var output = new float[4];

        mixer.Render(output);

        Assert.Equal(128f / 255f, output[0], 5);
        Assert.Equal(0f, output[1]);
        Assert.Equal(128f / 255f, output[2], 5);
        Assert.Equal(0f, output[3]);
    }

    [Fact]
    public void Render_resamples_with_fractional_linear_interpolation()
    {
        var mixer = new AudioMixer(4, 1);
        mixer.Play(Audio([0f, 1f, 0f, -1f], 4, 1), frequency: 2);
        var output = new float[6];

        mixer.Render(output);

        Assert.Equal(new float[] { 0f, 0f, 0.5f, 0.5f, 1f, 1f }, output);
    }

    [Fact]
    public void Render_loops_and_mixes_simultaneous_channels_with_clamping()
    {
        var mixer = new AudioMixer(4, 2);
        var sound = Audio([0.75f, 0.75f], 4, 1);
        var first = mixer.Play(sound, loop: true);
        var second = mixer.Play(sound);
        var output = new float[8];

        mixer.Render(output);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(1f, output[0]);
        Assert.Equal(0.75f, output[6]);
        Assert.True(mixer.IsPlaying(first));
        Assert.False(mixer.IsPlaying(second));
    }

    [Fact]
    public void Play_reuses_stopped_channel_and_explicit_channel_interrupts()
    {
        var mixer = new AudioMixer(4, 1);
        var first = Audio([0.25f, 0.25f], 4, 1);
        var second = Audio([-0.5f, -0.5f], 4, 1);
        Assert.Equal(0, mixer.Play(first));
        mixer.Stop(0);
        Assert.Equal(0, mixer.Play(second));
        mixer.Play(first, channel: 0);
        var output = new float[2];

        mixer.Render(output);

        Assert.Equal(0.25f, output[0]);
        Assert.Equal(0.25f, output[1]);
    }

    [Fact]
    public void Pause_resume_and_position_and_loop_controls_are_observable()
    {
        var mixer = new AudioMixer(4, 1);
        mixer.Play(Audio([0.1f, 0.2f, 0.3f, 0.4f], 4, 1), loop: true, paused: true);
        var output = new float[4];

        mixer.Render(output);
        Assert.Equal(new float[4], output);
        Assert.True(mixer.IsPlaying(0));
        Assert.Equal(0, mixer.GetPosition(0));
        mixer.SetLoopPoints(0, 1, 3);
        mixer.SetPosition(0, 2);
        mixer.SetPaused(0, false);
        mixer.Render(output);

        Assert.Equal(new float[] { 0.3f, 0.3f, 0.2f, 0.2f }, output);
        Assert.Equal(2, mixer.GetPosition(0));
    }

    [Fact]
    public void Controls_can_mute_retime_and_stop_all_channels_using_minus_two()
    {
        var mixer = new AudioMixer(4, 2);
        var sound = Audio([1f, 1f, 1f, 1f], 4, 1);
        mixer.Play(sound);
        mixer.Play(sound);
        mixer.SetVolume(-2, 0);
        mixer.SetFrequency(-2, 2);
        Assert.Equal(0, mixer.GetVolume(0));
        Assert.Equal(2, mixer.GetFrequency(1));
        var output = new float[4];
        mixer.Render(output);
        Assert.Equal(new float[4], output);
        mixer.Stop(-2);
        Assert.False(mixer.IsPlaying(0));
        Assert.False(mixer.IsPlaying(1));
    }

    [Fact]
    public void StopByAsset_stops_only_channels_using_that_decoded_object()
    {
        var mixer = new AudioMixer(4, 2);
        var sound = Audio([0.5f, 0.5f], 4, 1);
        mixer.Play(sound, loop: true);
        mixer.Play(Audio([0.25f, 0.25f], 4, 1), loop: true);

        mixer.StopByAsset(sound);

        Assert.False(mixer.IsPlaying(0));
        Assert.True(mixer.IsPlaying(1));
    }

    [Fact]
    public void Render_remains_allocation_free_and_clamps_finite_pcm_under_repeated_load()
    {
        var mixer = new AudioMixer(44100, 8);
        var loud = Audio([float.MaxValue, float.MaxValue], 44100, 1);
        for (var i = 0; i < mixer.ChannelCount; i++) mixer.Play(loud, loop: true);
        var output = new float[1024];
        mixer.Render(output); // Warm up the JIT before measuring this thread.
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 2000; i++) mixer.Render(output);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.All(output, sample =>
        {
            Assert.True(float.IsFinite(sample));
            Assert.InRange(sample, -1f, 1f);
        });
    }

    private static DecodedAudio Audio(float[] samples, int sampleRate, int channels) =>
        new(samples, sampleRate, channels);
}
