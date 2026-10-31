using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsWindowsSmokeTests
{
    [WindowsD3D11SmokeFact]
    public void Legacy_camera_renders_a_scene_graph_cube_to_the_window_backbuffer()
    {
        RunSmoke();
    }

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(320, 240);
            var camera = LegacyGraphics.CreateCamera();
            LegacyScene.Current.PositionEntity(camera, 0, 0, -5);
            LegacyGraphics.CameraRange(camera, 0.1f, 100);
            LegacyGraphics.CameraClsColor(camera, 12, 18, 28);
            var cube = LegacyGraphics.CreateCube();
            LegacyScene.Current.TurnEntity(cube, 0, 24, 0);

            LegacyGraphics.RenderWorld();
            var clear = LegacyGraphics.ReadPixelForTesting(4, 4);
            var rendered = LegacyGraphics.ReadPixelForTesting(160, 120);
            Assert.Equal((byte)12, clear.R);
            Assert.Equal((byte)18, clear.G);
            Assert.Equal((byte)28, clear.B);
            Assert.NotEqual(clear, rendered);

            LegacyGraphics.CameraZoom(camera, 2f);
            LegacyGraphics.RenderWorld();
            var zoomedEdge = LegacyGraphics.ReadPixelForTesting(220, 120);
            Assert.NotEqual(clear, zoomedEdge);

            LegacyGraphics.CameraProjMode(camera, 2);
            LegacyGraphics.RenderWorld();
            Assert.NotEqual(clear, LegacyGraphics.ReadPixelForTesting(160, 120));

            LegacyGraphics.CameraClsColor(camera, 200, 100, 50);
            var beforeDisabledCamera = LegacyGraphics.ReadPixelForTesting(4, 4);
            LegacyGraphics.CameraProjMode(camera, 0);
            LegacyGraphics.RenderWorld();
            Assert.Equal(beforeDisabledCamera, LegacyGraphics.ReadPixelForTesting(4, 4));
            LegacyGraphics.Flip(sync: 0);

            LegacyGraphics.FreeEntity(cube);
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CountSurfaces(cube));
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    private sealed class WindowsD3D11SmokeFactAttribute : FactAttribute
    {
        public WindowsD3D11SmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real device/window smoke test.";
        }
    }
}
