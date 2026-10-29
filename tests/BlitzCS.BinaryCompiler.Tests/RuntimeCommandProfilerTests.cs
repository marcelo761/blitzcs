using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class RuntimeCommandProfilerTests
{
    [Fact]
    public void Snapshot_counts_commands_and_subtracts_nested_child_time()
    {
        long ticks = 0;
        var profiler = new RuntimeCommandProfiler(null, () => ticks, () => UtcAt(ticks), frequency: 10);

        var outer = profiler.Begin("outer");
        ticks += 2;
        using (profiler.Begin("inner")) ticks += 3;
        ticks += 2;
        outer.Dispose();

        var snapshot = profiler.Snapshot();
        var outerRow = Assert.Single(Assert.Single(snapshot.Windows).Commands, row => row.Name == "outer");
        var innerRow = Assert.Single(snapshot.Windows[0].Commands, row => row.Name == "inner");

        Assert.Equal(1, outerRow.Count);
        Assert.Equal(700d, outerRow.TotalMilliseconds);
        Assert.Equal(400d, outerRow.SelfMilliseconds);
        Assert.Equal(1, innerRow.Count);
        Assert.Equal(300d, innerRow.TotalMilliseconds);
        Assert.Equal(300d, innerRow.SelfMilliseconds);
    }

    [Fact]
    public void Only_top_level_flip_exits_count_frames_and_close_bounded_time_windows()
    {
        long ticks = 0;
        var profiler = new RuntimeCommandProfiler(null, () => ticks, () => UtcAt(ticks), frequency: 10);

        // First top-level Flip establishes the baseline and must not invent a frame duration.
        using (profiler.Begin("Flip")) ticks += 10;

        using (profiler.Begin("Flip")) ticks += 2;
        using (profiler.Begin("Flip")) ticks += 4;
        using (profiler.Begin("Flip")) ticks += 4;

        var snapshot = profiler.Snapshot();

        Assert.Equal(2, snapshot.Windows.Length);
        Assert.Equal(0, snapshot.Windows[0].FrameCount);
        var measured = snapshot.Windows[1];
        Assert.Equal(3, measured.FrameCount);
        Assert.Equal(1000d, measured.FrameTotalMilliseconds);
        Assert.Equal(200d, measured.FrameMinMilliseconds);
        Assert.Equal(400d, measured.FrameMaxMilliseconds);
        Assert.Equal(Environment.CurrentManagedThreadId, measured.FrameOwnerThreadId);
        Assert.True(measured.ManagedAllocatedBytes >= 0);
        var flip = Assert.Single(measured.Commands, row => row.Name == "Flip");
        Assert.Equal(3, flip.Count);
    }

    [Fact]
    public void Nested_flip_exit_does_not_add_a_frame_boundary()
    {
        long ticks = 0;
        var profiler = new RuntimeCommandProfiler(null, () => ticks, () => UtcAt(ticks), frequency: 100);

        using (profiler.Begin("Flip")) ticks += 10;
        var parent = profiler.Begin("outer");
        ticks += 5;
        using (profiler.Begin("Flip")) ticks += 10;
        parent.Dispose();
        using (profiler.Begin("Flip")) ticks += 5;

        var snapshot = profiler.Snapshot();
        Assert.Equal(1, Assert.Single(snapshot.Windows).FrameCount);
    }

    [Fact]
    public void Frame_metrics_remain_owned_by_the_first_flip_thread_while_other_threads_are_counted()
    {
        long ticks = 0;
        var profiler = new RuntimeCommandProfiler(null, () => ticks, () => UtcAt(ticks), frequency: 1000);
        var ownerThreadId = Environment.CurrentManagedThreadId;

        using (profiler.Begin("Flip")) ticks += 10;

        var otherThreadId = 0;
        var otherThread = new Thread(() =>
        {
            otherThreadId = Environment.CurrentManagedThreadId;
            using (profiler.Begin("Flip")) ticks += 10;
        });
        otherThread.Start();
        otherThread.Join();

        using (profiler.Begin("Flip")) ticks += 10;

        var window = Assert.Single(profiler.Snapshot().Windows);
        Assert.NotEqual(ownerThreadId, otherThreadId);
        Assert.Equal(ownerThreadId, window.FrameOwnerThreadId);
        Assert.Equal(1, window.FrameCount);
        Assert.Equal(20d, window.FrameTotalMilliseconds);
        Assert.True(window.ManagedAllocatedBytes >= 0);
        var workerCommand = Assert.Single(window.Commands, row => row.Name == "Flip" && row.ThreadId == otherThreadId);
        Assert.Equal(1, workerCommand.Count);
    }

    [Fact]
    public void Snapshot_keeps_only_the_latest_128_windows()
    {
        long ticks = 0;
        var profiler = new RuntimeCommandProfiler(null, () => ticks, () => UtcAt(ticks), frequency: 1);

        for (var i = 0; i < 131; i++)
        {
            using (profiler.Begin("Flip")) ticks++;
        }

        var snapshot = profiler.Snapshot();

        Assert.Equal(128, snapshot.Windows.Length);
        Assert.Equal(4, snapshot.Windows[0].EndTimestamp);
        Assert.Equal(131, snapshot.Windows[^1].EndTimestamp);
    }

    [Fact]
    public void RegisterModule_keeps_copied_translation_metadata_and_distinct_module_instances()
    {
        var artifact = FakeGuest.Create().Artifact;
        var addressMap = artifact.AddressMap.ToList();
        var exports = artifact.Exports.ToList();
        var sourceAddress = addressMap[0];
        var sourceExport = exports[0];
        var mutableArtifact = artifact with { AddressMap = addressMap, Exports = exports };
        var profiler = new RuntimeCommandProfiler(null);

        profiler.RegisterModule("guest.dll", mutableArtifact, 0x1000_0000);
        profiler.RegisterModule("guest.dll", mutableArtifact, 0x2000_0000);
        addressMap[0] = new AddressMapEntry(sourceAddress.GuestRva + 1, sourceAddress.HostOffset + 1);
        addressMap.Add(new AddressMapEntry(0xFFFF, 0xFFFF));
        exports[0] = new TranslatedExport("mutated", sourceExport.Ordinal, 0xFFFF, 0xFFFF, sourceExport.Convention, sourceExport.ArgBytes);
        exports.Add(new TranslatedExport("added", 0, 0xEEEE, 0xEEEE, sourceExport.Convention, null));

        var modules = profiler.Snapshot().Modules;

        Assert.Equal(2, modules.Length);
        Assert.Equal(new ulong[] { 0x1000_0000UL, 0x2000_0000UL }, modules.Select(module => module.HostBase));
        foreach (var module in modules)
        {
            Assert.Equal("guest.dll", module.Name);
            Assert.Equal(artifact.SourceSha256, module.SourceSha256);
            Assert.Equal(artifact.GuestImageBase, module.GuestImageBase);
            Assert.Equal(artifact.CodeSize, module.CodeSize);
            Assert.Equal(artifact.EnterGuestOffset, module.EnterGuestOffset);
            Assert.Equal(artifact.AddressMap.Count, module.AddressMap.Count);
            Assert.Equal(sourceAddress, module.AddressMap[0]);
            Assert.Equal(artifact.Exports.Count, module.Exports.Count);
            Assert.Equal(sourceExport.Name, module.Exports[0].Name);
            Assert.Equal(sourceExport.GuestRva, module.Exports[0].GuestRva);
            Assert.Equal(sourceExport.HostOffset, module.Exports[0].HostOffset);
        }
    }

    [Fact]
    public void Metadata_only_mode_keeps_module_map_without_command_or_frame_timing()
    {
        var profiler = new RuntimeCommandProfiler(null, metadataOnly: true);
        profiler.RegisterModule("guest.dll", FakeGuest.Create().Artifact, 0x1000_0000);

        using (profiler.Begin("Flip")) { }
        using (profiler.Begin("DrawScene")) { }

        var snapshot = profiler.Snapshot();
        Assert.Single(snapshot.Modules);
        Assert.Empty(snapshot.Windows);
        Assert.Contains("Metadata-only mode", snapshot.AllocationMeasurement);
        Assert.Contains("no frame timing is inferred", snapshot.AllocationMeasurement);
    }

    private static DateTimeOffset UtcAt(long ticks) => DateTimeOffset.UnixEpoch.AddMilliseconds(ticks * 100);
}
