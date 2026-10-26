namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Chooses and creates the waveOut backend of a context.</summary>
public static class WaveOutBackends
{
    public const string EnvironmentVariable = "BLITZCS_AUDIO";

    /// <summary>The effective kind: BLITZCS_AUDIO (host|virtual|none) beats the option; Default resolves by host OS. An unknown value throws, it is never ignored.</summary>
    public static WaveOutBackendKind Resolve(Win32Options options)
    {
        var kind = options.WaveOut;
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } text)
            kind = text.Trim().ToLowerInvariant() switch
            {
                "host" => WaveOutBackendKind.HostWinmm,
                "virtual" => WaveOutBackendKind.VirtualSink,
                "none" => WaveOutBackendKind.None,
                _ => throw new Win32UsageException($"{EnvironmentVariable}='{text}' is not valid; use host, virtual or none."),
            };
        return kind != WaveOutBackendKind.Default ? kind : OperatingSystem.IsWindows() ? WaveOutBackendKind.HostWinmm : WaveOutBackendKind.None;
    }

    public static IWaveOutBackend Create(Win32Options options)
    {
        var kind = Resolve(options);
        var diag = options.DebugOutput;
        var recordPath = options.WaveOutRecordPath is { Length: > 0 } p ? p : Environment.GetEnvironmentVariable("BLITZCS_AUDIO_RECORD");
        WavRecorder? recorder = recordPath is { Length: > 0 } ? new WavRecorder(recordPath, diag) : null;
        switch (kind)
        {
            case WaveOutBackendKind.VirtualSink:
                return new VirtualSinkBackend(options.WaveOutObserver, recorder, options.WaveOutSliceObserver);
            case WaveOutBackendKind.HostWinmm:
                var user = options.WaveOutObserver;
                WaveOutObserver? tap = recorder is null ? user : w => { recorder.Append(w); user?.Invoke(w); };
                return new RecordingHost(new HostWinmmBackend(tap, diag), recorder);
            default:
                if (recorder is not null) { diag?.Invoke("WaveOutRecordPath is ignored: the None waveOut backend has no devices."); recorder.Dispose(); }
                return new NoDevicesBackend();
        }
    }

    private sealed class RecordingHost(HostWinmmBackend inner, WavRecorder? recorder) : IWaveOutBackend
    {
        public string Name => inner.Name;
        public uint DeviceCount => inner.DeviceCount;
        public uint GetCaps(uint deviceId, out WaveOutCaps? caps) => inner.GetCaps(deviceId, out caps);
        public uint Open(uint deviceId, WaveOutFormat format, uint flags, IWaveOutSink sink, out IWaveOutDevice? device) => inner.Open(deviceId, format, flags, sink, out device);
        public uint GetVolume(uint deviceId, out uint volume) => inner.GetVolume(deviceId, out volume);
        public uint SetVolume(uint deviceId, uint volume) => inner.SetVolume(deviceId, volume);
        public string? GetErrorText(uint code) => inner.GetErrorText(code);
        public void Dispose() { inner.Dispose(); recorder?.Dispose(); }
    }
}
