using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsDelayTests
{
    [Fact]
    public void Delay_pumps_at_most_100_milliseconds_apart_and_handles_timer_wrap()
    {
        var now = int.MaxValue - 20;
        var pumped = 0;
        var sleeps = new List<int>();
        LegacyGraphics.DelayCore(250, () => pumped++, () => now, duration =>
        {
            sleeps.Add(duration);
            now = unchecked(now + duration);
        });
        Assert.Equal(new[] { 100, 100, 50 }, sleeps);
        Assert.Equal(4, pumped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_delay_still_pumps_events_once(int delay)
    {
        var pumped = 0;
        LegacyGraphics.DelayCore(delay, () => pumped++, () => 123, _ => throw new InvalidOperationException());
        Assert.Equal(1, pumped);
    }
}
