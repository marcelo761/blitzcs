using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

/// <summary>
/// Native reproduction of the game's AALoadFont: one buffer (the glyph atlas texture) stays locked while another
/// buffer is selected and drawn into. In the original runtime lock state is per canvas (gxcanvas.cpp lock/unlock) and
/// bbSetBuffer/bbCls/bbText never look at it (bbgraphics.cpp), so this must work; only drawing into the locked
/// buffer itself stays rejected by the adapter.
/// </summary>
public sealed class LegacyGraphicsAaTextSmokeTests
{
    private const int AtlasSize = 64;
    private const int CharSize = 32;

    /// <summary>Report lines (hash=) produced by the last successful smoke run.</summary>
    internal static string LastReport { get; private set; } = "";

    [WindowsAaTextSmokeFact]
    public void Drawing_into_another_buffer_is_allowed_while_a_texture_buffer_is_locked() => RunSmoke();

    internal static void RunSmoke()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(96, 96, 0, 2);
            var font = LegacyGraphics.LoadFont("courier", 16);
            var atlas = LegacyGraphics.CreateTexture(AtlasSize, AtlasSize, 3);
            var atlasBuffer = LegacyGraphics.TextureBuffer(atlas);
            var charImage = LegacyGraphics.CreateImage(CharSize, CharSize);
            var charBuffer = LegacyGraphics.ImageBuffer(charImage);
            var backBuffer = LegacyGraphics.BackBuffer();

            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.LockBuffer(atlasBuffer);
            for (var x = 0; x < AtlasSize; x++)
                for (var y = 0; y < AtlasSize; y++)
                    LegacyGraphics.WritePixelFast(x, y, 0x00FFFFFF, atlasBuffer);

            // SetBuffer onto another buffer while the atlas is locked, then draw into it.
            LegacyGraphics.SetBuffer(charBuffer);
            LegacyGraphics.Cls();
            LegacyGraphics.Color(255, 255, 255);
            LegacyGraphics.SetFont(font);
            LegacyGraphics.Text(2, 2, "A");
            LegacyGraphics.Rect(24, 24, 4, 4);

            // The locked buffer itself still rejects drawing-pipeline operations.
            LegacyGraphics.SetBuffer(atlasBuffer);
            Assert.Throws<InvalidOperationException>(LegacyGraphics.Cls);
            Assert.Throws<InvalidOperationException>(() => LegacyGraphics.Rect(0, 0, 2, 2));
            Assert.Throws<InvalidOperationException>(() => LegacyGraphics.Text(0, 0, "A"));
            Assert.Throws<InvalidOperationException>(() => LegacyGraphics.LockBuffer(atlasBuffer));
            LegacyGraphics.SetBuffer(backBuffer);

            // Read the glyph back like AALoadFont does and write coverage into the atlas.
            LegacyGraphics.LockBuffer(charBuffer);
            var ink = 0;
            for (var y = 0; y < CharSize; y++)
                for (var x = 0; x < CharSize; x++)
                {
                    var coverage = LegacyGraphics.ReadPixelFast(x, y, charBuffer) & 0xFF;
                    if (coverage != 0) ink++;
                    LegacyGraphics.WritePixelFast(x, y, 0x00FFFFFF + (coverage << 24), atlasBuffer);
                }
            LegacyGraphics.UnlockBuffer(charBuffer);
            Assert.InRange(ink, 20, CharSize * CharSize - 1);
            Assert.Equal(unchecked((int)0xFFFFFFFF), ReadLocked(charBuffer, 25, 25));

            // CopyRect from the (unlocked) image into the locked atlas lands in the locked pixels.
            LegacyGraphics.CopyRect(24, 24, 4, 4, 40, 40, charBuffer, atlasBuffer);
            Assert.Equal(unchecked((int)0xFFFFFFFF), LegacyGraphics.ReadPixelFast(41, 41, atlasBuffer));
            LegacyGraphics.UnlockBuffer(atlasBuffer);

            // The atlas upload reached the GPU; the image buffer was not overwritten by it.
            var hash = new StringBuilder();
            LegacyGraphics.LockBuffer(atlasBuffer);
            var atlasInk = 0;
            for (var y = 0; y < AtlasSize; y++)
                for (var x = 0; x < AtlasSize; x++)
                {
                    var pixel = LegacyGraphics.ReadPixelFast(x, y, atlasBuffer);
                    if (x < CharSize && y < CharSize)
                        Assert.Equal(ReadLocked(charBuffer, x, y) & 0xFF, (pixel >> 24) & 0xFF);
                    if ((pixel >>> 24) != 0) atlasInk++;
                    hash.Append(pixel.ToString("X8", CultureInfo.InvariantCulture)).Append(',');
                }
            LegacyGraphics.UnlockBuffer(atlasBuffer);
            Assert.Equal(unchecked((int)0xFFFFFFFF), ReadLocked(atlasBuffer, 41, 41));
            Assert.Equal(unchecked((int)0xFFFFFFFF), ReadLocked(charBuffer, 25, 25));
            Assert.True(atlasInk >= ink);
            LegacyGraphics.FreeImage(charImage);
            LegacyGraphics.FreeTexture(atlas);
            LastReport = $"\nhash={Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(hash.ToString())))}\n";
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }

    private static int ReadLocked(int buffer, int x, int y)
    {
        LegacyGraphics.LockBuffer(buffer);
        try { return LegacyGraphics.ReadPixelFast(x, y, buffer); }
        finally { LegacyGraphics.UnlockBuffer(buffer); }
    }

    private sealed class WindowsAaTextSmokeFactAttribute : FactAttribute
    {
        public WindowsAaTextSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real D3D11 AALoadFont-style smoke test.";
        }
    }
}
