using Blitz3DCS.Graphics.D3D11;
using Vortice.DXGI;
using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class AntialiasTests
{
    [Fact]
    public void Sample_selection_uses_highest_common_count_then_falls_back()
    {
        Assert.Equal(4, D3D11Renderer.SelectAntialiasSampleCount((_, count) => count == 4 ? 1u : 0u));
        Assert.Equal(2, D3D11Renderer.SelectAntialiasSampleCount((_, count) => count == 2 ? 1u : 0u));
    }

    [Fact]
    public void Sample_selection_requires_support_for_both_color_and_depth()
    {
        Assert.Equal(2, D3D11Renderer.SelectAntialiasSampleCount((format, count) =>
            count == 4 && format == Format.R8G8B8A8_UNorm ? 1u :
            count == 2 ? 1u : 0u));
        Assert.Equal(0, D3D11Renderer.SelectAntialiasSampleCount((_, _) => 0u));
    }

    [Fact]
    public void Enabling_antialias_fails_explicitly_when_no_common_sample_count_exists()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            D3D11Renderer.RequireAntialiasSampleCount((_, _) => 0u));
    }
}
