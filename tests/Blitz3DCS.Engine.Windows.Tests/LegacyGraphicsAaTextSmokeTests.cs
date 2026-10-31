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
            var firstPass = $"\nhash={Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(hash.ToString())))}\n";
            // Second pass (new report line only; the lines above stay byte-identical): Menu.bb's toggle-OFF cleanup.
            LastReport = firstPass + $"pass.cleanup={RunCleanupPass()}\n";
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }

    /// <summary>
    /// Performs Menu.bb's "Antialiased text" OFF cleanup for real, on records shaped like AALoadFont leaves them:
    /// <c>For font.AAFont = Each AAFont : FreeFont font\lowResFont : If (Not AATextEnable) : FreeTexture font\texture :
    /// FreeImage font\backup : EndIf : Delete font : Next</c>. Record A is an AA font (atlas texture and backup image
    /// still referenced by the current buffer, the current font and a pixel lock); record B is the ConsoleFont, loaded
    /// with AATextScaleFactor = 1 so its texture and backup stay 0. The original runtime ignores every free of a dead
    /// or zero handle (bbFreeImage <c>if( !image_set.erase(i) ) return;</c>, bbFreeTexture <c>if( !t ) return;</c> plus
    /// a set erase, gxGraphics::freeFont <c>if( font_set.erase( f ) ) delete f;</c>) and frees a locked or current
    /// canvas without error (~gxCanvas unlocks; bbFreeImage first selects the front canvas), so none of this may throw.
    /// Returns a SHA-256 of the observable facts, which are identical on every run.
    /// </summary>
    private static string RunCleanupPass()
    {
        var backBuffer = LegacyGraphics.BackBuffer();
        var facts = new StringBuilder();

        var aaFont = LegacyGraphics.LoadFont("courier", 16);
        var aaTexture = LegacyGraphics.CreateTexture(AtlasSize, AtlasSize, 3);
        var aaBackup = LegacyGraphics.CreateImage(AtlasSize, AtlasSize);
        var aaTextureBuffer = LegacyGraphics.TextureBuffer(aaTexture);
        var aaBackupBuffer = LegacyGraphics.ImageBuffer(aaBackup);
        var consoleFont = LegacyGraphics.LoadFont("courier", 12);

        // An unrelated record that must survive the cleanup untouched.
        var keepFont = LegacyGraphics.LoadFont("courier", 14);
        var keepTexture = LegacyGraphics.CreateTexture(AtlasSize, AtlasSize, 3);
        var keepBackup = LegacyGraphics.CreateImage(AtlasSize, AtlasSize);
        var keepBackupBuffer = LegacyGraphics.ImageBuffer(keepBackup);
        LegacyGraphics.LockBuffer(keepBackupBuffer);
        LegacyGraphics.WritePixelFast(3, 5, unchecked((int)0xFF102030), keepBackupBuffer);
        LegacyGraphics.UnlockBuffer(keepBackupBuffer);

        LegacyGraphics.LockBuffer(aaBackupBuffer);
        LegacyGraphics.WritePixelFast(1, 1, unchecked((int)0xFFFFFFFF), aaBackupBuffer);
        LegacyGraphics.UnlockBuffer(aaBackupBuffer);
        LegacyGraphics.LockBuffer(aaTextureBuffer);              // outstanding lock on the doomed atlas
        LegacyGraphics.SetBuffer(aaBackupBuffer);                // the doomed backup image is the current canvas
        LegacyGraphics.SetFont(aaFont);                          // the doomed low-res font is the current font
        facts.Append("setup.buffer=").Append(LegacyGraphics.GraphicsBuffer() == aaBackupBuffer).Append(';');

        // Menu.bb order: FreeFont, FreeTexture, FreeImage - once per record.
        (int Font, int Texture, int Backup)[] records = [(aaFont, aaTexture, aaBackup), (consoleFont, 0, 0)];
        foreach (var record in records)
        {
            LegacyGraphics.FreeFont(record.Font);
            LegacyGraphics.FreeTexture(record.Texture);
            LegacyGraphics.FreeImage(record.Backup);
        }
        facts.Append("buffer.reset=").Append(LegacyGraphics.GraphicsBuffer() == backBuffer).Append(';');
        facts.Append("font.reset=").Append(LegacyGraphics.GetFont() != aaFont).Append(';');

        // The handles are dead now: every later use reports it, the frees do not.
        Assert.Throws<ArgumentException>(() => LegacyGraphics.TextureWidth(aaTexture));
        Assert.Throws<ArgumentException>(() => LegacyGraphics.LockBuffer(aaTextureBuffer));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.ImageWidth(aaBackup));
        Assert.Throws<ArgumentException>(() => LegacyGraphics.SetBuffer(aaBackupBuffer));
        Assert.Throws<ArgumentException>(() => LegacyGraphics.SetFont(aaFont));

        // Second free of the same handles, of 0, and of handles that never existed: all silent.
        foreach (var record in records)
        {
            LegacyGraphics.FreeFont(record.Font);
            LegacyGraphics.FreeTexture(record.Texture);
            LegacyGraphics.FreeImage(record.Backup);
        }
        LegacyGraphics.FreeFont(0);
        LegacyGraphics.FreeTexture(0);
        LegacyGraphics.FreeImage(0);
        LegacyGraphics.FreeFont(int.MaxValue);
        LegacyGraphics.FreeTexture(int.MaxValue);
        LegacyGraphics.FreeImage(int.MaxValue);
        facts.Append("repeat.free=ok;");

        // Nothing leaked into or was released from the survivor, and the renderer still draws.
        LegacyGraphics.SetFont(keepFont);
        facts.Append("keep.font=").Append(LegacyGraphics.GetFont() == keepFont).Append(';');
        facts.Append("keep.texture=").Append(LegacyGraphics.TextureWidth(keepTexture)).Append('x')
            .Append(LegacyGraphics.TextureHeight(keepTexture)).Append(';');
        facts.Append("keep.image=").Append(LegacyGraphics.ImageWidth(keepBackup)).Append('x')
            .Append(LegacyGraphics.ImageHeight(keepBackup)).Append(';');
        facts.Append("keep.pixel=").Append(ReadLocked(keepBackupBuffer, 3, 5).ToString("X8", CultureInfo.InvariantCulture)).Append(';');
        LegacyGraphics.ClsColor(0, 0, 0);
        LegacyGraphics.Cls();
        LegacyGraphics.Color(255, 255, 255);
        LegacyGraphics.Text(2, 2, "A");
        LegacyGraphics.DrawImage(keepBackup, 0, 0);

        LegacyGraphics.FreeImage(keepBackup);
        LegacyGraphics.FreeTexture(keepTexture);
        LegacyGraphics.FreeFont(keepFont);
        facts.Append("keep.freed=").Append(LegacyGraphics.GraphicsBuffer() == backBuffer).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(facts.ToString())));
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
