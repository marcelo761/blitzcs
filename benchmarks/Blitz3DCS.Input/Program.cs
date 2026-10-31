using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Blitz3DCS.Windowing;

// This measures managed state and an optional real SDL dummy driver, not original Blitz3D speed.
var iterations = 500_000;
var native = false;
var countSpecified = false;
foreach (var argument in args)
{
    if (argument == "--native") native = true;
    else if (!countSpecified && int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
    {
        iterations = count;
        countSpecified = true;
    }
    else throw new ArgumentException("Usage: Blitz3DCS.Input [iterations] [--native]");
}
if (iterations is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));

const int warmup = 20_000;
const int rounds = 5;
var results = new List<object>();
var state = new LegacyInputState();
Measure("state-empty-poll", () => state.KeyDown(30) + state.KeyHit(30) + state.GetScanCode()
    + state.MouseDown(2) + state.MouseHit(2) + state.GetMouse(), 0);
Measure("state-event-and-poll-cycle", () =>
{
    state.SetKey(30, true);
    state.SetKey(30, true, repeat: true);
    var sum = state.KeyDown(30) + state.KeyHit(30) + state.GetScanCode();
    state.SetKey(30, false);
    state.SetMouseButton(2, true);
    sum += state.MouseDown(2) + state.MouseHit(2) + state.GetMouse();
    state.SetMouseButton(2, false);
    state.MoveMouse(7, 16);
    state.SetMousePosition(10, 20);
    state.AddMouseWheel(120);
    sum += state.MouseXSpeed() + state.MouseYSpeed() + state.MouseZSpeed();
    state.AddMouseWheel(-120);
    _ = state.MouseZSpeed();
    return sum;
}, 44);

if (native)
{
    // Explicit opt-in exercises SDL's native video/event initialization without a desktop window.
    Environment.SetEnvironmentVariable("SDL_VIDEO_DRIVER", "dummy");
    using var host = new WindowHost(320, 240, "BlitzCS input benchmark");
    host.FlushKeys();
    host.FlushMouse();
    Measure("native-dummy-empty-pump", () => { host.PumpEvents(); return 1; }, 1);
    Measure("native-dummy-keydown-poll", () => host.KeyDown(30), 0);
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    iterations,
    warmup,
    rounds,
    native_enabled = native,
    native_video_driver = native ? "dummy" : null,
    note = "Median wall time and calling-thread managed allocation per operation; state cycle performs multiple events/queries. SDL dummy is a real native backend without desktop input. No timing assertion, native allocation measurement, original-runtime speed or rendering claim.",
    results
}, new JsonSerializerOptions { WriteIndented = true }));

void Measure(string name, Func<int> operation, int expectedPerOperation)
{
    long warmupChecksum = 0;
    for (var i = 0; i < warmup; i++) warmupChecksum += operation();
    if (warmupChecksum != (long)warmup * expectedPerOperation)
        throw new InvalidOperationException($"Unexpected warmup checksum for {name}: {warmupChecksum}.");
    var timings = new double[rounds];
    var allocations = new double[rounds];
    var checksums = new long[rounds];
    var totalAllocated = new long[rounds];
    for (var round = 0; round < rounds; round++)
    {
        long checksum = 0;
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var before = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) checksum += operation();
        var elapsed = Stopwatch.GetTimestamp() - before;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        if (checksum != (long)iterations * expectedPerOperation)
            throw new InvalidOperationException($"Unexpected measured checksum for {name}: {checksum}.");
        timings[round] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
        allocations[round] = (double)bytes / iterations;
        checksums[round] = checksum;
        totalAllocated[round] = bytes;
    }
    var perRoundNanoseconds = (double[])timings.Clone();
    Array.Sort(timings);
    Array.Sort(allocations);
    results.Add(new
    {
        name,
        median_nanoseconds_per_operation = timings[rounds / 2],
        median_allocated_bytes_per_operation = allocations[rounds / 2],
        nanoseconds_per_operation_by_round = perRoundNanoseconds,
        allocated_bytes_by_round = totalAllocated,
        checksum_by_round = checksums,
        expected_checksum_per_round = (long)iterations * expectedPerOperation
    });
}
