using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("BlitzCS.BinaryCompiler.Win32.Tests")]

namespace BlitzCS.BinaryCompiler.Win32.Com;

/// <summary>
/// Opt-in, per-thread counters for adapted COM calls. Snapshots require quiescent profiled
/// threads; aggregation deliberately avoids synchronization in the call path.
/// </summary>
public sealed class ComBridgeProfiler : IDisposable
{
    private const int WindowCapacity = 128;
    [ThreadStatic] private static Sample activeSample;
    private readonly ThreadLocal<ThreadState> locals = new(() => new ThreadState(), trackAllValues: true);
    private readonly string? outputPath;
    private readonly bool exportAtProcessExit;
    private readonly Func<long> timestampProvider;
    private readonly DateTimeOffset startedUtc;
    private readonly long startTimestamp;
    private readonly long frequency;
    private int stopped;
    private int disposed;

    internal static Sample CurrentSample => activeSample;
    internal static void RecordActiveCopy(int bytes) => activeSample.RecordCopy(bytes);
    internal static void RecordActiveAllocation(int bytes) => activeSample.RecordAllocation(bytes);

    public ComBridgeProfiler(string? outputPath = null, bool exportAtProcessExit = false)
        : this(outputPath, exportAtProcessExit, Stopwatch.GetTimestamp, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency)
    {
    }

