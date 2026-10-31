using System.Numerics;

namespace Blitz3DCS.Audio;

/// <summary>Legacy-style BASIC-callable entry points for managed audio playback.</summary>
public static class FSOUND
{
    internal static readonly AudioRuntime Runtime = new();

    public static string? LastError => Runtime.LastError;
    public static AudioStatistics Statistics => Runtime.Statistics;

    public static int Init(int mixRate, int maxSoftwareChannels, int flags) =>
        Runtime.FSOUND_Init(mixRate, maxSoftwareChannels, unchecked((uint)flags));

    /// <summary>Closes audio; returns 0 as an acknowledgment for BASIC-callable catalogs.</summary>
    public static int Close()
    {
        Runtime.FSOUND_Close();
        return 0;
    }

    public static int GetError() => Runtime.GetError();

    public static int StreamOpen(string path, int mode, int offset = 0, int length = 0) =>
        Runtime.StreamOpen(path, mode, offset, length);

    public static int StreamPlay(int channel, int handle) => Runtime.StreamPlay(channel, handle);
    public static int StreamStop(int handle) => Runtime.StreamStop(handle);
    public static int StreamClose(int handle) => Runtime.StreamClose(handle);

    public static int SetVolume(int channel, int volume) => Runtime.SetVolume(channel, volume);
    public static int SetPan(int channel, int pan) => Runtime.SetPan(channel, pan);
    public static int SetFrequency(int channel, int frequency) => Runtime.SetFrequency(channel, frequency);
    public static int SetPaused(int channel, int paused) => Runtime.SetPaused(channel, paused);
    public static int GetVolume(int channel) => Runtime.GetVolume(channel);
    public static int GetFrequency(int channel) => Runtime.GetFrequency(channel);
    /// <summary>Returns the zero-based source PCM frame position; this is not an elapsed-time value.</summary>
    public static int GetPosition(int channel) => Runtime.GetPosition(channel);
    public static int GetPan(int channel) => Runtime.GetPan(channel);
    public static int GetPaused(int channel) => Runtime.GetPaused(channel);
    public static int IsPlaying(int channel) => Runtime.IsPlaying(channel);
    public static int StopSound(int channel) => Runtime.StopSound(channel);

    public static int SampleLoad(string path, int mode = 0) => Runtime.SampleLoad(path, mode);
    public static int SampleFree(int handle) => Runtime.SampleFree(handle);
    public static int PlaySample(int handle) => Runtime.PlaySample(handle);
    public static int SampleLoop(int handle, int loop) => Runtime.SampleLoop(handle, loop != 0);
    public static int SampleVolume(int handle, float volume) => Runtime.SampleVolume(handle, volume);
    public static int SamplePan(int handle, float pan) => Runtime.SamplePan(handle, pan);
    public static int SampleFrequency(int handle, int frequency) => Runtime.SampleFrequency(handle, frequency);

    /// <summary>Starts a sample with a 3D emitter. Coordinates and velocities use world units; distances default to 1 and 10000.</summary>
    public static int PlaySample3D(int handle, float x, float y, float z, float velocityX, float velocityY, float velocityZ,
        float minimumDistance = 1f, float maximumDistance = 10000f) =>
        Runtime.PlaySample3D(handle, new AudioEmitterState(new Vector3(x, y, z), new Vector3(velocityX, velocityY, velocityZ), minimumDistance, maximumDistance));

    /// <summary>Sets a channel's 3D emitter. Coordinates and velocities use world units; distances default to 1 and 10000.</summary>
    public static int SetChannel3D(int channel, float x, float y, float z, float velocityX, float velocityY, float velocityZ,
        float minimumDistance = 1f, float maximumDistance = 10000f) =>
        Runtime.SetChannel3D(channel, new AudioEmitterState(new Vector3(x, y, z), new Vector3(velocityX, velocityY, velocityZ), minimumDistance, maximumDistance));

    public static int SetChannel2D(int channel) => Runtime.SetChannel2D(channel);

    /// <summary>Sets listener position, velocity, forward and up vectors using scalar BASIC-compatible arguments.</summary>
    public static int SetListener3D(
        float positionX, float positionY, float positionZ,
        float velocityX, float velocityY, float velocityZ,
        float forwardX, float forwardY, float forwardZ,
        float upX, float upY, float upZ) =>
        Runtime.SetListener(new AudioListenerState(
            new Vector3(positionX, positionY, positionZ),
            new Vector3(velocityX, velocityY, velocityZ),
            new Vector3(forwardX, forwardY, forwardZ),
            new Vector3(upX, upY, upZ)));

    public static int SetSpatialSettings(float rolloffFactor, float dopplerFactor, float distanceFactor, float speedOfSound) =>
        Runtime.SetSpatialSettings(new AudioSpatialSettings(rolloffFactor, dopplerFactor, distanceFactor, speedOfSound));
}
