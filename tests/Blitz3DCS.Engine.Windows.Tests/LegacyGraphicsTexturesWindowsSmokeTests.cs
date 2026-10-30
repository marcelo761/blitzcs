using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsTexturesWindowsSmokeTests
{
    [WindowsTextureSmokeFact]
    public void Procedural_texture_buffers_and_material_bindings_survive_caller_free_and_entity_copy()
    {
        RunSmoke();
    }

    [WindowsTextureSmokeFact]
    public void Independent_pixel_buffer_locks_allow_texture_queries_and_release_in_reverse_order()
    {
        RunIndependentPixelBufferLocks();
    }

    internal static void RunIndependentPixelBufferLocks()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(32, 32, 0, 2);
            VerifyIndependentPixelBufferLocks();
            LegacyGraphics.EndGraphics();
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            LegacyGraphics.Graphics3D(96, 96, 0, 2);
            VerifyAnimatedTextureLoad();
            VerifyIndependentPixelBufferLocks();
            var backBuffer = LegacyGraphics.BackBuffer();
            Assert.Equal(backBuffer, LegacyGraphics.GraphicsBuffer());
            Assert.Throws<NotSupportedException>(() => LegacyGraphics.FrontBuffer());

            var texture = LegacyGraphics.CreateTexture(2, 2, flags: 2 | 16 | 32, frames: 2);
            Assert.Equal(2, LegacyGraphics.TextureWidth(texture));
            Assert.Equal(2, LegacyGraphics.TextureHeight(texture));
            var frame0 = LegacyGraphics.TextureBuffer(texture);
            var frame1 = LegacyGraphics.TextureBuffer(texture, 1);
            Assert.NotEqual(frame0, frame1);

            var brush = LegacyGraphics.CreateBrush();
            LegacyGraphics.BrushTexture(brush, texture, frame: 1, index: 0);
            var brushTexture = LegacyGraphics.GetBrushTexture(brush);
            Assert.NotEqual(0, brushTexture);
            Assert.Equal(frame1, LegacyGraphics.TextureBuffer(brushTexture, 1));
            LegacyGraphics.TextureBlend(brushTexture, 3);
            LegacyGraphics.TextureBlend(texture, 0);
            LegacyGraphics.FreeTexture(brushTexture);

            LegacyGraphics.SetBuffer(frame0);
            Assert.Equal(frame0, LegacyGraphics.GraphicsBuffer());
            LegacyGraphics.LockBuffer();
            for (var y = 0; y < 2; y++)
                for (var x = 0; x < 2; x++)
                    LegacyGraphics.WritePixelFast(x, y, unchecked((int)0xFFFF8040));
            Assert.Equal(unchecked((int)0xFFFF8040), LegacyGraphics.ReadPixelFast(0, 0));
            LegacyGraphics.UnlockBuffer();

            LegacyGraphics.CopyRect(0, 0, 2, 2, 0, 0, frame0, frame1);
            LegacyGraphics.SetBuffer(frame1);
            LegacyGraphics.LockBuffer();
            Assert.Equal(unchecked((int)0xFFFF8040), LegacyGraphics.ReadPixelFast(0, 0));
            LegacyGraphics.UnlockBuffer();

            LegacyGraphics.ClearSelectedBuffer(0, 0, 0);
            LegacyGraphics.LockBuffer();
            Assert.Equal(unchecked((int)0xFF000000), LegacyGraphics.ReadPixelFast(0, 0));
            LegacyGraphics.UnlockBuffer();

            LegacyGraphics.SetBuffer(backBuffer);
            var camera = LegacyGraphics.CreateCamera();
            LegacyScene.Current.PositionEntity(camera, 0, 0, -5);
            LegacyGraphics.CameraRange(camera, 0.1f, 100);
            LegacyGraphics.CameraClsColor(camera, 8, 12, 16);

            var original = LegacyGraphics.CreateCube();
            LegacyGraphics.EntityTexture(original, texture, frame: 0);
            LegacyGraphics.RenderWorld();
            var expectedMaterialPixel = LegacyGraphics.ReadPixelForTesting(48, 48);
            var background = LegacyGraphics.ReadPixelForTesting(2, 2);
            Assert.NotEqual(background, expectedMaterialPixel);

            // Texture copies share their Rep in Blitz3D. Changes remain visible to
            // already-bound materials, while freeing the caller retains their frames.
            LegacyGraphics.TextureBlend(texture, 3);
            LegacyGraphics.RenderWorld();
            var changedMaterialPixel = LegacyGraphics.ReadPixelForTesting(48, 48);
            Assert.NotEqual(expectedMaterialPixel, changedMaterialPixel);
            var clone = LegacyGraphics.CopyEntity(original);
            LegacyGraphics.FreeTexture(texture);
            LegacyGraphics.FreeEntity(original);
            LegacyGraphics.RenderWorld();
            Assert.Equal(changedMaterialPixel, LegacyGraphics.ReadPixelForTesting(48, 48));

            LegacyGraphics.FreeEntity(clone);
            LegacyGraphics.EndGraphics();
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    private static void VerifyIndependentPixelBufferLocks()
    {
        var image = LegacyGraphics.CreateImage(2, 2);
        var imageBuffer = LegacyGraphics.ImageBuffer(image);
        var texture = LegacyGraphics.CreateTexture(3, 2, flags: 2);
        var textureBuffer = LegacyGraphics.TextureBuffer(texture);
        try
        {
            LegacyGraphics.LockBuffer(imageBuffer);
            try
            {
                Assert.Throws<InvalidOperationException>(() => LegacyGraphics.LockBuffer(imageBuffer));
                LegacyGraphics.LockBuffer(textureBuffer);
                try
                {
                    Assert.Equal(textureBuffer, LegacyGraphics.TextureBuffer(texture));
                    Assert.Equal(3, LegacyGraphics.TextureWidth(texture));
                    Assert.Equal(2, LegacyGraphics.TextureHeight(texture));
                    Assert.Equal(string.Empty, LegacyGraphics.TextureName(texture));
                    Assert.Equal(imageBuffer, LegacyGraphics.ImageBuffer(image));
                    Assert.Equal(LegacyGraphics.BackBuffer(), LegacyGraphics.GraphicsBuffer());

                    LegacyGraphics.WritePixelFast(0, 0, unchecked((int)0xFFFF2040), imageBuffer);
                    LegacyGraphics.WritePixelFast(2, 1, unchecked((int)0xFF40A0E0), textureBuffer);
                    Assert.Equal(unchecked((int)0xFFFF2040), LegacyGraphics.ReadPixelFast(0, 0, imageBuffer));
                    Assert.Equal(unchecked((int)0xFF40A0E0), LegacyGraphics.ReadPixelFast(2, 1, textureBuffer));
                }
                finally
                {
                    LegacyGraphics.UnlockBuffer(textureBuffer);
                }
            }
            finally
            {
                LegacyGraphics.UnlockBuffer(imageBuffer);
            }
        }
        finally
        {
            LegacyGraphics.FreeImage(image);
            LegacyGraphics.FreeTexture(texture);
        }
    }

    private static void VerifyAnimatedTextureLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-anim-texture-{Guid.NewGuid():N}.bmp");
        var texture = 0;
        try
        {
            File.WriteAllBytes(path, CreateTextureSheetBmp());
            texture = LegacyGraphics.LoadAnimTexture(path, flags: 4, width: 2, height: 1, first: 0, count: 4);
            Assert.NotEqual(0, texture);
            Assert.Equal(path, LegacyGraphics.TextureName(texture));
            Assert.Equal(2, LegacyGraphics.TextureWidth(texture));
            Assert.Equal(1, LegacyGraphics.TextureHeight(texture));

            var expected = new[]
            {
                new[] { unchecked((int)0xFFFF0000), unchecked((int)0xFF00FF00) },
                new[] { unchecked((int)0xFFFFFFFF), unchecked((int)0xFF00FFFF) },
                new[] { unchecked((int)0xFFFFFF00), unchecked((int)0xFFFF00FF) },
                new[] { unchecked((int)0x00000000), unchecked((int)0xFF0000FF) }
            };
            for (var frame = 0; frame < expected.Length; frame++)
            {
                var buffer = LegacyGraphics.TextureBuffer(texture, frame);
                LegacyGraphics.LockBuffer(buffer);
                try
                {
                    Assert.Equal(expected[frame][0], LegacyGraphics.ReadPixelFast(0, 0, buffer));
                    Assert.Equal(expected[frame][1], LegacyGraphics.ReadPixelFast(1, 0, buffer));
                }
                finally { LegacyGraphics.UnlockBuffer(buffer); }
            }
        }
        finally
        {
            if (texture != 0) LegacyGraphics.FreeTexture(texture);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] CreateTextureSheetBmp()
    {
        const int width = 4, height = 2, pixelOffset = 54, rowBytes = width * 3;
        var bmp = new byte[pixelOffset + rowBytes * height];
        "BM"u8.CopyTo(bmp);
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(rowBytes * height).CopyTo(bmp, 34);

        byte[][] logicalRows =
        [
            [255, 0, 0, 0, 255, 0, 255, 255, 255, 0, 255, 255],
            [255, 255, 0, 255, 0, 255, 0, 0, 0, 0, 0, 255]
        ];
        for (var fileRow = 0; fileRow < height; fileRow++)
        {
            var source = logicalRows[height - fileRow - 1];
            var destination = pixelOffset + fileRow * rowBytes;
            for (var x = 0; x < width; x++)
            {
                bmp[destination + x * 3] = source[x * 3 + 2];
                bmp[destination + x * 3 + 1] = source[x * 3 + 1];
                bmp[destination + x * 3 + 2] = source[x * 3];
            }
        }
        return bmp;
    }

    private sealed class WindowsTextureSmokeFactAttribute : FactAttribute
    {
        public WindowsTextureSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real texture/device smoke test.";
        }
    }
}
