using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsRenderQueueTests
{
    internal static void RunSmoke()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(128, 96);
            var camera = LegacyGraphics.CreateCamera();
            LegacyScene.PositionEntity(camera, 0, 0, -5);
            LegacyGraphics.CameraRange(camera, .1f, 100);
            var near = LegacyGraphics.CreateCube();
            var far = LegacyGraphics.CreateCube();
            LegacyScene.PositionEntity(far, 0, 0, 3);
            LegacyGraphics.EntityFX(near, 1);
            LegacyGraphics.EntityFX(far, 1);
            LegacyGraphics.EntityColor(near, 255, 0, 0);
            LegacyGraphics.EntityColor(far, 0, 255, 0);
            LegacyGraphics.EntityAlpha(near, .5f);
            LegacyGraphics.EntityAlpha(far, .5f);
            LegacyGraphics.RenderWorld();
            var pixel = LegacyGraphics.ReadPixelForTesting(64, 48);
            Assert.InRange(pixel.R, (byte)126, (byte)130);
            Assert.InRange(pixel.G, (byte)62, (byte)66);
            Assert.Equal((byte)0, pixel.B);
            Assert.Equal(24, LegacyGraphics.TrisRendered());

            LegacyGraphics.EntityAlpha(near, 1);
            LegacyGraphics.EntityAlpha(far, 1);
            LegacyGraphics.EntityOrder(far, -1);
            LegacyGraphics.RenderWorld();
            pixel = LegacyGraphics.ReadPixelForTesting(64, 48);
            Assert.Equal((byte)0, pixel.R);
            Assert.Equal((byte)255, pixel.G);
            LegacyGraphics.EntityOrder(far, 1);
            LegacyGraphics.RenderWorld();
            pixel = LegacyGraphics.ReadPixelForTesting(64, 48);
            Assert.Equal((byte)255, pixel.R);
            Assert.Equal((byte)0, pixel.G);

            LegacyScene.HideEntity(near);
            LegacyGraphics.EntityOrder(far, 0);
            LegacyGraphics.EntityAutoFade(far, 4, 12);
            LegacyGraphics.RenderWorld();
            pixel = LegacyGraphics.ReadPixelForTesting(64, 48);
            Assert.InRange(pixel.G, (byte)126, (byte)130);
            LegacyScene.PositionEntity(far, 0, 0, 7);
            LegacyGraphics.RenderWorld();
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(64, 48).G);
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }
}
