using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class ScenePickingTests
{
    [Fact]
    public void Polygon_geometry_selection_version_tracks_membership_changes_but_not_transforms_or_state_reads()
    {
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();

        var version = scene.PolygonGeometrySelectionVersion;
        Assert.False(scene.RequiresPolygonCollision(entity)); // Creating default collision state is not a selection change.
        Assert.Equal(version, scene.PolygonGeometrySelectionVersion);

        scene.PositionEntity(entity, 1, 2, 3);
        scene.RotateEntity(entity, 10, 20, 30);
        Assert.Equal(version, scene.PolygonGeometrySelectionVersion);

        scene.EntityType(entity, 2);
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
        version = scene.PolygonGeometrySelectionVersion;
        scene.EntityPickMode(entity, 2);
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
        version = scene.PolygonGeometrySelectionVersion;
        scene.Collisions(1, 2, 2, 1);
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
        version = scene.PolygonGeometrySelectionVersion;
        scene.ClearCollisions();
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
        version = scene.PolygonGeometrySelectionVersion;
        scene.SetInfinitePlane(entity);
        Assert.True(scene.PolygonGeometrySelectionVersion > version);

        version = scene.PolygonGeometrySelectionVersion;
        var copy = scene.CopyEntity(entity);
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
        version = scene.PolygonGeometrySelectionVersion;
        scene.ForgetCollisionState(copy);
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
        version = scene.PolygonGeometrySelectionVersion;
        scene.ForgetCollisionState(copy); // Forget must invalidate even if no state existed.
        Assert.True(scene.PolygonGeometrySelectionVersion > version);
    }

    [Fact]
    public void LinePick_uses_nearest_enabled_sphere_and_stores_world_contact_and_normal()
    {
        var scene = new SceneGraph();
        var far = scene.CreatePivot();
        var near = scene.CreatePivot();
        scene.PositionEntity(far, 0, 0, 7);
        scene.PositionEntity(near, 0, 0, 3);
        scene.EntityPickMode(far, 1);
        scene.EntityPickMode(near, 1);

        Assert.Equal(near, scene.LinePick(0, 0, 0, 0, 0, 10));
        Assert.Equal(near, scene.PickedEntity);
        Assert.Equal(2, scene.PickedZ, 4);
        Assert.Equal(-1, scene.PickedNZ, 4);
        Assert.Equal(0, scene.PickedX, 4);
    }

    [Fact]
    public void Polygon_picking_uses_collision_triangles_and_respects_one_sided_faces()
    {
        var scene = new SceneGraph();
        var target = scene.CreatePivot();
        scene.SetCollisionTriangles(target, [new(
            new(-2, -2, 5), new(0, 2, 5), new(2, -2, 5))]);
        scene.EntityPickMode(target, 2);

        Assert.Equal(target, scene.LinePick(0, 0, 0, 0, 0, 10));
        Assert.Equal(5, scene.PickedZ, 4);
        Assert.Equal(-1, scene.PickedNZ, 4);

        scene.SetCollisionTriangles(target, [new(
            new(-2, -2, 5), new(2, -2, 5), new(0, 2, 5))]);
        Assert.Equal(0, scene.LinePick(0, 0, 0, 0, 0, 10));
        Assert.Equal(0, scene.PickedEntity);
    }

    [Fact]
    public void Infinite_plane_copy_keeps_analytic_pick_and_collision_state_without_triangle_bvh()
    {
        var scene = new SceneGraph();
        var original = scene.CreatePivot();
        scene.SetInfinitePlane(original);
        scene.EntityPickMode(original, 2);
        var copy = scene.CopyEntity(original);
        scene.PositionEntity(copy, 0, 10, 0, global: true);

        Assert.Equal(copy, scene.LinePick(0, 12, 0, 0, -6, 0));
        Assert.Equal(10, scene.PickedY, 4);
        Assert.Equal(1, scene.PickedNY, 4);
        Assert.False(scene.HasTriangleColliderForTesting(copy));

        var source = scene.CreatePivot();
        scene.EntityType(source, 1);
        scene.EntityType(copy, 2);
        scene.EntityRadius(source, .5f);
        scene.Collisions(1, 2, method: 2, response: 1);
        scene.PositionEntity(source, 0, 12, 0);
        scene.ResetEntity(source);
        scene.PositionEntity(source, 0, 8, 0);
        scene.UpdateWorld();

        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(copy, scene.CollisionEntity(source, 1));
        Assert.False(scene.HasTriangleColliderForTesting(copy));
    }

    [Fact]
    public void EntityVisible_ignores_endpoints_and_nonobscurers_but_respects_enabled_obscurers()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var destination = scene.CreatePivot();
        var blocker = scene.CreatePivot();
        scene.PositionEntity(destination, 0, 0, 10);
        scene.PositionEntity(blocker, 0, 0, 5);
        scene.EntityPickMode(source, 1);
        scene.EntityPickMode(destination, 1);
        scene.EntityPickMode(blocker, 1, obscurer: false);
        Assert.True(scene.EntityVisible(source, destination));

        scene.EntityPickMode(blocker, 1);
        Assert.False(scene.EntityVisible(source, destination));

        scene.HideEntity(blocker);
        Assert.True(scene.EntityVisible(source, destination));
    }

    [Fact]
    public void Copy_preserves_pick_mode_obscurer_and_polygon_data_then_free_removes_pick_result()
    {
        var scene = new SceneGraph();
        var target = scene.CreatePivot();
        scene.PositionEntity(target, 0, 0, 5);
        scene.SetCollisionTriangles(target, [new(
            new(-2, -2, 5), new(0, 2, 5), new(2, -2, 5))]);
        scene.EntityPickMode(target, 2, obscurer: false);
        var copy = scene.CopyEntity(target);
        scene.PositionEntity(copy, 3, 0, 0, global: true);

        Assert.Equal(copy, scene.LinePick(3, 0, 0, 0, 0, 10));
        Assert.True(scene.EntityVisible(target, copy));
        scene.FreeEntity(copy);
        Assert.Equal(0, scene.PickedEntity);
    }
}
