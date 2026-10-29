using System.Reflection;
using Blitz3DCS.Engine.Windows;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsImagesTests
{
    [Theory]
    [InlineData("tiny.png", 2, 1)]
    [InlineData("tiny.jpg", 2, 1)]
    public void DecodeImage_reads_supported_raster_formats_as_rgba(string file, int width, int height)
    {
        using var stream = OpenFixture(file);

        var decoded = LegacyGraphics.DecodeImage(stream);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(width * height * 4, decoded.Rgba8.Length);
    }

    [Fact]
    public void Decoded_png_preserves_pixel_order_and_alpha()
    {
        using var stream = OpenFixture("tiny.png");

        var decoded = LegacyGraphics.DecodeImage(stream);

        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 128 }, decoded.Rgba8);
    }

    [Fact]
    public void DecodeImage_reads_png_from_a_non_seekable_stream()
    {
        using var fixture = OpenFixture("tiny.png");
        using var stream = new NonSeekableStream(new MemoryStream(ReadAllBytes(fixture)));

        var decoded = LegacyGraphics.DecodeImage(stream);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 128 }, decoded.Rgba8);
    }

    [Theory]
    [InlineData("DXT1", 8)]
    [InlineData("DXT3", 16)]
    [InlineData("DXT5", 16)]
    public void DecodeImage_decodes_DDS_block_compression(string format, int blockSize)
    {
        using var stream = new MemoryStream(CreateDds(format, 4, 4, blockSize));

        var decoded = LegacyGraphics.DecodeImage(stream);

        Assert.Equal(4, decoded.Width);
        Assert.Equal(4, decoded.Height);
        Assert.Equal(64, decoded.Rgba8.Length);
        Assert.Equal(new byte[] { 255, 0, 0 }, decoded.Rgba8[..3]);
        Assert.Equal(format == "DXT1" ? (byte)255 : format == "DXT3" ? (byte)0 : (byte)128, decoded.Rgba8[3]);
    }

    [Fact]
    public void DecodeImage_rejects_truncated_or_oversized_DDS()
    {
        var truncated = CreateDds("DXT1", 4, 4, 8)[..^1];
        using var truncatedStream = new MemoryStream(truncated);
        Assert.Throws<InvalidDataException>(() => LegacyGraphics.DecodeImage(truncatedStream));

        var oversizedHeader = CreateDds("DXT1", 4, 4, 8);
        BitConverter.GetBytes(16_385).CopyTo(oversizedHeader, 12);
        using var oversizedStream = new MemoryStream(oversizedHeader);
        Assert.Throws<InvalidDataException>(() => LegacyGraphics.DecodeImage(oversizedStream));
    }

    [Fact]
    public void DecodeImage_preserves_DXT1_transparency_and_clips_partial_blocks()
    {
        var transparent = CreateDds("DXT1", 4, 4, 8);
        BitConverter.GetBytes((ushort)0x07e0).CopyTo(transparent, 128);
        BitConverter.GetBytes((ushort)0xf800).CopyTo(transparent, 130);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(transparent, 132);
        using var transparentStream = new MemoryStream(transparent);
        var transparentDecoded = LegacyGraphics.DecodeImage(transparentStream);
        Assert.Equal(0, transparentDecoded.Rgba8[3]);

        using var partialStream = new MemoryStream(CreateDds("DXT5", 3, 2, 16));
        var partialDecoded = LegacyGraphics.DecodeImage(partialStream);
        Assert.Equal(3, partialDecoded.Width);
        Assert.Equal(2, partialDecoded.Height);
        Assert.Equal(24, partialDecoded.Rgba8.Length);
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, partialDecoded.Rgba8[..4]);
    }

    private static byte[] CreateDds(string format, int width, int height, int blockSize)
    {
        var bytes = new byte[128 + blockSize];
        "DDS "u8.CopyTo(bytes);
        BitConverter.GetBytes(124).CopyTo(bytes, 4);
        BitConverter.GetBytes(height).CopyTo(bytes, 12);
        BitConverter.GetBytes(width).CopyTo(bytes, 16);
        BitConverter.GetBytes(32).CopyTo(bytes, 76);
        BitConverter.GetBytes(4).CopyTo(bytes, 80);
        System.Text.Encoding.ASCII.GetBytes(format).CopyTo(bytes, 84);
        var block = bytes.AsSpan(128);
        var colorOffset = format == "DXT1" ? 0 : 8;
        BitConverter.GetBytes((ushort)0xf800).CopyTo(block[colorOffset..]);
        BitConverter.GetBytes((ushort)0x07e0).CopyTo(block[(colorOffset + 2)..]);
        if (format == "DXT3")
            block[..8].Clear();
        else if (format == "DXT5")
        {
            block[0] = 128;
            block[1] = 0;
        }
        return bytes;
    }

    [Fact]
    public void LoadImage_returns_zero_for_a_missing_or_undecodable_file()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"blitzcs-missing-{Guid.NewGuid():N}.png");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"blitzcs-invalid-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllText(invalidPath, "not an image");

            Assert.Equal(0, LegacyGraphics.LoadImage(missingPath));
            Assert.Equal(0, LegacyGraphics.LoadImage(invalidPath));
        }
        finally
        {
            if (File.Exists(invalidPath)) File.Delete(invalidPath);
        }
    }

    [Fact]
    public void MaskImage_changes_only_matching_rgb_pixels_to_transparent()
    {
        byte[] rgba = [0, 0, 0, 255, 2, 3, 4, 128, 0, 0, 0, 64];

        LegacyGraphics.MaskPixels(rgba, 0, 0, 0);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 2, 3, 4, 128, 0, 0, 0, 0 }, rgba);
    }

    [Fact]
    public void ResizePixels_resamples_all_rgba_channels_and_validates_dimensions()
    {
        byte[] source = [10, 20, 30, 40, 50, 60, 70, 80];

        var resized = LegacyGraphics.ResizePixels(source, 2, 1, 4, 1);

        Assert.Equal(new byte[]
        {
            10, 20, 30, 40, 10, 20, 30, 40,
            50, 60, 70, 80, 50, 60, 70, 80
        }, resized);
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.ResizePixels(source, 2, 1, 0, 1));
    }

    [Fact]
    public void RotatePixels_rotates_the_pixel_buffer_and_expands_the_bounds()
    {
        byte[] source = [255, 0, 0, 255, 0, 0, 255, 255];

        var rotated = LegacyGraphics.RotatePixels(source, 1, 2, 90);

        Assert.Equal(2, rotated.Width);
        Assert.Equal(1, rotated.Height);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, rotated.Rgba8);
    }

    [WindowsImageSmokeFact]
    public void Image_buffers_upload_and_draw_through_the_D3D11_backbuffer()
    {
        RunSmoke();
    }

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        var loadedImage = 0;
        var image = 0;
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-image-{Guid.NewGuid():N}.png");
        try
        {
            using (var fixture = OpenFixture("tiny.png"))
            using (var output = File.Create(path)) fixture.CopyTo(output);

            LegacyGraphics.Graphics3D(32, 32, 0, 2);
            loadedImage = LegacyGraphics.LoadImage(path);
            Assert.Equal(2, LegacyGraphics.ImageWidth(loadedImage));
            Assert.Equal(1, LegacyGraphics.ImageHeight(loadedImage));
            LegacyGraphics.SetBuffer(LegacyGraphics.ImageBuffer(loadedImage));
            LegacyGraphics.LockBuffer();
            Assert.Equal(unchecked((int)0xFFFF0000), LegacyGraphics.ReadPixelFast(0, 0));
            LegacyGraphics.UnlockBuffer();
            LegacyGraphics.SetBuffer(LegacyGraphics.BackBuffer());
            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.Cls();
            LegacyGraphics.DrawImage(loadedImage, 4, 4);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(4, 4).R);

            image = LegacyGraphics.CreateImage(2, 1);
            var imageBuffer = LegacyGraphics.ImageBuffer(image);
            LegacyGraphics.SetBuffer(imageBuffer);
            LegacyGraphics.LockBuffer();
            LegacyGraphics.WritePixelFast(0, 0, unchecked((int)0xFFFF0000));
            LegacyGraphics.WritePixelFast(1, 0, unchecked((int)0xFF00FF00));
            LegacyGraphics.UnlockBuffer();
            LegacyGraphics.SetBuffer(LegacyGraphics.BackBuffer());
            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.Cls();
            LegacyGraphics.DrawImage(image, 8, 4);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(8, 4).R);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(9, 4).G);

            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.Cls();
            LegacyGraphics.DrawImageRect(image, 20, 20, 1, 0, 1, 1);
            Assert.Equal((byte)0, LegacyGraphics.ReadPixelForTesting(19, 20).R);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(20, 20).G);
            LegacyGraphics.Color(255, 0, 0);
            LegacyGraphics.GetColor(20, 20);
            Assert.Equal((0, 255, 0), (LegacyGraphics.ColorRed(), LegacyGraphics.ColorGreen(), LegacyGraphics.ColorBlue()));

            // Overlap uses the image's handle-adjusted bounds, including transparent pixels.
            LegacyGraphics.HandleImage(image, 1, 0);
            Assert.Equal(1, LegacyGraphics.ImageRectOverlap(image, 8, 8, 7, 8, 1, 1));
            Assert.Equal(0, LegacyGraphics.ImageRectOverlap(image, 8, 8, 9, 8, 1, 1));

            // DrawBlock ignores the color key but still applies the image handle.
            LegacyGraphics.HandleImage(image, 1, 0);
            LegacyGraphics.ClsColor(0, 0, 0);
            LegacyGraphics.Cls();
            LegacyGraphics.DrawBlock(image, 8, 8);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(7, 8).R);
            Assert.Equal((byte)255, LegacyGraphics.ReadPixelForTesting(8, 8).G);

            LegacyGraphics.FreeImage(image);
            image = 0;
            LegacyGraphics.FreeImage(loadedImage);
            loadedImage = 0;
        }
        finally
        {
            if (image != 0) LegacyGraphics.FreeImage(image);
            if (loadedImage != 0) LegacyGraphics.FreeImage(loadedImage);
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Stream OpenFixture(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(value => value.EndsWith(name, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(resource)!;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class WindowsImageSmokeFactAttribute : FactAttribute
    {
        public WindowsImageSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real image upload and draw smoke test.";
        }
    }
}
