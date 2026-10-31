using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsFontSmokeTests
{
    [WindowsFontSmokeFact]
    public void Gdi_font_atlas_draws_cached_glyphs_to_backbuffer_and_image_buffers() => RunSmoke();

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(160, 96, 0, 2);
            var font = LegacyGraphics.LoadFont("courier", 16);
            Assert.True(font > 0);
            LegacyGraphics.SetFont(font);
            Assert.Equal(font, LegacyGraphics.GetFont());
            Assert.True(LegacyGraphics.FontWidth() > 0);
            Assert.InRange(LegacyGraphics.FontHeight(), 1, 32);
            Assert.Equal(LegacyGraphics.StringWidth("A") + LegacyGraphics.StringWidth("B"), LegacyGraphics.StringWidth("AB"));
            Assert.Equal(LegacyGraphics.FontHeight(), LegacyGraphics.StringHeight("AB\nCD"));
            var width = LegacyGraphics.StringWidth("AB€");
            var height = LegacyGraphics.FontHeight();
            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.Cls();
            LegacyGraphics.Color(255, 200, 50);
            LegacyGraphics.Text(0, 0, "AB€");
            var backbufferInk = CountInk(width, height);
            Assert.InRange(backbufferInk, 1, width * height - 1);

            var image = LegacyGraphics.CreateImage(width, height);
            LegacyGraphics.SetBuffer(LegacyGraphics.ImageBuffer(image));
            LegacyGraphics.Cls();
            LegacyGraphics.Text(0, 0, "AB€");
            Assert.Equal(backbufferInk, CountInk(width, height));
            LegacyGraphics.SetBuffer(LegacyGraphics.BackBuffer());
            LegacyGraphics.FreeImage(image);
            LegacyGraphics.FreeFont(font);
            Assert.NotEqual(font, LegacyGraphics.GetFont());
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    private static int CountInk(int width, int height)
    {
        LegacyGraphics.LockBuffer();
        try
        {
            var count = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if ((LegacyGraphics.ReadPixelFast(x, y) & 0xffffff) != 0) count++;
            return count;
        }
        finally { LegacyGraphics.UnlockBuffer(); }
    }

    private sealed class WindowsFontSmokeFactAttribute : FactAttribute
    {
        public WindowsFontSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "GDI and D3D11 require Windows.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the native font/device smoke test.";
        }
    }
}
