using System.Diagnostics;

namespace Blitz3DCS.Engine;

/// <summary>
/// Env-gated fine-grained engine counters (BLITZCS_ENGINE_PROFILE=1, same gate as the frame profiler). The static
/// readonly flag lets the JIT drop every call site when disabled; the arrays are empty then, so nothing is allocated.
/// Counters are per-frame sums; tick slots are Stopwatch ticks. The profiler drains both once per frame.
/// </summary>
internal static class EngineStats
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE") == "1";

    // Counter slots.
    internal const int ColSources = 0, ColPairs = 1, ColPairsRejType = 2, ColPairsRejBound = 3, ColBvhCands = 4, ColBvhNodes = 5,
        ColTriTests = 6, ColSphereTests = 7, ColBoxTests = 8, ColPlaneTests = 9, ColHits = 10, ColRespIters = 11, ColStill = 12,
        ColEnabled = 13, ColPrepSel = 14, ColPrepMeshes = 15, ColPrepRebuilt = 16,
        SceneNodes = 17, SceneVisited = 18, XformTotal = 19, XformDirty = 20, AnimActive = 21, AnimModels = 22;
    internal static readonly string[] CounterNames =
    [
        "colSources", "colPairs", "colPairsRejType", "colPairsRejBound", "colBvhCands", "colBvhNodes",
        "colTriTests", "colSphereTests", "colBoxTests", "colPlaneTests", "colHits", "colRespIters", "colStill",
        "colEnabled", "colPrepSel", "colPrepMeshes", "colPrepRebuilt",
        "sceneNodes", "sceneVisited", "xformTotal", "xformDirty", "animActive", "animModels"
    ];

    // Tick slots (printed as ms).
    internal const int ColBroad = 0, ColNarrow = 1, ColResp = 2;
    internal static readonly string[] TickNames = ["colBroad", "colNarrow", "colResp"];

    internal static readonly long[] Counts = Enabled ? new long[CounterNames.Length] : [];
    internal static readonly long[] Ticks = Enabled ? new long[TickNames.Length] : [];

    internal static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Add(int counter, long amount = 1)
    {
        if (!Enabled) return;
        Counts[counter] += amount;
    }

    internal static void AddTicks(int slot, long start)
    {
        if (!Enabled) return;
        Ticks[slot] += Stopwatch.GetTimestamp() - start;
    }

    internal static void AddRawTicks(int slot, long ticks)
    {
        if (!Enabled) return;
        Ticks[slot] += ticks;
    }
}
