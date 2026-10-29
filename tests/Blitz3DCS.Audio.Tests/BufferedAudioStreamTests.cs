using System.Collections.Concurrent;
using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class BufferedAudioStreamTests
{
    [Fact]
    public void Loads_and_reads_real_ogg_through_the_native_stream_decoder()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg");
        using var stream = BufferedAudioStream.Load(path);

        Assert.Equal(22050, stream.SampleRate);
        Assert.Equal(1, stream.Channels);
        Assert.True(stream.FrameCount > 1000);
        Assert.True(stream.TryAcquireBlock(0, out var block));
        using (block)
            Assert.Contains(block.Samples.ToArray(), sample => Math.Abs(sample) > 0.01f);
    }

    [Fact]
    public void Load_prefills_four_blocks_and_retains_only_four_blocks_plus_one_spare()
    {
        var decoder = new FakeDecoder(20_000, channels: 2);
        using var stream = BufferedAudioStream.LoadForTests(() => decoder);

        Assert.Equal(48_000, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(20_000, stream.FrameCount);
        Assert.Equal((long)5 * (BufferedAudioStream.BlockFrames + 1) * 2 * sizeof(float), stream.RetainedBufferBytes);
        Assert.True(stream.TryAcquireBlock(100, out var block));
        using (block)
        {
            Assert.Equal(0, block.StartFrame);
            Assert.Equal(BufferedAudioStream.BlockFrames + 1, block.FrameCount);
            Assert.Equal(2, block.Channels);
            Assert.Equal(0f, block.Samples[0]);
            Assert.Equal(4095f, block.Samples[(BufferedAudioStream.BlockFrames - 1) * 2]);
            Assert.Equal(4096f, block.Samples[BufferedAudioStream.BlockFrames * 2]);
        }
    }

    [Fact]
    public void Cache_miss_returns_without_waiting_and_leases_prevent_slot_mutation()
    {
        var decoder = new FakeDecoder(100_000, channels: 1) { GateBlockFour = true };
        var stream = BufferedAudioStream.LoadForTests(() => decoder);
        AudioStreamBlock first = default, second = default, third = default;
        try
        {
            Assert.True(stream.TryAcquireBlock(BufferedAudioStream.BlockFrames, out first));
            Assert.True(stream.TryAcquireBlock(2L * BufferedAudioStream.BlockFrames, out second));
            Assert.True(stream.TryAcquireBlock(3L * BufferedAudioStream.BlockFrames, out third));

            var retained = first.Samples[0];
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(stream.TryAcquireBlock(4L * BufferedAudioStream.BlockFrames, out _));
            Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(100));
            Assert.True(decoder.BlockFourReadStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(retained, first.Samples[0]);
        }
        finally
        {
            decoder.AllowBlockFourRead.Set();
            first.Dispose();
            second.Dispose();
            third.Dispose();
        }
        Assert.Equal(0, stream.ActiveLeaseCount);

        try
        {
            Assert.True(WaitForBlock(stream, 4L * BufferedAudioStream.BlockFrames, out var loaded));
            try
            {
                Assert.Equal(4f * BufferedAudioStream.BlockFrames, loaded.Samples[0]);
            }
            finally
            {
                loaded.Dispose();
            }
        }
        finally
        {
            stream.Dispose();
        }

        Assert.NotEqual(Environment.CurrentManagedThreadId, decoder.FactoryThreadId);
        Assert.NotEmpty(decoder.NativeCallThreadIds);
        Assert.All(decoder.NativeCallThreadIds, threadId => Assert.Equal(decoder.FactoryThreadId, threadId));
        Assert.True(decoder.IsDisposed);
    }

    [Fact]
    public void Block_zero_stays_pinned_and_last_lookahead_wraps_to_first_frame()
    {
        const long frameCount = 8L * BufferedAudioStream.BlockFrames + 3;
        var decoder = new FakeDecoder(frameCount, channels: 1);
        using var stream = BufferedAudioStream.LoadForTests(() => decoder);

        Assert.True(WaitForBlock(stream, frameCount - 1, out var last));
        using (last)
        {
            Assert.Equal(8L * BufferedAudioStream.BlockFrames, last.StartFrame);
            Assert.Equal(4, last.FrameCount);
            Assert.Equal((float)(frameCount - 1), last.Samples[2]);
            Assert.Equal(0f, last.Samples[3]);
        }

        Assert.True(stream.TryAcquireBlock(0, out var first));
        using (first) Assert.Equal(0f, first.Samples[0]);
    }

    [Fact]
    public void Long_frame_positions_are_not_truncated_and_worker_failures_propagate()
    {
        var longFrameCount = (long)int.MaxValue + 100_000;
        var decoder = new FakeDecoder(longFrameCount, channels: 1)
        {
            FailAtFrame = 4L * BufferedAudioStream.BlockFrames
        };
        using var stream = BufferedAudioStream.LoadForTests(() => decoder);
        Assert.Equal(longFrameCount, stream.FrameCount);

        Assert.False(stream.TryAcquireBlock(4L * BufferedAudioStream.BlockFrames, out _));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try
            {
                if (stream.TryAcquireBlock(4L * BufferedAudioStream.BlockFrames, out var lease))
                    lease.Dispose();
            }
            catch (InvalidOperationException error) when (error.InnerException is IOException) { return true; }
            return false;
        }, TimeSpan.FromSeconds(2)));
    }

    private static bool WaitForBlock(BufferedAudioStream stream, long frame, out AudioStreamBlock block)
    {
        block = default;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        do
        {
            if (stream.TryAcquireBlock(frame, out block)) return true;
            Thread.Sleep(1);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private sealed class FakeDecoder(long frameCount, int channels) : IAudioStreamDecoder
    {
        private long _position;
        private int _disposed;
        private int _factoryThreadId;
        private int _gateNextRead;

        public int SampleRate => 48_000;
        public int Channels => channels;
        public long FrameCount => frameCount;
        public int FactoryThreadId => Volatile.Read(ref _factoryThreadId);
        public ConcurrentQueue<int> NativeCallThreadIds { get; } = new();
        public ManualResetEventSlim ReadStarted { get; } = new();
        public ManualResetEventSlim BlockFourReadStarted { get; } = new();
        public ManualResetEventSlim AllowBlockFourRead { get; } = new();
        public bool GateBlockFour { get; init; }
        public long? FailAtFrame { get; init; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public long Seek(long frame)
        {
            RecordNativeCall();
            if (FailAtFrame == frame) throw new IOException("fake decoder failure");
            _position = frame;
            if (GateBlockFour && frame == 4L * BufferedAudioStream.BlockFrames)
                Volatile.Write(ref _gateNextRead, 1);
            return frame;
        }

        public long ReadFrames(float[] destination, int sampleOffset, long frameCount)
        {
            RecordNativeCall();
            ReadStarted.Set();
            if (Interlocked.Exchange(ref _gateNextRead, 0) != 0)
            {
                BlockFourReadStarted.Set();
                AllowBlockFourRead.Wait();
            }
            var frames = Math.Min(frameCount, Math.Min(1024, FrameCount - _position));
            for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                destination[sampleOffset + checked((int)frame) * channels + channel] = (float)(_position + frame) + channel * 0.5f;
            _position += frames;
            return frames;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            RecordNativeCall();
        }

        private void RecordNativeCall()
        {
            var threadId = Environment.CurrentManagedThreadId;
            Interlocked.CompareExchange(ref _factoryThreadId, threadId, 0);
            NativeCallThreadIds.Enqueue(threadId);
        }
    }
}
