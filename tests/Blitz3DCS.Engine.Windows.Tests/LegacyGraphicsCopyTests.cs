using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[CollectionDefinition("LegacyGraphics static state", DisableParallelization = true)]
public sealed class LegacyGraphicsStaticStateCollection
{
}

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsCopyTests
{
    [Fact]
    public void Copy_entity_clones_subtree_transforms_and_names_but_shares_surfaces_until_last_mesh_is_freed()
    {
        WithCleanState(scene =>
        {
            var source = LegacyGraphics.CreateMesh();
            LegacyScene.NameEntity(source, "source mesh");
            LegacyScene.PositionEntity(source, 1, 2, 3);
            LegacyScene.RotateEntity(source, 10, 20, 30);
            LegacyScene.ScaleEntity(source, 2, 3, 4);

            var sourceSurface = LegacyGraphics.CreateSurface(source);
            AddTriangle(sourceSurface);

            var pivot = LegacyScene.CreatePivot(source);
            LegacyScene.NameEntity(pivot, "nested pivot");
            LegacyScene.PositionEntity(pivot, 4, 5, 6);
            var nestedMesh = LegacyGraphics.CreateMesh(pivot);
            LegacyScene.NameEntity(nestedMesh, "nested mesh");
            var nestedSurface = LegacyGraphics.CreateSurface(nestedMesh);
            AddTriangle(nestedSurface);

            var copy = LegacyGraphics.CopyEntity(source);
            var copiedPivot = LegacyScene.GetChild(copy, 1);
            var copiedNestedMesh = LegacyScene.GetChild(copiedPivot, 1);

            Assert.NotEqual(source, copy);
            Assert.Equal(0, LegacyScene.GetParent(copy));
            Assert.Equal("source mesh", LegacyScene.EntityName(copy));
            Assert.Equal("nested pivot", LegacyScene.EntityName(copiedPivot));
            Assert.Equal("nested mesh", LegacyScene.EntityName(copiedNestedMesh));
            Assert.Equal((1f, 2f, 3f), (LegacyScene.EntityX(copy), LegacyScene.EntityY(copy), LegacyScene.EntityZ(copy)));
            Assert.Equal(10, LegacyScene.EntityPitch(copy), 4);
            Assert.Equal(20, LegacyScene.EntityYaw(copy), 4);
            Assert.Equal(30, LegacyScene.EntityRoll(copy), 4);
            Assert.Equal((2f, 3f, 4f), (LegacyScene.EntityScaleX(copy), LegacyScene.EntityScaleY(copy), LegacyScene.EntityScaleZ(copy)));
            Assert.Equal((4f, 5f, 6f), (LegacyScene.EntityX(copiedPivot), LegacyScene.EntityY(copiedPivot), LegacyScene.EntityZ(copiedPivot)));
            Assert.Equal(sourceSurface, LegacyGraphics.GetSurface(copy, 1));
            Assert.Equal(nestedSurface, LegacyGraphics.GetSurface(copiedNestedMesh, 1));

            LegacyScene.TranslateEntity(copy, 10, 0, 0);
            Assert.Equal(11, LegacyScene.EntityX(copy));
            Assert.Equal(1, LegacyScene.EntityX(source));

            var copySurface = LegacyGraphics.CreateSurface(copy);
            Assert.Equal(2, LegacyGraphics.CountSurfaces(source));
            Assert.Equal(copySurface, LegacyGraphics.GetSurface(source, 2));
            Assert.Equal(3, LegacyGraphics.AddVertex(sourceSurface, 3, 4, 5));
            Assert.Equal(4, LegacyGraphics.CountVertices(LegacyGraphics.GetSurface(copy, 1)));

            LegacyGraphics.FreeEntity(source);
            Assert.Equal(2, LegacyGraphics.CountSurfaces(copy));
            Assert.Equal(4, LegacyGraphics.CountVertices(sourceSurface));
            Assert.Equal(0, LegacyGraphics.AddVertex(copySurface, 6, 7, 8));

            LegacyGraphics.FreeEntity(copy);
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CountVertices(sourceSurface));
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CountVertices(copySurface));
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CountVertices(nestedSurface));
        });
    }

    [Fact]
    public void Copy_entity_rejects_a_camera_subtree_without_mutating_the_scene()
    {
        WithCleanState(_ =>
        {
            var root = LegacyScene.CreatePivot();
            var mesh = LegacyGraphics.CreateMesh(root);
            var camera = LegacyGraphics.CreateCamera(mesh);
            var before = LegacyScene.Current.GetSubtreeEntityIds(root);

            Assert.Throws<NotSupportedException>(() => LegacyGraphics.CopyEntity(root));

            Assert.Equal(before, LegacyScene.Current.GetSubtreeEntityIds(root));
            Assert.Equal(camera + 1, LegacyScene.Current.CreatePivot());
        });
    }

    private static void AddTriangle(int surface)
    {
        LegacyGraphics.AddVertex(surface, 0, 0, 0);
        LegacyGraphics.AddVertex(surface, 1, 0, 0);
        LegacyGraphics.AddVertex(surface, 0, 1, 0);
        LegacyGraphics.AddTriangle(surface, 0, 1, 2);
    }

    private static void WithCleanState(Action<SceneGraph> action)
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            action(scene);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }
}
