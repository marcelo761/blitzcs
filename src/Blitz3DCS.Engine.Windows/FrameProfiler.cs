using System.Diagnostics;
using System.Globalization;
using System.Text;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

/// <summary>
/// Pure aggregation core of the frame profiler: per-phase tick accumulators, a ring of the last
/// <c>window</c> frames for mean/p95/max, and a one-line report. Allocation-free except when reporting.
/// Definitions (when a frame phase index is supplied): 'frame' = Flip-to-Flip wall time (previous Flip's end to this
/// Flip's end, including game/BASIC logic, waiting and vsync); 'engine' = sum of the top-level adapter phases
/// (UpdateWorld, RenderWorld, 2D, CopyRect, present; nested phases such as anim/skinning/submit are not added twice);
/// 'outside' = frame - engine (game logic outside the adapter plus waiting).
/// A frame whose 'frame' time exceeds the hitch threshold additionally emits one immediate [hitch] line.
/// </summary>
internal sealed class FrameProfiler
{
    private readonly string[] _phaseNames;
    private readonly string[] _counterNames;
    private readonly int _window;
    private readonly double _msPerTick;
    private readonly Action<string> _sink;
    private readonly long[] _current;
    private readonly long[] _currentCounters;
    private readonly long[][] _samples;      // [phase][frame]
    private readonly long[] _counterTotals;
    private readonly long[] _allocSamples;
    private readonly long[] _scratch;
    private readonly int _frameIndex;
    private readonly int[] _engineIndexes;
    private readonly long _hitchTicks;
    private readonly Func<string>? _hitchExtra;
    private readonly long[] _engineSamples;
    private readonly long[] _outsideSamples;
    private int _frames;
    private long _gcGen0, _gcGen1, _gcGen2;
    private long _totalFrames;

    internal FrameProfiler(string[] phaseNames, string[] counterNames, int window, long ticksPerSecond, Action<string> sink,
        int frameIndex = -1, int[]? engineIndexes = null, double hitchMs = 0, Func<string>? hitchExtra = null)
    {
        _frameIndex = frameIndex;
        _engineIndexes = engineIndexes ?? [];
        _hitchTicks = hitchMs > 0 ? (long)(hitchMs * ticksPerSecond / 1000.0) : 0;
        _hitchExtra = hitchExtra;
        _engineSamples = new long[window];
        _outsideSamples = new long[window];
        _phaseNames = phaseNames;
        _counterNames = counterNames;
        _window = window;
        _msPerTick = 1000.0 / ticksPerSecond;
        _sink = sink;
        _current = new long[phaseNames.Length];
        _currentCounters = new long[counterNames.Length];
        _samples = new long[phaseNames.Length][];
        for (var i = 0; i < _samples.Length; i++) _samples[i] = new long[window];
        _counterTotals = new long[counterNames.Length];
        _allocSamples = new long[window];
        _scratch = new long[window];
    }

    internal int FramesInWindow => _frames;
    internal long TotalFrames => _totalFrames;

    internal void AddTicks(int phase, long ticks) => _current[phase] += ticks;
    internal void AddCount(int counter, long amount = 1) => _currentCounters[counter] += amount;

    /// <summary>Closes the current frame; emits a report line when the window is full.</summary>
    internal void EndFrame(long allocatedBytes, int gen0, int gen1, int gen2)
    {
        long engine = 0, outside = 0;
        if (_frameIndex >= 0)
        {
            foreach (var index in _engineIndexes) engine += _current[index];
            outside = Math.Max(0, _current[_frameIndex] - engine);
            _engineSamples[_frames] = engine;
            _outsideSamples[_frames] = outside;
            if (_hitchTicks > 0 && _current[_frameIndex] > _hitchTicks) _sink(FormatHitch(engine, outside, allocatedBytes, gen0, gen1, gen2));
        }
        for (var i = 0; i < _current.Length; i++) { _samples[i][_frames] = _current[i]; _current[i] = 0; }
        for (var i = 0; i < _currentCounters.Length; i++) { _counterTotals[i] += _currentCounters[i]; _currentCounters[i] = 0; }
        _allocSamples[_frames] = allocatedBytes;
        _gcGen0 += gen0; _gcGen1 += gen1; _gcGen2 += gen2;
        _frames++;
        _totalFrames++;
        if (_frames == _window) Report();
    }

    /// <summary>Emits a line for the frames collected so far (used at exit) and starts a new window.</summary>
    internal void Flush()
    {
        if (_frames > 0) Report();
    }

