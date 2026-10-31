using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyMathTests
{
    [Fact]
    public void Trigonometric_commands_use_degrees_and_atan2_uses_y_then_x()
    {
        Assert.Equal(1f, LegacyMath.Sin(90), 5);
        Assert.Equal(0f, LegacyMath.Cos(90), 5);
        Assert.Equal(90f, LegacyMath.ATan2(1, 0), 5);
        Assert.Equal(-90f, LegacyMath.ATan2(-1, 0), 5);
    }

    [Fact]
    public void Scalar_rounding_and_square_root_match_native_functions()
    {
        Assert.Equal(-2f, LegacyMath.Floor(-1.2f));
        Assert.Equal(-1f, LegacyMath.Ceil(-1.2f));
        Assert.Equal(3f, LegacyMath.Sqr(9));
    }

    [Fact]
    public void Park_miller_generator_reproduces_native_state_and_float_sequence()
    {
        LegacyMath.SeedRnd(0);
        Assert.Equal(1, LegacyMath.RndSeed());
        Assert.Equal(0.73656463623046875f, LegacyMath.Rnd(0, 1));
        Assert.Equal(48271, LegacyMath.RndSeed());
        _ = LegacyMath.Rnd(0, 1);
        Assert.Equal(182605794, LegacyMath.RndSeed());
    }

    [Fact]
    public void Integer_random_is_inclusive_swaps_reversed_bounds_and_masks_seed()
    {
        LegacyMath.SeedRnd(-1);
        Assert.Equal(int.MaxValue, LegacyMath.RndSeed());
        Assert.InRange(LegacyMath.Rand(1, 1), 1, 1);

        LegacyMath.SeedRnd(0x1234);
        var forward = LegacyMath.Rand(0, 10);
        LegacyMath.SeedRnd(0x1234);
        var reversed = LegacyMath.Rand(10, 0);
        Assert.Equal(forward, reversed);
        Assert.InRange(forward, 0, 10);

        LegacyMath.SeedRnd(0x1234);
        Assert.InRange(LegacyMath.Rand(6), 1, 6);
        LegacyMath.SeedRnd(0x1234);
        Assert.InRange(LegacyMath.Rnd(10), 0f, 10f);
    }
}
