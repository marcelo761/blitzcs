using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsBlendTests
{
    [Theory]
    [InlineData(0, 0, false, 1, 0, 0)]
    [InlineData(1, 1, false, 1, 0, 0)]
    [InlineData(0, 1, true, 1, 0, 1)]
    [InlineData(0, 2, true, 1, 0, 0)]
    [InlineData(1, 2, true, 1, 0, 1)]
    [InlineData(0, 0, false, .5f, 0, 1)]
    [InlineData(1, 1, false, .5f, 0, 1)]
    [InlineData(0, 0, false, 1, 32, 1)]
    [InlineData(2, 1, true, .5f, 32, 2)]
    [InlineData(3, 1, true, .5f, 32, 3)]
    public void Legacy_blend_resolves_texture_and_vertex_alpha_before_selecting_pipeline(
        int requested, int stages, bool transparentTexture, float alpha, int effects, int expected)
    {
        Assert.Equal(expected, LegacyGraphics.ResolveMaterialBlend(requested, stages, transparentTexture, alpha, effects));
    }
}
