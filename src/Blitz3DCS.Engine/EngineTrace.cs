using System.Globalization;
using System.Numerics;

namespace Blitz3DCS.Engine;

/// <summary>
/// Line sink shared by the diagnostics: prefixes every line with a monotonically increasing sequence number and the
/// managed thread id, and stops writing after <c>maxLines</c> lines (emitting one notice when capped).
/// </summary>
public sealed class TraceWriter(Action<string> sink, int maxLines)
{
    private long _sequence;
    public long Sequence => Volatile.Read(ref _sequence);
    public bool Capped => Sequence > maxLines;

    /// <summary>Returns false when the line was dropped because the cap was reached.</summary>
    public bool Write(string text, int? threadId = null)
    {
        var seq = Interlocked.Increment(ref _sequence);
        if (seq > maxLines)
        {
            if (seq == maxLines + 1L)
                sink(string.Create(CultureInfo.InvariantCulture, $"[trace] output capped at {maxLines} lines; further lines are dropped"));
            return false;
        }
        sink(string.Create(CultureInfo.InvariantCulture, $"[trace #{seq} T{threadId ?? Environment.CurrentManagedThreadId}] {text}"));
        return true;
    }
}

/// <summary>Allows an action at most once per period (used for the once-per-second snapshots).</summary>
public sealed class PeriodicGate(long periodTicks)
{
    private long _next = long.MinValue;
    public bool Due(long nowTicks)
    {
        if (nowTicks < _next) return false;
        _next = nowTicks + periodTicks;
        return true;
    }
}

/// <summary>Kinds of engine calls counted for the hitch report (BLITZCS_ENGINE_PROFILE=1).</summary>
public static class TraceCall
{
    public const int LoadTexture = 0, LoadMesh = 1, LoadAnimMesh = 2, CreateTexture = 3, CopyEntity = 4, FreeEntity = 5, MeshBuild = 6, Other = 7;
    public static readonly string[] Names = ["LoadTexture", "LoadMesh", "LoadAnimMesh", "CreateTexture", "CopyEntity", "FreeEntity", "MeshBuild", "other"];

    public static string FormatCounts(long[] counts)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Names.Length && i < counts.Length; i++)
            if (counts[i] != 0) text.Append(CultureInfo.InvariantCulture, $"{(text.Length > 0 ? " " : "")}{Names[i]}={counts[i]}");
        return text.Length == 0 ? "none" : text.ToString();
    }
}

/// <summary>
/// Per-call-type volume control for the entity trace: keeps the first <c>keepFirst</c> lines of each call type, then one in
/// <c>every</c>; "always" lines (loaders, animated entities, exceptions) bypass it. Also counts seen/logged per window.
/// </summary>
public sealed class TraceSampler(int keepFirst = 50, int every = 100)
{
    private sealed class Counter { public long Total, WindowSeen, WindowLogged; }
    private readonly Dictionary<string, Counter> _counters = [];

    public bool ShouldLog(string call, bool always)
    {
        if (!_counters.TryGetValue(call, out var c)) _counters[call] = c = new Counter();
        c.Total++; c.WindowSeen++;
        var log = always || c.Total <= keepFirst || (c.Total - keepFirst) % every == 0;
        if (log) c.WindowLogged++;
        return log;
    }

    /// <summary>Returns the per-window "[trace-stats]" line (call=seen/logged) and resets the window; null when idle.</summary>
    public string? TakeStats()
    {
        var text = new System.Text.StringBuilder();
        foreach (var p in _counters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (p.Value.WindowSeen == 0) continue;
            text.Append(CultureInfo.InvariantCulture, $" {p.Key}={p.Value.WindowSeen}/{p.Value.WindowLogged}");
            p.Value.WindowSeen = 0; p.Value.WindowLogged = 0;
        }
        return text.Length == 0 ? null : "[trace-stats] calls seen/logged:" + text;
    }
}

