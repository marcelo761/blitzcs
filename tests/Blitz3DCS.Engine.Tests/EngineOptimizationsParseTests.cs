using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class EngineOptimizationsParseTests
{
    [Fact]
    public void ParsesKnownNamesIgnoringCaseAndWhitespace()
    {
        var set = EngineOptimizations.Parse(" Broadphase , SETTERGUARD,,drawsort ", out var unknown);
        Assert.Equal(["broadphase", "drawsort", "setterguard"], set.Order().ToArray());
        Assert.Empty(unknown);
    }

    [Fact]
    public void ReportsUnknownNamesAndIgnoresThem()
    {
        var set = EngineOptimizations.Parse("collgeom,bogus, Other", out var unknown);
        Assert.Equal(["collgeom"], set.ToArray());
        Assert.Equal(["bogus", "Other"], unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValueDisablesNothing(string? value)
    {
        Assert.Empty(EngineOptimizations.Parse(value, out var unknown));
        Assert.Empty(unknown);
    }

    [Fact]
    public void EveryNameIsParsable()
    {
        Assert.Equal(EngineOptimizations.Names.Length, EngineOptimizations.Parse(string.Join(',', EngineOptimizations.Names), out _).Count);
    }
}
