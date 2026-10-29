using Blitz3DCS.Engine.Windows;
using Blitz3DCS.Graphics.D3D11;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsRenderStateTests
{
    [Fact]
    public void Wireframe_toggles_the_scene_rasterizer_request()
    {
        LegacyGraphics.ResetRenderStateForTesting();
        Assert.False(LegacyGraphics.WireframeForRendering);
        LegacyGraphics.WireFrame(1);
        Assert.True(LegacyGraphics.WireframeForRendering);
        LegacyGraphics.WireFrame(0);
        Assert.False(LegacyGraphics.WireframeForRendering);
        LegacyGraphics.WireFrame(-7);
        Assert.True(LegacyGraphics.WireframeForRendering);
        LegacyGraphics.ResetRenderStateForTesting();
    }

    [Fact]
    public void Antialias_enable_fails_explicitly_and_disable_is_accepted()
    {
        LegacyGraphics.AntiAlias(0);
        Assert.Throws<NotSupportedException>(() => LegacyGraphics.AntiAlias(1));
    }

    [Fact]
    public void Draw_state_defaults_to_solid_fill()
    {
        Assert.False(D3D11LightingDrawState.Default.Wireframe);
    }

    [Fact]
    public void Active_textures_counts_live_texture_objects()
    {
        var before = LegacyGraphics.ActiveTextures();
        var rep = new LegacyGraphics.TextureRep(910001, 4, 4, 1, []);
        LegacyGraphics.RegisterTextureForTesting(rep);
        try { Assert.Equal(before + 1, LegacyGraphics.ActiveTextures()); }
        finally { LegacyGraphics.ForgetTextureForTesting(rep.Handle); }
        Assert.Equal(before, LegacyGraphics.ActiveTextures());
    }

    [Fact]
    public void Bump_environment_state_is_stored_per_texture_and_validated()
    {
        var rep = new LegacyGraphics.TextureRep(910002, 4, 4, 1, []);
        LegacyGraphics.RegisterTextureForTesting(rep);
        try
        {
            LegacyGraphics.TextureBumpEnvMat(rep.Handle, 0, 0, -0.012f);
            LegacyGraphics.TextureBumpEnvMat(rep.Handle, 1, 1, 0.012f);
            LegacyGraphics.TextureBumpEnvScale(rep.Handle, 2);
            LegacyGraphics.TextureBumpEnvOffset(rep.Handle, 0.5f);
            Assert.Equal(-0.012f, rep.BumpEnvMat[0, 0]);
            Assert.Equal(0.012f, rep.BumpEnvMat[1, 1]);
            Assert.Equal(0f, rep.BumpEnvMat[0, 1]);
            Assert.Equal(2f, rep.BumpEnvScale);
            Assert.Equal(0.5f, rep.BumpEnvOffset);
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.TextureBumpEnvMat(rep.Handle, 2, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.TextureBumpEnvMat(rep.Handle, 0, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.TextureBumpEnvScale(rep.Handle, float.NaN));
            Assert.Throws<NotSupportedException>(() => LegacyGraphics.TextureBlend(rep.Handle, 6));
        }
        finally { LegacyGraphics.ForgetTextureForTesting(rep.Handle); }
        Assert.Throws<ArgumentException>(() => LegacyGraphics.TextureBumpEnvOffset(rep.Handle, 0));
    }

    [Fact]
    public void Video_memory_arithmetic_never_underflows_and_saturates_for_basic()
    {
        Assert.Equal(new D3D11VideoMemory(8000, 300), D3D11VideoMemory.FromAdapterValues(8000, 1000, 700));
        Assert.Equal(new D3D11VideoMemory(8000, 0), D3D11VideoMemory.FromAdapterValues(8000, 500, 900));
        Assert.Equal(new D3D11VideoMemory(1000, 1000), D3D11VideoMemory.FromAdapterValues(0, 1000, 0));
        Assert.Equal(int.MaxValue, D3D11VideoMemory.ToBasicInteger(8UL << 30));
        Assert.Equal(1234, D3D11VideoMemory.ToBasicInteger(1234));
    }

    [Fact]
    public void Texture_lod_bias_requires_a_display()
    {
        Assert.Throws<InvalidOperationException>(() => LegacyGraphics.TextureLodBias(-0.4f));
    }

    [Fact]
    public void Video_memory_commands_fail_explicitly_without_a_display()
    {
        Assert.Throws<InvalidOperationException>(() => LegacyGraphics.TotalVidMem());
        Assert.Throws<InvalidOperationException>(() => LegacyGraphics.AvailVidMem());
    }
}
