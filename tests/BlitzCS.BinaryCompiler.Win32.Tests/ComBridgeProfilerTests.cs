using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

[Collection("GuestRuntime")]
public sealed class ComBridgeProfilerTests
{
    [Fact]
    public void A_profiled_host_com_call_reports_marshalling_native_time_and_copy_volume()
    {
        using var profiler = new ComBridgeProfiler();
        using var rig = new ComRig(options: new Win32Options { ComBridgeProfiler = profiler });
        using var native = new FakeNativeCom();
        native.Impl = (_, _) => 0;
        var host = new HostComInterface(native.Pointer, "IFake");
        var row = new HostComMethod("Copy", 3, ComArgs.InStruct(8), ComArgs.Value);
        var iface = new GuestComInterface("IFakeGuest", Guid.NewGuid()).Add(row.ToGuest(_ => host));
        var obj = GuestComHost.For(rig.Resolver.Context, rig.Module).Create("Fake", host, iface);
        rig.Module.Memory.Write(ComRig.Data(0x300), [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal(0u, rig.Vt(obj.Address, 3, ComRig.Data(0x300), 17));
        var measured = Assert.Single(profiler.StopAndSnapshot());

        Assert.Equal("IFakeGuest", measured.InterfaceName);
        Assert.Equal("Copy", measured.MethodName);
        Assert.Equal(1, measured.Calls);
        Assert.Equal(1, measured.NativeCalls);
        Assert.Equal(0, measured.AdaptationFailures);
        Assert.Equal(8, measured.GuestToHostCopyBytes);
        Assert.Equal(1, measured.NativeScratchAllocationCount);
        Assert.Equal(8, measured.NativeScratchAllocationBytes);
        Assert.True(measured.ManagedPreHostTicks > 0);
        Assert.True(measured.NativeHostInclusiveTicks > 0);
        Assert.True(measured.TotalTicks >= measured.NativeHostInclusiveTicks);
        var window = Assert.Single(profiler.StopAndSnapshotWindows());
        Assert.Equal(measured.Calls, Assert.Single(window.Methods).Calls);
        Assert.Equal(window.StartTimestamp + Stopwatch.Frequency, window.EndTimestamp);
    }

    [Fact]
    public void Snapshots_retain_only_the_latest_128_one_second_windows()
    {
        const long frequency = 1000;
        const long origin = 5000;
        var timestamp = origin;
        using var profiler = new ComBridgeProfiler(null, false, () => timestamp, DateTimeOffset.UnixEpoch, origin, frequency);

        for (var second = 0; second <= 130; second++)
        {
            timestamp = origin + second * frequency;
            var sample = profiler.Begin("IFake", "Ping");
            sample.Complete();
        }

        var windows = profiler.StopAndSnapshotWindows();
        Assert.Equal(128, windows.Count);
        Assert.Equal(origin + 3 * frequency, windows[0].StartTimestamp);
        Assert.Equal(origin + 130 * frequency, windows[windows.Count - 1].StartTimestamp);
        Assert.All(windows, window => Assert.Equal(frequency, window.EndTimestamp - window.StartTimestamp));
        Assert.Equal(128, Assert.Single(profiler.StopAndSnapshot()).Calls);
    }

    [Fact]
    public void Native_scratch_copies_guest_memory_directly_and_keeps_it_isolated()
    {
        using var rig = new ComRig();
        var memory = new GuestMemoryAdapter(rig.Module.Memory);
        var source = ComRig.Data(0x300);
        var input = new byte[] { 3, 5, 7, 9 };
        rig.Module.Memory.Write(source, input);

        using var scope = new NativeScope();
        var native = scope.CopyFromGuest(memory, source, input.Length);
        var copied = new byte[input.Length];
        Marshal.Copy(native, copied, 0, copied.Length);
        Assert.Equal(input, copied);

        Marshal.WriteByte(native, 0, 99);
        Assert.Equal(input[0], rig.Module.Memory.ReadByte(source));
        var empty = scope.CopyFromGuest(memory, source, 0);
        Assert.Equal((byte)0, Marshal.ReadByte(empty));
    }

    [Fact]
    public void Failed_guest_copy_does_not_report_scratch_allocations_or_copied_bytes()
    {
        using var profiler = new ComBridgeProfiler();
        using var rig = new ComRig(options: new Win32Options { ComBridgeProfiler = profiler });
        var sample = profiler.Begin("IFake", "BadCopy");
        using (sample.Activate())
        using (var scope = new NativeScope())
            Assert.ThrowsAny<Exception>(() => scope.CopyFromGuest(new GuestMemoryAdapter(rig.Module.Memory), 0xFFFF_FFFF, 8));
        sample.Complete();

        var row = Assert.Single(profiler.StopAndSnapshot());
        Assert.Equal(0, row.NativeScratchAllocationCount);
        Assert.Equal(0, row.GuestToHostCopyBytes);
    }

    [Fact]
    public void Direct_guest_copy_avoids_the_managed_intermediate_array()
    {
        const int iterations = 128;
        const int length = 64;
        using var rig = new ComRig();
        var memory = new GuestMemoryAdapter(rig.Module.Memory);
        var source = ComRig.Data(0x300);
        rig.Module.Memory.Write(source, new byte[length]);

        long Measure(bool direct)
        {
            using var scope = new NativeScope();
            _ = GC.GetAllocatedBytesForCurrentThread();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                if (direct) scope.CopyFromGuest(memory, source, length);
                else scope.Copy(memory.ReadBytes(source, length));
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        _ = Measure(direct: false);
        _ = Measure(direct: true);
        var legacyBytes = Measure(direct: false);
        var directBytes = Measure(direct: true);

        Assert.True(legacyBytes - directBytes >= (long)iterations * length,
            $"Expected at least {iterations * length} fewer managed bytes; legacy={legacyBytes}, direct={directBytes}.");
    }

    [Fact]
    public void Counted_array_input_uses_the_direct_native_guest_copy()
    {
        using var rig = new ComRig();
        var memory = new GuestMemoryAdapter(rig.Module.Memory);
        var source = ComRig.Data(0x300);
        byte[] input = [0x21, 0x43, 0x65, 0x87];
        rig.Module.Memory.Write(source, input);
        using var scope = new ComCallScope(memory, rig.Resolver.Context, [source, 2]);

        var native = HostArgs.CountedArray(1, 2, optional: false).ToHost(scope, source);
        var output = new byte[input.Length];
        Marshal.Copy(native, output, 0, output.Length);
        Assert.Equal(input, output);
    }

    [Fact]
    public void An_adaptation_failure_is_counted_without_entering_the_native_method()
    {
        using var profiler = new ComBridgeProfiler();
        using var rig = new ComRig(options: new Win32Options { ComBridgeProfiler = profiler });
        using var native = new FakeNativeCom();
        var host = new HostComInterface(native.Pointer, "IFake");
        var row = new HostComMethod("Copy", 3, ComArgs.InStruct(8));
        var iface = new GuestComInterface("IFakeGuest", Guid.NewGuid()).Add(row.ToGuest(_ => host));
        var obj = GuestComHost.For(rig.Resolver.Context, rig.Module).Create("Fake", host, iface);

        Assert.Equal(ComHr.InvalidArg, rig.Vt(obj.Address, 3, 0));
        var measured = Assert.Single(profiler.StopAndSnapshot());

        Assert.Equal(1, measured.Calls);
        Assert.Equal(0, measured.NativeCalls);
        Assert.Equal(1, measured.AdaptationFailures);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Custom_guest_com_handlers_inherit_copy_and_native_call_attribution()
    {
        using var profiler = new ComBridgeProfiler();
        using var rig = new ComRig(options: new Win32Options { ComBridgeProfiler = profiler });
        using var native = new FakeNativeCom();
        var host = new HostComInterface(native.Pointer, "IFake");
        var method = new GuestComMethod("CustomCopy", 1, call =>
        {
            using var scratch = new NativeScope();
            scratch.Copy(call.Mem.ReadBytes(call.Arg(0), 4));
            return host.Invoke(3, 4, 5);
        });
        var iface = new GuestComInterface("IFakeGuest", Guid.NewGuid()).Add(method);
        var obj = GuestComHost.For(rig.Resolver.Context, rig.Module).Create("Fake", host, iface);
        rig.Module.Memory.Write(ComRig.Data(0x300), [1, 2, 3, 4]);

        Assert.Equal(0u, rig.Vt(obj.Address, 3, ComRig.Data(0x300)));
        var measured = Assert.Single(profiler.StopAndSnapshot());

        Assert.Equal("IFakeGuest", measured.InterfaceName);
        Assert.Equal("CustomCopy", measured.MethodName);
        Assert.Equal(1, measured.Calls);
        Assert.Equal(1, measured.NativeCalls);
        Assert.Equal(4, measured.GuestToHostCopyBytes);
        Assert.Equal(1, measured.NativeScratchAllocationCount);
    }

    [Fact]
    public void The_environment_opt_in_exports_a_json_profile_when_the_context_disposes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"com-profile-{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable("BLITZCS_PROFILE_COM_PATH");
        Environment.SetEnvironmentVariable("BLITZCS_PROFILE_COM_PATH", path);
        try
        {
            using (var rig = new ComRig())
            using (var native = new FakeNativeCom())
            {
                var host = new HostComInterface(native.Pointer, "IFake");
                var row = new HostComMethod("Ping", 3, ComArgs.Value);
                var iface = new GuestComInterface("IFakeGuest", Guid.NewGuid()).Add(row.ToGuest(_ => host));
                var obj = GuestComHost.For(rig.Resolver.Context, rig.Module).Create("Fake", host, iface);
                Assert.Equal(0u, rig.Vt(obj.Address, 3, 19));
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.GetProperty("StopwatchFrequency").GetInt64() > 0);
            Assert.True(document.RootElement.GetProperty("StartTimestamp").GetInt64() > 0);
            Assert.Equal(1, document.RootElement.GetProperty("Windows").GetArrayLength());
            var method = Assert.Single(document.RootElement.GetProperty("Methods").EnumerateArray());
            Assert.Equal("IFakeGuest", method.GetProperty("InterfaceName").GetString());
            Assert.Equal("Ping", method.GetProperty("MethodName").GetString());
            Assert.Equal(1, method.GetProperty("Calls").GetInt64());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLITZCS_PROFILE_COM_PATH", previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_captured_process_exit_callback_is_harmless_after_dispose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"com-profile-exit-{Guid.NewGuid():N}.json");
        var profiler = new ComBridgeProfiler(path, exportAtProcessExit: true);
        EventHandler captured = profiler.OnProcessExit;
        profiler.Dispose();

        var previous = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            captured(AppDomain.CurrentDomain, EventArgs.Empty);
        }
        finally
        {
            Console.SetError(previous);
            if (File.Exists(path)) File.Delete(path);
        }

        Assert.DoesNotContain("COM bridge profile export failed", error.ToString(), StringComparison.Ordinal);
    }

    private sealed unsafe class FakeNativeCom : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Qi(nint self, nint iid, nint ppv);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Ref(nint self);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int M(nint self, nint a, nint b, nint c, nint d);

        private readonly List<Delegate> keep = [];
        private readonly nint block;
        public readonly List<nint> Calls = [];
        public Func<nint, nint, int> Impl = (_, _) => 0;
        public nint Pointer => block;

        public FakeNativeCom()
        {
            var vtbl = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            Qi qi = (_, _, ppv) => { *(nint*)ppv = 0; return unchecked((int)ComHr.NoInterface); };
            Ref add = _ => 1;
            Ref release = _ => 1;
            M invoke = (_, a, b, _, _) => { Calls.Add(a); return Impl(a, b); };
            keep.AddRange([qi, add, release, invoke]);
            vtbl[0] = Marshal.GetFunctionPointerForDelegate(qi);
            vtbl[1] = Marshal.GetFunctionPointerForDelegate(add);
            vtbl[2] = Marshal.GetFunctionPointerForDelegate(release);
            vtbl[3] = Marshal.GetFunctionPointerForDelegate(invoke);
            block = (nint)NativeMemory.Alloc((nuint)sizeof(nint));
            *(nint**)block = vtbl;
        }

        public void Dispose() { NativeMemory.Free(*(void**)block); NativeMemory.Free((void*)block); }
    }
}
