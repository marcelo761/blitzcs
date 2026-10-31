using System.Runtime.InteropServices;
using System.Threading;

namespace Blitz3DCS.Audio;

/// <summary>A bounded, asynchronously decoded PCM stream for realtime consumers.</summary>
public sealed class BufferedAudioStream : IDisposable
{
    public const int BlockFrames = 4096;
    public const int CacheBlockCount = 4;
    private static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<IAudioStreamDecoder> _decoderFactory;
    private readonly TimeSpan _disposeTimeout;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _disposeGate = new();
    private readonly Thread _worker;
    private AudioStreamCacheBlock[]? _blocks;
    private float[]? _spare;
    private int _channels;
    private int _sampleRate;
    private long _frameCount;
    private long _requestedBlock = -1;
    private long _prefetchBlock = -1;
    private long _lastHitBlock = -1;
    private int _activeLeases;
    private int _activeCalls;
    private int _shutdownRequested;
    private Exception? _workerError;
    private bool _workerJoined;
    private bool _locksDisposed;
    private bool _eventsDisposed;

    private BufferedAudioStream(Func<IAudioStreamDecoder> decoderFactory, TimeSpan loadTimeout, TimeSpan disposeTimeout)
    {
        _decoderFactory = decoderFactory;
        _disposeTimeout = disposeTimeout;
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "BlitzCS buffered audio decoder"
        };
        _worker.Start();

        if (!_ready.Wait(loadTimeout))
        {
            try { Dispose(); }
            catch (TimeoutException) { }
            throw new TimeoutException("Timed out while opening and priming the buffered audio stream.");
        }

