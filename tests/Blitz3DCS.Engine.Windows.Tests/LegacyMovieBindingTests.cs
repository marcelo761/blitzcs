using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyMovieBindingTests
{
    [Fact]
    public void Movie_bindings_open_metadata_play_stop_and_release_decoder()
    {
        var factory = LegacyGraphics.MovieVideoFactory;
        var video = new TestVideo();
        try
        {
            LegacyGraphics.MovieVideoFactory = _ => video;
            Assert.Equal(1, LegacyGraphics.BlitzMovie_Open("fixture.avi"));
            Assert.Equal(2, LegacyGraphics.BlitzMovie_GetWidth());
            Assert.Equal(1, LegacyGraphics.BlitzMovie_GetHeight());
            Assert.Equal(1, LegacyGraphics.BlitzMovie_Play());
            Assert.Equal(1, LegacyGraphics.BlitzMovie_Stop());
            LegacyGraphics.BlitzMovie_Close();
            Assert.True(video.Disposed);
            Assert.Equal(0, LegacyGraphics.BlitzMovie_Play());
            Assert.Equal(0, LegacyGraphics.BlitzMovie_GetWidth());
        }
        finally { LegacyGraphics.BlitzMovie_Close(); LegacyGraphics.MovieVideoFactory = factory; }
    }

    internal static void RunSmoke()
    {
        var factory = LegacyGraphics.MovieVideoFactory;
        var clock = LegacyGraphics.MovieClock100ns;
        long time = 0;
        var video = new TestVideo();
        LegacyGraphics.ResetForTests();
        try
        {
            LegacyGraphics.MovieVideoFactory = _ => video;
            LegacyGraphics.MovieClock100ns = () => time;
            LegacyGraphics.Graphics3D(64, 48);
            var image = LegacyGraphics.CreateImage(2, 1);
            Assert.Equal(1, LegacyGraphics.BlitzMovie_OpenDecodeToImage("fixture.avi", image));
            LegacyGraphics.BlitzMovie_Play();
            LegacyGraphics.CopyRect(0, 0, 2, 1, 10, 10, LegacyGraphics.ImageBuffer(image), LegacyGraphics.BackBuffer());
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(10, 10).R);
            time = 20;
            LegacyGraphics.CopyRect(0, 0, 2, 1, 10, 10, LegacyGraphics.ImageBuffer(image), LegacyGraphics.BackBuffer());
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(10, 10).G);
            LegacyGraphics.FreeImage(image);
            Assert.True(video.Disposed);
            Assert.Equal(0, LegacyGraphics.BlitzMovie_Play());
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyGraphics.MovieVideoFactory = factory;
            LegacyGraphics.MovieClock100ns = clock;
        }
    }

    private sealed class TestVideo : IMovieVideo
    {
        public int Width => 2;
        public int Height => 1;
        public long Duration100ns => 100;
        public byte[] RgbaFrame { get; } = new byte[8];
        public long FrameTimestamp100ns { get; private set; } = -1;
        public bool Disposed { get; private set; }
        public bool DecodeAt(long time)
        {
            if (FrameTimestamp100ns == time) return false;
            FrameTimestamp100ns = time;
            for (var offset = 0; offset < 8; offset += 4)
            {
                RgbaFrame[offset] = time < 10 ? (byte)255 : (byte)0;
                RgbaFrame[offset + 1] = time < 10 ? (byte)0 : (byte)255;
                RgbaFrame[offset + 3] = 255;
            }
            return true;
        }
        public void SeekToStart() => FrameTimestamp100ns = -1;
        public void Dispose() => Disposed = true;
    }
}
