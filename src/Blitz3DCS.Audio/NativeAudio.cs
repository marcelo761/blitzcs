namespace Blitz3DCS.Audio;

/// <summary>Convenience audio functions used by the BlitzCS native audio catalog.</summary>
public static class NativeAudio
{
    /// <summary>Loads a sound, opening the default 44.1 kHz/64-channel runtime when needed.</summary>
    public static int LoadSound(string path)
    {
        if (!FSOUND.Runtime.IsInitialized && FSOUND.Init(44100, 64, 0) == 0) return 0;
        return HandleOrZero(FSOUND.SampleLoad(path));
    }

    public static int FreeSound(int handle) => FSOUND.SampleFree(handle);
    public static int PlaySound(int handle)
    {
        return HandleOrZero(FSOUND.PlaySample(handle));
    }
    public static int ChannelPlaying(int channel) => FSOUND.IsPlaying(channel);
    public static int StopChannel(int channel) => FSOUND.StopSound(channel);

    /// <summary>Sets channel volume, where 0 is silent and 1 is full volume.</summary>
    public static int ChannelVolume(int channel, float volume) => FSOUND.SetVolume(channel, ToByte(volume));

    /// <summary>Sets channel pan, where -1 is left, 0 is centered, and 1 is right.</summary>
    public static int ChannelPan(int channel, float pan) => FSOUND.SetPan(channel, ToPan(pan));

    public static int ChannelPitch(int channel, int frequency) => FSOUND.SetFrequency(channel, frequency);
    public static int PauseChannel(int channel) => FSOUND.SetPaused(channel, 1);
    public static int ResumeChannel(int channel) => FSOUND.SetPaused(channel, 0);
    public static int LoopSound(int handle) => FSOUND.SampleLoop(handle, 1);
    public static int SoundVolume(int handle, float volume) => FSOUND.SampleVolume(handle, volume);
    public static int SoundPan(int handle, float pan) => FSOUND.SamplePan(handle, pan);
    public static int SoundPitch(int handle, int frequency) => FSOUND.SampleFrequency(handle, frequency);

    private static int ToByte(float value)
    {
        if (float.IsNaN(value)) return 0;
        return (int)(Math.Clamp(value, 0f, 1f) * 255f);
    }

    internal static int HandleOrZero(int handle) => handle > 0 ? handle : 0;

    private static int ToPan(float value)
    {
        if (float.IsNaN(value)) return 128;
        return (int)((Math.Clamp(value, -1f, 1f) + 1f) * 127.5f);
    }
}