    internal ComBridgeProfiler(string? outputPath, bool exportAtProcessExit, Func<long> timestampProvider, DateTimeOffset startedUtc, long startTimestamp, long frequency)
    {
        this.outputPath = outputPath;
        this.exportAtProcessExit = exportAtProcessExit;
        this.timestampProvider = timestampProvider;
        this.startedUtc = startedUtc;
        this.startTimestamp = startTimestamp;
        this.frequency = frequency;
        if (exportAtProcessExit) AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal Sample Begin(string interfaceName, string methodName) => BeginAt(interfaceName, methodName, timestampProvider());

    internal Sample BeginAt(string interfaceName, string methodName, long started)
    {
        if (Volatile.Read(ref stopped) != 0) return default;
        var index = Math.Max(0, (started - startTimestamp) / frequency);
        var bucket = locals.Value!.GetBucket(index);
        var key = new MethodKey(interfaceName, methodName);
        if (!bucket.Rows.TryGetValue(key, out var row)) bucket.Rows.Add(key, row = new Counters());
        row.Calls++;
        return new Sample(row, started, timestampProvider);
    }

    /// <summary>Returns the aggregate over the currently retained 128 one-second windows.</summary>
    public IReadOnlyList<ComBridgeProfileRow> StopAndSnapshot() => Snapshot().Methods;

    /// <summary>Returns immutable per-second rows over the currently retained 128 one-second windows.</summary>
    public IReadOnlyList<ComBridgeProfileWindow> StopAndSnapshotWindows() => Snapshot().Windows;

    private ComBridgeProfileDocument Snapshot()
    {
        Interlocked.Exchange(ref stopped, 1);
        var latestIndex = Math.Max(0, (timestampProvider() - startTimestamp) / frequency);
        var oldestRetainedIndex = Math.Max(0, latestIndex - WindowCapacity + 1);
        var windows = new SortedDictionary<long, Dictionary<MethodKey, Counters>>();
        foreach (var local in locals.Values)
        {
            foreach (var bucket in local.Buckets)
            {
                if (bucket.Index < oldestRetainedIndex) continue;
                if (!windows.TryGetValue(bucket.Index, out var rows)) windows.Add(bucket.Index, rows = []);
                foreach (var (key, value) in bucket.Rows)
                {
                    if (!rows.TryGetValue(key, out var row)) rows.Add(key, row = new Counters());
                    row.Add(value);
                }
            }
        }

        var allRows = new Dictionary<MethodKey, Counters>();
        var windowRows = new List<ComBridgeProfileWindow>(windows.Count);
        foreach (var (index, rows) in windows)
        {
            foreach (var (key, value) in rows)
            {
                if (!allRows.TryGetValue(key, out var total)) allRows.Add(key, total = new Counters());
                total.Add(value);
            }
            var start = startTimestamp + index * frequency;
            windowRows.Add(new ComBridgeProfileWindow(
                startedUtc.AddTicks(index * TimeSpan.TicksPerSecond),
                startedUtc.AddTicks((index + 1) * TimeSpan.TicksPerSecond),
                start,
                start + frequency,
                ToRows(rows)));
        }

        return new ComBridgeProfileDocument(frequency, TimingNotes, ToRows(allRows))
        {
            InstanceStartedUtc = startedUtc,
            StartTimestamp = startTimestamp,
            Windows = windowRows.AsReadOnly()
        };
    }

    private const string TimingNotes = "Ticks are Stopwatch ticks. NativeHostInclusiveTicks includes nested COM calls and can double-count them. Snapshot requires quiescent profiled threads. Methods aggregates only the retained 128 one-second windows; older samples are discarded on ring reuse.";

    private static IReadOnlyList<ComBridgeProfileRow> ToRows(Dictionary<MethodKey, Counters> rows)
    {
        var result = rows
            .OrderByDescending(pair => pair.Value.Calls)
            .ThenBy(pair => pair.Key.InterfaceName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.MethodName, StringComparer.Ordinal)
            .Select(pair => new ComBridgeProfileRow(
                pair.Key.InterfaceName,
                pair.Key.MethodName,
                pair.Value.Calls,
                pair.Value.NativeCalls,
                pair.Value.AdaptationFailures,
                pair.Value.ManagedPreHostTicks,
                pair.Value.NativeHostInclusiveTicks,
                pair.Value.ManagedPostHostTicks,
                pair.Value.GuestToHostCopyBytes,
                pair.Value.NativeScratchAllocationCount,
                pair.Value.NativeScratchAllocationBytes,
                pair.Value.TotalTicks))
            .ToArray();
        return Array.AsReadOnly(result);
    }

    private void Export()
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        try
        {
            var document = Snapshot();
            File.WriteAllText(outputPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"COM bridge profile export failed: {ex.Message}"); } catch { }
        }
    }

    internal void OnProcessExit(object? sender, EventArgs e)
    {
        // ProcessExit may already have captured this delegate before Dispose unsubscribes it.
        if (Volatile.Read(ref disposed) != 0) return;
        Export();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (exportAtProcessExit) AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Export();
        locals.Dispose();
    }

    internal readonly struct Sample
    {
        private readonly Counters? counters;
        private readonly long started;
        private readonly Func<long>? timestampProvider;

        internal Sample(Counters counters, long started, Func<long> timestampProvider) { this.counters = counters; this.started = started; this.timestampProvider = timestampProvider; }

        internal bool Enabled => counters is not null;
        internal SampleScope Activate() => new(this);
        internal void RecordPreHost(long ticks) { if (counters is { } c) c.ManagedPreHostTicks += ticks; }
        internal void RecordPostHost(long ticks) { if (counters is { } c) c.ManagedPostHostTicks += ticks; }
        internal void RecordNativeCall(long ticks) { if (counters is { } c) { c.NativeCalls++; c.NativeHostInclusiveTicks += ticks; } }
        internal void RecordAdaptationFailure() { if (counters is { } c) c.AdaptationFailures++; }
        internal void RecordCopy(int bytes) { if (counters is { } c) c.GuestToHostCopyBytes += (uint)bytes; }
        internal void RecordAllocation(int bytes) { if (counters is { } c) { c.NativeScratchAllocationCount++; c.NativeScratchAllocationBytes += (uint)bytes; } }
        internal void Complete() { if (counters is { } c) c.TotalTicks += timestampProvider!() - started; }
    }

    internal readonly struct SampleScope : IDisposable
    {
        private readonly Sample previous;
        internal SampleScope(Sample current) { previous = activeSample; activeSample = current; }
        public void Dispose() => activeSample = previous;
    }

    private readonly record struct MethodKey(string InterfaceName, string MethodName);

    private sealed class ThreadState
    {
        internal readonly WindowBucket[] Buckets = Enumerable.Range(0, WindowCapacity).Select(_ => new WindowBucket()).ToArray();
        internal WindowBucket GetBucket(long index)
        {
            var bucket = Buckets[(int)(index % WindowCapacity)];
            if (bucket.Index != index) { bucket.Index = index; bucket.Rows.Clear(); }
            return bucket;
        }
    }

    private sealed class WindowBucket
    {
        internal long Index = long.MinValue;
        internal readonly Dictionary<MethodKey, Counters> Rows = [];
    }

    internal sealed class Counters
    {
        public long Calls, NativeCalls, AdaptationFailures;
        public long ManagedPreHostTicks, NativeHostInclusiveTicks, ManagedPostHostTicks;
        public long GuestToHostCopyBytes, NativeScratchAllocationCount, NativeScratchAllocationBytes, TotalTicks;

        public void Add(Counters other)
        {
            Calls += other.Calls;
            NativeCalls += other.NativeCalls;
            AdaptationFailures += other.AdaptationFailures;
            ManagedPreHostTicks += other.ManagedPreHostTicks;
            NativeHostInclusiveTicks += other.NativeHostInclusiveTicks;
            ManagedPostHostTicks += other.ManagedPostHostTicks;
            GuestToHostCopyBytes += other.GuestToHostCopyBytes;
            NativeScratchAllocationCount += other.NativeScratchAllocationCount;
            NativeScratchAllocationBytes += other.NativeScratchAllocationBytes;
            TotalTicks += other.TotalTicks;
        }
    }
}

public sealed record ComBridgeProfileRow(
    string InterfaceName,
    string MethodName,
    long Calls,
    long NativeCalls,
    long AdaptationFailures,
    long ManagedPreHostTicks,
    long NativeHostInclusiveTicks,
    long ManagedPostHostTicks,
    long GuestToHostCopyBytes,
    long NativeScratchAllocationCount,
    long NativeScratchAllocationBytes,
    long TotalTicks);

public sealed record ComBridgeProfileWindow(
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    long StartTimestamp,
    long EndTimestamp,
    IReadOnlyList<ComBridgeProfileRow> Methods);

public sealed record ComBridgeProfileDocument(long StopwatchFrequency, string TimingNotes, IReadOnlyList<ComBridgeProfileRow> Methods)
{
    public DateTimeOffset InstanceStartedUtc { get; init; }
    public long StartTimestamp { get; init; }
    public IReadOnlyList<ComBridgeProfileWindow> Windows { get; init; } = Array.Empty<ComBridgeProfileWindow>();
}
