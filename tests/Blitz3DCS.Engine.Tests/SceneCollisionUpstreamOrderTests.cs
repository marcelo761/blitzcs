using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>
/// Upstream World::update (blitz3d/world.cpp:485-493) runs beginUpdate / collide / endUpdate per object in order.
/// collide() sweeps each target at dst->getPrevWorldTform() (world.cpp:206), which endUpdate sets to the current
/// transform (object.cpp:75-78) only when that target's own turn has passed.
/// </summary>
public sealed class SceneCollisionUpstreamOrderTests
{
    /// <summary>Source moves 0 to 10 in X (radius 1); a radius 1 target moves 5 to 7 in the same frame.</summary>
    private static (SceneGraph Scene, int Source, int Target) MovingTarget(bool sourceFirst)
    {
        var scene = new SceneGraph();
        int source, target;
        if (sourceFirst) { source = scene.CreatePivot(); target = scene.CreatePivot(); }
        else { target = scene.CreatePivot(); source = scene.CreatePivot(); }
        scene.PositionEntity(target, 5, 0, 0);
        scene.EntityType(source, 1);
        scene.EntityType(target, 2);
        scene.EntityRadius(source, 1);
        scene.EntityRadius(target, 1);
        scene.Collisions(1, 2, method: 1, response: 1);
        scene.PositionEntity(target, 7, 0, 0);
        scene.PositionEntity(source, 10, 0, 0);
        return (scene, source, target);
    }

    [Fact]
    public void Source_ordered_before_a_moving_target_sweeps_the_targets_previous_transform()
    {
        // Target prev x=5: relative start -5, direction 10, a=100, b=2*(-5*10)=-100, c=25-(1+1)^2=21,
        // discriminant=10000-8400=1600, t=(100-40)/200=0.3; stop plane at contact 3 minus epsilon => 2.999.
        var (scene, source, target) = MovingTarget(sourceFirst: true);
        scene.UpdateWorld();
        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(.3f, scene.CollisionTime(source, 1), 4);
        Assert.Equal(2.999f, scene.EntityX(source, global: true), 3);
        // The target's own beginUpdate runs after the source's collide and clears the record it received.
        Assert.Equal(0, scene.CountCollisions(target));
        // endUpdate stored the new transform: next frame (target now stationary at 7) uses it.
        scene.PositionEntity(source, 0, 0, 0);
        scene.UpdateWorld();
        scene.PositionEntity(source, 10, 0, 0);
        scene.UpdateWorld();
        // Target prev x=7: b=-140, c=49-4=45, disc=19600-18000=1600, t=(140-40)/200=0.5 => 5-0.001.
        Assert.Equal(.5f, scene.CollisionTime(source, 1), 4);
        Assert.Equal(4.999f, scene.EntityX(source, global: true), 3);
    }

    [Fact]
    public void Source_ordered_after_a_moving_target_sees_the_targets_current_transform_and_keeps_both_records()
    {
        // The target's endUpdate already ran this frame, so prev == current == 7: t=0.5, source rests at 4.999.
        var (scene, source, target) = MovingTarget(sourceFirst: false);
        scene.UpdateWorld();
        Assert.Equal(.5f, scene.CollisionTime(source, 1), 4);
        Assert.Equal(4.999f, scene.EntityX(source, global: true), 3);
        Assert.Equal(1, scene.CountCollisions(target));
    }

    [Fact]
    public void Target_processed_later_loses_the_record_made_by_an_earlier_source()
    {
        // Consequence (b): object.cpp:63 clears the target's records at ITS turn, after the source recorded one.
        var (scene, source, target) = MovingTarget(sourceFirst: true);
        scene.UpdateWorld();
        Assert.Equal(1, scene.CountCollisions(source));
        Assert.Equal(0, scene.CountCollisions(target));
    }

    [Fact]
    public void Moving_platform_is_swept_at_last_frames_position_every_frame()
    {
        // Source (radius 1) created first; platform (radius 1) rises 0.5 per frame. Each frame the source starts 3 above the
        // platform's PREVIOUS centre p and aims at p+1. Upstream sweeps the platform at its previous transform (world.cpp:206):
        // separation 3 -> 2 along a path of length 2 gives t=(3-2)/2=0.5, the source stops at p+2+0.001 (backed off along the approach) whatever the
        // platform's current height (a current-transform sweep would stop at p+0.5+2+0.001).
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var platform = scene.CreatePivot();
        scene.EntityType(source, 1); scene.EntityType(platform, 2);
        scene.EntityRadius(source, 1); scene.EntityRadius(platform, 1);
        scene.Collisions(1, 2, method: 1, response: 1);
        scene.UpdateWorld();
        for (var frame = 1; frame <= 4; frame++)
        {
            var previous = 0.5f * (frame - 1);
            scene.PositionEntity(platform, 0, previous, 0);
            scene.UpdateWorld();
            scene.PositionEntity(platform, 0, previous + 0.5f, 0);
            scene.PositionEntity(source, 0, previous + 3, 0);
            scene.ResetEntity(source);
            scene.PositionEntity(source, 0, previous + 1, 0);
            scene.UpdateWorld();
            Assert.Equal(1, scene.CountCollisions(source));
            Assert.Equal(.5f, scene.CollisionTime(source, 1), 4);
            Assert.Equal(previous + 2.001f, scene.EntityY(source, global: true), 3);
        }
    }
}
