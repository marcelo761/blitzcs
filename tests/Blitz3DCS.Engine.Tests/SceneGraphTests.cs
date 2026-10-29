using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed partial class SceneGraphTests
{
    [Fact]
    public void Handles_are_positive_stable_and_zero_is_the_root_sentinel()
    {
        var scene = new SceneGraph();
        var first = scene.CreatePivot();
        var second = scene.CreatePivot();

        Assert.True(first > 0);
        Assert.True(second > first);
        Assert.Equal(0, scene.GetParent(first));
        scene.FreeEntity(first);
        Assert.True(scene.CreatePivot() > second);
    }

    [Fact]
    public void World_position_composes_parent_and_local_transforms_and_dirtying_reaches_descendants()
    {
        var scene = new SceneGraph();
        var root = scene.CreatePivot();
        var child = scene.CreatePivot(root);
        var grandchild = scene.CreatePivot(child);
        scene.PositionEntity(root, 10, 20, 30);
        scene.PositionEntity(child, 1, 2, 3);
        scene.PositionEntity(grandchild, -2, 5, 4);

        AssertVector((11, 22, 33), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));
        AssertVector((9, 27, 37), (scene.EntityX(grandchild, true), scene.EntityY(grandchild, true), scene.EntityZ(grandchild, true)));

        scene.TranslateEntity(root, 100, -5, 2);
        AssertVector((109, 22, 39), (scene.EntityX(grandchild, true), scene.EntityY(grandchild, true), scene.EntityZ(grandchild, true)));
        AssertVector((-2, 5, 4), (scene.EntityX(grandchild), scene.EntityY(grandchild), scene.EntityZ(grandchild)));
    }

    [Fact]
    public void Yaw_rotation_composes_world_basis_and_euler_query()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var child = scene.CreatePivot(parent);
        scene.PositionEntity(parent, 10, 20, 30);
        scene.PositionEntity(child, 1, 2, 3);
        scene.RotateEntity(parent, 0, 90, 0);

        AssertVector((7, 22, 31), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));
        Assert.Equal(90, scene.EntityYaw(parent), 4);
        AssertVector((0, 0, 1), (scene.GetWorldTransform(parent).BasisX.X, scene.GetWorldTransform(parent).BasisX.Y, scene.GetWorldTransform(parent).BasisX.Z));
    }

    [Fact]
    public void Move_entity_uses_its_local_quaternion_basis()
    {
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();
        scene.RotateEntity(entity, 0, 90, 0);
        scene.MoveEntity(entity, 0, 0, 1);

        AssertVector((-1, 0, 0), (scene.EntityX(entity), scene.EntityY(entity), scene.EntityZ(entity)));
    }

    [Fact]
    public void Rotation_translation_and_global_reparent_match_the_initialized_native_runtime()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var child = scene.CreatePivot(parent);
        scene.PositionEntity(parent, 10, 20, 30);
        scene.PositionEntity(child, 1, 2, 3);

        AssertVector((11, 22, 33), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));
        scene.RotateEntity(parent, 0, 90, 0);
        Assert.Equal(90, scene.EntityYaw(parent), 4);
        AssertVector((7, 22, 31), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));

        scene.RotateEntity(child, 15, 25, 35);
        Assert.Equal(15, scene.EntityPitch(child), 4);
        Assert.Equal(25, scene.EntityYaw(child), 4);
        Assert.Equal(35, scene.EntityRoll(child), 4);
        scene.TranslateEntity(child, 2, 0, 0);
        AssertVector((3, 2, 3), (scene.EntityX(child), scene.EntityY(child), scene.EntityZ(child)));
        AssertVector((7, 22, 33), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));

        scene.EntityParent(child, 0);
        Assert.Equal(0, scene.GetParent(child));
        AssertVector((7, 22, 33), (scene.EntityX(child, true), scene.EntityY(child, true), scene.EntityZ(child, true)));
    }

    [Fact]
    public void Parenting_can_keep_local_or_preserve_world_position()
    {
        var scene = new SceneGraph();
        var firstParent = scene.CreatePivot();
        var secondParent = scene.CreatePivot();
        var child = scene.CreatePivot(firstParent);
        scene.PositionEntity(firstParent, 10, 0, 0);
        scene.PositionEntity(secondParent, 100, 0, 0);
        scene.PositionEntity(child, 3, 0, 0);

        scene.EntityParent(child, secondParent, global: false);
        Assert.Equal(secondParent, scene.GetParent(child));
        Assert.Equal(3, scene.EntityX(child));
        Assert.Equal(103, scene.EntityX(child, global: true));

        scene.EntityParent(child, firstParent);
        Assert.Equal(103, scene.EntityX(child, global: true));
        Assert.Equal(93, scene.EntityX(child));
    }

    [Fact]
    public void Parent_default_is_global_and_descendant_cycles_are_rejected()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var child = scene.CreatePivot();
        scene.PositionEntity(parent, 20, 0, 0);
        scene.PositionEntity(child, 5, 0, 0);

        scene.EntityParent(child, parent);
        Assert.Equal(5, scene.EntityX(child, global: true));
        Assert.Equal(-15, scene.EntityX(child));
        Assert.Throws<ArgumentException>(() => scene.EntityParent(parent, child, global: false));
        Assert.Throws<ArgumentException>(() => scene.EntityParent(child, child));
    }

    [Fact]
    public void Free_entity_recursively_removes_descendants_and_invalid_handles_throw()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var child = scene.CreatePivot(parent);
        var grandchild = scene.CreatePivot(child);
        scene.FreeEntity(child);
        Assert.Equal(0, scene.CountChildren(parent));
        Assert.Throws<KeyNotFoundException>(() => scene.EntityX(child));
        Assert.Throws<KeyNotFoundException>(() => scene.FreeEntity(grandchild));
        Assert.Throws<KeyNotFoundException>(() => scene.FreeEntity(int.MaxValue));
    }

    [Fact]
    public void Child_enumeration_and_recursive_name_search_follow_insertion_order()
    {
        var scene = new SceneGraph();
        var root = scene.CreatePivot();
        var first = scene.CreatePivot(root);
        var second = scene.CreatePivot(root);
        var nested = scene.CreatePivot(first);
        scene.NameEntity(first, "left");
        scene.NameEntity(second, "right");
        scene.NameEntity(nested, "hand");

        Assert.Equal(2, scene.CountChildren(root));
        Assert.Equal(first, scene.GetChild(root, 1));
        Assert.Equal(second, scene.GetChild(root, 2));
        Assert.Equal(0, scene.GetChild(root, 3));
        Assert.Equal("left", scene.EntityName(first));
        Assert.Equal(nested, scene.FindChild(root, "hand"));
        Assert.Equal(first, scene.FindChild(root, "left"));
        Assert.Equal(0, scene.FindChild(root, "LEFT"));
        Assert.Equal(1, scene.CountChildren(first));
    }

    [Fact]
    public void Copy_entity_deep_clones_pivots_in_child_order_and_keeps_local_transforms()
    {
        var scene = new SceneGraph();
        var sourceParent = scene.CreatePivot();
        var source = scene.CreatePivot(sourceParent);
        var firstChild = scene.CreatePivot(source);
        var secondChild = scene.CreatePivot(source);
        var grandchild = scene.CreatePivot(firstChild);
        var destinationParent = scene.CreatePivot();
        scene.PositionEntity(sourceParent, 10, 0, 0);
        scene.PositionEntity(source, 2, 3, 4);
        scene.PositionEntity(firstChild, 5, 0, 0);
        scene.PositionEntity(secondChild, 0, 6, 0);
        scene.PositionEntity(grandchild, 0, 0, 7);
        scene.PositionEntity(destinationParent, 100, 0, 0);
        scene.NameEntity(source, "source");
        scene.NameEntity(firstChild, "first");
        scene.NameEntity(grandchild, "nested");

        var copy = scene.CopyEntity(source, destinationParent);

        Assert.NotEqual(source, copy);
        Assert.Equal(destinationParent, scene.GetParent(copy));
        Assert.Equal("source", scene.EntityName(copy));
        Assert.Equal(2, scene.CountChildren(copy));
        var copiedFirst = scene.GetChild(copy, 1);
        var copiedSecond = scene.GetChild(copy, 2);
        var copiedGrandchild = scene.GetChild(copiedFirst, 1);
        Assert.Equal("first", scene.EntityName(copiedFirst));
        Assert.Equal("nested", scene.EntityName(copiedGrandchild));
        Assert.Equal(5, scene.EntityX(copiedFirst));
        Assert.Equal(6, scene.EntityY(copiedSecond));
        Assert.Equal(7, scene.EntityZ(copiedGrandchild));
        AssertVector((102, 3, 4), (scene.EntityX(copy, true), scene.EntityY(copy, true), scene.EntityZ(copy, true)));
        AssertVector((107, 3, 4), (scene.EntityX(copiedFirst, true), scene.EntityY(copiedFirst, true), scene.EntityZ(copiedFirst, true)));

        scene.PositionEntity(firstChild, 50, 0, 0);
        scene.NameEntity(source, "changed");
        Assert.Equal(5, scene.EntityX(copiedFirst));
        Assert.Equal("source", scene.EntityName(copy));
        Assert.NotEqual(firstChild, copiedFirst);
        Assert.NotEqual(secondChild, copiedSecond);
        Assert.NotEqual(grandchild, copiedGrandchild);
    }

    [Fact]
    public void Copy_entity_defaults_to_root_and_attaches_only_after_snapshotting_source_subtree()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var child = scene.CreatePivot(source);
        var grandchild = scene.CreatePivot(child);
        scene.PositionEntity(source, 4, 0, 0);
        scene.PositionEntity(child, 2, 0, 0);

        var rootCopy = scene.CopyEntity(source);
        Assert.Equal(0, scene.GetParent(rootCopy));
        Assert.Equal(4, scene.EntityX(rootCopy));
        Assert.Equal(1, scene.CountChildren(rootCopy));

        // Attaching the copy below an original descendant must not make the copy operation
        // traverse the newly attached subtree or recursively copy its own result.
        var nestedCopy = scene.CopyEntity(source, grandchild);
        Assert.Equal(grandchild, scene.GetParent(nestedCopy));
        Assert.Equal(nestedCopy, scene.GetChild(grandchild, 1));
        Assert.Equal(1, scene.CountChildren(nestedCopy));
        Assert.NotEqual(scene.GetChild(nestedCopy, 1), scene.GetChild(child, 1));
    }

    [Fact]
    public void Copy_entity_validates_source_and_destination_before_mutating_scene()
    {
        var scene = new SceneGraph();
        var source = scene.CreatePivot();
        var before = scene.CreatePivot();

        Assert.Throws<KeyNotFoundException>(() => scene.CopyEntity(int.MaxValue));
        Assert.Throws<KeyNotFoundException>(() => scene.CopyEntity(source, int.MaxValue));
        Assert.Equal(0, scene.GetParent(source));
        Assert.Equal(0, scene.CountChildren(source));
        Assert.Equal(before + 1, scene.CreatePivot());
    }

    [Fact]
    public void Subtree_entity_ids_are_depth_first_and_root_first()
    {
        var scene = new SceneGraph();
        var root = scene.CreatePivot();
        var first = scene.CreatePivot(root);
        var second = scene.CreatePivot(root);
        var nested = scene.CreatePivot(first);

        Assert.Equal(new[] { root, first, nested, second }, scene.GetSubtreeEntityIds(root));
    }

    [Fact]
    public void Legacy_copy_entity_uses_the_selected_scene()
    {
        var scene = new SceneGraph();
        var previous = LegacyScene.Current;
        try
        {
            LegacyScene.Current = scene;
            var source = LegacyScene.CreatePivot();
            var child = LegacyScene.CreatePivot(source);
            LegacyScene.NameEntity(child, "child");

            var copy = LegacyScene.CopyEntity(source);

            Assert.Equal(0, LegacyScene.GetParent(copy));
            Assert.Equal("child", LegacyScene.EntityName(LegacyScene.GetChild(copy, 1)));
        }
        finally
        {
            LegacyScene.Current = previous;
        }
    }

    [Fact]
    public void Distance_uses_world_positions()
    {
        var scene = new SceneGraph();
        var root = scene.CreatePivot();
        var first = scene.CreatePivot(root);
        var second = scene.CreatePivot();
        scene.PositionEntity(root, 10, 0, 0);
        scene.PositionEntity(first, 3, 0, 0);
        scene.PositionEntity(second, 0, 4, 0);

        Assert.Equal(MathF.Sqrt(185), scene.EntityDistance(first, second), 5);
    }

    [Fact]
    public void Global_scale_is_componentwise_and_legacy_facade_uses_selected_scene()
    {
        var scene = new SceneGraph();
        var parent = scene.CreatePivot();
        var child = scene.CreatePivot(parent);
        scene.ScaleEntity(parent, 2, 3, 4);
        scene.ScaleEntity(child, 5, 6, 7);
        AssertVector((10, 18, 28), (scene.EntityScaleX(child, true), scene.EntityScaleY(child, true), scene.EntityScaleZ(child, true)));
        scene.ScaleEntity(child, 20, 30, 40, global: true);
        AssertVector((20, 30, 40), (scene.EntityScaleX(child, true), scene.EntityScaleY(child, true), scene.EntityScaleZ(child, true)));

        var previous = LegacyScene.Current;
        try
        {
            LegacyScene.Current = scene;
            var outer = LegacyScene.CreatePivot();
            var created = LegacyScene.CreatePivot(outer);
            LegacyScene.PositionEntity(outer, 100, 0, 0);
            LegacyScene.PositionEntity(created, 7, 8, 9);
            Assert.Equal(7, LegacyScene.EntityX(created));
            Assert.Equal(107, LegacyScene.EntityX(created, 1));
            Assert.Equal(8, LegacyScene.EntityY(created));
            Assert.Equal(9, LegacyScene.EntityZ(created));
            LegacyScene.EntityParent(created, 0);
            Assert.Equal(0, LegacyScene.GetParent(created));
            Assert.Equal(107, LegacyScene.EntityX(created));
        }
        finally
        {
            LegacyScene.Current = previous;
        }
    }

    private static void AssertVector((float X, float Y, float Z) expected, (float X, float Y, float Z) actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
