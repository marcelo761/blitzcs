using System.Runtime.InteropServices;

namespace Blitz3DCS.Audio;

/// <summary>SDL3-backed implementation of the Blitz3D FMOD initialization entry points.</summary>
public sealed class AudioRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly ISdlAudio _sdl;
    private uint _device;
    private bool _ownsAudioSubsystem;
    private bool _disposed;
    private int _mixRate;
    private int _maxSoftwareChannels;
    private uint _flags;
    public string? LastError { get; private set; }

    public AudioRuntime() : this(new SdlAudio()) { }

    internal AudioRuntime(ISdlAudio sdl) => _sdl = sdl;

    public bool IsInitialized
    {
        get { lock (_gate) return _device != 0; }
    }

    /// <summary>Initializes SDL audio and opens a default stereo playback device.</summary>
    public sbyte FSOUND_Init(int mixRate, int maxSoftwareChannels, uint flags)
    {
        lock (_gate)
        {
            if (_disposed || mixRate <= 0 || maxSoftwareChannels < 0 || flags != 0)
            {
                LastError = flags != 0 ? "Unsupported FSOUND_Init flags; only flags=0 is currently implemented." : "Invalid initialization parameters or runtime already disposed.";
                return 0;
            }
            if (_device != 0)
            {
                if (mixRate != _mixRate || maxSoftwareChannels != _maxSoftwareChannels || flags != _flags)
                {
                    LastError = "Audio is initialized with different settings; close it before reinitializing.";
                    return 0;
                }
                LastError = null;
                return 1;
            }

            try
            {
                if (!_sdl.InitAudio())
                {
                    LastError = _sdl.GetError();
                    return 0;
                }

                _ownsAudioSubsystem = true;
                var spec = new SdlAudioSpec { Format = SdlAudioFormatF32, Channels = 2, Frequency = mixRate };
                var device = _sdl.OpenDefaultPlayback(in spec);
                if (device == 0)
                {
                    LastError = _sdl.GetError();
                    _sdl.QuitAudio();
                    _ownsAudioSubsystem = false;
                    return 0;
                }

                _device = device;
                _mixRate = mixRate;
                _maxSoftwareChannels = maxSoftwareChannels;
                _flags = flags;
                LastError = null;
                return 1;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
            {
                LastError = $"SDL3 audio initialization failed: {error.Message}";
                if (_ownsAudioSubsystem)
                {
                    try { _sdl.QuitAudio(); } catch { /* preserve the initialization failure */ }
                    _ownsAudioSubsystem = false;
                }
                return 0;
            }
        }
    }

    /// <summary>Closes the playback device and releases the SDL audio subsystem.</summary>
    public void FSOUND_Close()
    {
        lock (_gate)
        {
            if (_device != 0)
            {
                _sdl.CloseAudioDevice(_device);
                _device = 0;
                _mixRate = 0;
                _maxSoftwareChannels = 0;
                _flags = 0;
            }

            if (_ownsAudioSubsystem)
            {
                _sdl.QuitAudio();
                _ownsAudioSubsystem = false;
            }
            LastError = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            FSOUND_Close();
            _disposed = true;
        }
    }

    private const uint SdlAudioFormatF32 = 0x8120; // SDL_AUDIO_F32 native-endian

    [StructLayout(LayoutKind.Sequential)]
    internal struct SdlAudioSpec
    {
        public uint Format;
        public int Channels;
        public int Frequency;
    }
}

internal interface ISdlAudio
{
    bool InitAudio();
    uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec);
    string GetError();
    void CloseAudioDevice(uint device);
    void QuitAudio();
}

internal sealed class SdlAudio : ISdlAudio
{
    private const string SdlLibrary = "libSDL3.so.0";

    [DllImport(SdlLibrary, EntryPoint = "SDL_InitSubSystem", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_InitSubSystem(uint flags);

    [DllImport(SdlLibrary, EntryPoint = "SDL_OpenAudioDevice", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_OpenAudioDevice(uint deviceId, in AudioRuntime.SdlAudioSpec spec);

    [DllImport(SdlLibrary, EntryPoint = "SDL_CloseAudioDevice", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_CloseAudioDevice(uint deviceId);

    [DllImport(SdlLibrary, EntryPoint = "SDL_QuitSubSystem", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);

    [DllImport(SdlLibrary, EntryPoint = "SDL_GetError", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();

    public bool InitAudio() => SDL_InitSubSystem(0x10);
    public uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec) => SDL_OpenAudioDevice(0xFFFFFFFF, in spec);
    public void CloseAudioDevice(uint device) => SDL_CloseAudioDevice(device);
    public void QuitAudio() => SDL_QuitSubSystem(0x10);
    public string GetError() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "Unknown SDL3 error.";
}
