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
}
