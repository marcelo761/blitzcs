namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>A machine without multimedia hardware: zero devices, every device id fails (WAVE_MAPPER with MMSYSERR_NODRIVER, a numbered device with MMSYSERR_BADDEVICEID). No audio is ever played.</summary>
public sealed class NoDevicesBackend : IWaveOutBackend
{
    public string Name => "None";
    public uint DeviceCount => 0;
    private static uint Error(uint id) => id == WaveOutConst.WaveMapper ? WaveOutConst.MmsyserrNoDriver : WaveOutConst.MmsyserrBadDeviceId;
    public uint GetCaps(uint deviceId, out WaveOutCaps? caps) { caps = null; return Error(deviceId); }
    public uint Open(uint deviceId, WaveOutFormat format, uint flags, IWaveOutSink sink, out IWaveOutDevice? device) { device = null; return Error(deviceId); }
    public uint GetVolume(uint deviceId, out uint volume) { volume = 0; return Error(deviceId); }
    public uint SetVolume(uint deviceId, uint volume) => Error(deviceId);
    public string? GetErrorText(uint code) => WaveOutErrors.Text(code);
    public void Dispose() { }
}
