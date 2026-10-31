using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class TraceDiagnosticsTests
{
    // phases: 0 update, 1 render, 2 frame(wall); engine = update + render
    private static FrameProfiler Make(List<string> lines, double hitchMs, Func<string>? extra = null) =>
        new(["update", "render", "frame"], [], 4, 1000, lines.Add, 2, [0, 1], hitchMs, extra);

    private static void Frame(FrameProfiler p, long update, long render, long frame)
    {
        p.AddTicks(0, update); p.AddTicks(1, render); p.AddTicks(2, frame);
        p.EndFrame(10, 0, 0, 0);
    }

    [Fact]
    public void HitchLineOnlyAboveThresholdAndBreaksDownPhases()
    {
        var lines = new List<string>();
        var p = Make(lines, 50, () => "calls[LoadMesh=2]");
        Frame(p, 1, 1, 16);
        Frame(p, 1, 1, 50);          // equal to threshold: no hitch
        Assert.Empty(lines);
        Frame(p, 5, 7, 1013);
        var line = Assert.Single(lines);
        Assert.StartsWith("[hitch]", line);
        Assert.Contains("frame=1013.0ms engine=12.0ms outside=1001.0ms", line);
        Assert.Contains("update=5.0 render=7.0", line);
        Assert.Contains("calls[LoadMesh=2]", line);
    }

    [Fact]
    public void SummaryDefinesFrameEngineOutside()
    {
        var lines = new List<string>();
        var p = Make(lines, 0);
        for (var i = 0; i < 4; i++) Frame(p, 2, 3, 20);
        var line = Assert.Single(lines);
        Assert.Contains("frame=Flip-to-Flip wall time", line);
        Assert.Contains("engine=5.000/5.000/5.000ms", line);
        Assert.Contains("outside=15.000/15.000/15.000ms", line);
    }

    [Fact]
    public void HitchThresholdParsingDefaultsTo50()
    {
        Assert.Equal(50, EngineProfile.ParseHitchMs(null));
        Assert.Equal(50, EngineProfile.ParseHitchMs("abc"));
        Assert.Equal(50, EngineProfile.ParseHitchMs("-3"));
        Assert.Equal(120.5, EngineProfile.ParseHitchMs("120.5"));
    }

    [Fact]
    public void TracesAreGatedOffByDefault()
    {
        if (Environment.GetEnvironmentVariable("BLITZCS_TRACE_ENTITIES") == "1") return;
        Assert.False(EngineTrace.Entities);
        Assert.False(EngineTrace.Hooked == EngineTrace.CountCalls ? false : EngineTrace.Entities);
    }

    [Fact]
    public void WriterNumbersLinesAndCapsOnce()
    {
        var lines = new List<string>();
        var w = new TraceWriter(lines.Add, 2);
        Assert.True(w.Write("a", 7));
        Assert.True(w.Write("b", 7));
        Assert.False(w.Write("c", 7));
        Assert.False(w.Write("d", 7));
        Assert.Equal(["[trace #1 T7] a", "[trace #2 T7] b", "[trace] output capped at 2 lines; further lines are dropped"], lines);
        Assert.True(w.Capped);
    }

    [Fact]
    public void PeriodicGateFiresOncePerPeriod()
    {
        var gate = new PeriodicGate(100);
        Assert.True(gate.Due(5));
        Assert.False(gate.Due(50));
        Assert.True(gate.Due(105));
    }

    [Fact]
    public void FormatsEntityCollisionAndCallLines()
    {
        Assert.Equal("LoadAnimMesh id=5 kind=animated src=npc.b3d visible=1 parent=0",
            EngineTrace.Describe("LoadAnimMesh", 5, "animated", "GFX/npcs/npc.b3d", 0, true));
        var entity = EngineTrace.FormatAnimatedLine(5, "a/npc.b3d", true, false, new Vector3(1, 2, 3), 4.25f, true, 2, 1.5f);
        Assert.Equal("[entities] id=5 src=npc.b3d visible=1 enabled=0 pos=(1.0,2.0,3.0) dist=4.2 inFrustum=1 seq=2 time=1.50", entity);
        var collision = CollisionSampler.Format(9, new Vector3(0, -1, 0), new Vector3(0, -0.5f, 0), 2,
            new Dictionary<int, int> { [2] = 2 }, new Vector3(0, 1, 0), 0f);
        Assert.Contains("collisions=2 [type2=2] lastNormal=(0.00,1.00,0.00) lowestFloorY=0.00 belowFloor=1", collision);
        Assert.Equal("LoadMesh=2 FreeEntity=1", TraceCall.FormatCounts([0, 2, 0, 0, 0, 1, 0, 0]));
        Assert.Equal("none", TraceCall.FormatCounts(new long[8]));
    }

    [Fact]
    public void DistanceIsNotAvailableInsteadOfZeroWithoutCamera()
    {
        var d = EngineTrace.Distance(new Vector3(3, 4, 0), null);
        Assert.True(float.IsNaN(d));
        Assert.Equal(5f, EngineTrace.Distance(new Vector3(3, 4, 0), Vector3.Zero));
        var line = EngineTrace.FormatAnimatedLine(1, "x/guard.b3d", true, true, new Vector3(1, 2, 3), d, null, 0, 0);
        Assert.Contains("dist=n/a inFrustum=n/a", line);
    }

    [Fact]
    public void CameraPrefersTheLastRenderedOneWhileItExists()
    {
        Assert.Equal(7, EngineTrace.PickCamera(7, true, 4));
        Assert.Equal(4, EngineTrace.PickCamera(7, false, 4));
        Assert.Equal(4, EngineTrace.PickCamera(0, false, 4));
        Assert.Equal(0, EngineTrace.PickCamera(0, false, 0));
    }

    [Fact]
    public void SelectorRanksCollidingThenMovingTypedEntities()
    {
        var sel = new CollisionTraceSelector(2);
        CollisionTraceSelector.Candidate C(int id, int type, int n, float x) => new(id, type, n, new Vector3(x, 0, 0));
        sel.Select([C(1, 1, 0, 0), C(2, 1, 0, 0)]);
        var picks = sel.Select([C(1, 1, 0, 5), C(2, 2, 3, 0), C(3, 0, 9, 9), C(4, 3, 0, 0), C(5, 1, 0, 0)]);
        Assert.Equal([2, 1], picks.Select(p => p.Id));
        Assert.Equal(5f, picks[1].Delta.X);
        Assert.Equal(3, picks[0].Collisions);
        var still = new CollisionTraceSelector();
        still.Select([C(9, 1, 0, 1)]);
        Assert.Empty(still.Select([C(9, 1, 0, 1)]));
    }

    [Fact]
    public void SamplerKeepsFirstThenOneInNAndAlwaysBypasses()
    {
        var s = new TraceSampler(3, 10);
        var logged = Enumerable.Range(1, 33).Count(_ => s.ShouldLog("CreateMesh", false));
        Assert.Equal(3 + 3, logged);   // calls 1-3, then 13, 23, 33
        Assert.True(s.ShouldLog("CreateMesh", true));
        var stats = s.TakeStats();
        Assert.Equal("[trace-stats] calls seen/logged: CreateMesh=34/7", stats);
        Assert.Null(s.TakeStats());
    }

    [Fact]
    public void MaxLinesParsingDefaultsTo400000()
    {
        Assert.Equal(400_000, EngineTrace.ParseMaxLines(null));
        Assert.Equal(400_000, EngineTrace.ParseMaxLines("0"));
        Assert.Equal(1234, EngineTrace.ParseMaxLines("1234"));
    }
}