        if (Volatile.Read(ref _workerError) is { } error)
        {
            try { Dispose(); }
            catch (TimeoutException) { }
            throw new InvalidDataException("Could not open and prime the buffered audio stream.", error);
        }
    }

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public long FrameCount => _frameCount;
    internal int ActiveLeaseCount => Volatile.Read(ref _activeLeases);

    /// <summary>Bytes retained by four cache blocks and the worker's one-block decode spare.</summary>
    public long RetainedBufferBytes => checked((long)CacheBlockCount + 1) * (BlockFrames + 1) * Channels * sizeof(float);

    /// <summary>Opens and primes a WAV or OGG stream. Decoder ownership remains on its worker thread.</summary>
    public static BufferedAudioStream Load(string path, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only WAV and OGG audio files are supported.", nameof(path));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Audio file was not found.", fullPath);
        return new BufferedAudioStream(() => SndFileAudioStreamReader.Open(fullPath), timeout ?? DefaultLoadTimeout, DefaultDisposeTimeout);
    }

    internal static BufferedAudioStream LoadForTests(
        Func<IAudioStreamDecoder> decoderFactory,
        TimeSpan? timeout = null,
        TimeSpan? disposeTimeout = null) =>
        new(decoderFactory, timeout ?? DefaultLoadTimeout, disposeTimeout ?? DefaultDisposeTimeout);

    /// <summary>
    /// Acquires the cached block containing <paramref name="frame"/> without waiting for decoding.
    /// A miss queues the block and returns false. The lease contains one interpolation lookahead frame.
    /// </summary>
    public bool TryAcquireBlock(long frame, out AudioStreamBlock lease)
    {
        lease = default;
        Interlocked.Increment(ref _activeCalls);
        try
        {
            ThrowIfDisposed();
            ThrowIfWorkerFailed();
            if (frame < 0 || frame >= _frameCount) throw new ArgumentOutOfRangeException(nameof(frame));

            var blockIndex = frame / BlockFrames;
            var blocks = _blocks!;
            foreach (var block in blocks)
            {
                if (!block.Lock.TryEnterReadLock(0)) continue;
                if (Volatile.Read(ref _shutdownRequested) != 0)
                {
                    block.Lock.ExitReadLock();
                    throw new ObjectDisposedException(nameof(BufferedAudioStream));
                }
                if (block.IsValid && block.BlockIndex == blockIndex)
                {
                    Interlocked.Increment(ref _activeLeases);
                    lease = new AudioStreamBlock(this, block, blockIndex * BlockFrames, block.ActualFrames + 1, _channels);
                    OnCacheHit(blockIndex);
                    return true;
                }
                block.Lock.ExitReadLock();
            }

            Interlocked.Exchange(ref _requestedBlock, blockIndex);
            _wake.Set();
            return false;
        }
        finally { Interlocked.Decrement(ref _activeCalls); }
    }

    private void OnCacheHit(long blockIndex)
    {
        var previous = Interlocked.Exchange(ref _lastHitBlock, blockIndex);
        if (previous == blockIndex) return;
        var lookaheadBlock = blockIndex + 2;
        if (lookaheadBlock < BlockCount)
        {
            Interlocked.Exchange(ref _prefetchBlock, lookaheadBlock);
            _wake.Set();
        }
    }

    private long BlockCount => (_frameCount - 1) / BlockFrames + 1;

    private void WorkerMain()
    {
        IAudioStreamDecoder? decoder = null;
        try
        {
            decoder = _decoderFactory();
            ValidateMetadata(decoder);
            _sampleRate = decoder.SampleRate;
            _channels = decoder.Channels;
            _frameCount = decoder.FrameCount;
            _blocks = Enumerable.Range(0, CacheBlockCount)
                .Select(_ => new AudioStreamCacheBlock(checked((BlockFrames + 1) * _channels)))
                .ToArray();
            _spare = new float[checked((BlockFrames + 1) * _channels)];

            var initialBlocks = Math.Min(CacheBlockCount, BlockCount);
            for (long blockIndex = 0; blockIndex < initialBlocks; blockIndex++)
            {
                ReadBlock(decoder, blockIndex);
                var block = _blocks[blockIndex];
                block.ActualFrames = _pendingActualFrames;
                _spare.AsSpan(0, (block.ActualFrames + 1) * _channels).CopyTo(block.Samples);
                block.BlockIndex = blockIndex;
                block.IsValid = true;
            }
            _ready.Set();

            while (Volatile.Read(ref _shutdownRequested) == 0)
            {
                var target = Interlocked.Exchange(ref _requestedBlock, -1);
                if (target < 0)
                {
                    target = Interlocked.Exchange(ref _prefetchBlock, -1);
                }
                if (target < 0)
                {
                    _wake.WaitOne();
                    continue;
                }
                if (target >= BlockCount || IsCached(target)) continue;

                ReadBlock(decoder, target);
                if (Volatile.Read(ref _shutdownRequested) != 0) break;
                while (Volatile.Read(ref _shutdownRequested) == 0 && !PublishBlock(target))
                    _wake.WaitOne(20);

            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _workerError, error);
            _ready.Set();
        }
        finally
        {
            try { decoder?.Dispose(); }
            catch (Exception error) { Interlocked.CompareExchange(ref _workerError, error, null); }
            _ready.Set();
        }
    }

    private static void ValidateMetadata(IAudioStreamDecoder decoder)
    {
        if (decoder.SampleRate <= 0 || decoder.FrameCount <= 0)
            throw new InvalidDataException("Audio metadata must contain a positive sample rate and frame count.");
        if (decoder.Channels is not (1 or 2))
            throw new InvalidDataException("Buffered audio supports mono or stereo sources only.");
    }

    private void ReadBlock(IAudioStreamDecoder decoder, long blockIndex)
    {
        var start = checked(blockIndex * BlockFrames);
        var actualFrames = (int)Math.Min(BlockFrames, _frameCount - start);
        var hasFollowingSourceFrame = start + actualFrames < _frameCount;
        var framesToRead = actualFrames + (hasFollowingSourceFrame ? 1 : 0);
        if (decoder.Seek(start) != start)
            throw new InvalidDataException($"Could not seek to audio frame {start}.");

        var samplesToRead = checked(framesToRead * _channels);
        var frameOffset = 0;
        while (frameOffset < framesToRead)
        {
            var destinationOffset = checked(frameOffset * _channels);
            var remaining = framesToRead - frameOffset;
            var read = decoder.ReadFrames(_spare!, destinationOffset, remaining);
            if (read <= 0)
                throw new InvalidDataException($"Audio ended early while decoding frame {start + frameOffset}.");
            if (read > remaining)
                throw new InvalidDataException("The audio decoder returned more frames than requested.");
            frameOffset += checked((int)read);
        }

        if (!hasFollowingSourceFrame)
        {
            var firstFrame = blockIndex == 0
                ? _spare!.AsSpan(0, _channels)
                : _blocks![0].Samples.AsSpan(0, _channels);
            firstFrame.CopyTo(_spare!.AsSpan(actualFrames * _channels, _channels));
        }
        if (!_spare!.AsSpan(0, samplesToRead).IsFinite())
            throw new InvalidDataException("Decoded audio contains non-finite sample values.");
        // At EOF the repeated block-zero sample is also required to be finite.
        if (!_spare!.AsSpan(actualFrames * _channels, _channels).IsFinite())
            throw new InvalidDataException("Decoded audio contains a non-finite loop-seam sample.");

        _pendingActualFrames = actualFrames;
    }

    private int _pendingActualFrames;

    private bool PublishBlock(long blockIndex)
    {
        if (blockIndex == 0) return true; // Cache block zero remains pinned for loop interpolation.
        var blocks = _blocks!;
        AudioStreamCacheBlock? selected = null;
        long farthestDistance = -1;
        foreach (var block in blocks.AsSpan(1))
        {
            if (!block.Lock.TryEnterWriteLock(0)) continue;
            if (!block.IsValid)
            {
                selected?.Lock.ExitWriteLock();
                selected = block;
                break;
            }
            var distance = block.BlockIndex >= blockIndex ? block.BlockIndex - blockIndex : blockIndex - block.BlockIndex;
            if (distance > farthestDistance)
            {
                selected?.Lock.ExitWriteLock();
                selected = block;
                farthestDistance = distance;
            }
            else
            {
                block.Lock.ExitWriteLock();
            }
        }
        if (selected is null) return false;

        try
        {
            Volatile.Write(ref selected.IsValid, false);
            var sampleCount = checked((_pendingActualFrames + 1) * _channels);
            _spare!.AsSpan(0, sampleCount).CopyTo(selected.Samples);
            selected.BlockIndex = blockIndex;
            selected.ActualFrames = _pendingActualFrames;
            Volatile.Write(ref selected.IsValid, true);
            return true;
        }
        finally
        {
            selected.Lock.ExitWriteLock();
        }
    }

    private bool IsCached(long blockIndex)
    {
        foreach (var block in _blocks!)
        {
            if (!block.Lock.TryEnterReadLock(0)) continue;
            var found = block.IsValid && block.BlockIndex == blockIndex;
            block.Lock.ExitReadLock();
            if (found) return true;
        }
        return false;
    }

    internal void ReleaseLease(AudioStreamCacheBlock block)
    {
        block.Lock.ExitReadLock();
        _wake.Set();
        Interlocked.Decrement(ref _activeLeases);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _shutdownRequested) != 0) throw new ObjectDisposedException(nameof(BufferedAudioStream));
    }

    private void ThrowIfWorkerFailed()
    {
        if (Volatile.Read(ref _workerError) is { } error)
            throw new InvalidOperationException("The buffered audio decoder worker failed.", error);
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_eventsDisposed) return;
            Interlocked.Exchange(ref _shutdownRequested, 1);
            _wake.Set();
            if (!_workerJoined)
            {
            if (!_worker.Join(_disposeTimeout))
                    throw new TimeoutException("Timed out while stopping the buffered audio decoder worker.");
                _workerJoined = true;
            }

            var deadline = Environment.TickCount64 + (long)_disposeTimeout.TotalMilliseconds;
            while ((Volatile.Read(ref _activeLeases) != 0 || Volatile.Read(ref _activeCalls) != 0)
                && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
            if (Volatile.Read(ref _activeLeases) != 0 || Volatile.Read(ref _activeCalls) != 0)
                throw new TimeoutException($"Release all audio block leases and finish stream reads before disposing the stream (leases={Volatile.Read(ref _activeLeases)}, calls={Volatile.Read(ref _activeCalls)}).");

            if (!_locksDisposed && _blocks is not null)
            {
                foreach (var block in _blocks) block.Lock.Dispose();
                _locksDisposed = true;
            }
            _ready.Dispose();
            _wake.Dispose();
            _eventsDisposed = true;
        }
    }
}

