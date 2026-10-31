using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BlitzCS.BinaryCompiler;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Opt-in per-command and top-level Flip timing for a translated runtime.</summary>
internal sealed class RuntimeCommandProfiler
{
    private const string OutputPathVariable = "BLITZCS_PROFILE_COMMAND_PATH";
    private const string MetadataOnlyVariable = "BLITZCS_PROFILE_COMMAND_METADATA_ONLY";
    private const int WindowCapacity = 128;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static RuntimeCommandProfiler? Instance => EnvironmentProfiler.Instance;

    private readonly string? outputPath;
    private readonly Func<long>? timestampProvider;
    private readonly Func<DateTimeOffset>? utcNowProvider;
    private readonly long frequency;
    private readonly bool metadataOnly;
    private readonly ThreadLocal<ThreadState> states;
    private readonly ConcurrentQueue<RuntimeCommandProfilerModuleSnapshot> modules = new();
    private readonly Window[] completedWindows = new Window[WindowCapacity];
    private volatile Window currentWindow;
    private int nextWindowIndex;
    private int completedWindowCount;
    private long lastFlipEndTicks;
    private long lastFlipAllocatedBytes;
    private bool hasPreviousFlip;
    private int frameOwnerThreadId;
    private readonly DateTimeOffset startedUtc;

    internal RuntimeCommandProfiler(
        string? outputPath,
        Func<long>? timestampProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        long? frequency = null,
        bool metadataOnly = false)
    {
        this.outputPath = outputPath;
        this.timestampProvider = timestampProvider;
        this.utcNowProvider = utcNowProvider;
        this.frequency = frequency ?? Stopwatch.Frequency;
        this.metadataOnly = metadataOnly;
        if (this.frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        startedUtc = UtcNow();
        var startTicks = Timestamp();
        currentWindow = new Window(startTicks, startedUtc);
        states = new ThreadLocal<ThreadState>(static () => new ThreadState(), trackAllValues: true);
    }

    private static RuntimeCommandProfiler? CreateFromEnvironment()
    {
        var requestedPath = Environment.GetEnvironmentVariable(OutputPathVariable);
        if (string.IsNullOrWhiteSpace(requestedPath)) return null;

        try
        {
            var absolutePath = Path.GetFullPath(requestedPath);
            var metadataOnly = string.Equals(Environment.GetEnvironmentVariable(MetadataOnlyVariable), "1", StringComparison.Ordinal);
            var profiler = new RuntimeCommandProfiler(absolutePath, metadataOnly: metadataOnly);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => profiler.WriteReport();
            return profiler;
        }
        catch (Exception exception)
        {
            WriteDiagnostic($"Command profiling disabled: could not initialize '{OutputPathVariable}': {exception.Message}");
            return null;
        }
    }

    private static class EnvironmentProfiler
    {
        internal static readonly RuntimeCommandProfiler? Instance = CreateFromEnvironment();
    }

    /// <summary>Begins one command scope. The default scope is a no-op when a caller has no profiler instance.</summary>
    internal CommandScope Begin(string name)
    {
        if (metadataOnly || string.IsNullOrEmpty(name)) return default;

        var state = states.Value!;
        var window = currentWindow;
        if (!ReferenceEquals(state.Window, window))
        {
            state.Window = window;
            state.Rows = window.RowsByState.GetOrAdd(state, static _ => new Dictionary<string, CommandRow>(StringComparer.Ordinal));
        }

        var rows = state.Rows!;
        if (!rows.TryGetValue(name, out var row))
        {
            row = new CommandRow(name);
            rows.Add(name, row);
        }

        var depth = state.Depth;
        if (depth == state.Frames.Length) Array.Resize(ref state.Frames, state.Frames.Length * 2);
        var now = Timestamp();
        var isTopLevelFlip = depth == 0 && string.Equals(name, "Flip", StringComparison.OrdinalIgnoreCase);
        state.Frames[depth] = new ScopeFrame(row, window, now, isTopLevelFlip, state.ThreadId);
        state.Depth = depth + 1;
        return new CommandScope(this, state, depth);
    }

    /// <summary>Copies translated-code metadata once for later correlation with sampled host instruction addresses.</summary>
    internal void RegisterModule(string name, TranslationArtifact artifact, ulong hostBase)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(artifact);

        var addressMap = Array.AsReadOnly(artifact.AddressMap.ToArray());
        var exports = Array.AsReadOnly(artifact.Exports
            .Select(static export => new RuntimeCommandProfilerExportSnapshot(export.Name, export.GuestRva, export.HostOffset))
            .ToArray());
        modules.Enqueue(new RuntimeCommandProfilerModuleSnapshot(
            Name: name,
            SourceSha256: artifact.SourceSha256,
            GuestImageBase: artifact.GuestImageBase,
            HostBase: hostBase,
            CodeSize: artifact.CodeSize,
            EnterGuestOffset: artifact.EnterGuestOffset,
            AddressMap: addressMap,
            Exports: exports));
    }

