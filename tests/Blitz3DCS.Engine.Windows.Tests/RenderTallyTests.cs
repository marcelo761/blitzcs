using System.Numerics;
using Blitz3DCS.Graphics.D3D11;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class RenderTallyTests
{
    [Fact]
    public void CategoryTalliesMatchTheOldPerDrawCounts()
    {
        var tally = new RenderTally();
        // blend 0 opaque, 1 alpha, 3 additive, anything else other.
        (int Blend, bool Sprite, bool Masked, bool Anim)[] draws =
            [(0, false, false, false), (1, true, false, false), (3, false, true, true), (2, true, true, false), (5, false, false, true), (0, false, false, false)];
        foreach (var d in draws) tally.Category(d.Blend, d.Sprite, d.Masked, d.Anim);
        Assert.Equal((2, 1, 1, 2), (tally.Opaque, tally.Alpha, tally.Additive, tally.OtherBlend));
        Assert.Equal((2, 2, 2), (tally.Sprite, tally.Masked, tally.Anim));
        tally.Flush();
        Assert.Equal(0, tally.Opaque + tally.Alpha + tally.Sprite + tally.Batchable);
    }

    [Fact]
    public void LayerEqualityAndHashAgreeWithRecordEquality()
    {
        var rng = new Random(7);
        D3D11TextureLayer Make() => new(null, rng.Next(0, 3), rng.Next(2) == 0, rng.Next(2) == 0, rng.Next(2) == 0,
            rng.Next(2) == 0 ? null : Matrix3x2.CreateTranslation(rng.Next(0, 2), 0), rng.Next(0, 2), rng.Next(2) == 0,
            rng.Next(2) == 0 ? null : new Vector4(rng.Next(0, 2)), rng.Next(0, 2), 0);
        var pool = Enumerable.Range(0, 40).Select(_ => Make()).ToArray();
        foreach (var a in pool)
            foreach (var b in pool)
            {
                Assert.Equal(a.Equals(b), LayerStats.Same(in a, in b));
                if (a.Equals(b)) Assert.Equal(LayerStats.Hash(3, in a), LayerStats.Hash(3, in b));
            }
        var left = pool.Take(8).ToArray();
        Assert.True(LayerStats.Same(left, left.ToArray()));
    }
}
