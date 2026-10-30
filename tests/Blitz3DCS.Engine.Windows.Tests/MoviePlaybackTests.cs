using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class MoviePlaybackTests
{
    [Fact]
    public void Movie_clock_pauses_resumes_loops_and_disposes_decoder()
    {
        long clock = 0;
        var decoder = new FakeVideo();
        var movie = new MoviePlayback(decoder, () => clock) { Looping = true };
        movie.Play();
        clock = 40;
        movie.Update();
        Assert.Equal(40, decoder.RequestedTime);
        movie.Stop();
        clock = 80;
        movie.Update();
        Assert.Equal(40, decoder.RequestedTime);
        movie.Play();
        clock = 150;
        movie.Update();
        Assert.Equal(10, decoder.RequestedTime);
        Assert.Equal(1, decoder.Restarts);
        movie.Dispose();
        Assert.True(decoder.Disposed);
    }

    [Fact]
    public void Nonlooping_movie_stops_on_last_frame_at_end()
    {
        long clock = 0;
        var decoder = new FakeVideo();
        using var movie = new MoviePlayback(decoder, () => clock);
        movie.Play();
        clock = 200;
        movie.Update();
        Assert.False(movie.Playing);
        Assert.Equal(99, decoder.RequestedTime);
        clock = 400;
        movie.Update();
        Assert.Equal(99, decoder.RequestedTime);
    }

    private sealed class FakeVideo : IMovieVideo
    {
        public int Width => 1;
        public int Height => 1;
        public long Duration100ns => 100;
        public byte[] RgbaFrame { get; } = [0, 0, 0, 255];
        public long FrameTimestamp100ns => RequestedTime;
        public long RequestedTime { get; private set; }
        public int Restarts { get; private set; }
        public bool Disposed { get; private set; }
        public bool DecodeAt(long time) { RequestedTime = time; return true; }
        public void SeekToStart() => Restarts++;
        public void Dispose() => Disposed = true;
    }
}
