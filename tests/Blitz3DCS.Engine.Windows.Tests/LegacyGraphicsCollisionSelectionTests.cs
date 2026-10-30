using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsCollisionSelectionTests
{
    [Fact]
    public void Polygon_pick_cache_tracks_pick_mode_and_still_syncs_geometry_edits()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            var target = CreateTriangle();
            Assert.Equal(0, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            Assert.Equal(0, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            scene.EntityPickMode(target, 2);
            Assert.Equal(target, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            Assert.Equal(1, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            scene.EntityPickMode(target, 0);
            Assert.Equal(0, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            Assert.Equal(0, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            scene.EntityPickMode(target, 2);
            Assert.Equal(target, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            var surface = LegacyGraphics.GetSurface(target, 1);
            for (var vertex = 0; vertex < 3; vertex++)
                LegacyGraphics.VertexCoords(surface, vertex, vertex == 1 ? 0 : vertex == 0 ? -10 : 10,
                    vertex == 1 ? 10 : -10, 5);
            Assert.Equal(0, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));

            for (var vertex = 0; vertex < 3; vertex++)
                LegacyGraphics.VertexCoords(surface, vertex, vertex == 1 ? 0 : vertex == 0 ? -10 : 10,
                    vertex == 1 ? 10 : -10, 0);
            Assert.Equal(target, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }

    [Fact]
    public void Polygon_pick_cache_tracks_entity_type_and_collision_rule_changes()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            var source = scene.CreatePivot();
            scene.EntityType(source, 1);
            var target = CreateTriangle();
            scene.Collisions(1, 2, 2, 1);

            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            scene.EntityType(target, 2);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(1, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            scene.ClearCollisions();
            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, LegacyGraphics.PolygonCollisionMeshCountForTesting);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }

    [Fact]
    public void Copy_free_and_same_count_mesh_replacement_refresh_and_release_selection_cache()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            var original = CreateTriangle();
            scene.EntityPickMode(original, 2);
            Assert.Equal(original, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            Assert.Equal(1, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            var copy = LegacyGraphics.CopyEntity(original);
            Assert.Equal(copy, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            Assert.Equal(2, LegacyGraphics.PolygonCollisionMeshCountForTesting);

            LegacyGraphics.FreeEntity(original);
            Assert.Equal(0, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            var replacement = CreateTriangle();
            scene.PositionEntity(replacement, 100, 0, 0);
            scene.EntityPickMode(replacement, 2);
            Assert.Equal(copy, LegacyGraphics.LinePick(0, 0, -1, 0, 0, 2));
            Assert.Equal(2, LegacyGraphics.PolygonCollisionMeshCountForTesting);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }

    private static int CreateTriangle()
    {
        var mesh = LegacyGraphics.CreateMesh();
        var surface = LegacyGraphics.CreateSurface(mesh);
        LegacyGraphics.AddVertex(surface, -10, -10, 0);
        LegacyGraphics.AddVertex(surface, 0, 10, 0);
        LegacyGraphics.AddVertex(surface, 10, -10, 0);
        LegacyGraphics.AddTriangle(surface, 0, 1, 2);
        return mesh;
    }
}
