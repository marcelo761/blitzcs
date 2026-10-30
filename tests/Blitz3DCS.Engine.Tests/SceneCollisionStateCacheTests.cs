using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>Invalidation rules for the per-node collision state cache used by UpdateWorld.</summary>
public sealed class SceneCollisionStateCacheTests
{
    private static (SceneGraph Scene, int Source, int Target) Arena()
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
        return (scene, source, target);
    }

    [Fact]
    public void Type_changes_after_cached_updates_are_honoured()
    {
        var (scene, source, target) = Arena();
        scene.UpdateWorld();
        Assert.Equal(1, scene.CountCollisions(source));
        scene.EntityType(target, 0);
        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        Assert.Equal(0, scene.CountCollisions(source));
        scene.PositionEntity(source, 0, 0, 0);
        scene.UpdateWorld();
        scene.EntityType(target, 2);
        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        Assert.Equal(1, scene.CountCollisions(source));
    }

    [Fact]
    public void Copied_entity_uses_its_own_state_and_freeing_it_drops_the_cached_reference()
    {
        var (scene, source, target) = Arena();
        scene.UpdateWorld();
        var copy = scene.CopyEntity(target);
        scene.PositionEntity(copy, 20, 0, 0);
        scene.UpdateWorld();
        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        Assert.True(scene.CountCollisions(source) >= 1);
        scene.FreeEntity(copy);
        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        Assert.Equal(target, scene.EntityCollided(source, 2));
    }
}