    /// <summary>Returns a stable report view. Call only when no profiled command scopes are active.</summary>
    internal RuntimeCommandProfilerSnapshot Snapshot()
    {
        var now = Timestamp();
        var utcNow = UtcNow();
        var windows = new List<RuntimeCommandProfilerWindowSnapshot>(metadataOnly ? 0 : completedWindowCount + 1);
        var firstIndex = (nextWindowIndex - completedWindowCount + WindowCapacity) % WindowCapacity;
        for (var i = 0; !metadataOnly && i < completedWindowCount; i++)
        {
            var window = completedWindows[(firstIndex + i) % WindowCapacity];
            if (window is not null) windows.Add(CreateSnapshot(window, window.EndTicks, window.EndUtc));
        }

        if (!metadataOnly && currentWindow.HasData)
            windows.Add(CreateSnapshot(currentWindow, now, utcNow));

        return new RuntimeCommandProfilerSnapshot(
            SchemaVersion: 1,
            OutputPath: outputPath,
            StartedUtc: startedUtc,
            StopwatchFrequency: frequency,
            AllocationMeasurement: metadataOnly
                ? "Metadata-only mode: command scopes and frame timing are disabled; no frame timing is inferred."
                : "ManagedAllocatedBytes is the current frame-owner thread's allocated-byte delta between top-level Flip exits and includes profiler overhead. GC collection counts are process-wide deltas, not pause durations.",
            Modules: modules.ToArray(),
            Windows: windows.ToArray());
    }

    internal void WriteReport()
    {
        if (outputPath is null) return;
        try
        {
            var json = JsonSerializer.Serialize(Snapshot(), JsonOptions);
            File.WriteAllText(outputPath, json);
        }
        catch (Exception exception)
        {
            WriteDiagnostic($"Command profiling report could not be written to '{outputPath}': {exception.Message}");
        }
    }

    private void End(ThreadState state, int depth)
    {
        if (state.Depth != depth + 1) return; // Also makes copied or repeated Dispose calls harmless.

        var frame = state.Frames[depth];
        state.Frames[depth] = default;
        state.Depth = depth;

        var endTicks = Timestamp();
        var elapsed = Math.Max(0, endTicks - frame.StartTicks);
        var selfTicks = Math.Max(0, elapsed - frame.ChildTicks);
        frame.Row.Count++;
        frame.Row.TotalTicks += elapsed;
        frame.Row.SelfTicks += selfTicks;

        if (depth > 0)
        {
            var parent = state.Frames[depth - 1];
            parent.ChildTicks += elapsed;
            state.Frames[depth - 1] = parent;
        }

        if (frame.IsTopLevelFlip) EndFrame(frame.Window, endTicks, frame.ThreadId);
    }

