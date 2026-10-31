using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

/// <summary>The surface mutation epoch must never let a stale collision-triangle snapshot survive a geometry edit.</summary>
[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsCollisionEpochCacheTests
{
    [Fact]
    public void Geometry_edits_after_idle_frames_refresh_collision_triangles()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            LegacyGraphics.AddVertex(surface, -10, -10, 0);
            LegacyGraphics.AddVertex(surface, 0, 10, 0);
            LegacyGraphics.AddVertex(surface, 10, -10, 0);
            LegacyGraphics.AddTriangle(surface, 0, 1, 2);
            var source = scene.CreatePivot();
            scene.EntityType(source, 1);
            scene.EntityRadius(source, 1);
            scene.EntityType(mesh, 2);
            scene.Collisions(1, 2, 2, 1);
            scene.PositionEntity(source, 0, 0, -5);
            LegacyGraphics.UpdateWorld();
            LegacyGraphics.UpdateWorld(); // idle frame: the epoch fast path is taken

            for (var vertex = 0; vertex < 3; vertex++)
                LegacyGraphics.VertexCoords(surface, vertex, vertex == 0 ? -10 : vertex == 1 ? 0 : 10, vertex == 1 ? 10 : -10, 100);
            scene.PositionEntity(source, 0, 0, 5);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, scene.CountCollisions(source));

            for (var vertex = 0; vertex < 3; vertex++)
                LegacyGraphics.VertexCoords(surface, vertex, vertex == 0 ? -10 : vertex == 1 ? 0 : 10, vertex == 1 ? 10 : -10, 0);
            scene.PositionEntity(source, 0, 0, -5);
            scene.ResetEntity(source);
            scene.PositionEntity(source, 0, 0, 5);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(1, scene.CountCollisions(source));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }
}