    /// <summary>Formats the immediate hitch line for the frame currently being closed (before its accumulators reset).</summary>
    internal string FormatHitch(long engine, long outside, long allocatedBytes, int gen0, int gen1, int gen2)
    {
        var text = new StringBuilder(384);
        text.Append(CultureInfo.InvariantCulture,
            $"[hitch] frame#{_totalFrames + 1} frame={_current[_frameIndex] * _msPerTick:F1}ms engine={engine * _msPerTick:F1}ms outside={outside * _msPerTick:F1}ms");
        for (var p = 0; p < _phaseNames.Length; p++)
            if (p != _frameIndex && _current[p] != 0) text.Append(CultureInfo.InvariantCulture, $" {_phaseNames[p]}={_current[p] * _msPerTick:F1}");
        text.Append(CultureInfo.InvariantCulture, $" allocB={allocatedBytes} gc0/1/2={gen0}/{gen1}/{gen2}");
        if (_hitchExtra is not null) text.Append(' ').Append(_hitchExtra());
        return text.ToString();
    }

    private static long Percentile95(long[] values, long[] scratch, int n)
    {
        Array.Copy(values, scratch, n);
        Array.Sort(scratch, 0, n);
        return scratch[Math.Min(n - 1, (int)Math.Ceiling(n * 0.95) - 1)];
    }

    private void Report()
    {
        var n = _frames;
        var text = new StringBuilder(512);
        text.Append(CultureInfo.InvariantCulture, $"[profile] frames={_totalFrames} window={n}");
        if (_frameIndex >= 0)
        {
            long es = 0, em = 0, os = 0, om = 0;
            for (var i = 0; i < n; i++) { es += _engineSamples[i]; em = Math.Max(em, _engineSamples[i]); os += _outsideSamples[i]; om = Math.Max(om, _outsideSamples[i]); }
            text.Append(CultureInfo.InvariantCulture,
                $" [frame=Flip-to-Flip wall time; engine=sum of adapter phases UpdateWorld+RenderWorld+2D+CopyRect+present; outside=frame-engine (game logic+waiting)] engine={es * _msPerTick / n:F3}/{Percentile95(_engineSamples, _scratch, n) * _msPerTick:F3}/{em * _msPerTick:F3}ms outside={os * _msPerTick / n:F3}/{Percentile95(_outsideSamples, _scratch, n) * _msPerTick:F3}/{om * _msPerTick:F3}ms");
        }
        for (var p = 0; p < _phaseNames.Length; p++)
        {
            long sum = 0, max = 0;
            for (var i = 0; i < n; i++) { var v = _samples[p][i]; sum += v; _scratch[i] = v; if (v > max) max = v; }
            Array.Sort(_scratch, 0, n);
            var p95 = _scratch[Math.Min(n - 1, (int)Math.Ceiling(n * 0.95) - 1)];
            text.Append(CultureInfo.InvariantCulture,
                $" {_phaseNames[p]}={sum * _msPerTick / n:F3}/{p95 * _msPerTick:F3}/{max * _msPerTick:F3}ms");
        }
        for (var c = 0; c < _counterNames.Length; c++)
            text.Append(CultureInfo.InvariantCulture, $" {_counterNames[c]}={(double)_counterTotals[c] / n:F1}");
        long alloc = 0, allocMax = 0;
        for (var i = 0; i < n; i++) { alloc += _allocSamples[i]; allocMax = Math.Max(allocMax, _allocSamples[i]); }
        text.Append(CultureInfo.InvariantCulture, $" allocB={alloc / n}/{allocMax} gc0/1/2={_gcGen0}/{_gcGen1}/{_gcGen2}");
        _sink(text.ToString());
        _frames = 0;
        Array.Clear(_counterTotals);
        _gcGen0 = _gcGen1 = _gcGen2 = 0;
    }
}