    private void EndFrame(Window window, long endTicks, int threadId)
    {
        if (frameOwnerThreadId == 0) frameOwnerThreadId = threadId;
        if (threadId != frameOwnerThreadId) return;
        window.FrameOwnerThreadId = frameOwnerThreadId;

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        if (hasPreviousFlip)
        {
            var frameTicks = Math.Max(0, endTicks - lastFlipEndTicks);
            window.ManagedAllocatedBytes += Math.Max(0, allocatedBytes - lastFlipAllocatedBytes);
            window.FrameCount++;
            window.FrameTotalTicks += frameTicks;
            window.FrameMinTicks = Math.Min(window.FrameMinTicks, frameTicks);
            window.FrameMaxTicks = Math.Max(window.FrameMaxTicks, frameTicks);
        }
        lastFlipEndTicks = endTicks;
        lastFlipAllocatedBytes = allocatedBytes;
        hasPreviousFlip = true;

        if (!ReferenceEquals(window, currentWindow) || endTicks - window.StartTicks < frequency) return;

        window.EndTicks = endTicks;
        window.EndUtc = UtcNow();
        window.Gen0End = GC.CollectionCount(0) - window.Gen0Start;
        window.Gen1End = GC.CollectionCount(1) - window.Gen1Start;
        window.Gen2End = GC.CollectionCount(2) - window.Gen2Start;
        completedWindows[nextWindowIndex] = window;
        nextWindowIndex = (nextWindowIndex + 1) % WindowCapacity;
        completedWindowCount = Math.Min(completedWindowCount + 1, WindowCapacity);
        currentWindow = new Window(endTicks, window.EndUtc);
    }

    private RuntimeCommandProfilerWindowSnapshot CreateSnapshot(Window window, long endTicks, DateTimeOffset endUtc)
    {
        var commands = new List<RuntimeCommandProfilerCommandSnapshot>();
        foreach (var pair in window.RowsByState)
        {
            foreach (var row in pair.Value.Values)
            {
                commands.Add(new RuntimeCommandProfilerCommandSnapshot(
                    Name: row.Name,
                    ThreadId: pair.Key.ThreadId,
                    Count: row.Count,
                    TotalMilliseconds: ToMilliseconds(row.TotalTicks),
                    SelfMilliseconds: ToMilliseconds(row.SelfTicks)));
            }
        }
        commands.Sort(static (left, right) =>
        {
            var byName = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return byName != 0 ? byName : left.ThreadId.CompareTo(right.ThreadId);
        });

        return new RuntimeCommandProfilerWindowSnapshot(
            StartedUtc: window.StartUtc,
            EndedUtc: endUtc,
            StartTimestamp: window.StartTicks,
            EndTimestamp: endTicks,
            ElapsedMilliseconds: ToMilliseconds(Math.Max(0, endTicks - window.StartTicks)),
            FrameOwnerThreadId: window.FrameOwnerThreadId,
            FrameCount: window.FrameCount,
            FrameTotalMilliseconds: ToMilliseconds(window.FrameTotalTicks),
            FrameMinMilliseconds: window.FrameCount == 0 ? null : ToMilliseconds(window.FrameMinTicks),
            FrameMaxMilliseconds: window.FrameCount == 0 ? null : ToMilliseconds(window.FrameMaxTicks),
            ManagedAllocatedBytes: window.ManagedAllocatedBytes,
            Gen0Collections: window.Gen0End ?? GC.CollectionCount(0) - window.Gen0Start,
            Gen1Collections: window.Gen1End ?? GC.CollectionCount(1) - window.Gen1Start,
            Gen2Collections: window.Gen2End ?? GC.CollectionCount(2) - window.Gen2Start,
            Commands: commands.ToArray());
    }

    private long Timestamp() => timestampProvider is null ? Stopwatch.GetTimestamp() : timestampProvider();
    private DateTimeOffset UtcNow() => utcNowProvider is null ? DateTimeOffset.UtcNow : utcNowProvider();
    private double ToMilliseconds(long ticks) => ticks * 1000d / frequency;

