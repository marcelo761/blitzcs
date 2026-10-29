using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class SceneCollisionTests
{
    [Fact]
    public void Sphere_collision_records_symmetric_one_based_results_and_stop_response()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var target = scene.CreatePivot();
        scene.PositionEntity(target, 5, 0, 0);
        scene.EntityType(source, 1);
        scene.EntityType(target, 2);
        scene.EntityRadius(source, 1);
        scene.EntityRadius(target, 1);
        scene.PositionEntity(source, 10, 0, 0);
        scene.Collisions(1, 2, method: 1, response: 1);

        scene.UpdateWorld();

        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(1, scene.CountCollisions(target));
        Assert.Equal(target, scene.EntityCollided(source, 2));
        Assert.Equal(target, scene.CollisionEntity(source, 1));
        Assert.Equal(source, scene.CollisionEntity(target, 1));
        Assert.Equal(.3f, scene.CollisionTime(source, 1), 4);
        Assert.Equal(4, scene.CollisionX(source, 1), 4);
        Assert.Equal(-1, scene.CollisionNX(source, 1), 4);
        Assert.Equal(3.001f, scene.EntityX(source, global: true), 3);

        scene.UpdateWorld();
        Assert.Equal(0, scene.CountCollisions(source));
        Assert.Equal(0, scene.CountCollisions(target));

        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        Assert.Equal(1, scene.CountCollisions(source));
        scene.FreeEntity(target);
        Assert.Equal(0, scene.CountCollisions(source));
    }

    [Fact]
    public void Box_collision_slides_along_wall_and_box_stop_halts_at_contact()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var wall = scene.CreatePivot();
        scene.PositionEntity(wall, 5, 0, 0);
        scene.EntityType(source, 1);
        scene.EntityType(wall, 2);
        scene.EntityRadius(source, .5f);
        scene.EntityBox(wall, 0, -5, -5, 1, 10, 10);
        scene.PositionEntity(source, 10, 0, 10);
        scene.Collisions(1, 2, method: 3, response: 2);

        scene.UpdateWorld();

        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(-1, scene.CollisionNX(source, 1), 4);
        Assert.Equal(4.501f, scene.EntityX(source, global: true), 3);
        Assert.Equal(10, scene.EntityZ(source, global: true), 3);
    }

    [Fact]
    public void Polygon_collision_uses_triangle_index_and_recursive_type_rules()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var targetRoot = scene.CreatePivot();
        var target = scene.CreatePivot(targetRoot);
        scene.PositionEntity(source, 0, 0, 2);
        scene.EntityType(targetRoot, 2, recursive: true);
        scene.EntityType(source, 1);
        scene.EntityRadius(source, .5f);
        scene.SetCollisionTriangles(target, [new(
            new(-2, -2, 0), new(2, -2, 0), new(0, 2, 0), Index: 37)]);
        scene.Collisions(1, 2, method: 2, response: 1);
        scene.Collisions(1, 2, method: 1, response: 0); // duplicate pair is ignored

        scene.PositionEntity(source, 0, 0, -2);
        scene.UpdateWorld();

        Assert.Equal(2, scene.GetEntityType(target));
        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(37, scene.CollisionTriangle(source, 1));
        Assert.Equal(0, scene.CollisionZ(source, 1), 4);
        Assert.Equal(0, scene.EntityCollided(source, 999));
    }

    [Fact]
    public void Copy_preserves_collision_configuration_and_free_removes_sidecar_results()
    {
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();
        scene.EntityType(entity, 7);
        scene.EntityRadius(entity, 2, 3);
        var copy = scene.CopyEntity(entity);

        Assert.Equal(7, scene.GetEntityType(copy));
        scene.FreeEntity(entity);
        Assert.Equal(7, scene.GetEntityType(copy));
    }

    [Fact]
    public void ResetEntity_clears_collision_results_and_restarts_the_next_sweep_at_current_transform()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var target = scene.CreatePivot();
        scene.PositionEntity(target, 5, 0, 0);
        scene.EntityType(source, 1);
        scene.EntityType(target, 2);
        scene.EntityRadius(source, .5f);
        scene.EntityRadius(target, 1);
        scene.Collisions(1, 2, 1, 0);
        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        Assert.Equal(1, scene.CountCollisions(source));

        scene.ResetEntity(source);
        Assert.Equal(0, scene.CountCollisions(source));
        scene.PositionEntity(source, 0, 0, 0);
        scene.ResetEntity(source);
        scene.UpdateWorld();

        Assert.Equal(0, scene.CountCollisions(source));
        Assert.Equal(0, scene.CountCollisions(target));
    }

    [Fact]
    public void Warmed_world_updates_do_not_allocate_per_frame()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        scene.EntityType(source, 1);
        scene.EntityRadius(source, 1);
        for (var index = 0; index < 32; index++)
        {
            var target = scene.CreatePivot();
            scene.PositionEntity(target, 1000 + index * 10, 0, 0);
            scene.EntityType(target, 2);
            scene.EntityRadius(target, 1);
        }
        scene.Collisions(1, 2, method: 1, response: 0);
        scene.UpdateWorld();

        for (var index = 0; index < 32; index++)
        {
            scene.PositionEntity(source, (index & 1) == 0 ? 10 : 0, 0, 0);
            scene.UpdateWorld();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 256; index++)
        {
            scene.PositionEntity(source, (index & 1) == 0 ? 10 : 0, 0, 0);
            scene.UpdateWorld();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
