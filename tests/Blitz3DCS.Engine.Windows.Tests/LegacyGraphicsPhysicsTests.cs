using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsPhysicsTests
{
    [Fact]
    public void World_update_connects_mesh_collision_surface_and_refreshes_edited_geometry()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var source = LegacyScene.CreatePivot();
            LegacyScene.EntityType(source, 1);
            LegacyScene.EntityRadius(source, .5f);
            LegacyScene.PositionEntity(source, 0, 0, -3);
            var target = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(target);
            LegacyGraphics.AddVertex(surface, -10, -10, 0);
            LegacyGraphics.AddVertex(surface, 0, 10, 0);
            LegacyGraphics.AddVertex(surface, 10, -10, 0);
            LegacyGraphics.AddTriangle(surface, 0, 1, 2);
            LegacyScene.EntityType(target, 2);
            LegacyScene.Collisions(1, 2, 2, 1);
            LegacyGraphics.UpdateWorld();
            LegacyScene.PositionEntity(source, 0, 0, 3);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(target, LegacyScene.CollisionEntity(source, 1));
            Assert.Equal(surface, LegacyScene.CollisionSurface(source, 1));
            Assert.Equal(0, LegacyScene.CollisionTriangle(source, 1));
            Assert.InRange(LegacyScene.EntityZ(source), -.502f, -.498f);

            // Geometry edits must replace cached triangles even if no GPU upload occurred.
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var position = LegacyGraphics.GetVertexForTesting(surface, vertex).Position;
                LegacyGraphics.VertexCoords(surface, vertex, position.X, position.Y, 5);
            }
            LegacyScene.PositionEntity(source, 0, 0, -3);
            LegacyGraphics.UpdateWorld();
            LegacyScene.PositionEntity(source, 0, 0, 3);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, LegacyScene.CountCollisions(source));
            Assert.Equal(3, LegacyScene.EntityZ(source));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }
}
