namespace Blitz3DCS.Engine.Windows;

internal interface IMovieVideo : IDisposable
{
    int Width { get; }
    int Height { get; }
    long Duration100ns { get; }
    byte[] RgbaFrame { get; }
    long FrameTimestamp100ns { get; }
    bool DecodeAt(long targetTime100ns);
    void SeekToStart();
}

internal sealed class MoviePlayback(IMovieVideo video, Func<long> clock100ns) : IDisposable
{
    private long _started;
    private long _position;
    private long _loopIndex;
    internal IMovieVideo Video { get; } = video;
    internal bool Playing { get; private set; }
    internal bool Looping { get; set; }
    internal int Image { get; set; }

    internal void Play()
    {
        if (Playing) return;
        _started = clock100ns();
        Playing = true;
    }

    internal void Stop()
    {
        if (!Playing) return;
        _position += Math.Max(0, clock100ns() - _started);
        Playing = false;
    }

    internal bool Update()
    {
        var position = _position + (Playing ? Math.Max(0, clock100ns() - _started) : 0);
        var duration = Video.Duration100ns;
        if (duration > 0)
        {
            if (Looping)
            {
                var loop = position / duration;
                if (loop != _loopIndex)
                {
                    Video.SeekToStart();
                    _loopIndex = loop;
                }
                position %= duration;
            }
            else if (position >= duration)
            {
                position = duration - 1;
                _position = position;
                Playing = false;
            }
        }
        return Video.DecodeAt(position);
    }

    public void Dispose() => Video.Dispose();
}