/// <summary>
/// Chooses which entities the collision trace samples without game-specific names: entities with a nonzero collision type
/// that collided or moved since the previous sample, ranked by collision count then movement, capped per sample.
/// </summary>
public sealed class CollisionTraceSelector(int maxPerSample = 8)
{
    public readonly record struct Candidate(int Id, int Type, int Collisions, Vector3 Position);
    public readonly record struct Pick(int Id, int Type, int Collisions, Vector3 Position, Vector3 Delta);

    private const float MovedEpsilon = 0.0001f;
    private readonly Dictionary<int, Vector3> _previous = [];

    public List<Pick> Select(IEnumerable<Candidate> candidates)
    {
        var picks = new List<Pick>();
        var seen = new HashSet<int>();
        foreach (var c in candidates)
        {
            if (c.Type == 0) continue;
            seen.Add(c.Id);
            var delta = _previous.TryGetValue(c.Id, out var prev) ? c.Position - prev : Vector3.Zero;
            _previous[c.Id] = c.Position;
            if (c.Collisions > 0 || delta.LengthSquared() > MovedEpsilon * MovedEpsilon)
                picks.Add(new Pick(c.Id, c.Type, c.Collisions, c.Position, delta));
        }
        foreach (var id in _previous.Keys.Where(id => !seen.Contains(id)).ToList()) _previous.Remove(id);
        return picks.OrderByDescending(p => p.Collisions).ThenByDescending(p => p.Delta.LengthSquared()).ThenBy(p => p.Id)
            .Take(maxPerSample).ToList();
    }
}

