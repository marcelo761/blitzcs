using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsCameraQueryTests
{
    [Fact]
    public void Projection_uses_local_viewport_pixels_and_near_plane_depth()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraViewport(camera, 17, 29, 800, 600);
            LegacyGraphics.CameraRange(camera, 2, 100);
            LegacyScene.PositionEntity(camera, 10, 0, 0);
            LegacyGraphics.CameraProject(camera, 12, 0, 4);
            Assert.Equal(600, LegacyGraphics.ProjectedX(), 4);
            Assert.Equal(300, LegacyGraphics.ProjectedY(), 4);
            Assert.Equal(2, LegacyGraphics.ProjectedZ());
            LegacyGraphics.CameraProject(camera, 10, 0, -1);
            Assert.Equal(0, LegacyGraphics.ProjectedX());
            Assert.Equal(0, LegacyGraphics.ProjectedZ());
            LegacyGraphics.CameraProjMode(camera, 2);
            LegacyGraphics.CameraProject(camera, 12, 0, -1);
            Assert.Equal(800, LegacyGraphics.ProjectedX(), 4);
            Assert.Equal(2, LegacyGraphics.ProjectedZ());
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previousScene; }
    }

    [Fact]
    public void In_view_uses_mesh_extents_ignores_visibility_and_cull_override()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraViewport(camera, 0, 0, 800, 600);
            var mesh = LegacyGraphics.CreateCube();
            LegacyScene.PositionEntity(mesh, 0, 0, 5);
            LegacyScene.HideEntity(mesh);
            Assert.Equal(1, LegacyGraphics.EntityInView(mesh, camera));
            LegacyGraphics.MeshCullBox(mesh, 100, 100, 100, 1, 1, 1);
            Assert.Equal(1, LegacyGraphics.EntityInView(mesh, camera));
            LegacyScene.PositionEntity(mesh, 100, 0, 5);
            Assert.Equal(0, LegacyGraphics.EntityInView(mesh, camera));
            LegacyScene.PositionEntity(mesh, 5.5f, 0, 5);
            Assert.Equal(1, LegacyGraphics.EntityInView(mesh, camera));
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previousScene; }
    }
}
