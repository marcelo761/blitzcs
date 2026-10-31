using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Blitz3DCS.Audio;

static object GetSdlAudioDiagnostics(string? requestedDriver)
{
    nint library = 0;
    var localLibrary = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
    if (!NativeLibrary.TryLoad(localLibrary, out library) && !NativeLibrary.TryLoad("libSDL3.so.0", out library))
        return new { audioDrivers = Array.Empty<string>(), audioInitialized = false, currentAudioDriver = (string?)null, playbackDevices = Array.Empty<string>() };
    var count = Marshal.GetDelegateForFunctionPointer<SdlGetNumAudioDrivers>(NativeLibrary.GetExport(library, "SDL_GetNumAudioDrivers"))();
    var getDriver = Marshal.GetDelegateForFunctionPointer<SdlGetAudioDriver>(NativeLibrary.GetExport(library, "SDL_GetAudioDriver"));
    var drivers = Enumerable.Range(0, Math.Max(0, count))
        .Select(index => Marshal.PtrToStringUTF8(getDriver(index)) ?? "")
        .ToArray();
    bool? hintAccepted = null;
    if (!string.IsNullOrWhiteSpace(requestedDriver))
    {
        var setHint = Marshal.GetDelegateForFunctionPointer<SdlSetHint>(NativeLibrary.GetExport(library, "SDL_SetHint"));
        var name = Marshal.StringToCoTaskMemUTF8("SDL_AUDIO_DRIVER");
        var value = Marshal.StringToCoTaskMemUTF8(requestedDriver);
        try { hintAccepted = setHint(name, value); }
        finally { Marshal.FreeCoTaskMem(value); Marshal.FreeCoTaskMem(name); }
    }
    var initAudio = Marshal.GetDelegateForFunctionPointer<SdlInitSubSystem>(NativeLibrary.GetExport(library, "SDL_InitSubSystem"));
    if (!initAudio(0x10))
        return new { audioDrivers = drivers, requestedDriver, hintAccepted, audioInitialized = false, currentAudioDriver = (string?)null, playbackDevices = Array.Empty<string>() };
    var currentDriver = Marshal.GetDelegateForFunctionPointer<SdlGetCurrentAudioDriver>(NativeLibrary.GetExport(library, "SDL_GetCurrentAudioDriver"));
    var getDevices = Marshal.GetDelegateForFunctionPointer<SdlGetPlaybackDevices>(NativeLibrary.GetExport(library, "SDL_GetAudioPlaybackDevices"));
    var getDeviceName = Marshal.GetDelegateForFunctionPointer<SdlGetAudioDeviceName>(NativeLibrary.GetExport(library, "SDL_GetAudioDeviceName"));
    var openDevice = Marshal.GetDelegateForFunctionPointer<SdlOpenAudioDevice>(NativeLibrary.GetExport(library, "SDL_OpenAudioDevice"));
    var closeDevice = Marshal.GetDelegateForFunctionPointer<SdlCloseAudioDevice>(NativeLibrary.GetExport(library, "SDL_CloseAudioDevice"));
    var getError = Marshal.GetDelegateForFunctionPointer<SdlGetError>(NativeLibrary.GetExport(library, "SDL_GetError"));
    var free = Marshal.GetDelegateForFunctionPointer<SdlFree>(NativeLibrary.GetExport(library, "SDL_free"));
    nint deviceIds = getDevices(out var deviceCount);
    try
    {
        var devices = new string[Math.Max(0, deviceCount)];
        for (var index = 0; index < devices.Length; index++)
        {
            var deviceId = unchecked((uint)Marshal.ReadInt32(deviceIds, index * sizeof(uint)));
            devices[index] = Marshal.PtrToStringUTF8(getDeviceName(deviceId)) ?? "";
        }
        var requestedSpec = new NativeSdlAudioSpec { Format = 0x8120, Channels = 2, Frequency = 44100 };
        var probeDevice = openDevice(uint.MaxValue, in requestedSpec);
        var probeDeviceError = probeDevice == 0 ? Marshal.PtrToStringUTF8(getError()) : null;
        if (probeDevice != 0) closeDevice(probeDevice);
        return new
        {
            audioDrivers = drivers,
            requestedDriver,
            hintAccepted,
            audioInitialized = true,
            currentAudioDriver = Marshal.PtrToStringUTF8(currentDriver()),
            playbackDevices = devices,
            directDefaultDeviceOpen = probeDevice != 0,
            directDefaultDeviceError = probeDeviceError,
        };
    }
    finally
    {
        if (deviceIds != 0) free(deviceIds);
    }
}

