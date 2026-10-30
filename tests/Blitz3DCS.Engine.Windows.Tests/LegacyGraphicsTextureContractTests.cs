using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsTextureContractTests
{
    [Fact]
    public void Texture_flags_accept_masks_and_mipmaps_and_reject_cube_modes()
    {
        LegacyGraphics.ValidateTextureFlags(0);
        LegacyGraphics.ValidateTextureFlags(1 | 2 | 16 | 32 | 256);
        LegacyGraphics.ValidateTextureFlags(2 | 16);

        LegacyGraphics.ValidateTextureFlags(1 | 4);
        LegacyGraphics.ValidateTextureFlags(8);
        LegacyGraphics.ValidateTextureFlags(0x109);
        Assert.Throws<NotSupportedException>(() => LegacyGraphics.ValidateTextureFlags(64));
        Assert.Throws<NotSupportedException>(() => LegacyGraphics.ValidateTextureFlags(128));
        Assert.Throws<NotSupportedException>(() => LegacyGraphics.ValidateTextureFlags(-1));
    }

    [Fact]
    public void Texture_rep_keeps_caller_handle_aliases_alive_independently()
    {
        var rep = new LegacyGraphics.TextureRep(1, 4, 4, 1, []);
        Assert.Equal(1, rep.CallerHandles);

        rep.RetainCallerHandle();
        Assert.Equal(2, rep.CallerHandles);
        rep.ReleaseCallerHandle();
        Assert.Equal(1, rep.CallerHandles);
        rep.ReleaseCallerHandle();
        Assert.Equal(0, rep.CallerHandles);
        Assert.Throws<InvalidOperationException>(rep.ReleaseCallerHandle);
    }

    [Fact]
    public void Pixel_conversion_preserves_legacy_argb_order_and_signed_high_alpha()
    {
        var pixels = LegacyGraphics.ArgbToRgbaPixels([unchecked((int)0x80402010), unchecked((int)0xFF010203)]);

        Assert.Equal(new byte[] { 0x40, 0x20, 0x10, 0x80, 0x01, 0x02, 0x03, 0xFF }, pixels);
        Assert.Equal(unchecked((int)0x80402010), LegacyGraphics.RgbaToArgb(0x40, 0x20, 0x10, 0x80));
        Assert.Equal(unchecked((int)0xFF010203), LegacyGraphics.RgbaToArgb(0x01, 0x02, 0x03, 0xFF));
    }

    [Fact]
    public void Copy_rect_clips_both_surfaces_without_changing_source_to_destination_alignment()
    {
        var clipped = LegacyGraphics.ClipCopy(-1, 1, 5, 4, 1, -1, 4, 4, 5, 4);

        Assert.Equal(new LegacyGraphics.CopyRegion(0, 2, 2, 0, 3, 2), clipped);
        Assert.Equal(default, LegacyGraphics.ClipCopy(9, 0, 2, 2, 0, 0, 4, 4, 4, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.ClipCopy(0, 0, -1, 1, 0, 0, 4, 4, 4, 4));
    }

    [Fact]
    public void Procedural_texture_creation_rejects_unsupported_flags_before_requiring_a_display()
    {
        Assert.Throws<NotSupportedException>(() => LegacyGraphics.CreateTexture(4, 4, flags: 64));
    }

    [Fact]
    public void Texture_mask_replaces_alpha_using_black_key_and_preserves_rgb()
    {
        byte[] pixels = [0, 0, 0, 255, 1, 0, 0, 0, 40, 50, 60, 17];
        LegacyGraphics.BuildTextureMask(pixels);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0, 255, 40, 50, 60, 255 }, pixels);
    }

    [Fact]
    public void Animated_texture_slicing_selects_row_major_frames_and_ignores_incomplete_edges()
    {
        var source = new byte[5 * 5 * 4];
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 5; x++)
        {
            var color = y < 2
                ? (x < 2 ? (byte)0 : (byte)20)
                : (x < 2 ? (byte)40 : (byte)60);
            var offset = (y * 5 + x) * 4;
            source[offset] = color;
            source[offset + 3] = 255;
        }

        var frames = LegacyGraphics.SliceTextureFrames(source, 5, 5, 2, 2, first: 1, count: 3);

        Assert.NotNull(frames);
        Assert.Equal(3, frames.Length);
        AssertTile(frames[0], 20);
        AssertTile(frames[1], 40);
        AssertTile(frames[2], 60);
        Assert.Null(LegacyGraphics.SliceTextureFrames(source, 5, 5, 2, 2, first: 3, count: 2));

        static void AssertTile(byte[] frame, byte color)
        {
            for (var pixel = 0; pixel < frame.Length; pixel += 4)
            {
                Assert.Equal(color, frame[pixel]);
                Assert.Equal((byte)255, frame[pixel + 3]);
            }
        }
    }

    [Fact]
    public void Animated_texture_slicing_keeps_frame_boundaries_for_masking()
    {
        byte[] source =
        [
            0,0,0,255, 10,0,0,255,
            20,0,0,255, 30,0,0,255
        ];

        var frames = LegacyGraphics.SliceTextureFrames(source, 2, 2, 1, 1, first: 0, count: 4);

        Assert.NotNull(frames);
        Assert.Equal(4, frames.Length);
        foreach (var frame in frames) LegacyGraphics.BuildTextureMask(frame);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, frames[0]);
        Assert.Equal(new byte[] { 10, 0, 0, 255 }, frames[1]);
        Assert.Equal(new byte[] { 20, 0, 0, 255 }, frames[2]);
        Assert.Equal(new byte[] { 30, 0, 0, 255 }, frames[3]);
    }
}
