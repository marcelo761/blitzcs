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
        // Upstream World::collide: coll_plane.d -= COLLISION_EPSILON moves the contact plane
        // away from the obstacle, so the sphere rests 0.001 outside the contact (3 - 0.001).
        Assert.Equal(2.999f, scene.EntityX(source, global: true), 3);

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
        // Same upstream rule (plane.d -= COLLISION_EPSILON): rests 0.001 outside the box contact (4.5 - 0.001).
        Assert.Equal(4.499f, scene.EntityX(source, global: true), 3);
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
    public void Infinite_plane_collision_uses_parented_world_transform_and_stops_swept_sphere()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var plane = scene.CreatePivot(parent);
        var source = scene.CreatePivot();
        scene.PositionEntity(parent, 1, 2, 3);
        scene.RotateEntity(parent, 90, 0, 0);
        scene.PositionEntity(plane, 0, 0, 2);
        scene.SetInfinitePlane(plane);
        scene.EntityType(source, 1);
        scene.EntityType(plane, 2);
        scene.EntityRadius(source, .5f);
        scene.Collisions(1, 2, method: 2, response: 1);

        var transform = scene.GetWorldTransform(plane);
        var normal = scene.TransformNormal(new(0, 1, 0), source: plane);
        var start = new SceneVector3(transform.Position.X + normal.X * 2, transform.Position.Y + normal.Y * 2, transform.Position.Z + normal.Z * 2);
        var end = new SceneVector3(transform.Position.X - normal.X * 2, transform.Position.Y - normal.Y * 2, transform.Position.Z - normal.Z * 2);
        scene.PositionEntity(source, start.X, start.Y, start.Z, global: true);
        scene.ResetEntity(source);
        scene.PositionEntity(source, end.X, end.Y, end.Z, global: true);

        scene.UpdateWorld();

        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(plane, scene.CollisionEntity(source, 1));
        Assert.Equal(.375f, scene.CollisionTime(source, 1), 4);
        Assert.Equal(normal.X, scene.CollisionNX(source, 1), 4);
        Assert.Equal(normal.Y, scene.CollisionNY(source, 1), 4);
        Assert.Equal(normal.Z, scene.CollisionNZ(source, 1), 4);
        Assert.Equal(transform.Position.X, scene.CollisionX(source, 1), 4);
        Assert.Equal(transform.Position.Y, scene.CollisionY(source, 1), 4);
        Assert.Equal(transform.Position.Z, scene.CollisionZ(source, 1), 4);
    }

    [Fact]
    public void Infinite_plane_collision_slides_along_plane_and_does_not_build_triangle_bvh()
    {
        var scene = new SceneGraph();
        var plane = scene.CreatePivot();
        var source = scene.CreatePivot();
        scene.PositionEntity(plane, 0, 1, 0);
        scene.SetInfinitePlane(plane);
        scene.EntityType(plane, 2);
        scene.EntityType(source, 1);
        scene.EntityRadius(source, .5f);
        scene.Collisions(1, 2, method: 2, response: 2);
        scene.PositionEntity(source, 0, 4, 0);
        scene.ResetEntity(source);
        scene.PositionEntity(source, 5, -2, 4);

        scene.UpdateWorld();

        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(1.499f, scene.EntityY(source, global: true), 2);
        Assert.Equal(5, scene.EntityX(source, global: true), 3);
        Assert.Equal(4, scene.EntityZ(source, global: true), 3);
        Assert.False(scene.HasTriangleColliderForTesting(plane));
    }

    [Fact]
    public void Infinite_plane_does_not_hit_when_contact_is_exactly_at_sweep_endpoint()
    {
        var scene = new SceneGraph();
        var plane = scene.CreatePivot();
        var source = scene.CreatePivot();
        scene.SetInfinitePlane(plane);
        scene.EntityType(plane, 2);
        scene.EntityType(source, 1);
        scene.EntityRadius(source, .5f);
        scene.Collisions(1, 2, method: 2, response: 1);
        scene.PositionEntity(source, 0, 1.5f, 0);
        scene.ResetEntity(source);
        scene.PositionEntity(source, 0, .5f, 0);

        scene.UpdateWorld();

        Assert.Equal(0, scene.CountCollisions(source));
        Assert.Equal(.5f, scene.EntityY(source, global: true), 4);
    }

    [Fact]
    public void Earlier_polygon_hit_wins_equal_time_tie_with_infinite_plane()
    {
        var scene = new SceneGraph();
        var triangle = scene.CreatePivot();
        var plane = scene.CreatePivot();
        var source = scene.CreatePivot();
        scene.SetCollisionTriangles(triangle, [new(
            new(-5, 0, -5), new(0, 0, 5), new(5, 0, -5), Index: 42)]);
        scene.SetInfinitePlane(plane);
        scene.EntityType(triangle, 2);
        scene.EntityType(plane, 2);
        scene.EntityType(source, 1);
        scene.EntityRadius(source, .5f);
        scene.Collisions(1, 2, method: 2, response: 1);
        scene.PositionEntity(source, 0, 2, 0);
        scene.ResetEntity(source);
        scene.PositionEntity(source, 0, -2, 0);

        scene.UpdateWorld();

        Assert.Equal(triangle, scene.CollisionEntity(source, 1));
        Assert.Equal(42, scene.CollisionTriangle(source, 1));
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

    [Theory]
    [InlineData(.15f)]
    [InlineData(.30f)]
    public void Sphere_dropped_onto_floor_rests_on_surface_and_does_not_fall_through(float radius)
    {
        var scene = new SceneGraph();
        var floor = scene.CreatePivot();
        var sphere = scene.CreatePivot();
        scene.SetCollisionTriangles(floor, Quad(
            new(-10, 0, -10), new(-10, 0, 10), new(10, 0, 10), new(10, 0, -10)));
        scene.EntityType(floor, 1);
        scene.EntityType(sphere, 2);
        scene.EntityRadius(sphere, radius);
        scene.Collisions(2, 1, method: 2, response: 2);
        scene.PositionEntity(sphere, 0, 1, 0);
        scene.ResetEntity(sphere);

        var landed = false;
        for (var frame = 0; frame < 60; frame++)
        {
            scene.TranslateEntity(sphere, 0, -.05f, 0);
            scene.UpdateWorld();
            var y = scene.EntityY(sphere, global: true);
            if (scene.CountCollisions(sphere) > 0) landed = true;
            if (!landed) continue;

            Assert.True(scene.CountCollisions(sphere) > 0, $"frame {frame}: no contact after landing, y={y}");
            Assert.True(y >= radius - .005f, $"frame {frame}: sphere fell below floor, y={y}");
        }

        Assert.True(landed);
        var finalY = scene.EntityY(sphere, global: true);
        Assert.InRange(finalY, radius - .005f, radius + .01f);
    }

    [Theory]
    [InlineData(.15f)]
    [InlineData(.30f)]
    public void Sphere_sliding_into_vertical_wall_stops_at_surface(float radius)
    {
        var scene = new SceneGraph();
        var wall = scene.CreatePivot();
        var sphere = scene.CreatePivot();
        scene.SetCollisionTriangles(wall, Quad(
            new(10, -10, -10), new(10, -10, 10), new(10, 10, 10), new(10, 10, -10)));
        scene.EntityType(wall, 1);
        scene.EntityType(sphere, 2);
        scene.EntityRadius(sphere, radius);
        scene.Collisions(2, 1, method: 2, response: 2);
        scene.PositionEntity(sphere, 0, 1, 0);
        scene.ResetEntity(sphere);

        for (var frame = 0; frame < 60; frame++)
        {
            scene.TranslateEntity(sphere, .3f, 0, 0);
            scene.UpdateWorld();
        }

        var x = scene.EntityX(sphere, global: true);
        Assert.True(x <= 10 - radius + .005f, $"sphere went through wall, x={x}");
        Assert.True(x > 10 - radius - .02f, $"sphere stopped too early, x={x}");
    }

    // Two triangles forming a quad; winding gives the normal (cross(b-a, c-a)) of the first corner order.
    private static SceneCollisionTriangle[] Quad(SceneVector3 a, SceneVector3 b, SceneVector3 c, SceneVector3 d) =>
        [new(a, b, c), new(a, c, d)];
}
