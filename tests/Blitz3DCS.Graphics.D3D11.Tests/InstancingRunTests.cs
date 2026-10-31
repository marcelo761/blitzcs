using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

/// <summary>Run formation on real-shaped queues: sprites sharing a texture, interleaved textures and blends.</summary>
public sealed class InstancingRunTests
{
    /// <summary>Key = identical-draw class; Additive marks commutative draws.</summary>
    private struct Source(int[] keys, bool[] additive) : IReorderSource
    {
        public readonly int Count => keys.Length;
        public readonly bool SameTransform(int a, int b) => a == b;
        public readonly bool SameMaterial(int a, int b) => keys[a] == keys[b];
        public readonly bool CanInstance(int first, int candidate) => keys[first] == keys[candidate];
        public readonly bool Commutative(int index) => additive[index];
    }

    private static (int[] Order, bool Changed) Group(int[] keys, bool[] additive)
    {
        var source = new Source(keys, additive);
        var order = new int[keys.Length];
        var changed = DrawRecordPlanner.GroupCommutative(ref source, order, new int[keys.Length], new int[keys.Length + 1]);
        return (order, changed);
    }

    private static List<PlannedDraw> Plan(int[] keys, bool[] additive)
    {
        var source = new Source(keys, additive);
        var plan = new List<PlannedDraw>();
        DrawRecordPlanner.Plan(ref source, instancing: true, plan);
        return plan;
    }

    private static bool[] All(int count, bool value) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void FortySpritesSharingTextureAndBlendFormOneRunPerWindow()
    {
        var plan = Plan(Enumerable.Repeat(1, 40).ToArray(), All(40, true));
        Assert.Equal([(0, 40)], plan.Select(d => (d.First, d.Count)));
    }

    [Fact]
    public void InterleavedAdditiveTexturesAreGatheredIntoRuns()
    {
        // A B C A B C ... as sorted by distance: no run without grouping.
        var keys = Enumerable.Range(0, 30).Select(i => i % 3).ToArray();
        var additive = All(30, true);
        Assert.Equal(30, Plan(keys, additive).Count);
        var (order, changed) = Group(keys, additive);
        Assert.True(changed);
        Assert.Equal(Enumerable.Range(0, 30).OrderBy(i => i % 3).ThenBy(i => i), order);
        Assert.Equal(Enumerable.Range(0, 30), order.OrderBy(i => i));
        var reordered = order.Select(i => keys[i]).ToArray();
        Assert.Equal([(0, 10), (10, 10), (20, 10)], Plan(reordered, additive).Select(d => (d.First, d.Count)));
    }

    [Fact]
    public void BlendedAlphaDrawsKeepTheirOrder()
    {
        var keys = new[] { 1, 2, 1, 2, 1, 2 };
        var (order, changed) = Group(keys, All(6, false));
        Assert.False(changed);
        Assert.Equal(Enumerable.Range(0, 6), order);
    }

    [Fact]
    public void NothingMovesAcrossANonCommutativeDraw()
    {
        // additive A B A | alpha X | additive A B A: groups stay inside their block, the alpha draw stays in place.
        var keys = new[] { 1, 2, 1, 9, 1, 2, 1 };
        bool[] additive = [true, true, true, false, true, true, true];
        var (order, changed) = Group(keys, additive);
        Assert.True(changed);
        Assert.Equal([0, 2, 1, 3, 4, 6, 5], order);
    }

    [Fact]
    public void OpaqueMeshesInterleavedWithAdditiveSpritesStayPut()
    {
        var keys = new[] { 5, 1, 6, 1, 7, 1 };
        bool[] additive = [false, true, false, true, false, true];
        var (order, changed) = Group(keys, additive);
        Assert.False(changed);
        Assert.Equal(Enumerable.Range(0, 6), order);
    }

    [Fact]
    public void MembersKeepRelativeOrderAndGroupsKeepFirstAppearance()
    {
        var keys = new[] { 3, 1, 3, 2, 1, 3, 2 };
        var (order, _) = Group(keys, All(7, true));
        Assert.Equal([0, 2, 5, 1, 4, 3, 6], order);
    }

    [Fact]
    public void SingleDrawBlocksAndDistinctDrawsAreUntouched()
    {
        var (order, changed) = Group([1, 2, 3, 4], All(4, true));
        Assert.False(changed);
        Assert.Equal([0, 1, 2, 3], order);
    }

    [Fact]
    public void OnlyAdditiveWithReadOnlyDepthIsCommutative()
    {
        Assert.True(D3D11Renderer.IsCommutative(new D3D11LightingDrawState(1, 0, Blend: 3, DepthMode: 1)));
        Assert.False(D3D11Renderer.IsCommutative(new D3D11LightingDrawState(1, 0, Blend: 3, DepthMode: 2)));
        Assert.False(D3D11Renderer.IsCommutative(new D3D11LightingDrawState(1, 0, Blend: 3, DepthMode: 0)));
        Assert.False(D3D11Renderer.IsCommutative(new D3D11LightingDrawState(1, 0, Blend: 1, DepthMode: 1)));
        Assert.False(D3D11Renderer.IsCommutative(new D3D11LightingDrawState(1, 0, Blend: 2, DepthMode: 1)));
    }

    [Fact]
    public void BreakClassificationNamesTheFirstFailingCriterion()
    {
        var state = new D3D11LightingDrawState(1, 0, Blend: 1, DepthMode: 1);
        var mesh = new D3D11Mesh(null!, null!, null!, 6, 4);
        var texture = new D3D11Texture(null!, null!, null!, 4, 4, false);
        D3D11Texture?[] textures = [texture, null, null, null, null, null, null, null];
        D3D11Texture?[] other = [new D3D11Texture(null!, null!, null!, 4, 4, false), null, null, null, null, null, null, null];
        var a = new D3D11Renderer.QueuedMeshDraw(mesh, 6, 1, 0, state, false);
        Assert.Equal(D3D11FrameCounters.InstRunBreakMesh, D3D11Renderer.ClassifyBreak(a, a with { Mesh = new D3D11Mesh(null!, null!, null!, 6, 4) }, textures, textures));
        Assert.Equal(D3D11FrameCounters.InstRunBreakTex, D3D11Renderer.ClassifyBreak(a, a, textures, other));
        Assert.Equal(D3D11FrameCounters.InstRunBreakSampler, D3D11Renderer.ClassifyBreak(a, a with { SamplerBits = 1 }, textures, textures));
        Assert.Equal(D3D11FrameCounters.InstRunBreakState, D3D11Renderer.ClassifyBreak(a, a with { State = state with { Blend = 3 } }, textures, textures));
        Assert.Equal(D3D11FrameCounters.InstRunBreakOther, D3D11Renderer.ClassifyBreak(a, a, textures, textures));
    }
}
