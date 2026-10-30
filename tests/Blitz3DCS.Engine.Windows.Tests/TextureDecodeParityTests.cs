using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class TextureDecodeParityTests
{
    [Theory]
    [InlineData("DXT1", 8, 8, 8)]
    [InlineData("DXT1", 13, 7, 8)]
    [InlineData("DXT3", 5, 9, 16)]
    [InlineData("DXT5", 64, 33, 16)]
    [InlineData("DXT5", 1, 1, 16)]
    public void Streamed_dds_decode_matches_the_legacy_whole_payload_decode(string format, int width, int height, int blockSize)
    {
        var bytes = CreateRandomDds(format, width, height, blockSize);

        var (w1, h1, legacy) = Decode(bytes, legacy: true);
        var (w2, h2, streamed) = Decode(bytes, legacy: false);

        Assert.Equal((w1, h1), (w2, h2));
        Assert.Equal(legacy, streamed);
    }

    [Fact]
    public void Truncated_dds_still_throws_invalid_data_when_streamed()
    {
        var bytes = CreateRandomDds("DXT5", 16, 16, 16)[..^1];
        Assert.Throws<InvalidDataException>(() => Decode(bytes, legacy: false));
        Assert.Throws<InvalidDataException>(() => Decode(bytes, legacy: true));
    }

    [Theory]
    [InlineData("tiny.png", 0)]
    [InlineData("tiny.png", 4)]
    [InlineData("tiny.jpg", 4)]
    [InlineData("tiny.png", 1 | 2 | 8 | 16 | 32 | 256)]
    public void PrepareTexturePixels_matches_decode_plus_color_key_and_is_shared_for_repeat_loads(string fixture, int flags)
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-texparity-{Guid.NewGuid():N}-{fixture}");
        try
        {
            using (var source = OpenFixture(fixture)) using (var target = File.Create(path)) source.CopyTo(target);

            LegacyGraphics.DecodedImage expected;
            using (var stream = File.OpenRead(path)) expected = LegacyGraphics.DecodeImage(stream);
            if ((flags & 4) != 0) LegacyGraphics.BuildTextureMask(expected.Rgba8);

            var first = LegacyGraphics.PrepareTexturePixels(path, flags);
            var second = LegacyGraphics.PrepareTexturePixels(path, flags);

            Assert.Equal(expected.Width, first.Width);
            Assert.Equal(expected.Height, first.Height);
            Assert.Equal(expected.Rgba8, first.Rgba8);
            Assert.Equal(expected.Rgba8, second.Rgba8);
            if (!TextureLoadOptions.Disabled) Assert.Same(first.Rgba8, second.Rgba8);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rewritten_file_is_not_served_from_the_cache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitzcs-texparity-{Guid.NewGuid():N}.dds");
        try
        {
            File.WriteAllBytes(path, CreateRandomDds("DXT5", 8, 8, 16, seed: 1));
            var before = LegacyGraphics.PrepareTexturePixels(path, 0).Rgba8.ToArray();
            File.WriteAllBytes(path, CreateRandomDds("DXT5", 8, 8, 16, seed: 2));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            var after = LegacyGraphics.PrepareTexturePixels(path, 0).Rgba8;
            Assert.NotEqual(before, after);
        }
        finally { File.Delete(path); }
    }

    private static (int, int, byte[]) Decode(byte[] bytes, bool legacy)
    {
        using var stream = new MemoryStream(bytes);
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        return DdsDecoder.Decode(stream, signature, legacy);
    }

    private static byte[] CreateRandomDds(string format, int width, int height, int blockSize, int seed = 11)
    {
        var blocks = ((width + 3) / 4) * ((height + 3) / 4);
        var bytes = new byte[128 + blocks * blockSize];
        "DDS "u8.CopyTo(bytes);
        BitConverter.GetBytes(124).CopyTo(bytes, 4);
        BitConverter.GetBytes(height).CopyTo(bytes, 12);
        BitConverter.GetBytes(width).CopyTo(bytes, 16);
        BitConverter.GetBytes(32).CopyTo(bytes, 76);
        BitConverter.GetBytes(4).CopyTo(bytes, 80);
        System.Text.Encoding.ASCII.GetBytes(format).CopyTo(bytes, 84);
        new Random(seed).NextBytes(bytes.AsSpan(128));
        return bytes;
    }

    private static Stream OpenFixture(string file)
    {
        var assembly = typeof(TextureDecodeParityTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(name)!;
    }
}
