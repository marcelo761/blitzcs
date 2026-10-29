using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class SceneGraphPoseTests
{
    [Fact]
    public void Set_local_pose_applies_scalar_first_quaternion_and_invalidates_descendant_world_transforms()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var child = scene.CreatePivot(parent);
        var halfAngle = MathF.Sqrt(.5f);

        scene.SetLocalPose(parent, new SceneVector3(5, 0, 0), new SceneVector3(2, 2, 2), 1, 0, 0, 0);
        scene.SetLocalPose(child, new SceneVector3(1, 2, 3), new SceneVector3(1, 1, 1), halfAngle, 0, halfAngle, 0);

        AssertVector((7, 4, 6), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));
        AssertVector((-2, 0, 0), (scene.GetWorldTransform(child).BasisZ.X, scene.GetWorldTransform(child).BasisZ.Y, scene.GetWorldTransform(child).BasisZ.Z));

        scene.SetLocalPose(parent, new SceneVector3(9, 0, 0), new SceneVector3(1, 1, 1), 1, 0, 0, 0);

        AssertVector((10, 2, 3), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));
        AssertVector((-1, 0, 0), (scene.GetWorldTransform(child).BasisZ.X, scene.GetWorldTransform(child).BasisZ.Y, scene.GetWorldTransform(child).BasisZ.Z));
    }

    [Fact]
    public void Animation_pose_updates_only_keyed_channels_and_local_pose_preserves_raw_quaternion()
    {
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();
        var halfAngle = MathF.Sqrt(.5f);
        scene.SetLocalPose(entity, new SceneVector3(1, 2, 3), new SceneVector3(2, 3, 4), halfAngle, 0, halfAngle, 0);

        var before = scene.GetLocalPose(entity);
        Assert.Equal(halfAngle, before.RotationW, 6);
        Assert.Equal(halfAngle, before.RotationY, 6);

        var positionAndRotation = new B3dPoseNode(null!, -1, new B3dVector3(8, 9, 10),
            new B3dVector3(7, 7, 7), new B3dQuaternion(halfAngle, 0, 0, halfAngle),
            default, default, HasPositionKeys: true, HasScaleKeys: false, HasRotationKeys: true);
        scene.ApplyAnimationPose(entity, positionAndRotation);

        var actual = scene.GetLocalPose(entity);
        Assert.Equal(new SceneVector3(8, 9, 10), actual.Position);
        Assert.Equal(new SceneVector3(2, 3, 4), actual.Scale);
        Assert.Equal(halfAngle, actual.RotationW, 6);
        Assert.Equal(halfAngle, actual.RotationZ, 6);

        scene.ApplyAnimationPose(entity, positionAndRotation with { HasPositionKeys = false, HasRotationKeys = false });
        Assert.Equal(actual, scene.GetLocalPose(entity));
    }

    private static void AssertVector((float X, float Y, float Z) expected, (float X, float Y, float Z) actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
