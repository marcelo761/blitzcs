using Xunit;

namespace Blitz3DCS.Windowing.Tests;

public sealed class LegacyInputStateTests
{
    [Fact]
    public void KeyHitsAndQueueAreIndependentAndRepeatIsIgnored()
    {
        var state = new LegacyInputState();
        state.SetKey(30, true);
        state.SetKey(30, true, repeat: true);
        state.SetKey(30, true);
        Assert.Equal(1, state.KeyDown(30));
        Assert.Equal(2, state.KeyHit(30));
        Assert.Equal(0, state.KeyHit(30));
        Assert.Equal(30, state.GetScanCode());
        Assert.Equal(30, state.GetScanCode());
        Assert.Equal(0, state.GetScanCode());
        state.SetKey(30, false);
        Assert.Equal(0, state.KeyDown(30));
    }

    [Fact]
    public void FullKeyboardQueueDropsNewEventsButRetainsHitsAndHeldState()
    {
        var state = new LegacyInputState();
        for (var key = 1; key <= 34; key++) state.SetKey(key, true);
        for (var key = 1; key <= 32; key++) Assert.Equal(key, state.GetScanCode());
        Assert.Equal(0, state.GetScanCode());
        Assert.Equal(1, state.KeyDown(34));
        Assert.Equal(1, state.KeyHit(34));
        state.SetKey(200, true);
        Assert.Equal(200, state.GetScanCode());
    }

    [Fact]
    public void QueuesReuseSlotsAcrossWraparound()
    {
        var state = new LegacyInputState();
        for (var round = 0; round < 80; round++)
        {
            for (var key = 1; key <= 20; key++) state.SetKey(key, true);
            for (var key = 1; key <= 20; key++) Assert.Equal(key, state.GetScanCode());
        }
        Assert.Equal(80, state.KeyHit(20));
    }

    [Fact]
    public void MouseQueueStoresLegacyButtonNumbersAndDropsNewestWhenFull()
    {
        var state = new LegacyInputState();
        for (var i = 0; i < 32; i++) state.SetMouseButton(i % 3 + 1, true);
        state.SetMouseButton(3, true);
        Assert.Equal(1, state.MouseDown(3));
        Assert.Equal(11, state.MouseHit(3));
        Assert.Equal(0, state.MouseHit(3));
        for (var i = 0; i < 32; i++) Assert.Equal(i % 3 + 1, state.GetMouse());
        Assert.Equal(0, state.GetMouse());
        state.SetMouseButton(3, false);
        Assert.Equal(0, state.MouseDown(3));
    }

    [Fact]
    public void FlushClearsHitsAndQueuesWhilePreservingHeldButtonsAndAxes()
    {
        var state = new LegacyInputState();
        state.SetKey(30, true);
        state.SetMouseButton(2, true);
        state.SetMousePosition(40, 50);
        state.AddMouseWheel(240);
        state.FlushKeys();
        state.FlushMouse();
        Assert.Equal(1, state.KeyDown(30));
        Assert.Equal(1, state.MouseDown(2));
        Assert.Equal(0, state.KeyHit(30));
        Assert.Equal(0, state.MouseHit(2));
        Assert.Equal(0, state.GetScanCode());
        Assert.Equal(0, state.GetMouse());
        Assert.Equal(40, state.MouseX);
        Assert.Equal(50, state.MouseY);
        Assert.Equal(2, state.MouseZ);
        Assert.Equal(40, state.MouseXSpeed());
        Assert.Equal(50, state.MouseYSpeed());
        Assert.Equal(2, state.MouseZSpeed());
    }

    [Fact]
    public void SpeedQueriesUpdateOnlyTheirOwnBaselinesAndMoveMouseResetsXY()
    {
        var state = new LegacyInputState();
        state.SetMousePosition(10, 20);
        state.AddMouseWheel(360);
        Assert.Equal(10, state.MouseXSpeed());
        Assert.Equal(0, state.MouseXSpeed());
        Assert.Equal(20, state.MouseYSpeed());
        Assert.Equal(3, state.MouseZSpeed());
        state.MoveMouse(100, 200);
        Assert.Equal(0, state.MouseXSpeed());
        Assert.Equal(0, state.MouseYSpeed());
        state.SetMousePosition(97, 205);
        Assert.Equal(-3, state.MouseXSpeed());
        Assert.Equal(5, state.MouseYSpeed());
        state.AddMouseWheel(-120);
        Assert.Equal(-1, state.MouseZSpeed());
    }

    [Fact]
    public void WheelAccumulatesRawUnitsAndTruncatesWholeNotchesTowardZero()
    {
        var state = new LegacyInputState();
        state.AddMouseWheel(119);
        Assert.Equal(0, state.MouseZ);
        state.AddMouseWheel(1);
        Assert.Equal(1, state.MouseZ);
        state.AddMouseWheel(-239);
        Assert.Equal(0, state.MouseZ);
        state.AddMouseWheel(-1);
        Assert.Equal(-1, state.MouseZ);
    }

    [Fact]
    public void DeviceResetClearsStateAndAxesWhilePreservingSpeedBaselines()
    {
        var state = new LegacyInputState();
        state.SetKey(30, true);
        state.SetMouseButton(1, true);
        state.MoveMouse(10, 20);
        state.AddMouseWheel(240);
        Assert.Equal(2, state.MouseZSpeed());
        state.ResetDevices();
        Assert.Equal(0, state.KeyDown(30));
        Assert.Equal(0, state.KeyHit(30));
        Assert.Equal(0, state.MouseDown(1));
        Assert.Equal(0, state.MouseHit(1));
        Assert.Equal(0, state.GetScanCode());
        Assert.Equal(0, state.GetMouse());
        Assert.Equal(0, state.MouseX);
        Assert.Equal(0, state.MouseY);
        Assert.Equal(0, state.MouseZ);
        Assert.Equal(-10, state.MouseXSpeed());
        Assert.Equal(-20, state.MouseYSpeed());
        Assert.Equal(-2, state.MouseZSpeed());
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void InvalidScanCodesReturnZeroAndDoNotEnterQueue(int key)
    {
        var state = new LegacyInputState();
        state.SetKey(key, true);
        Assert.Equal(0, state.KeyDown(key));
        Assert.Equal(0, state.KeyHit(key));
        Assert.Equal(0, state.GetScanCode());
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void InvalidMouseButtonsReturnZeroAndDoNotEnterQueue(int button)
    {
        var state = new LegacyInputState();
        state.SetMouseButton(button, true);
        Assert.Equal(0, state.MouseDown(button));
        Assert.Equal(0, state.MouseHit(button));
        Assert.Equal(0, state.GetMouse());
    }

    [Fact]
    public void EventsAndPollingDoNotAllocateAfterConstruction()
    {
        var state = new LegacyInputState();
        Exercise(state);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Exercise(state);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static void Exercise(LegacyInputState state)
    {
        state.SetKey(30, true);
        state.SetKey(30, false);
        _ = state.KeyDown(30);
        _ = state.KeyHit(30);
        _ = state.GetScanCode();
        state.SetMouseButton(1, true);
        state.SetMouseButton(1, false);
        _ = state.MouseDown(1);
        _ = state.MouseHit(1);
        _ = state.GetMouse();
        state.SetMousePosition(3, 4);
        state.AddMouseWheel(120);
        _ = state.MouseXSpeed();
        _ = state.MouseYSpeed();
        _ = state.MouseZSpeed();
        state.MoveMouse(1, 2);
        state.FlushKeys();
        state.FlushMouse();
        state.ResetDevices();
    }
}