/// <summary>
/// Opt-in entity/collision diagnostics. BLITZCS_TRACE_ENTITIES=1 logs entity-creating and entity-mutating calls plus a
/// once-per-second animated-entity snapshot; BLITZCS_TRACE_COLLISION=1 logs a once-per-second player collision sample;
/// BLITZCS_TRACE_LOG=path sends both to a file (stderr otherwise). BLITZCS_ENGINE_PROFILE=1 additionally counts call
/// kinds for the hitch report. All gates are static readonly so the JIT drops the call sites when disabled.
/// </summary>
public static class EngineTrace
{
    public static readonly bool Entities = Environment.GetEnvironmentVariable("BLITZCS_TRACE_ENTITIES") == "1";
    public static readonly bool Collision = Environment.GetEnvironmentVariable("BLITZCS_TRACE_COLLISION") == "1";
    public static readonly bool CountCalls = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE") == "1";
    /// <summary>True when the call wrappers must run (tracing or call counting).</summary>
    public static readonly bool Hooked = Entities || CountCalls;
    public const int DefaultMaxLines = 400_000;
    /// <summary>Line cap, configurable with BLITZCS_TRACE_MAX_LINES.</summary>
    public static readonly int MaxLines = ParseMaxLines(Environment.GetEnvironmentVariable("BLITZCS_TRACE_MAX_LINES"));
    public static int ParseMaxLines(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : DefaultMaxLines;
    private static readonly HashSet<string> AlwaysCalls = ["LoadMesh", "LoadAnimMesh", "LoadTexture", "CreateTexture", "CopyEntity"];
    private static readonly TraceSampler Sampler = new();

    private static readonly object Gate = new();
    private static TraceWriter? _writer;
    private static StreamWriter? _file;
    private static readonly Dictionary<int, Tracked> Registry = [];
    private static readonly long[] Counts = new long[TraceCall.Names.Length];
    [ThreadStatic] private static int _depth;

    private sealed class Tracked(string kind, string? source, bool animated)
    {
        public string Kind = kind; public string? Source = source; public bool Animated = animated;
        public bool HasPos; public float X, Y, Z;
    }

    /// <summary>Counts the call and returns true when it is the outermost traced call on this thread.</summary>
    public static bool Enter(int callKind)
    {
        if (CountCalls) Interlocked.Increment(ref Counts[callKind]);
        return _depth++ == 0;
    }
    public static void Exit() { if (_depth > 0) _depth--; }

    /// <summary>Copies and clears the per-kind call counters (hitch report).</summary>
    public static long[] TakeCounts()
    {
        var snapshot = new long[Counts.Length];
        for (var i = 0; i < Counts.Length; i++) snapshot[i] = Interlocked.Exchange(ref Counts[i], 0);
        return snapshot;
    }

    public static void Log(string text)
    {
        lock (Gate)
        {
            if (_writer is null)
            {
                var path = Environment.GetEnvironmentVariable("BLITZCS_TRACE_LOG");
                if (!string.IsNullOrEmpty(path)) _file = new StreamWriter(path, append: true) { AutoFlush = true };
                _writer = new TraceWriter(line => { if (_file is not null) _file.WriteLine(line); else Console.Error.WriteLine(line); }, MaxLines);
            }
            _writer.Write(text);
        }
    }

    /// <summary>Logs through the per-call-type sampler; <paramref name="always"/> bypasses it.</summary>
    private static void LogCall(string call, bool always, string text)
    {
        bool log;
        lock (Gate) log = Sampler.ShouldLog(call, always || AlwaysCalls.Contains(call));
        if (log) Log(text);
    }

    private static bool IsAnimated(int id)
    {
        lock (Gate) return Registry.TryGetValue(id, out var t) && t.Animated;
    }

    /// <summary>The "[trace-stats]" line for the window since the previous call, or null when nothing was seen.</summary>
    public static string? TakeStatsLine()
    {
        lock (Gate) return Sampler.TakeStats();
    }

    public static string Describe(string call, int id, string kind, string? source, int parent, bool visible) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{call} id={id} kind={kind}{(source is null ? "" : $" src={Path.GetFileName(source)}")} visible={(visible ? 1 : 0)} parent={parent}");

    public static void Created(string call, int id, string kind, string? source, int parent, bool animated = false, bool visible = true)
    {
        if (!Entities) return;
        var anim = animated || kind == "animated";
        if (id != 0) lock (Gate) Registry[id] = new Tracked(kind, source, anim);
        LogCall(call, anim, Describe(call, id, kind, source, parent, visible));
    }

    public static void Copied(int sourceId, int id, int parent)
    {
        if (!Entities) return;
        Tracked? src;
        lock (Gate) Registry.TryGetValue(sourceId, out src);
        var kind = src?.Kind ?? "unknown";
        if (id != 0) lock (Gate) Registry[id] = new Tracked(kind, src?.Source, src?.Animated ?? false);
        LogCall("CopyEntity", true, string.Create(CultureInfo.InvariantCulture, $"CopyEntity src={sourceId} {Describe("->", id, kind, src?.Source, parent, true)}"));
    }

    public static void Freed(int id)
    {
        if (!Entities) return;
        bool anim;
        lock (Gate) { anim = Registry.TryGetValue(id, out var t) && t.Animated; Registry.Remove(id); }
        LogCall("FreeEntity", anim, string.Create(CultureInfo.InvariantCulture, $"FreeEntity id={id}"));
    }

    public static void Detail(string call, int id, string detail)
    {
        if (!Entities) return;
        LogCall(call, IsAnimated(id), string.Create(CultureInfo.InvariantCulture, $"{call} id={id} {detail}"));
    }

    /// <summary>Logs PositionEntity only for tracked animated entities and only when the position changed.</summary>
    public static void Positioned(int id, float x, float y, float z)
    {
        if (!Entities) return;
        lock (Gate)
        {
            if (!Registry.TryGetValue(id, out var t) || !t.Animated) return;
            if (t.HasPos && t.X == x && t.Y == y && t.Z == z) return;
            t.HasPos = true; t.X = x; t.Y = y; t.Z = z;
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"PositionEntity id={id} pos=({x:F2},{y:F2},{z:F2})"));
    }

    /// <summary>Exception-filter helper: logs the failure and returns false so the exception keeps propagating untouched.</summary>
    public static bool Failed(string call, Exception ex, string args)
    {
        Log(string.Create(CultureInfo.InvariantCulture, $"EXCEPTION in {call}({args}): {ex.GetType().Name}: {ex.Message}"));
        return false;
    }