internal interface IAudioStreamDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    long FrameCount { get; }
    long Seek(long frame);
    long ReadFrames(float[] destination, int sampleOffset, long frameCount);
}

internal sealed class AudioStreamCacheBlock
{
    internal AudioStreamCacheBlock(int sampleCapacity)
    {
        Samples = new float[sampleCapacity];
        Lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
    }

    internal readonly float[] Samples;
    internal readonly ReaderWriterLockSlim Lock;
    internal long BlockIndex;
    internal int ActualFrames;
    internal bool IsValid;
}

/// <summary>
/// Scoped read lease for one cached stream block and its interpolation lookahead frame.
/// Dispose the lease on the thread that acquired it.
/// </summary>
public ref struct AudioStreamBlock
{
    private BufferedAudioStream? _owner;
    private AudioStreamCacheBlock? _block;
    private readonly int _sampleCount;

    internal AudioStreamBlock(BufferedAudioStream owner, AudioStreamCacheBlock block, long startFrame, int frameCount, int channels)
    {
        _owner = owner;
        _block = block;
        StartFrame = startFrame;
        FrameCount = frameCount;
        Channels = channels;
        _sampleCount = checked(frameCount * channels);
    }

    public long StartFrame { get; }
    /// <summary>Frame count includes the additional interpolation lookahead frame.</summary>
    public int FrameCount { get; }
    public int Channels { get; }
    public ReadOnlySpan<float> Samples => _block is null ? ReadOnlySpan<float>.Empty : _block.Samples.AsSpan(0, _sampleCount);

    public void Dispose()
    {
        var owner = _owner;
        var block = _block;
        if (owner is null || block is null) return;
        _owner = null;
        _block = null;
        owner.ReleaseLease(block);
    }
}

internal static class AudioSampleValidation
{
    internal static bool IsFinite(this ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
            if (!float.IsFinite(sample)) return false;
        return true;
    }
}
