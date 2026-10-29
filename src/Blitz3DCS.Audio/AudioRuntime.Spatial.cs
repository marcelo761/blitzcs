namespace Blitz3DCS.Audio;

public sealed partial class AudioRuntime
{
    /// <summary>Sets listener position, orientation and velocity for active 3D channels.</summary>
    public int SetListener(AudioListenerState listener)
    {
        lock (_gate)
        {
            if (_mixer is null) return Fail(2, "Audio is not initialized.");
            try { _mixer.SetListener(listener); return Success(); }
            catch (ArgumentException error) { return Fail(14, error.Message); }
        }
    }

    /// <summary>Sets distance, rolloff and Doppler settings used by the software mixer.</summary>
    public int SetSpatialSettings(AudioSpatialSettings settings)
    {
        lock (_gate)
        {
            if (_mixer is null) return Fail(2, "Audio is not initialized.");
            try { _mixer.SetSpatialSettings(settings); return Success(); }
            catch (ArgumentException error) { return Fail(14, error.Message); }
        }
    }

    /// <summary>Attaches a spatial emitter to a managed channel token.</summary>
    public int SetChannel3D(int channel, AudioEmitterState emitter) => Control(channel, mixer => mixer.SetChannel3D(Slot(channel), emitter));

    /// <summary>Returns a spatial channel to ordinary 2D pan and stereo behavior.</summary>
    public int SetChannel2D(int channel) => Control(channel, mixer => mixer.SetChannel2D(Slot(channel)));
}
