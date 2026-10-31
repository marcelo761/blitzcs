using System.Numerics;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsVisibilityTests
{
    [Theory]
    [InlineData(0, 0, 5, true)]
    [InlineData(0, 0, -5, false)]
    [InlineData(100, 0, 5, false)]
    [InlineData(0, 100, 5, false)]
    [InlineData(0, 0, 100, false)]
    public void Bounding_box_culls_against_left_handed_clip_volume(float x, float y, float z, bool visible)
    {
        var projection = LegacyGraphics.CreateLeftHandedPerspective(MathF.PI / 2, 1, 1, 50);
        var matrix = Matrix4x4.CreateTranslation(x, y, z) * projection;
        Assert.Equal(visible, LegacyGraphics.BoxIntersectsClipVolume(new Vector3(-.5f), new Vector3(.5f), matrix));
    }

    [Fact]
    public void Plane_support_test_matches_eight_corner_reference_for_rotated_scaled_boxes()
    {
        var random = new Random(53495);
        var projection = LegacyGraphics.CreateLeftHandedPerspective(MathF.PI / 3, 4f / 3, .1f, 100);
        var minimum = new Vector3(-1, -2, -3);
        var maximum = new Vector3(3, 2, 1);
        Span<Vector4> corners = stackalloc Vector4[8];
        for (var trial = 0; trial < 500; trial++)
        {
            var world = Matrix4x4.CreateScale((float)random.NextDouble() * 3 + .1f, 2, -1)
                * Matrix4x4.CreateRotationY((float)random.NextDouble() * 6)
                * Matrix4x4.CreateTranslation(random.Next(-100, 100), random.Next(-50, 50), random.Next(-30, 130));
            var matrix = world * projection;
            for (var i = 0; i < 8; i++)
                corners[i] = Vector4.Transform(new Vector4((i & 1) == 0 ? minimum.X : maximum.X,
                    (i & 2) == 0 ? minimum.Y : maximum.Y, (i & 4) == 0 ? minimum.Z : maximum.Z, 1), matrix);
            var rejected = false;
            for (var plane = 0; plane < 6; plane++)
            {
                var allOutside = true;
                foreach (var corner in corners)
                    allOutside &= plane switch
                    {
                        0 => corner.X < -corner.W,
                        1 => corner.X > corner.W,
                        2 => corner.Y < -corner.W,
                        3 => corner.Y > corner.W,
                        4 => corner.Z < 0,
                        _ => corner.Z > corner.W
                    };
                rejected |= allOutside;
            }
            Assert.Equal(!rejected, LegacyGraphics.BoxIntersectsClipVolume(minimum, maximum, matrix));
        }
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var trial = 0; trial < 256; trial++)
            _ = LegacyGraphics.BoxIntersectsClipVolume(minimum, maximum, projection);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocationStart);
    }
}
