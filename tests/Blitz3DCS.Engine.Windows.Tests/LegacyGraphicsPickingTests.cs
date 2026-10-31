using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsPickingTests
{
    [Fact]
    public void Pick_only_geometry_updates_without_world_step_and_camera_uses_viewport_relative_pixels()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraViewport(camera, 30, 40, 800, 600);
            var mesh = LegacyGraphics.CreateCube();
            LegacyScene.PositionEntity(mesh, 0, 0, 5);
            LegacyScene.EntityPickMode(mesh, 2);
            Assert.Equal(mesh, LegacyGraphics.CameraPick(camera, 400, 300));
            Assert.Equal(4, LegacyScene.PickedZ(), 4);
            var surface = LegacyGraphics.GetSurface(mesh, 1);
            for (var index = 0; index < LegacyGraphics.CountVertices(surface); index++)
                LegacyGraphics.VertexCoords(surface, index, 100, 100, 100);
            // Editing all geometry must invalidate the cached pick BVH before the next query.
            LegacyGraphics.PositionMesh(mesh, 100, 0, 0);
            Assert.Equal(0, LegacyGraphics.CameraPick(camera, 400, 300));
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }
}
