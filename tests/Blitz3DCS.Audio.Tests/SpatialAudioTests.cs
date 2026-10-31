using System.Numerics;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class SpatialAudioTests
{
    private static AudioMixer Mixer(out int channel)
    {
        var mixer = new AudioMixer(48000, 2);
        channel = mixer.Play(new DecodedAudio(Enumerable.Repeat(1f, 20000).ToArray(), 48000, 1));
        return mixer;
    }

    [Fact]
    public void SourceToTheRightIsAttenuatedAndPannedWithoutChangingItsUserVolume()
    {
        var mixer = Mixer(out var channel);
        mixer.SetChannel3D(channel, new(new(10, 0, 0), Vector3.Zero));
        var pcm = new float[8];
        mixer.Render(pcm);
        Assert.Equal(0f, pcm[0], 5);
        Assert.Equal(0.1f, pcm[1], 5);
        Assert.Equal(255, mixer.GetVolume(channel));
    }

    [Fact]
    public void ListenerOrientationAndTranslationChangeTheSpatialImage()
    {
        var mixer = Mixer(out var channel);
        mixer.SetListener(new(new(0, 0, 9), Vector3.Zero, Vector3.UnitX, Vector3.UnitY));
        mixer.SetChannel3D(channel, new(new(0, 0, 10), Vector3.Zero));
        var pcm = new float[2];
        mixer.Render(pcm);
        Assert.Equal(1f, pcm[0], 5);
        Assert.Equal(0f, pcm[1], 5);
    }

    [Fact]
    public void ApproachingSourceAdvancesFasterAndDopplerCanBeDisabled()
    {
        var moving = Mixer(out var movingChannel);
        var reference = Mixer(out var referenceChannel);
        moving.SetChannel3D(movingChannel, new(new(0, 0, 10), new(0, 0, -30)));
        reference.SetChannel3D(referenceChannel, new(new(0, 0, 10), Vector3.Zero));
        var pcm = new float[2000];
        moving.Render(pcm);
        reference.Render(pcm);
        Assert.True(moving.GetPosition(movingChannel) > reference.GetPosition(referenceChannel));

        var disabled = Mixer(out var disabledChannel);
        disabled.SetSpatialSettings(AudioSpatialSettings.Default with { DopplerFactor = 0 });
        disabled.SetChannel3D(disabledChannel, new(new(0, 0, 10), new(0, 0, -30)));
        disabled.Render(pcm);
        Assert.Equal(reference.GetPosition(referenceChannel), disabled.GetPosition(disabledChannel));
    }

    [Fact]
    public void SpatialStereoDownmixPreservesBothSourceChannels()
    {
        var mixer = new AudioMixer(48000, 1);
        var channel = mixer.Play(new DecodedAudio([0.8f, 0.2f, 0.8f, 0.2f], 48000, 2));
        mixer.SetChannel3D(channel, new(new(0, 0, 1), Vector3.Zero));
        var pcm = new float[2];
        mixer.Render(pcm);
        var expected = 0.5f * MathF.Sqrt(0.5f);
        Assert.Equal(expected, pcm[0], 5);
        Assert.Equal(expected, pcm[1], 5);
    }

    [Fact]
    public void DistanceClampsAtBothLimitsAndZeroRolloffDisablesAttenuation()
    {
        var mixer = Mixer(out var channel);
        var pcm = new float[2];
        mixer.SetChannel3D(channel, new(new(0.5f, 0, 0), Vector3.Zero, 2, 10));
        mixer.Render(pcm);
        Assert.Equal(1f, pcm[1], 5);
        mixer.SetChannel3D(channel, new(new(100, 0, 0), Vector3.Zero, 2, 10));
        mixer.Render(pcm);
        Assert.Equal(0.2f, pcm[1], 5);
        mixer.SetSpatialSettings(AudioSpatialSettings.Default with { RolloffFactor = 0 });
        mixer.Render(pcm);
        Assert.Equal(1f, pcm[1], 5);
    }

    [Fact]
    public void EqualListenerAndSourceVelocityDoesNotShiftPitch()
    {
        var mixer = Mixer(out var channel);
        var velocity = new Vector3(0, 0, 30);
        mixer.SetListener(AudioListenerState.Default with { Velocity = velocity });
        mixer.SetChannel3D(channel, new(new(0, 0, 10), velocity));
        mixer.Render(new float[2000]);
        Assert.Equal(1000, mixer.GetPosition(channel));
    }

    [Fact]
    public void InvalidSpatialStateFailsAndReusingChannelRestoresTwoDimensionalPlayback()
    {
        var mixer = Mixer(out var channel);
        Assert.Throws<ArgumentException>(() => mixer.SetListener(new(Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.UnitY)));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetChannel3D(channel, new(new(float.NaN, 0, 0), Vector3.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetSpatialSettings(AudioSpatialSettings.Default with { DistanceFactor = 0 }));
        mixer.SetChannel3D(channel, new(new(10, 0, 0), Vector3.Zero));
        Assert.Throws<ArgumentException>(() => mixer.SetPan(channel, 128));
        mixer.Play(new DecodedAudio([0.5f, 0.5f], 48000, 1), channel: channel);
        var pcm = new float[2];
        mixer.Render(pcm);
        Assert.Equal(new[] { 0.5f, 0.5f }, pcm);
    }
}
