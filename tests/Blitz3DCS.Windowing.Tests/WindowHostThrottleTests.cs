using Xunit;

namespace Blitz3DCS.Windowing.Tests;

public sealed class WindowHostThrottleTests
{
    private sealed class WireBackend : IWindowBackend
    {
        public int ClientWidth => 640;
        public int ClientHeight => 480;
        public bool HasFocus => true;
        public int Pumps;
        public Queue<WindowInputEvent> Wire { get; } = new();
        public void PumpEvents(Action<WindowInputEvent> dispatch) { Pumps++; while (Wire.TryDequeue(out var e)) dispatch(e); }
        public void WarpMouse(float x, float y) { }
        public uint TranslateScanCode(int scanCode, bool shift, bool ctrl, bool alt) => scanCode is >= 4 and <= 29 ? (uint)('a' + scanCode - 4) : 0;
        public void Dispose() { }
    }

    private sealed record Totals(int KeyHits, int MouseHits, int XSpeed, int YSpeed, int ZSpeed, string Keys, string Buttons, int FinalX, int FinalY, int Down)
    {
        public bool Equals(Totals? o) => o is not null && ToString2() == o.ToString2();
        public override int GetHashCode() => ToString2().GetHashCode();
        private string ToString2() => $"{KeyHits}|{MouseHits}|{XSpeed}|{YSpeed}|{ZSpeed}|{Keys}|{Buttons}|{FinalX}|{FinalY}|{Down}";
    }

    /// <summary>Runs a deterministic frame script: events arrive between polls; each frame ends with a Flip-style forced pump.</summary>
    private static (Totals Totals, int Pumps) Run(int seed, long intervalTicks)
    {
        var rng = new Random(seed);
        long now = 0;
        var backend = new WireBackend();
        using var host = new WindowHost(backend, intervalTicks, () => now);
        int keyHits = 0, mouseHits = 0, xs = 0, ys = 0, zs = 0;
        var keys = new System.Text.StringBuilder();
        var buttons = new System.Text.StringBuilder();
        var down = new HashSet<int>();
        for (var frame = 0; frame < 300; frame++)
        {
            var steps = rng.Next(5, 40);
            for (var step = 0; step < steps; step++)
            {
                now += rng.Next(0, 400);
                switch (rng.Next(8))
                {
                    case 0:
                        var scan = rng.Next(4, 30);
                        if (down.Add(scan)) backend.Wire.Enqueue(new(WindowInputEventKind.KeyDown, ScanCode: scan));
                        else { down.Remove(scan); backend.Wire.Enqueue(new(WindowInputEventKind.KeyUp, ScanCode: scan)); }
                        break;
                    case 1:
                        backend.Wire.Enqueue(new(WindowInputEventKind.MouseDown, Button: rng.Next(1, 4)));
                        backend.Wire.Enqueue(new(WindowInputEventKind.MouseUp, Button: rng.Next(1, 4)));
                        break;
                    case 2: backend.Wire.Enqueue(new(WindowInputEventKind.MouseMove, X: rng.Next(0, 640), Y: rng.Next(0, 480))); break;
                    case 3: backend.Wire.Enqueue(new(WindowInputEventKind.MouseWheel, WheelY: rng.Next(-2, 3))); break;
                    default:
                        for (var k = 4; k < 30; k += 5) keyHits += host.KeyHit(LegacyScanCodes.FromSdl(k));
                        for (var b = 1; b <= 3; b++) mouseHits += host.MouseHit(b);
                        xs += host.MouseXSpeed(); ys += host.MouseYSpeed(); zs += host.MouseZSpeed();
                        _ = host.KeyDown(30); _ = host.MouseX();
                        break;
                }
            }
            // Flip boundary: forced pump, then the next frame's polls see everything.
            host.PumpEvents();
            for (var k = 4; k < 30; k++) keyHits += host.KeyHit(LegacyScanCodes.FromSdl(k));
            for (var b = 1; b <= 3; b++) mouseHits += host.MouseHit(b);
            xs += host.MouseXSpeed(); ys += host.MouseYSpeed(); zs += host.MouseZSpeed();
            int key;
            while ((key = host.GetKey()) != 0) keys.Append(key).Append(',');
            int mouse;
            while ((mouse = host.GetMouse()) != 0) buttons.Append(mouse).Append(',');
            keys.Append('|');
        }
        host.PumpEvents();
        var downCount = Enumerable.Range(1, 255).Count(code => host.KeyDown(code) != 0);
        return (new Totals(keyHits, mouseHits, xs, ys, zs, keys.ToString(), buttons.ToString(), host.MouseX(), host.MouseY(), downCount), backend.Pumps);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Throttled_polls_give_the_same_observable_totals_after_flip_boundaries(int seed)
    {
        var (plain, plainPumps) = Run(seed, 0);
        var (throttled, throttledPumps) = Run(seed, 300);
        Assert.Equal(plain.Down, throttled.Down);
        Assert.Equal((plain.FinalX, plain.FinalY), (throttled.FinalX, throttled.FinalY));
        Assert.Equal(plain.XSpeed + plain.YSpeed + plain.ZSpeed, throttled.XSpeed + throttled.YSpeed + throttled.ZSpeed);
        Assert.Equal(plain.XSpeed, throttled.XSpeed);
        Assert.Equal(plain.ZSpeed, throttled.ZSpeed);
        Assert.Equal(plain.KeyHits, throttled.KeyHits);
        Assert.Equal(plain.MouseHits, throttled.MouseHits);
        Assert.True(throttledPumps < plainPumps, $"{throttledPumps} should be below {plainPumps}");
    }

    [Fact]
    public void Polls_within_the_interval_skip_the_pump_but_boundaries_always_pump()
    {
        long now = 0;
        var backend = new WireBackend();
        using var host = new WindowHost(backend, 500, () => now);
        _ = host.KeyDown(30);
        Assert.Equal(1, backend.Pumps);
        now = 100; _ = host.KeyHit(30); _ = host.MouseX();
        Assert.Equal(1, backend.Pumps);
        host.FlushKeys(); host.FlushMouse();
        Assert.Equal(3, backend.Pumps);
        now = 700; _ = host.MouseHit(1);
        Assert.Equal(4, backend.Pumps);
        host.PumpEvents();
        Assert.Equal(5, backend.Pumps);
    }

    [Fact]
    public void WaitKey_blocks_until_a_key_arrives_even_when_throttled()
    {
        long now = 0;
        var backend = new WireBackend();
        using var host = new WindowHost(backend, 1_000_000, () => now);
        _ = host.KeyDown(30);
        backend.Wire.Enqueue(new(WindowInputEventKind.KeyDown, ScanCode: 4));
        Assert.Equal('a', host.WaitKey());
    }
}