    public static int[] AnimatedIds()
    {
        lock (Gate) return Registry.Where(p => p.Value.Animated).Select(p => p.Key).ToArray();
    }

    public static string? SourceOf(int id)
    {
        lock (Gate) return Registry.TryGetValue(id, out var t) ? t.Source : null;
    }

    public static void Forget(int id) { lock (Gate) Registry.Remove(id); }

    public static string FormatAnimatedLine(int id, string? source, bool visible, bool enabled, Vector3 pos, float distance,
        bool? inFrustum, int seq, float time) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[entities] id={id} src={(source is null ? "?" : Path.GetFileName(source))} visible={(visible ? 1 : 0)} enabled={(enabled ? 1 : 0)} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) dist={(float.IsNaN(distance) ? "n/a" : distance.ToString("F1", CultureInfo.InvariantCulture))} inFrustum={(inFrustum is null ? "n/a" : inFrustum.Value ? "1" : "0")} seq={seq} time={time:F2}");

    /// <summary>Camera for the animated snapshot: the one used by the last RenderWorld while it still exists, else the fallback.</summary>
    public static int PickCamera(int lastRendered, bool lastRenderedExists, int fallback) =>
        lastRendered != 0 && lastRenderedExists ? lastRendered : fallback;

    /// <summary>World-space distance, or NaN (printed n/a) when no camera is known, never a misleading 0.</summary>
    public static float Distance(Vector3 entity, Vector3? camera) =>
        camera is { } c ? Vector3.Distance(entity, c) : float.NaN;
}

/// <summary>Per-entity state for BLITZCS_TRACE_COLLISION: lowest floor hit so far.</summary>
public sealed class CollisionSampler
{
    private float _lowestFloor = float.PositiveInfinity;
    private const float FloorNormalY = 0.5f;

    public string Sample(SceneGraph scene, CollisionTraceSelector.Pick pick)
    {
        var entity = pick.Id;
        var count = scene.CountCollisions(entity);
        var byType = new SortedDictionary<int, int>();
        Vector3? lastNormal = null;
        for (var i = 1; i <= count; i++)
        {
            int type;
            try { type = scene.GetEntityType(scene.CollisionEntity(entity, i)); } catch (Exception) { type = -1; }
            byType[type] = byType.GetValueOrDefault(type) + 1;
            lastNormal = new Vector3(scene.CollisionNX(entity, i), scene.CollisionNY(entity, i), scene.CollisionNZ(entity, i));
            if (lastNormal.Value.Y > FloorNormalY) _lowestFloor = Math.Min(_lowestFloor, scene.CollisionY(entity, i));
        }
        return Format(entity, pick.Position, pick.Delta, count, byType, lastNormal,
            float.IsPositiveInfinity(_lowestFloor) ? null : _lowestFloor, pick.Type);
    }

    public static string Format(int entity, Vector3 pos, Vector3 delta, int count, IReadOnlyDictionary<int, int> byType,
        Vector3? lastNormal, float? lowestFloorY, int entityType = -1)
    {
        var rules = string.Join(",", byType.Select(p => string.Create(CultureInfo.InvariantCulture, $"type{p.Key}={p.Value}")));
        var normal = lastNormal is { } n ? string.Create(CultureInfo.InvariantCulture, $"({n.X:F2},{n.Y:F2},{n.Z:F2})") : "none";
        var floor = lowestFloorY is { } f ? f.ToString("F2", CultureInfo.InvariantCulture) : "none";
        var belowBy = lowestFloorY is { } lf && pos.Y < lf ? lf - pos.Y : 0f;
        return string.Create(CultureInfo.InvariantCulture,
            $"[collision] entity={entity} type={entityType} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) delta=({delta.X:F3},{delta.Y:F3},{delta.Z:F3}) collisions={count} [{rules}] lastNormal={normal} lowestFloorY={floor} belowFloor={(belowBy > 0 ? 1 : 0)} belowBy={belowBy:F2}");
    }
}
