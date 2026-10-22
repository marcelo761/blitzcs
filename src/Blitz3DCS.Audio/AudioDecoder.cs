using System.Runtime.InteropServices;

namespace Blitz3DCS.Audio;

/// <summary>Decoded interleaved floating point audio and its source format.</summary>
public sealed class DecodedAudio
{
    public DecodedAudio(float[] samples, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        if (samples.Length == 0 || samples.Length % channels != 0)
            throw new ArgumentException("Samples must contain complete, non-empty interleaved frames.", nameof(samples));
        if (samples.Any(sample => !float.IsFinite(sample)))
            throw new ArgumentException("Samples must contain only finite values.", nameof(samples));

        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
}

/// <summary>Decodes WAV and Vorbis OGG assets through the system libsndfile library.</summary>
public static class AudioDecoder
{
    private const int ReadMode = 0x10;
    private const int FramesPerRead = 4096;
    private const long MaxDecodedBytes = 256L * 1024 * 1024;

    /// <summary>Decodes a WAV or OGG file into normalized, interleaved float samples.</summary>
    /// <exception cref="ArgumentException">The path is empty or has an unsupported extension.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed or exceeds supported bounds.</exception>
    public static DecodedAudio Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only WAV and OGG audio files are supported.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Audio file was not found.", fullPath);

        NativeAudioInfo info = default;
        IntPtr file;
        try
        {
            file = SndFile.Open(fullPath, ReadMode, ref info);
        }
        catch (DllNotFoundException error)
        {
            throw new InvalidOperationException("Audio decoding requires the Linux libsndfile runtime (libsndfile.so.1).", error);
        }
        if (file == IntPtr.Zero)
            throw new InvalidDataException($"Could not decode audio file '{fullPath}': {SndFile.GetError(IntPtr.Zero)}");

        try
        {
            if (info.Frames <= 0 || info.SampleRate <= 0 || info.Channels is < 1 or > 8)
                throw new InvalidDataException("Audio metadata has invalid frame count, sample rate, or channel count.");

            long sampleCount;
            try { sampleCount = checked(info.Frames * info.Channels); }
            catch (OverflowException error) { throw new InvalidDataException("Audio sample count exceeds supported bounds.", error); }
            if (sampleCount > int.MaxValue || sampleCount * sizeof(float) > MaxDecodedBytes)
                throw new InvalidDataException("Decoded audio exceeds the 256 MiB sample limit.");

            var samples = new float[(int)sampleCount];
            var chunk = new float[FramesPerRead * info.Channels];
            long frameOffset = 0;
            while (frameOffset < info.Frames)
            {
                var requestedFrames = Math.Min(FramesPerRead, info.Frames - frameOffset);
                var framesRead = SndFile.ReadFrames(file, chunk, requestedFrames);
                if (framesRead <= 0)
                    throw new InvalidDataException($"Audio ended early while decoding '{fullPath}': {SndFile.GetError(file)}");

                var sampleOffset = checked((int)(frameOffset * info.Channels));
                var samplesToCopy = checked((int)(framesRead * info.Channels));
                Array.Copy(chunk, 0, samples, sampleOffset, samplesToCopy);
                frameOffset += framesRead;
            }

            if (samples.Any(sample => !float.IsFinite(sample)))
                throw new InvalidDataException("Decoded audio contains non-finite sample values.");
            return new DecodedAudio(samples, info.SampleRate, info.Channels);
        }
        finally
        {
            SndFile.Close(file);
        }
    }

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
        internal static extern IntPtr Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode, ref NativeAudioInfo info);

        [DllImport(Library, EntryPoint = "sf_readf_float", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long ReadFrames(IntPtr file, [Out] float[] samples, long frameCount);

        [DllImport(Library, EntryPoint = "sf_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(IntPtr file);

        [DllImport(Library, EntryPoint = "sf_strerror", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Error(IntPtr file);

        internal static string GetError(IntPtr file) => Marshal.PtrToStringUTF8(Error(file)) ?? "Unknown libsndfile error.";
    }
}
