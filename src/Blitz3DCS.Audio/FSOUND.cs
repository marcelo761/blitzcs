namespace Blitz3DCS.Audio;

/// <summary>Legacy-style static entry points for Blitz3D audio initialization.</summary>
public static class FSOUND
{
    private static readonly object Gate = new();
    private static readonly AudioRuntime Runtime = new();

    public static string? LastError { get; private set; }

    public static int Init(int mixRate, int maxSoftwareChannels, int flags)
    {
        lock (Gate)
        {
            var result = Runtime.FSOUND_Init(mixRate, maxSoftwareChannels, unchecked((uint)flags));
            LastError = result != 0 ? null : Runtime.LastError;
            return result != 0 ? 1 : 0;
        }
    }

    /// <summary>Closes audio; returns 0 as an acknowledgment for BASIC-callable catalogs.</summary>
    public static int Close()
    {
        lock (Gate)
        {
            Runtime.FSOUND_Close();
            LastError = null;
            return 0;
        }
    }
}
