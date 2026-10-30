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
    public const int MaxLines = 200_000;

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

    public static string Describe(string call, int id, string kind, string? source, int parent, bool visible) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{call} id={id} kind={kind}{(source is null ? "" : $" src={Path.GetFileName(source)}")} visible={(visible ? 1 : 0)} parent={parent}");

    public static void Created(string call, int id, string kind, string? source, int parent, bool animated = false, bool visible = true)
    {
        if (!Entities) return;
        if (id != 0) lock (Gate) Registry[id] = new Tracked(kind, source, animated || kind == "animated");
        Log(Describe(call, id, kind, source, parent, visible));
    }

    public static void Copied(int sourceId, int id, int parent)
    {
        if (!Entities) return;
        Tracked? src;
        lock (Gate) Registry.TryGetValue(sourceId, out src);
        var kind = src?.Kind ?? "unknown";
        if (id != 0) lock (Gate) Registry[id] = new Tracked(kind, src?.Source, src?.Animated ?? false);
        Log(string.Create(CultureInfo.InvariantCulture, $"CopyEntity src={sourceId} {Describe("->", id, kind, src?.Source, parent, true)}"));
    }

    public static void Freed(int id)
    {
        if (!Entities) return;
        lock (Gate) Registry.Remove(id);
        Log(string.Create(CultureInfo.InvariantCulture, $"FreeEntity id={id}"));
    }

    public static void Detail(string call, int id, string detail)
    {
        if (!Entities) return;
        Log(string.Create(CultureInfo.InvariantCulture, $"{call} id={id} {detail}"));
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
        bool inFrustum, int seq, float time) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[entities] id={id} src={(source is null ? "?" : Path.GetFileName(source))} visible={(visible ? 1 : 0)} enabled={(enabled ? 1 : 0)} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) dist={distance:F1} inFrustum={(inFrustum ? 1 : 0)} seq={seq} time={time:F2}");
}

/// <summary>Per-sample state for BLITZCS_TRACE_COLLISION: velocity as position delta, lowest floor hit so far.</summary>
public sealed class CollisionSampler
{
    private Vector3 _previous;
    private bool _hasPrevious;
    private float _lowestFloor = float.PositiveInfinity;
    private const float FloorNormalY = 0.5f;

    public string Sample(SceneGraph scene, int entity)
    {
        var pos = new Vector3(scene.EntityX(entity, true), scene.EntityY(entity, true), scene.EntityZ(entity, true));
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
        var delta = _hasPrevious ? pos - _previous : Vector3.Zero;
        _previous = pos; _hasPrevious = true;
        return Format(entity, pos, delta, count, byType, lastNormal, float.IsPositiveInfinity(_lowestFloor) ? null : _lowestFloor);
    }

    public static string Format(int entity, Vector3 pos, Vector3 delta, int count, IReadOnlyDictionary<int, int> byType,
        Vector3? lastNormal, float? lowestFloorY)
    {
        var rules = string.Join(",", byType.Select(p => string.Create(CultureInfo.InvariantCulture, $"type{p.Key}={p.Value}")));
        var normal = lastNormal is { } n ? string.Create(CultureInfo.InvariantCulture, $"({n.X:F2},{n.Y:F2},{n.Z:F2})") : "none";
        var floor = lowestFloorY is { } f ? f.ToString("F2", CultureInfo.InvariantCulture) : "none";
        var below = lowestFloorY is { } lf && pos.Y < lf ? 1 : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"[collision] entity={entity} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) delta=({delta.X:F3},{delta.Y:F3},{delta.Z:F3}) collisions={count} [{rules}] lastNormal={normal} lowestFloorY={floor} belowFloor={below}");
    }
}
