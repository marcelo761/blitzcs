using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsDrawingTests
{
    [WindowsDrawingSmokeFact]
    public void Rect_and_plot_draw_the_current_color_to_backbuffer_and_selected_texture()
    {
        RunSmoke();
    }

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        var texture = 0;
        try
        {
            LegacyGraphics.Graphics3D(32, 32, 0, 2);
            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.Cls();

            LegacyGraphics.Color(255, 0, 0);
            LegacyGraphics.Rect(2, 3, 5, 4);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(4, 4).R);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(1, 4).R);

            LegacyGraphics.Color(0, 255, 0);
            LegacyGraphics.Rect(10, 3, 5, 4, solid: 0);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(10, 3).G);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(14, 6).G);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(12, 4).G);

            LegacyGraphics.Color(0, 0, 255);
            LegacyGraphics.Plot(20, 10);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(20, 10).B);

            LegacyGraphics.Color(255, 0, 255);
            LegacyGraphics.Line(2, 14, 8, 14);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(2, 14).R);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(8, 14).B);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(9, 14).R);
            LegacyGraphics.Line(2, 2, 6, 4);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(3, 3).R);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(3, 2).R);

            LegacyGraphics.Color(255, 255, 0);
            LegacyGraphics.Oval(10, 20, 8, 6);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(14, 22).R);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(10, 20).R);

            LegacyGraphics.Color(0, 255, 255);
            LegacyGraphics.Oval(20, 20, 8, 8, solid: 0);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(20, 23).G);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(23, 23).G);

            texture = LegacyGraphics.CreateTexture(8, 8, flags: 1);
            var textureBuffer = LegacyGraphics.TextureBuffer(texture);
            LegacyGraphics.SetBuffer(textureBuffer);
            LegacyGraphics.Color(255, 255, 0);
            LegacyGraphics.Rect(1, 1, 3, 3);
            LegacyGraphics.SetBuffer(LegacyGraphics.BackBuffer());
            LegacyGraphics.LockBuffer(textureBuffer);
            Assert.Equal(unchecked((int)0xFFFFFF00), LegacyGraphics.ReadPixelFast(2, 2, textureBuffer));
            Assert.Equal(unchecked((int)0xFF000000), LegacyGraphics.ReadPixelFast(0, 0, textureBuffer));
            LegacyGraphics.UnlockBuffer(textureBuffer);
        }
        finally
        {
            LegacyGraphics.CloseDrawing();
            if (texture != 0) LegacyGraphics.FreeTexture(texture);
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    private sealed class WindowsDrawingSmokeFactAttribute : FactAttribute
    {
        public WindowsDrawingSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real drawing smoke test.";
        }
    }
}
