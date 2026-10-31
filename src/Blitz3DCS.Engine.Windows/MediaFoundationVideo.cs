using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Blitz3DCS.Engine.Windows;

/// <summary>
/// Synchronous, bounded-memory AVI/video decoder backed by the Windows Media Foundation
/// Source Reader. The Source Reader decodes compressed frames but does not provide a clock;
/// callers select the frame using DecodeAt and their own playback clock.
/// </summary>
internal sealed class MediaFoundationVideo : IMovieVideo
{
    private const int CoInitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly object MediaFoundationGate = new();
    private static int _mediaFoundationUsers;

    private readonly IMFSourceReader _reader;
    private readonly FileStream _stream;
    private readonly int _ownerThreadId;
    private readonly bool _ownsComInitialization;
    private byte[] _currentFrame;
    private byte[] _nextFrame;
    private long _nextFrameTimestamp100ns;
    private long _firstFrameTimestamp100ns;
    private bool _hasCurrentFrame;
    private bool _hasNextFrame;
    private bool _endOfStream;
    private bool _disposed;

    public MediaFoundationVideo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Media Foundation video decoding requires Windows.");

        _ownerThreadId = Environment.CurrentManagedThreadId;
        var comResult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (comResult < 0 && comResult != RpcEChangedMode)
            Marshal.ThrowExceptionForHR(comResult);
        _ownsComInitialization = comResult >= 0;

        IMFSourceReader? reader = null;
        FileStream? stream = null;
        var mediaFoundationAcquired = false;
        try
        {
            AcquireMediaFoundation();
            mediaFoundationAcquired = true;

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.RandomAccess);
            using (var attributes = MediaFactory.MFCreateAttributes(2))
            {
                attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true);
                reader = MediaFactory.MFCreateSourceReaderFromByteStream(stream, attributes);
            }

            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using (var outputType = MediaFactory.MFCreateMediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
            }

            using var currentType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            MediaFactory.MFGetAttributeSize(currentType, MediaTypeAttributeKeys.FrameSize,
                out var width, out var height).CheckError();
            if (width == 0 || height == 0 || (long)width * height * 4 > int.MaxValue)
                throw new InvalidDataException($"Invalid video dimensions: {width}x{height}.");

            Width = checked((int)width);
            Height = checked((int)height);
            var duration = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource,
                PresentationDescriptionAttributeKeys.Duration);
            Duration100ns = Convert.ToInt64(duration.Value, CultureInfo.InvariantCulture);
            if (Duration100ns <= 0)
                throw new InvalidDataException("The video source has no positive duration.");

            var strideResult = currentType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var strideBits);
            var stride = strideResult.Failure ? checked(Width * 4) : unchecked((int)strideBits);
            if (stride == int.MinValue || Math.Abs(stride) < Width * 4)
                throw new InvalidDataException($"Invalid RGB32 stride: {stride}.");
            _stride = stride;

            _currentFrame = new byte[checked(Width * Height * 4)];
            _nextFrame = new byte[_currentFrame.Length];
            _reader = reader;
            _stream = stream;
            reader = null;
            stream = null;
        }
        catch
        {
            reader?.Dispose();
            stream?.Dispose();
            if (mediaFoundationAcquired) ReleaseMediaFoundation();
            if (_ownsComInitialization) CoUninitialize();
            throw;
        }
    }

    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }
    public long Duration100ns { get; }
    public byte[] RgbaFrame => _currentFrame;
    public long FrameTimestamp100ns { get; private set; }

    /// <summary>
    /// Decodes forward until the current frame is the latest frame at or before the target.
    /// Keeps only the current and one look-ahead frame in managed memory.
    /// </summary>
    public bool DecodeAt(long targetTime100ns)
    {
        VerifyUsable();
        if (targetTime100ns < 0) targetTime100ns = 0;

        if (_hasCurrentFrame && targetTime100ns < FrameTimestamp100ns &&
            (targetTime100ns >= _firstFrameTimestamp100ns || FrameTimestamp100ns != _firstFrameTimestamp100ns))
            SeekToStart();

        var changed = false;
        if (!_hasCurrentFrame)
        {
            if (!ReadFrame(_currentFrame, out var firstTimestamp)) return false;
            FrameTimestamp100ns = firstTimestamp;
            _hasCurrentFrame = true;
            _firstFrameTimestamp100ns = FrameTimestamp100ns;
            changed = true;
        }

        while (true)
        {
            if (!_hasNextFrame)
            {
                if (_endOfStream || !ReadFrame(_nextFrame, out _nextFrameTimestamp100ns))
                {
                    _endOfStream = true;
                    break;
                }
                _hasNextFrame = true;
            }

            if (_nextFrameTimestamp100ns > targetTime100ns) break;

            (_currentFrame, _nextFrame) = (_nextFrame, _currentFrame);
            FrameTimestamp100ns = _nextFrameTimestamp100ns;
            _hasNextFrame = false;
            changed = true;
        }

        return changed;
    }

    public void SeekToStart()
    {
        VerifyUsable();
        _reader.Flush(SourceReaderIndex.FirstVideoStream);
        _reader.SetCurrentPosition(0);
        _hasCurrentFrame = false;
        _hasNextFrame = false;
        _endOfStream = false;
        FrameTimestamp100ns = 0;
    }

    private unsafe bool ReadFrame(byte[] destination, out long timestamp100ns)
    {
        while (true)
        {
            using var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlag.None, out _, out var flags, out timestamp100ns);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) return false;
            if ((flags & SourceReaderFlag.StreamTick) != 0) continue;
            if (sample is null) continue;

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var data, out _, out var currentLength);
            try
            {
                var sourceStride = Math.Abs(_stride);
                var requiredLength = checked((long)sourceStride * Height);
                if (currentLength < requiredLength)
                    throw new InvalidDataException($"RGB32 sample is truncated ({currentLength} bytes; expected {requiredLength}).");

                for (var y = 0; y < Height; y++)
                {
                    var sourceY = _stride < 0 ? Height - 1 - y : y;
                    var sourceRow = (byte*)data + (long)sourceY * sourceStride;
                    var targetRow = destination.AsSpan(y * Width * 4, Width * 4);
                    for (var x = 0; x < Width; x++)
                    {
                        var sourceOffset = x * 4;
                        var targetOffset = sourceOffset;
                        targetRow[targetOffset] = sourceRow[sourceOffset + 2];
                        targetRow[targetOffset + 1] = sourceRow[sourceOffset + 1];
                        targetRow[targetOffset + 2] = sourceRow[sourceOffset];
                        targetRow[targetOffset + 3] = byte.MaxValue;
                    }
                }
            }
            finally
            {
                buffer.Unlock();
            }
            return true;
        }
    }

    private void VerifyUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Media Foundation video must be used and disposed on its creating thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyUsable();
        _reader.Dispose();
        _stream.Dispose();
        ReleaseMediaFoundation();
        if (_ownsComInitialization) CoUninitialize();
        _disposed = true;
    }

    private static void AcquireMediaFoundation()
    {
        lock (MediaFoundationGate)
        {
            if (_mediaFoundationUsers == 0)
            {
                var result = MediaFactory.MFStartup();
                if (result.Failure) result.CheckError();
            }
            _mediaFoundationUsers++;
        }
    }

    private static void ReleaseMediaFoundation()
    {
        lock (MediaFoundationGate)
        {
            if (--_mediaFoundationUsers == 0)
            {
                var result = MediaFactory.MFShutdown();
                if (result.Failure) result.CheckError();
            }
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
