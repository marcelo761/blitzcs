using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class FrameProfilerTests
{
    [Fact]
    public void AggregatesMeanP95MaxAndCountersPerWindow()
    {
        var lines = new List<string>();
        var profiler = new FrameProfiler(["a"], ["draws"], 4, 1000, lines.Add);
        long[] ticks = [10, 20, 30, 40]; // 1000 ticks/s: 10 ticks = 10 ms
        foreach (var t in ticks)
        {
            profiler.AddTicks(0, t);
            profiler.AddCount(0, 5);
            profiler.EndFrame(100, 1, 0, 0);
        }
        var line = Assert.Single(lines);
        Assert.Contains("a=25.000/40.000/40.000ms", line);
        Assert.Contains("draws=5.0", line);
        Assert.Contains("allocB=100/100", line);
        Assert.Contains("gc0/1/2=4/0/0", line);
        Assert.Equal(0, profiler.FramesInWindow);
    }

    [Fact]
    public void ReportsD3D11OptimizationCountersAsPerFrameAverages()
    {
        var lines = new List<string>();
        var profiler = new FrameProfiler(["a"], EngineProfile.CounterNames, 2, 1000, lines.Add);
        // Frame 1 and frame 2 counts; the printed value is the per-frame average over the window.
        long[][] perFrame =
        [
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 3, 0],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 5, 10],
        ];
        foreach (var frame in perFrame)
        {
            profiler.AddTicks(0, 10);
            for (var c = 0; c < frame.Length; c++) profiler.AddCount(c, frame[c]);
            profiler.EndFrame(0, 0, 0, 0);
        }
        var line = Assert.Single(lines);
        Assert.Contains(" skippedBinds=4.0", line);
        Assert.Contains(" blitBatched=4.0", line);
        Assert.Contains(" cbMapsSkipped=5.0", line);
    }

    [Fact]
    public void FlushReportsPartialWindowOnce()
    {
        var lines = new List<string>();
        var profiler = new FrameProfiler(["a"], [], 120, 1000, lines.Add);
        profiler.AddTicks(0, 8);
        profiler.EndFrame(0, 0, 0, 0);
        profiler.Flush();
        profiler.Flush();
        Assert.Single(lines);
    }

    [Fact]
    public void DisabledProfilerIsInertAndAllocationFree()
    {
        Assert.False(EngineProfile.Enabled); // test process runs without BLITZCS_ENGINE_PROFILE=1
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            EngineProfile.Add(EngineProfile.Present, EngineProfile.Now());
            EngineProfile.AddTicks(EngineProfile.Frame, 1);
            EngineProfile.EndFrame();
        }
        EngineProfile.Flush();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void FineCounterTablesAreAlignedWithTheirSlots()
    {
        Assert.Equal(EngineProfile.RenderBase + EngineProfile.RenderCounterNames.Length, EngineProfile.CounterNames.Length);
        Assert.Equal(EngineProfile.RenderBase, EngineProfile.StatsBase + EngineStats.CounterNames.Length);
        Assert.Equal("colSources", EngineProfile.CounterNames[EngineProfile.StatsBase + EngineStats.ColSources]);
        Assert.Equal("animModels", EngineProfile.CounterNames[EngineProfile.StatsBase + EngineStats.AnimModels]);
        Assert.Equal("skinModels", EngineProfile.CounterNames[EngineProfile.SkinModels]);
        Assert.Equal("rtLights", EngineProfile.CounterNames[EngineProfile.RtLightCount]);
        Assert.Equal(EngineProfile.RtState + 1, EngineProfile.PhaseNames.Length);
        Assert.Equal("colBroad", EngineProfile.PhaseNames[EngineProfile.ColBroad + EngineStats.ColBroad]);
        Assert.Equal("colResp", EngineProfile.PhaseNames[EngineProfile.ColBroad + EngineStats.ColResp]);
        Assert.Equal("rtState", EngineProfile.PhaseNames[EngineProfile.RtState]);
        Assert.Equal(EngineProfile.CounterNames.Length, EngineProfile.CounterNames.Distinct().Count());
    }

    [Fact]
    public void FoldsEngineStatsIntoTheFrameAndClearsThem()
    {
        var lines = new List<string>();
        var profiler = new FrameProfiler(EngineProfile.PhaseNames, EngineProfile.CounterNames, 2, 1000, lines.Add);
        long[] counts = new long[EngineStats.CounterNames.Length];
        long[] ticks = new long[EngineStats.TickNames.Length];
        for (var frame = 0; frame < 2; frame++)
        {
            counts[EngineStats.ColPairs] = frame == 0 ? 10 : 20;
            counts[EngineStats.XformDirty] = 4;
            ticks[EngineStats.ColNarrow] = 6;
            EngineProfile.FoldEngineStats(profiler, counts, ticks);
            Assert.All(counts, c => Assert.Equal(0, c));
            Assert.All(ticks, t => Assert.Equal(0, t));
            profiler.AddCount(EngineProfile.SkinVerts, 100);
            profiler.EndFrame(0, 0, 0, 0);
        }
        var line = Assert.Single(lines);
        Assert.Contains(" colPairs=15.0", line);
        Assert.Contains(" xformDirty=4.0", line);
        Assert.Contains(" skinVerts=100.0", line);
        Assert.Contains(" colNarrow=6.000/6.000/6.000ms", line);
    }

    [Fact]
    public void HitchLineCarriesNonzeroFineCounters()
    {
        var lines = new List<string>();
        var profiler = new FrameProfiler(EngineProfile.PhaseNames, EngineProfile.CounterNames, 4, 1000, lines.Add,
            EngineProfile.Frame, EngineProfile.EnginePhases, hitchMs: 10);
        profiler.AddTicks(EngineProfile.Frame, 50);
        profiler.AddCount(EngineProfile.StatsBase + EngineStats.ColHits, 3);
        profiler.EndFrame(0, 0, 0, 0);
        var line = Assert.Single(lines);
        Assert.StartsWith("[hitch]", line);
        Assert.Contains(" colHits=3", line);
    }

    [Fact]
    public void DisabledEngineStatsAreInertAndAllocationFree()
    {
        Assert.False(EngineStats.Enabled);
        Assert.Empty(EngineStats.Counts);
        Assert.Empty(EngineStats.Ticks);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            EngineStats.Add(EngineStats.ColPairs, 5);
            EngineStats.AddTicks(EngineStats.ColBroad, EngineStats.Now());
            EngineStats.AddRawTicks(EngineStats.ColResp, 3);
            EngineProfile.Count(EngineProfile.SkinVerts, 7);
            EngineProfile.AddSince(EngineProfile.RtSort, 0);
            EngineProfile.Emit("ignored");
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void CensusIsDueOnFirstWorldFrameAndEvery600th()
    {
        Assert.True(EngineProfile.CensusDue());
        for (var i = 1; i < 600; i++) Assert.False(EngineProfile.CensusDue());
        Assert.True(EngineProfile.CensusDue());
    }
}