    private static void WriteDiagnostic(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch { /* Diagnostics must not take down the host during shutdown. */ }
    }

    internal readonly struct CommandScope : IDisposable
    {
        private readonly RuntimeCommandProfiler? profiler;
        private readonly ThreadState? state;
        private readonly int depth;

        internal CommandScope(RuntimeCommandProfiler profiler, ThreadState state, int depth)
        {
            this.profiler = profiler;
            this.state = state;
            this.depth = depth;
        }

        public void Dispose()
        {
            if (profiler is not null && state is not null) profiler.End(state, depth);
        }
    }

    internal sealed class ThreadState
    {
        internal readonly int ThreadId = Environment.CurrentManagedThreadId;
        internal ScopeFrame[] Frames = new ScopeFrame[16];
        internal int Depth;
        internal Window? Window;
        internal Dictionary<string, CommandRow>? Rows;
    }

    internal struct ScopeFrame(CommandRow row, Window window, long startTicks, bool isTopLevelFlip, int threadId)
    {
        internal CommandRow Row = row;
        internal Window Window = window;
        internal long StartTicks = startTicks;
        internal long ChildTicks;
        internal bool IsTopLevelFlip = isTopLevelFlip;
        internal int ThreadId = threadId;
    }

    internal sealed class CommandRow(string name)
    {
        internal readonly string Name = name;
        internal long Count;
        internal long TotalTicks;
        internal long SelfTicks;
    }

    internal sealed class Window(long startTicks, DateTimeOffset startUtc)
    {
        internal readonly long StartTicks = startTicks;
        internal readonly DateTimeOffset StartUtc = startUtc;
        internal readonly ConcurrentDictionary<ThreadState, Dictionary<string, CommandRow>> RowsByState = new();
        internal readonly int Gen0Start = GC.CollectionCount(0);
        internal readonly int Gen1Start = GC.CollectionCount(1);
        internal readonly int Gen2Start = GC.CollectionCount(2);
        internal long EndTicks;
        internal DateTimeOffset EndUtc;
        internal int? FrameOwnerThreadId;
        internal long FrameCount;
        internal long FrameTotalTicks;
        internal long ManagedAllocatedBytes;
        internal long FrameMinTicks = long.MaxValue;
        internal long FrameMaxTicks;
        internal int? Gen0End;
        internal int? Gen1End;
        internal int? Gen2End;
        internal bool HasData => FrameCount != 0 || RowsByState.Count != 0;
    }
}

internal sealed record RuntimeCommandProfilerSnapshot(
    int SchemaVersion,
    string? OutputPath,
    DateTimeOffset StartedUtc,
    long StopwatchFrequency,
    string AllocationMeasurement,
    RuntimeCommandProfilerModuleSnapshot[] Modules,
    RuntimeCommandProfilerWindowSnapshot[] Windows);

internal sealed record RuntimeCommandProfilerModuleSnapshot(
    string Name,
    string SourceSha256,
    uint GuestImageBase,
    ulong HostBase,
    int CodeSize,
    uint EnterGuestOffset,
    IReadOnlyList<AddressMapEntry> AddressMap,
    IReadOnlyList<RuntimeCommandProfilerExportSnapshot> Exports);

internal sealed record RuntimeCommandProfilerExportSnapshot(string? Name, uint GuestRva, uint HostOffset);

internal sealed record RuntimeCommandProfilerWindowSnapshot(
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    long StartTimestamp,
    long EndTimestamp,
    double ElapsedMilliseconds,
    int? FrameOwnerThreadId,
    long FrameCount,
    double FrameTotalMilliseconds,
    double? FrameMinMilliseconds,
    double? FrameMaxMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    RuntimeCommandProfilerCommandSnapshot[] Commands);

internal sealed record RuntimeCommandProfilerCommandSnapshot(
    string Name,
    int ThreadId,
    long Count,
    double TotalMilliseconds,
    double SelfMilliseconds);
