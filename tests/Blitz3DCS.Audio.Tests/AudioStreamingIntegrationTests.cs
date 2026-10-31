using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioStreamingIntegrationTests
{
    [Fact]
    public void Mixer_plays_the_same_stream_on_independent_channel_positions()
    {
        var stream = BufferedAudioStream.LoadForTests(() => new TestDecoder(
            1000, 1, static (frame, _) => 0.05f + frame * 0.001f, 4));
        using (stream)
        {
            var mixer = new AudioMixer(4, 2);
            var left = mixer.Play(stream, pan: 0);
            var right = mixer.Play(stream, pan: 255);
            mixer.SetPosition(left, 11);
            mixer.SetPosition(right, 23);
            var output = new float[4];

            mixer.Render(output);

            Assert.Equal(0.061f, output[0], 5);
            Assert.Equal(0.074f, output[3], 5);
            Assert.Equal(13, mixer.GetPosition(left));
            Assert.Equal(25, mixer.GetPosition(right));
            Assert.True(mixer.IsPlaying(left));
            Assert.True(mixer.IsPlaying(right));
        }
    }

    [Fact]
    public void Mixer_cache_miss_is_silent_does_not_advance_and_recovers_after_prefetch()
    {
        var decoder = new TestDecoder(10L * BufferedAudioStream.BlockFrames, 1, static (_, _) => 0.25f, 48_000)
        {
            GateFrame = 4L * BufferedAudioStream.BlockFrames,
        };
        var stream = BufferedAudioStream.LoadForTests(() => decoder);
        using (stream)
        {
            var mixer = new AudioMixer(48_000, 1);
            var channel = mixer.Play(stream);
            mixer.SetPosition(channel, checked((int)(4L * BufferedAudioStream.BlockFrames)));
            var output = new float[16];

            mixer.Render(output);

            Assert.Equal(new float[16], output);
            Assert.Equal(4L * BufferedAudioStream.BlockFrames, mixer.GetPosition(channel));
            Assert.Equal(1, mixer.StreamUnderruns);
            Assert.True(decoder.BlockFourReadStarted.Wait(TimeSpan.FromSeconds(2)));

            decoder.AllowBlockFourRead.Set();
            Assert.True(WaitForBlock(stream, 4L * BufferedAudioStream.BlockFrames));
            mixer.Render(output);

            Assert.Equal(0.25f, output[0]);
            Assert.Equal(4L * BufferedAudioStream.BlockFrames + 8, mixer.GetPosition(channel));
            Assert.Equal(1, mixer.StreamUnderruns);
        }
    }

    [Fact]
    public void Mixer_nonlooped_stream_clamps_eof_interpolation_instead_of_wrapping_to_frame_zero()
    {
        using var stream = BufferedAudioStream.LoadForTests(() => new TestDecoder(
            5, 1, static (frame, _) => frame == 4 ? 1f : 0f, 4));
        var mixer = new AudioMixer(4, 1);
        var channel = mixer.Play(stream, frequency: 2);
        mixer.SetPosition(channel, 3);
        var output = new float[10];

        mixer.Render(output);

        Assert.Equal(new float[] { 0, 0, 0.5f, 0.5f, 1, 1, 1, 1, 0, 0 }, output);
        Assert.Equal(5, mixer.GetPosition(channel));
        Assert.False(mixer.IsPlaying(channel));
    }

    [Fact]
    public void Runtime_streams_share_bounded_cache_and_close_discards_all_channels()
    {
        using var runtime = new AudioRuntime(new FakeSdl());
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(22050, 4, 0));
        var stream = runtime.StreamOpen(Fixture("generated-tone.ogg"), 2);
        Assert.True(stream > 0);
        var first = runtime.StreamPlay(-1, stream);
        var second = runtime.StreamPlay(-1, stream);
        Assert.True(first > 0 && second > first);
        Assert.Equal(1, runtime.SetPosition(first, 100));
        Assert.Equal(1, runtime.SetPosition(second, 20));
        var output = new float[16];

        runtime.Render(output);

        Assert.Equal(108, runtime.GetPosition(first));
        Assert.Equal(28, runtime.GetPosition(second));
        Assert.Equal(2, runtime.Statistics.Channels);
        Assert.Equal(1, runtime.Statistics.Assets);
        Assert.Equal((long)5 * (BufferedAudioStream.BlockFrames + 1) * sizeof(float), runtime.Statistics.StreamBufferBytes);
        Assert.Equal(1, runtime.StreamClose(stream));
        Assert.Equal(0, runtime.IsPlaying(first));
        Assert.Equal(0, runtime.IsPlaying(second));
        Assert.Equal(0, runtime.Statistics.StreamBufferBytes);
    }

    [Fact]
    public async Task Stream_open_finishing_after_close_disposes_the_unpublished_worker()
    {
        var decoder = new TestDecoder(1000, 1, static (_, _) => 0.25f, 22050);
        using var stream = BufferedAudioStream.LoadForTests(() => decoder);
        using var enteredFactory = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        using var runtime = new AudioRuntime(new FakeSdl(), _ =>
        {
            enteredFactory.Set();
            releaseFactory.Wait();
            return stream;
        });
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(22050, 2, 0));

        var opening = Task.Run(() => runtime.StreamOpen("not-read-by-test-factory.wav", 2));
        Assert.True(enteredFactory.Wait(TimeSpan.FromSeconds(2)));
        runtime.FSOUND_Close();
        releaseFactory.Set();

        Assert.Equal(0, await opening);
        Assert.Equal(0, runtime.Statistics.StreamBufferBytes);
        Assert.True(decoder.IsDisposed);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static bool WaitForBlock(BufferedAudioStream stream, long frame)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        do
        {
            if (stream.TryAcquireBlock(frame, out var lease))
            {
                lease.Dispose();
                return true;
            }
            Thread.Sleep(1);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private sealed class TestDecoder(long frameCount, int channels, Func<long, int, float> sample, int sampleRate) : IAudioStreamDecoder
    {
        private long _position;
        private int _disposed;
        private int _gateNextRead;

        public int SampleRate => sampleRate;
        public int Channels => channels;
        public long FrameCount => frameCount;
        public long GateFrame { private get; init; } = -1;
        public ManualResetEventSlim BlockFourReadStarted { get; } = new();
        public ManualResetEventSlim AllowBlockFourRead { get; } = new();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public long Seek(long frame)
        {
            _position = frame;
            if (frame == GateFrame) Volatile.Write(ref _gateNextRead, 1);
            return frame;
        }

        public long ReadFrames(float[] destination, int sampleOffset, long requestedFrames)
        {
            if (Interlocked.Exchange(ref _gateNextRead, 0) != 0)
            {
                BlockFourReadStarted.Set();
                AllowBlockFourRead.Wait();
            }
            var frames = Math.Min(requestedFrames, FrameCount - _position);
            for (var frame = 0L; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                destination[sampleOffset + checked((int)frame) * channels + channel] = sample(_position + frame, channel);
            _position += frames;
            return frames;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class FakeSdl : ISdlAudio
    {
        public bool InitAudio() => true;
        public uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec) => 1;
        public string GetError() => "fake SDL error";
        public void CloseAudioDevice(uint device) { }
        public void QuitAudio() { }
    }
}
