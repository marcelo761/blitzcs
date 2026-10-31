using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static MoviePlayback? _movie;
    internal static Func<string, IMovieVideo> MovieVideoFactory = path => new MediaFoundationVideo(path);
    internal static Func<long> MovieClock100ns = () => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;

    public static int BlitzMovie_Open(string file)
    {
        CheckThreadIfStarted();
        BlitzMovie_Close();
        try
        {
            _movie = new MoviePlayback(MovieVideoFactory(file.Replace('\\', Path.DirectorySeparatorChar)), MovieClock100ns);
            return 1;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (COMException) { return 0; }
    }

    public static void BlitzMovie_Close()
    {
        CheckThreadIfStarted();
        _movie?.Dispose();
        _movie = null;
    }

    public static int BlitzMovie_GetWidth() => _movie?.Video.Width ?? 0;
    public static int BlitzMovie_GetHeight() => _movie?.Video.Height ?? 0;

    public static int BlitzMovie_OpenDecodeToImage(string file, int image, int looping = 0)
    {
        var target = GetImage(image);
        if (BlitzMovie_Open(file) == 0) return 0;
        if (target.Width != _movie!.Video.Width || target.Height != _movie.Video.Height)
        {
            BlitzMovie_Close();
            throw new ArgumentException("The movie image must match the video dimensions.", nameof(image));
        }
        _movie.Image = image;
        _movie.Looping = looping != 0;
        UpdateMovieImage(image);
        return 1;
    }

    public static int BlitzMovie_Play()
    {
        CheckThreadIfStarted();
        if (_movie is null) return 0;
        _movie.Play();
        return 1;
    }

    public static int BlitzMovie_Stop()
    {
        CheckThreadIfStarted();
        if (_movie is null) return 0;
        _movie.Stop();
        return 1;
    }

    private static void UpdateMovieImage(int image)
    {
        if (_movie is null || _movie.Image != image || !_movie.Update()) return;
        CheckThreadIfStarted();
        var target = GetImage(image);
        EnsureTextureUnlocked(GetTexture(target.Texture));
        var video = _movie.Video;
        if (target.Width != video.Width || target.Height != video.Height)
            throw new InvalidOperationException("The movie target image dimensions changed during playback.");
        var texture = GetTexture(target.Texture);
        RequireRenderer().UpdateTexturePixels(texture.Frames[0].Resource, 0, 0,
            video.Width, video.Height, video.RgbaFrame);
    }
}