/// <summary>
/// Env-gated profiler front end. BLITZCS_ENGINE_PROFILE=1 enables; BLITZCS_ENGINE_PROFILE_LOG=path writes there
/// (no file otherwise: stderr). When disabled the static readonly flag lets the JIT drop every call site.
/// </summary>
internal static class EngineProfile
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE") == "1";

    internal const int UpdateWorld = 0, UpdateAnimation = 1, UpdateCollision = 2, UpdateScene = 3, RenderWorld = 4,
        Skinning = 5, Traversal = 6, Submit = 7, Draw2D = 8, Copy = 9, Present = 10, Frame = 11,
        MipGen = 12, DynUpload = 13, TexUpload = 14, Readback = 15;
    internal static readonly string[] PhaseNames =
    [
        "UpdateWorld", "anim", "collision", "sceneUpdate", "RenderWorld", "skinning", "traversal+sort", "submit(incl.gpu-upload)",
        "2D", "CopyRect", "present", "frame", "mipgen", "dynVB", "texUpload", "readback"
    ];
    internal const int DrawCalls = 0, Tris = 1, BlitDraws = 2, ClearStates = 3, CbMaps = 4, Resolves = 5, ReadbackCount = 6, MipCount = 7, DynCount = 8, TexCount = 9;
    internal static readonly string[] CounterNames =
        ["draws", "tris", "blitDraws", "pipelineResets", "cbMaps", "resolves", "READBACKS", "mips", "dynUploads", "texUploads"];

    internal static void AddTicks(int phase, long ticks)
    {
        if (!Enabled) return;
        var p = Get();
        if (_firstCall == 0) _firstCall = Stopwatch.GetTimestamp() - ticks;
        p.AddTicks(phase, ticks);
    }

    private static FrameProfiler? _profiler;
    private static StreamWriter? _log;
    private static long _lastAlloc;
    private static int _g0, _g1, _g2;
    private static long _frameStart;

    internal static readonly double HitchMs = ParseHitchMs(Environment.GetEnvironmentVariable("BLITZCS_ENGINE_HITCH_MS"));
    internal static readonly int[] EnginePhases = [UpdateWorld, RenderWorld, Draw2D, Copy, Present];

    /// <summary>Parses BLITZCS_ENGINE_HITCH_MS: positive number of milliseconds, otherwise the 50 ms default.</summary>
    internal static double ParseHitchMs(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 50;

    private static long _lastFlipEnd, _firstCall;

    internal static long Now() => Stopwatch.GetTimestamp();

    internal static void Add(int phase, long start)
    {
        if (!Enabled) return;
        var p = Get();
        if (_firstCall == 0) _firstCall = start;
        p.AddTicks(phase, Stopwatch.GetTimestamp() - start);
    }

    private static string HitchExtra(long now)
    {
        var gap = _firstCall == 0 || _lastFlipEnd == 0 ? 0 : _firstCall - _lastFlipEnd;
        return string.Create(CultureInfo.InvariantCulture,
            $"preFirstCall={gap * 1000.0 / Stopwatch.Frequency:F1}ms calls[{TraceCall.FormatCounts(_hitchCounts)}]");
    }
    private static long[] _hitchCounts = new long[TraceCall.Names.Length];

    private static FrameProfiler Get()
    {
        if (_profiler is not null) return _profiler;
        var path = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE_LOG");
        if (!string.IsNullOrEmpty(path)) _log = new StreamWriter(path, append: true) { AutoFlush = true };
        _profiler = new FrameProfiler(PhaseNames, CounterNames, 120, Stopwatch.Frequency,
            line => { if (_log is not null) _log.WriteLine(line); else Console.Error.WriteLine(line); },
            Frame, EnginePhases, HitchMs, () => HitchExtra(0));
        _lastAlloc = GC.GetAllocatedBytesForCurrentThread();
        _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
        _frameStart = Stopwatch.GetTimestamp();
        _lastFlipEnd = _frameStart;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        return _profiler;
    }

    /// <summary>Call after Present: folds renderer counters in and closes the frame.</summary>
    internal static void EndFrame()
    {
        if (!Enabled) return;
        var p = Get();
        var v = D3D11FrameCounters.Values;
        p.AddTicks(MipGen, v[D3D11FrameCounters.MipTicks]);
        p.AddTicks(DynUpload, v[D3D11FrameCounters.DynamicUploadTicks]);
        p.AddTicks(TexUpload, v[D3D11FrameCounters.TextureUploadTicks]);
        p.AddTicks(Readback, v[D3D11FrameCounters.ReadbackTicks]);
        p.AddCount(DrawCalls, v[D3D11FrameCounters.MeshDraws]);
        p.AddCount(Tris, v[D3D11FrameCounters.Triangles]);
        p.AddCount(BlitDraws, v[D3D11FrameCounters.BlitDraws]);
        p.AddCount(ClearStates, v[D3D11FrameCounters.ClearPipelineStates]);
        p.AddCount(CbMaps, v[D3D11FrameCounters.ConstantBufferMaps]);
        p.AddCount(Resolves, v[D3D11FrameCounters.Resolves]);
        p.AddCount(ReadbackCount, v[D3D11FrameCounters.Readbacks]);
        p.AddCount(MipCount, v[D3D11FrameCounters.MipGenerations]);
        p.AddCount(DynCount, v[D3D11FrameCounters.DynamicVertexUploads]);
        p.AddCount(TexCount, v[D3D11FrameCounters.TextureUploads]);
        Array.Clear(v);
        var now = Stopwatch.GetTimestamp();
        p.AddTicks(Frame, now - _frameStart);
        _frameStart = now;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
        _hitchCounts = EngineTrace.TakeCounts();
        p.EndFrame(alloc - _lastAlloc, c0 - _g0, c1 - _g1, c2 - _g2);
        _lastFlipEnd = now;
        _firstCall = 0;
        _lastAlloc = GC.GetAllocatedBytesForCurrentThread();
        _g0 = c0; _g1 = c1; _g2 = c2;
    }

    internal static void Flush()
    {
        if (!Enabled || _profiler is null) return;
        _profiler.Flush();
        _log?.Flush();
    }
}
