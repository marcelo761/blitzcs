using System.Runtime.InteropServices;

namespace Blitz3DCS.Audio;

internal sealed class SndFileAudioStreamReader : IAudioStreamDecoder
{
    private const int ReadMode = 0x10;
    private const int SeekSet = 0;
    private nint _file;

    private SndFileAudioStreamReader(nint file, NativeAudioInfo info)
    {
        _file = file;
        SampleRate = info.SampleRate;
        Channels = info.Channels;
        FrameCount = info.Frames;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount { get; }

    internal static SndFileAudioStreamReader Open(string path)
    {
        NativeAudioImportResolver.EnsureInstalled();
        NativeAudioInfo info = default;
        nint file;
        try { file = SndFile.Open(path, ReadMode, ref info); }
        catch (DllNotFoundException error)
        {
            throw new InvalidOperationException("Audio streaming requires the matching libsndfile native runtime beside the application on Windows, or libsndfile.so.1 on Linux.", error);
        }
        if (file == 0)
            throw new InvalidDataException($"Could not open audio stream '{path}': {SndFile.GetError(0)}");
        if (info.Seekable == 0)
        {
            SndFile.Close(file);
            throw new InvalidDataException("Buffered audio streaming requires a seekable WAV or OGG source.");
        }
        return new SndFileAudioStreamReader(file, info);
    }

    public long Seek(long frame)
    {
        var file = GetFile();
        return SndFile.Seek(file, frame, SeekSet);
    }

    public long ReadFrames(float[] destination, int sampleOffset, long frameCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (sampleOffset < 0 || frameCount < 0
            || checked((long)sampleOffset + checked(frameCount * Channels)) > destination.Length)
            throw new ArgumentOutOfRangeException(nameof(sampleOffset));
        if (frameCount == 0) return 0;

        var pin = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var samples = IntPtr.Add(pin.AddrOfPinnedObject(), checked(sampleOffset * sizeof(float)));
            return SndFile.ReadFrames(GetFile(), samples, frameCount);
        }
        finally { pin.Free(); }
    }

    public void Dispose()
    {
        var file = Interlocked.Exchange(ref _file, 0);
        if (file == 0) return;
        if (SndFile.Close(file) != 0)
            throw new IOException("libsndfile could not close the audio stream.");
    }

    private nint GetFile() => _file != 0 ? _file : throw new ObjectDisposedException(nameof(SndFileAudioStreamReader));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAudioInfo
    {
        public long Frames;
        public int SampleRate;
        public int Channels;
        public int Format;
        public int Sections;
        public int Seekable;
    }

    private static class SndFile
    {
        private const string Library = "libsndfile.so.1";

        [DllImport(Library, EntryPoint = "sf_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode, ref NativeAudioInfo info);

        [DllImport(Library, EntryPoint = "sf_seek", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long Seek(nint file, long frames, int whence);

        [DllImport(Library, EntryPoint = "sf_readf_float", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long ReadFrames(nint file, nint samples, long frameCount);

        [DllImport(Library, EntryPoint = "sf_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(nint file);

        [DllImport(Library, EntryPoint = "sf_strerror", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint Error(nint file);

        internal static string GetError(nint file) => Marshal.PtrToStringUTF8(Error(file)) ?? "Unknown libsndfile error.";
    }
}