static string Arg(string[] args, string name, string? fallback = null)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return fallback ?? throw new ArgumentException($"Missing required option {name}.");
    if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
    return args[index + 1];
}

static int WriteWave(string path)
{
    const int rate = 44100;
    const int seconds = 3;
    const short channels = 2;
    const short bits = 16;
    var frames = rate * seconds;
    using var file = File.Create(path);
    using var writer = new BinaryWriter(file);
    writer.Write("RIFF"u8); writer.Write(36 + frames * channels * sizeof(short)); writer.Write("WAVE"u8);
    writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write(channels);
    writer.Write(rate); writer.Write(rate * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write(bits);
    writer.Write("data"u8); writer.Write(frames * channels * sizeof(short));
    for (var i = 0; i < frames; i++)
    {
        var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 5000);
        writer.Write(sample); writer.Write((short)-sample);
    }
    writer.Flush();
    return (int)file.Length;
}

object? diagnostics = null;
try
{
    var oggPath = Path.GetFullPath(Arg(args, "--ogg"));
    var workDir = Path.GetFullPath(Arg(args, "--work-dir", Path.Combine(AppContext.BaseDirectory, "audio-smoke")));
    var durationMs = int.Parse(Arg(args, "--duration-ms", "1200"));
    var requestedDriver = Arg(args, "--sdl-driver", "");
    if (!File.Exists(oggPath) || !oggPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("--ogg must identify an existing OGG file.");
    if (durationMs is < 250 or > 10000) throw new ArgumentOutOfRangeException("--duration-ms", "Use 250..10000 ms.");

    Directory.CreateDirectory(workDir);
    var wavPath = Path.Combine(workDir, "generated-stereo.wav");
    var wavLength = WriteWave(wavPath);
    var wavDecoded = AudioDecoder.Decode(wavPath);
    var oggDecoded = AudioDecoder.Decode(oggPath);
    var audioDiagnostics = GetSdlAudioDiagnostics(string.IsNullOrWhiteSpace(requestedDriver) ? null : requestedDriver);
    diagnostics = new
    {
        audio = audioDiagnostics,
        wavDecoded = new { wavDecoded.SampleRate, wavDecoded.Channels, frames = wavDecoded.Samples.Length / wavDecoded.Channels },
        oggDecoded = new { oggDecoded.SampleRate, oggDecoded.Channels, frames = oggDecoded.Samples.Length / oggDecoded.Channels },
    };
    var initialized = FSOUND.Init(44100, 8, 0);
    if (initialized != 1) throw new InvalidOperationException($"FSOUND.Init failed: {FSOUND.LastError}");

    var sample = FSOUND.SampleLoad(wavPath);
    var stream = FSOUND.StreamOpen(wavPath, 2, 0, 0);
    if (sample <= 0 || stream <= 0) throw new InvalidOperationException($"Asset load failed: {FSOUND.LastError}");
    Require(FSOUND.SampleLoop(sample, 1), "sample loop");
    Require(FSOUND.SampleVolume(sample, 0.55f), "sample volume");
    Require(FSOUND.SamplePan(sample, -0.35f), "sample pan");

    var channelA = FSOUND.PlaySample(sample);
    var channelB = FSOUND.PlaySample(sample);
    var streamChannel = FSOUND.StreamPlay(-1, stream);
    if (channelA <= 0 || channelB <= 0 || streamChannel <= 0)
        throw new InvalidOperationException($"Concurrent playback setup failed: {FSOUND.LastError}");
    Require(FSOUND.SetVolume(channelA, 192), "channel volume");
    Require(FSOUND.SetPan(channelA, 48), "channel pan");
    Require(FSOUND.SetFrequency(channelA, 48000), "channel frequency");
    Require(FSOUND.SetPaused(channelB, 1), "pause channel");
    Thread.Sleep(50);
    Require(FSOUND.SetPaused(channelB, 0), "resume channel");

    Thread.Sleep(durationMs);
    var stats = FSOUND.Statistics;
    var playingBeforeStop = new[] { FSOUND.IsPlaying(channelA), FSOUND.IsPlaying(channelB), FSOUND.IsPlaying(streamChannel) };
    var streamPosition = FSOUND.GetPosition(streamChannel);
    if (stats.Assets != 2 || stats.Channels < 3 || stats.SubmittedFrames <= 0 || stats.NonSilentFrames <= 0 || stats.PcmEnergy <= 0)
        throw new InvalidOperationException($"SDL produced no verified active PCM: {JsonSerializer.Serialize(stats)}");
    if (streamPosition <= 4 * 4096 || stats.StreamBufferBytes is <= 0 or > 262144 || stats.StreamUnderruns > 32)
        throw new InvalidOperationException($"Stream did not advance through the bounded cache: position={streamPosition}, stats={JsonSerializer.Serialize(stats)}");
    if (playingBeforeStop.Any(value => value != 1))
        throw new InvalidOperationException($"A looped channel stopped unexpectedly: {string.Join(',', playingBeforeStop)}");

    Require(FSOUND.StreamStop(stream), "stream stop");
    Require(FSOUND.StreamClose(stream), "stream close");
    Require(FSOUND.SampleFree(sample), "sample free");
    if (FSOUND.Close() != 0) throw new InvalidOperationException($"audio close failed: {FSOUND.LastError}");

    var report = new
    {
        result = "pass",
        processArchitecture = Environment.Is64BitProcess ? "x64" : "x86",
        sdlAudioDriver = requestedDriver,
        wav = new { path = Path.GetFileName(wavPath), bytes = wavLength, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wavPath))).ToLowerInvariant() },
        ogg = new { fileName = Path.GetFileName(oggPath), bytes = new FileInfo(oggPath).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oggPath))).ToLowerInvariant() },
        audio = audioDiagnostics,
        decoded = new
        {
            wav = new { wavDecoded.SampleRate, wavDecoded.Channels, frames = wavDecoded.Samples.Length / wavDecoded.Channels },
            ogg = new { oggDecoded.SampleRate, oggDecoded.Channels, frames = oggDecoded.Samples.Length / oggDecoded.Channels },
        },
        concurrentChannels = stats.Channels,
        activeBeforeStop = playingBeforeStop,
        streamPositionFrames = streamPosition,
        streamBufferBytes = stats.StreamBufferBytes,
        streamUnderruns = stats.StreamUnderruns,
        submittedFrames = stats.SubmittedFrames,
        nonSilentFrames = stats.NonSilentFrames,
        pcmEnergy = stats.PcmEnergy,
        controls = new[] { "loop", "sample volume/pan", "channel volume/pan/frequency", "pause/resume", "bounded multi-block WAV stream", "stream stop/close" },
        note = "SDL submitted non-silent PCM; this does not prove physical audibility."
    };
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { result = "fail", error = error.ToString(), lastError = FSOUND.LastError, diagnostics }));
    try { FSOUND.Close(); } catch { }
    return 1;
}

static void Require(int result, string operation)
{
    if (result != 1) throw new InvalidOperationException($"{operation} failed: {FSOUND.LastError}");
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int SdlGetNumAudioDrivers();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nint SdlGetAudioDriver(int index);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nint SdlGetCurrentAudioDriver();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nint SdlGetPlaybackDevices(out int count);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nint SdlGetAudioDeviceName(uint deviceId);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint SdlOpenAudioDevice(uint deviceId, in NativeSdlAudioSpec spec);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void SdlCloseAudioDevice(uint deviceId);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nint SdlGetError();
[StructLayout(LayoutKind.Sequential)]
struct NativeSdlAudioSpec
{
    public uint Format;
    public int Channels;
    public int Frequency;
}
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void SdlFree(nint memory);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SdlSetHint(nint name, nint value);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SdlInitSubSystem(uint flags);
