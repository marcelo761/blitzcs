namespace Blitz3DCS.Engine;

/// <summary>
/// Runtime switches for CPU optimizations, read once at process start. BLITZCS_ENGINE_NOOPT=1 restores every original
/// code path; BLITZCS_ENGINE_NO=name[,name...] (case-insensitive) restores only the named ones. Names: broadphase,
/// setterguard, incrselection, collgeom, collstate, skinfuse, skincull, traversal, drawsort.
/// </summary>
public static class EngineOptimizations
{
    /// <summary>All switchable optimization names.</summary>
    public static readonly string[] Names =
        ["broadphase", "setterguard", "incrselection", "collgeom", "collstate", "skinfuse", "skincull", "traversal", "drawsort"];

    /// <summary>True when BLITZCS_ENGINE_NOOPT=1 was set at process start (read once).</summary>
    public static readonly bool Disabled = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_NOOPT") == "1";

    private static List<string> _unknown = [];
    private static readonly HashSet<string> Off = Load();

    /// <summary>Swept-volume broad rejection in SceneCollision.TryHit.</summary>
    public static readonly bool Broadphase = Active("broadphase");
    /// <summary>No-op position/scale/rotation/pose setter guards.</summary>
    public static readonly bool SetterGuard = Active("setterguard");
    /// <summary>Incremental polygon-collision selection and idempotent selection-version setters.</summary>
    public static readonly bool IncrSelection = Active("incrselection");
    /// <summary>Per-geometry collision-prep tracking.</summary>
    public static readonly bool CollGeom = Active("collgeom");
    /// <summary>Per-node collision state cache, persistent lists and per-source rule lists in UpdateWorld.</summary>
    public static readonly bool CollState = Active("collstate");
    /// <summary>Fused skinning, vertex dedup and skip-unchanged.</summary>
    public static readonly bool SkinFuse = Active("skinfuse");
    /// <summary>Cull-before-skin.</summary>
    public static readonly bool SkinCull = Active("skincull");
    /// <summary>Root-list traversal with inline world transforms.</summary>
    public static readonly bool Traversal = Active("traversal");
    /// <summary>Index-sorted draw queue.</summary>
    public static readonly bool DrawSort = Active("drawsort");

    static EngineOptimizations()
    {
        if (Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE") != "1") return;
        var text = string.Join(' ', Names.Select(n => $"{n}={(Active(n) ? "on" : "off")}"));
        var env = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_NO");
        Console.Error.WriteLine($"[engine-opt] {text}{(Disabled ? " (BLITZCS_ENGINE_NOOPT=1)" : "")}{(string.IsNullOrEmpty(env) ? "" : $" (BLITZCS_ENGINE_NO={env})")}");
        if (_unknown.Count != 0) Console.Error.WriteLine($"[engine-opt] unknown names ignored: {string.Join(',', _unknown)}");
    }

    private static bool Active(string name) => !Disabled && !Off.Contains(name);

    private static HashSet<string> Load() => Parse(Environment.GetEnvironmentVariable("BLITZCS_ENGINE_NO"), out _unknown);

    /// <summary>Parses a comma-separated list of optimization names (case-insensitive, whitespace trimmed); unknown names are reported.</summary>
    public static HashSet<string> Parse(string? value, out List<string> unknownNames)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        unknownNames = [];
        if (string.IsNullOrWhiteSpace(value)) return known;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.ToLowerInvariant();
            if (Array.IndexOf(Names, name) >= 0) known.Add(name);
            else unknownNames.Add(part);
        }
        return known;
    }
}
