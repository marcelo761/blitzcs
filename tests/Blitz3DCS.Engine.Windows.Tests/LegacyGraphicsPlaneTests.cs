using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsPlaneTests
{
    [Fact]
    public void Plane_copy_retains_analytic_geometry_without_a_world_step()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var plane = LegacyGraphics.CreatePlane(2);
            LegacyScene.EntityPickMode(plane, 2);
            var copy = LegacyGraphics.CopyEntity(plane);
            LegacyScene.PositionEntity(copy, 0, 10, 0);
            Assert.Equal(copy, LegacyGraphics.LinePick(50000, 20, 50000, 0, -15, 0));
            Assert.Equal(10, LegacyScene.PickedY(), 4);
            LegacyGraphics.FreeEntity(plane);
            Assert.Equal(copy, LegacyGraphics.LinePick(-50000, 20, -50000, 0, -15, 0));
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }

    internal static void RunSmoke()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(128, 96);
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraRange(camera, .1f, 100);
            LegacyGraphics.CameraClsColor(camera, 255, 0, 0);
            LegacyScene.PositionEntity(camera, 0, 5, 0);
            var plane = LegacyGraphics.CreatePlane(2);
            LegacyGraphics.EntityFX(plane, 1);
            var texture = LegacyGraphics.CreateTexture(2, 2, 1);
            LegacyGraphics.SetBuffer(LegacyGraphics.TextureBuffer(texture));
            LegacyGraphics.ClsColor(0, 0, 255);
            LegacyGraphics.Cls();
            LegacyGraphics.SetBuffer(LegacyGraphics.BackBuffer());
            LegacyGraphics.EntityTexture(plane, texture);
            LegacyGraphics.FreeTexture(texture);
            LegacyGraphics.RenderWorld();
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(64, 80).B);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(64, 16).R);
            LegacyScene.PositionEntity(camera, 50000, 5, 50000);
            LegacyGraphics.RenderWorld();
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(64, 80).B);
            LegacyScene.PositionEntity(camera, 0, -5, 0);
            LegacyGraphics.RenderWorld();
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(64, 80).R);
            Assert.Equal(0, LegacyGraphics.TrisRendered());
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }
}
