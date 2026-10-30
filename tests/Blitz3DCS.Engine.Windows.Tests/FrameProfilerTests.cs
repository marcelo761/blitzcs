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
}
