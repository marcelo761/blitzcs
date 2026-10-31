using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class GpuMeshUpdatePlanTests
{
    [Fact]
    public void First_upload_creates_an_immutable_mesh()
    {
        Assert.Equal(GpuMeshAction.CreateImmutable, GpuMeshUpdatePlan.Decide(false, false, 0, 0, 10, 30).Action);
    }

    [Fact]
    public void A_rebuilt_immutable_mesh_becomes_resizable_with_headroom_then_updates_in_place_while_it_fits()
    {
        var plan = GpuMeshUpdatePlan.Decide(true, false, 0, 0, 40, 60);
        Assert.Equal(GpuMeshAction.CreateResizable, plan.Action);
        Assert.True(plan.VertexCapacity >= 60 && plan.IndexCapacity >= 90 && plan.IndexCapacity >= plan.VertexCapacity * 3u);

        var same = GpuMeshUpdatePlan.Decide(true, true, plan.VertexCapacity, plan.IndexCapacity, 55, 80);
        Assert.Equal(GpuMeshAction.UpdateInPlace, same.Action);
        var grow = GpuMeshUpdatePlan.Decide(true, true, plan.VertexCapacity, plan.IndexCapacity, plan.VertexCapacity + 1, 6);
        Assert.Equal(GpuMeshAction.CreateResizable, grow.Action);
        Assert.True(grow.VertexCapacity > plan.VertexCapacity);
    }
}
