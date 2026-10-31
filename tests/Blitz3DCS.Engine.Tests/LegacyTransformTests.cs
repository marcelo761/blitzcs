using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed partial class SceneGraphTests
{
    [Fact]
    public void Hide_and_show_apply_enabled_visible_state_through_ancestors_and_copies_start_visible()
    {
        var scene = new SceneGraph();
        var root = scene.CreatePivot();
        var child = scene.CreatePivot(root);
        var grandchild = scene.CreatePivot(child);

        scene.HideEntity(root);
        Assert.False(scene.IsVisibleInHierarchy(root));
        Assert.False(scene.IsVisibleInHierarchy(child));
        Assert.False(scene.IsVisibleInHierarchy(grandchild));

        scene.ShowEntity(child);
        Assert.False(scene.IsVisibleInHierarchy(child));
        scene.ShowEntity(root);
        Assert.True(scene.IsVisibleInHierarchy(grandchild));

        scene.HideEntity(child);
        Assert.True(scene.IsVisibleInHierarchy(root));
        Assert.False(scene.IsVisibleInHierarchy(grandchild));
        scene.ShowEntity(child);
        Assert.True(scene.IsVisibleInHierarchy(grandchild));

        scene.HideEntity(root);
        var copy = scene.CopyEntity(root);
        var copiedChild = scene.GetChild(copy, 1);
        Assert.True(scene.IsVisibleInHierarchy(copy));
        Assert.True(scene.IsVisibleInHierarchy(copiedChild));
    }

    [Fact]
    public void Point_entity_and_align_to_vector_match_legacy_axis_and_angle_conventions()
    {
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();
        var target = scene.CreatePivot();
        scene.PositionEntity(target, 1, 0, 0);

        scene.PointEntity(entity, target);
        Assert.Equal(-90, scene.EntityYaw(entity, global: true), 4);
        Assert.Equal(0, scene.EntityPitch(entity, global: true), 4);

        scene.RotateEntity(entity, 0, 0, 0);
        scene.AlignToVector(entity, 1, 0, 0, axis: 3);
        var forward = scene.GetWorldTransform(entity).BasisZ;
        Assert.Equal(1, forward.X, 4);
        Assert.Equal(0, forward.Y, 4);
        Assert.Equal(0, forward.Z, 4);
    }

    [Fact]
    public void Tform_distinguishes_points_vectors_and_normals_and_zero_world_is_identity()
    {
        var previous = LegacyScene.Current;
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            var entity = scene.CreatePivot();
            scene.PositionEntity(entity, 10, 20, 30);
            scene.ScaleEntity(entity, 2, 3, 4);

            LegacyScene.TFormPoint(1, 1, 1, entity, 0);
            AssertTFormVector((12, 23, 34), ReadTformed());
            LegacyScene.TFormVector(1, 1, 1, entity, 0);
            AssertTFormVector((2, 3, 4), ReadTformed());
            LegacyScene.TFormNormal(1, 1, 0, entity, 0);
            AssertTFormVector((12 / MathF.Sqrt(208), 8 / MathF.Sqrt(208), 0), ReadTformed());

            LegacyScene.TFormPoint(1, 2, 3, 0, 0);
            AssertTFormVector((1, 2, 3), ReadTformed());
        }
        finally
        {
            LegacyScene.Current = previous;
        }
    }

    [Fact]
    public void Vector_and_delta_angles_use_blitz_degrees_and_wrapping()
    {
        var scene = new SceneGraph();
        Assert.Equal(-90, scene.VectorYaw(1, 0, 0), 4);
        Assert.Equal(-90, scene.VectorPitch(0, 1, 0), 4);

        var source = scene.CreatePivot();
        var destination = scene.CreatePivot();
        scene.RotateEntity(source, 0, 90, 0);
        scene.PositionEntity(destination, 1, 0, 0);
        Assert.Equal(-180, scene.DeltaYaw(source, destination), 4);
        Assert.Equal(0, scene.DeltaPitch(source, destination), 4);
    }

    private static (float X, float Y, float Z) ReadTformed() =>
        (LegacyScene.TFormedX(), LegacyScene.TFormedY(), LegacyScene.TFormedZ());

    private static void AssertTFormVector((float X, float Y, float Z) expected, (float X, float Y, float Z) actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
