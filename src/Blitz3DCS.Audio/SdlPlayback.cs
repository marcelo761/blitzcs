using System.Runtime.InteropServices;

namespace Blitz3DCS.Audio;

// SDL's own audio thread consumes a bounded queue; no managed callback crosses the ABI.
internal sealed class SdlPlayback : IDisposable
{
    private nint _stream;
    private GCHandle _pinned;
    internal float[] Buffer { get; } = new float[1024];
    internal int QueuedBytes => SDL_GetAudioStreamQueued(_stream);

    internal SdlPlayback(uint device, int frequency)
    {
        var spec = new AudioRuntime.SdlAudioSpec { Format = 0x8120, Channels = 2, Frequency = frequency };
        _stream = SDL_CreateAudioStream(in spec, in spec);
        if (_stream == 0) throw new InvalidOperationException(Error());
        try
        {
            if (!SDL_BindAudioStream(device, _stream) || !SDL_ResumeAudioDevice(device))
                throw new InvalidOperationException(Error());
            _pinned = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
        }
        catch { Dispose(); throw; }
    }

    internal void Submit()
    {
        if (!SDL_PutAudioStreamData(_stream, _pinned.AddrOfPinnedObject(), Buffer.Length * sizeof(float)))
            throw new InvalidOperationException(Error());
    }

    public void Dispose()
    {
        if (_stream != 0) { SDL_DestroyAudioStream(_stream); _stream = 0; }
        if (_pinned.IsAllocated) _pinned.Free();
    }

    private static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "SDL audio queue failed.";
    private const string Library = "libSDL3.so.0";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_CreateAudioStream(in AudioRuntime.SdlAudioSpec source, in AudioRuntime.SdlAudioSpec destination);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_BindAudioStream(uint device, nint stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_ResumeAudioDevice(uint device);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_PutAudioStreamData(nint stream, nint buffer, int bytes);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_GetAudioStreamQueued(nint stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroyAudioStream(nint stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();
}
