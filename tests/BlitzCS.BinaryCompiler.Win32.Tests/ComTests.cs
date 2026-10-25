using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.BinaryCompiler.Win32.Com;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

[Collection("GuestRuntime")]
public sealed class StubPoolTests
{
    [Fact]
    public void A_pool_stub_is_a_guest_callable_stdcall_function_with_the_exact_pop_count()
    {
        using var g = new ComRig(8);
        Assert.Equal(8, g.Module.StubCapacity);
        var add = g.Module.AllocateStub(c => c.Arg(0) + c.Arg(1), 8);
        Assert.True(g.Module.IsStubAddress(add));
        Assert.Equal(1, g.Module.StubsInUse);
        Assert.Equal(7u + 35u, g.F("callptr2", add, 7, 35));
        // The wrapper returns through its own frame: a callee that popped the wrong number of bytes would have corrupted it.
        for (var i = 0; i < 200; i++) Assert.Equal((uint)i + 1u, g.F("callptr2", add, (uint)i, 1));
    }

    [Fact]
    public void The_pool_is_bounded_exhaustion_is_explicit_and_released_stubs_are_reused()
    {
        using var g = new ComRig(3);
        var a = Enumerable.Range(0, 3).Select(i => g.Module.AllocateStub(c => (uint)i, 0)).ToArray();
        Assert.Equal(3, a.Distinct().Count());
        var full = Assert.Throws<GuestStubException>(() => g.Module.AllocateStub(_ => 0, 0));
        Assert.Contains("exhausted", full.Message);
        g.Module.ReleaseStub(a[1]);
        Assert.Equal(a[1], g.Module.AllocateStub(_ => 9, 0));
        Assert.Throws<GuestStubException>(() => g.Module.ReleaseStub(0x1234));
    }

    [Fact]
    public void Releasing_twice_is_detected_and_a_stub_that_is_not_allocated_faults_by_name_and_index()
    {
        using var g = new ComRig(4);
        var s0 = g.Module.AllocateStub(_ => 1, 8);
        var s1 = g.Module.AllocateStub(_ => 2, 8);
        g.Module.ReleaseStub(s0);
        Assert.Contains("double release", Assert.Throws<GuestStubException>(() => g.Module.ReleaseStub(s0)).Message);

        var released = Assert.Throws<GuestImportException>(() => g.F("callptr2", s0, 1, 2));
        Assert.Contains("dynamic stub #0", released.Message + released.InnerException?.Message);
        Assert.Contains("released", released.Message + released.InnerException?.Message);
        var never = Assert.Throws<GuestImportException>(() => g.F("callptr2", s1 + 16, 1, 2));
        Assert.Contains("dynamic stub #2", never.Message + never.InnerException?.Message);
        Assert.Contains("never handed out", never.Message + never.InnerException?.Message);
        Assert.Equal(2u, g.F("callptr2", s1, 1, 2));   // the allocated neighbour is unaffected, and the module still works after the faults
    }

    [Fact]
    public void A_module_translated_without_a_pool_says_so()
    {
        using var g = new ComRig(0);
        var error = Assert.Throws<GuestStubException>(() => g.Module.AllocateStub(_ => 0, 0));
        Assert.Contains("DynamicStubs", error.Message);
    }

    [Fact]
    public void Stubs_can_be_allocated_and_called_from_several_threads()
    {
        using var g = new ComRig(64);
        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 300; i++)
                {
                    var stub = g.Module.AllocateStub(c => c.Arg(0) * 2 + (uint)t, 8);
                    Assert.Equal((uint)i * 2 + (uint)t, g.F("callptr2", stub, (uint)i, 0));
                    g.Module.ReleaseStub(stub);
                }
            }
            catch (Exception e) { lock (errors) errors.Add(e); }
        })).ToList();
        threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.Equal(0, g.Module.StubsInUse);
    }
}

[Collection("GuestRuntime")]
public sealed class GuestComTests
{
    private static readonly Guid IFoo = new("11111111-2222-3333-4444-555555555555"), IBar = new("66666666-7777-8888-9999-AAAAAAAAAAAA"), INope = new("DEADBEEF-0000-0000-0000-000000000001");

    private static (GuestComInterface Foo, GuestComInterface Bar, List<string> Log) Interfaces()
    {
        var log = new List<string>();
        var foo = new GuestComInterface("IFoo", IFoo)
            .Method("Zero", 0, c => { lock (log) log.Add("Zero"); return 100; })
            .Method("One", 1, c => c.Arg(0) + 1)
            .Method("Three", 3, c => c.Arg(0) * 100 + c.Arg(1) * 10 + c.Arg(2))
            .NotImplemented("Later", 2, "documented as not implemented in this test interface");
        var bar = new GuestComInterface("IBar", IBar).Method("Ping", 1, c => c.Arg(0) ^ 0xFF);
        return (foo, bar, log);
    }

    [Fact]
    public void Vtable_methods_run_with_exact_stdcall_frames_and_unimplemented_slots_answer_E_NOTIMPL()
    {
        using var g = new ComRig();
        var (foo, bar, _) = Interfaces();
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var obj = com.Create("Obj", null, [foo, bar]);
        var p = obj.Address;
        Assert.NotEqual(0u, p);
        Assert.Equal(100u, g.Vt(p, 3));
        Assert.Equal(42u, g.Vt(p, 4, 41));
        Assert.Equal(123u, g.Vt(p, 5, 1, 2, 3));
        Assert.Equal(ComHr.NotImpl, g.Vt(p, 6, 0, 0));
        // an out-of-range slot is not in the vtable at all: the guest reads whatever follows (not asserted); the table has exactly 3 + 4 entries
        Assert.Equal(7, foo.Methods.Count + 3);
        // the second face is a different interface pointer of the same object
        g.QueryInterface(p, IBar, out var barPtr);
        Assert.NotEqual(p, barPtr);
        Assert.Equal(0x55u ^ 0xFF, g.Vt(barPtr, 3, 0x55));
        g.Release(barPtr);
        Assert.Equal(0u, g.Release(p));
    }

    [Fact]
    public void QueryInterface_uses_the_IID_table_with_identity_and_E_NOINTERFACE()
    {
        using var g = new ComRig();
        var (foo, bar, _) = Interfaces();
        foo.Answers(new Guid("0F0F0F0F-0000-0000-0000-000000000000"));
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var obj = com.Create("Obj", null, [foo, bar]);
        var p = obj.Address;

        Assert.Equal(ComHr.Ok, g.QueryInterface(p, ComIids.IUnknown, out var unk));
        Assert.Equal(p, unk);                                   // IUnknown identity = the primary face
        Assert.Equal(ComHr.Ok, g.QueryInterface(p, IFoo, out var same));
        Assert.Equal(p, same);
        Assert.Equal(ComHr.Ok, g.QueryInterface(p, new Guid("0F0F0F0F-0000-0000-0000-000000000000"), out var alias));
        Assert.Equal(p, alias);
        Assert.Equal(ComHr.Ok, g.QueryInterface(p, IBar, out var other));
        Assert.Equal(ComHr.NoInterface, g.QueryInterface(p, INope, out var none));
        Assert.Equal(0u, none);                                 // *ppv is cleared on failure
        Assert.Equal(5, obj.RefCount);                          // 1 creator + unk + same + alias + other (each successful QI AddRefs)
        Assert.Equal(ComHr.Pointer, g.Vt(p, 0, ComRig.Data(0x100), 0));
        // IUnknown identity through the other face
        g.QueryInterface(other, ComIids.IUnknown, out var unk2);
        Assert.Equal(p, unk2);
    }

    [Fact]
    public void AddRef_and_Release_keep_a_real_count_and_the_last_release_destroys_exactly_once()
    {
        using var g = new ComRig();
        var (foo, _, _) = Interfaces();
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var destroyed = 0;
        var obj = com.Create("Obj", "state", foo, o => destroyed++);
        var p = obj.Address;
        Assert.Equal(1, obj.RefCount);
        Assert.Equal(2u, g.AddRef(p));
        Assert.Equal(3u, g.AddRef(p));
        Assert.Equal(2u, g.Release(p));
        Assert.Equal(1u, g.Release(p));
        Assert.Equal(0, destroyed);
        Assert.Equal(1, com.LiveObjects);
        Assert.Equal(0u, g.Release(p));
        Assert.Equal(1, destroyed);
        Assert.False(obj.IsAlive);
        Assert.Equal(0, com.LiveObjects);

        // double release / use after free fault by name (the block now points at a dead vtable)
        var second = Assert.Throws<GuestImportException>(() => g.Release(p));
        var text = second.Message + second.InnerException?.Message;
        Assert.Contains("Obj #1", text);
        Assert.Contains("destroyed", text);
        Assert.Throws<GuestImportException>(() => g.Vt(p, 3));
        Assert.Equal(1, destroyed);
    }

    [Fact]
    public void Two_guest_threads_AddRef_and_Release_concurrently_without_losing_a_count()
    {
        using var g = new ComRig();
        var (foo, _, _) = Interfaces();
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var obj = com.Create("Obj", null, foo);
        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            try { g.F("hammer", obj.Address, 20000); }
            catch (Exception e) { lock (errors) errors.Add(e); }
        })).ToList();
        threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.Equal(1, obj.RefCount);
        Assert.True(obj.IsAlive);
        // methods are dispatched concurrently too
        var counter = 0;
        var calls = new GuestComInterface("ICount", new Guid("33333333-0000-0000-0000-000000000000")).Method("Inc", 0, _ => (uint)Interlocked.Increment(ref counter));
        var obj2 = com.Create("Counter", null, calls);
        threads = [.. Enumerable.Range(0, 2).Select(_ => new Thread(() => { try { for (var i = 0; i < 5000; i++) g.Vt(obj2.Address, 3); } catch (Exception e) { lock (errors) errors.Add(e); } }))];
        threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.Equal(10000, counter);
    }

    [Fact]
    public void Disposing_the_context_destroys_objects_the_guest_never_released()
    {
        var destroyed = new List<string>();
        var g = new ComRig();
        var (foo, bar, _) = Interfaces();
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        com.Create("A", null, foo, o => destroyed.Add(o.Name));
        var b = com.Create("B", null, bar, o => destroyed.Add(o.Name));
        g.AddRef(b.Address);
        g.Module.Dispose();                  // the module goes first: the host resources behind the objects must still be released
        g.Resolver.Dispose();
        Assert.Equal(["A", "B"], destroyed.Order().ToArray());
    }

    [Fact]
    public void A_handler_that_throws_fails_the_guest_call_naming_the_method()
    {
        using var g = new ComRig();
        var iface = new GuestComInterface("IBoom", new Guid("44444444-0000-0000-0000-000000000000")).Method("Boom", 0, _ => throw new InvalidOperationException("kaboom"));
        var obj = GuestComHost.For(g.Resolver.Context, g.Module).Create("Boom", null, iface);
        var error = Assert.Throws<GuestImportException>(() => g.Vt(obj.Address, 3));
        Assert.Contains("kaboom", error.Message);
        Assert.Equal(1, obj.RefCount);
    }

    [Fact]
    public void A_pointer_that_is_no_com_object_is_rejected_by_name()
    {
        using var g = new ComRig();
        var (foo, _, _) = Interfaces();
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var obj = com.Create("Obj", null, foo);
        // a fake object whose vtable pointer is a real vtable but whose address is not registered
        g.Module.Memory.WriteUInt32(ComRig.Data(0x200), g.Module.Memory.ReadUInt32(obj.Address));
        var error = Assert.Throws<GuestImportException>(() => g.AddRef(ComRig.Data(0x200)));
        Assert.Contains("not an object of this module", error.Message + error.InnerException?.Message);
    }
}

/// <summary>A hand-built native (64-bit) COM object: vtable of marshalled delegates in unmanaged memory, using the platform's native convention. Runs on Linux and Windows alike.</summary>
internal sealed unsafe class FakeNativeCom : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Qi(nint self, nint iid, nint ppv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Ref(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int M(nint self, nint a, nint b, nint c, nint d);

    public readonly List<(nint A, nint B, nint C, nint D)> Calls = [];
    public int Refs = 1;
    private readonly List<Delegate> keep = [];
    private readonly nint block;
    public nint Pointer => block;
    public Func<nint, nint, nint, nint, int> Impl = (_, _, _, _) => 0;

    public FakeNativeCom()
    {
        var vtbl = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        Qi qi = (_, _, ppv) => { *(nint*)ppv = 0; return unchecked((int)ComHr.NoInterface); };
        Ref add = _ => (uint)Interlocked.Increment(ref Refs);
        Ref rel = _ => (uint)Interlocked.Decrement(ref Refs);
        M m = (_, a, b, c, d) => { lock (Calls) Calls.Add((a, b, c, d)); return Impl(a, b, c, d); };
        keep.AddRange([qi, add, rel, m]);
        vtbl[0] = Marshal.GetFunctionPointerForDelegate(qi); vtbl[1] = Marshal.GetFunctionPointerForDelegate(add);
        vtbl[2] = Marshal.GetFunctionPointerForDelegate(rel); vtbl[3] = Marshal.GetFunctionPointerForDelegate(m);
        block = (nint)NativeMemory.Alloc((nuint)sizeof(nint));
        *(nint**)block = vtbl;
    }

    public void Dispose() { NativeMemory.Free(*(void**)block); NativeMemory.Free((void*)block); }
}

[Collection("GuestRuntime")]
public sealed class HostComProxyTests
{
    [Fact]
    public void The_proxy_calls_a_native_vtable_with_pointer_sized_arguments_and_returns_the_host_hresult()
    {
        using var native = new FakeNativeCom();
        var host = new HostComInterface(native.Pointer, "IFake");
        native.Impl = (a, b, c, d) => unchecked((int)0x8878000A);
        Assert.Equal(0x8878000Au, host.Invoke(3, 1, -2, 7, 8));
        Assert.Equal((1, -2, 7, 8), native.Calls.Single());                 // -2 stayed sign-extended, not 0xFFFFFFFE
        Assert.Equal(ComHr.NoInterface, host.QueryInterface(Guid.NewGuid(), out var none));
        Assert.Equal(0, none);
        Assert.Equal(2u, host.AddRef());
        Assert.Equal(1u, host.Release());
        Assert.Equal(0u, host.Release());                                    // the wrapper owns one reference: a second Release is a no-op
        Assert.Equal(1, native.Refs);                                        // AddRef +1, the wrapper's single Release -1, the no-op second Release changed nothing
        Assert.Throws<ObjectDisposedException>(() => host.Invoke(3));
    }

    [Fact]
    public void An_adapter_row_converts_each_argument_kind_and_copies_results_back_only_on_success()
    {
        using var g = new ComRig();
        using var native = new FakeNativeCom();
        var host = new HostComInterface(native.Pointer, "IFake");
        var memory = g.Module.Memory;
        // guest data: GUID at 0x300, ANSI string at 0x320, 8-byte struct at 0x340, out DWORD at 0x360, in/out struct at 0x380
        var guid = new Guid("01234567-89AB-CDEF-0123-456789ABCDEF");
        memory.Write(ComRig.Data(0x300), guid.ToByteArray());
        memory.Write(ComRig.Data(0x320), "hello\0"u8);
        memory.Write(ComRig.Data(0x340), [1, 2, 3, 4, 5, 6, 7, 8]);
        memory.Write(ComRig.Data(0x380), [9, 9, 9, 9]);

        var row = new HostComMethod("Do", 3, ComArgs.Guid(false), ComArgs.AnsiString(false), ComArgs.InStruct(8), ComArgs.OutU32());
        var rowTwo = new HostComMethod("Do2", 3, ComArgs.Signed, ComArgs.Value, ComArgs.InOutStruct(4), ComArgs.OutU32(optional: true));
        var seen = new List<string>();
        unsafe
        {
            native.Impl = (a, b, c, d) =>
            {
                if (native.Calls.Count == 1)
                {
                    seen.Add(new Guid(new ReadOnlySpan<byte>((void*)a, 16)).ToString() + "|" + Marshal.PtrToStringAnsi(b) + "|" + string.Join(",", new ReadOnlySpan<byte>((void*)c, 8).ToArray()));
                    *(uint*)d = 0xCAFEF00D;
                    return 0;
                }
                *(uint*)c = 0x01010101;                         // in/out struct written by the host
                if (d != 0) *(uint*)d = 0x77777777;
                return unchecked((int)0x80004005);              // failure: nothing may be copied back
            };
        }
        var iface = new GuestComInterface("IFakeGuest", new Guid("55555555-0000-0000-0000-000000000000"))
            .Add(row.ToGuest(_ => host)).Add(rowTwo.ToGuest(_ => host));
        var obj = GuestComHost.For(g.Resolver.Context, g.Module).Create("Fake", host, iface);

        Assert.Equal(0u, g.Vt(obj.Address, 3, ComRig.Data(0x300), ComRig.Data(0x320), ComRig.Data(0x340), ComRig.Data(0x360)));
        Assert.Equal(["01234567-89ab-cdef-0123-456789abcdef|hello|1,2,3,4,5,6,7,8"], seen);
        Assert.Equal(0xCAFEF00Du, memory.ReadUInt32(ComRig.Data(0x360)));

        memory.WriteUInt32(ComRig.Data(0x360), 0x11111111);
        Assert.Equal(0x80004005u, g.Vt(obj.Address, 4, 0xFFFFFFFE, 5, ComRig.Data(0x380), ComRig.Data(0x360)));
        Assert.Equal(0x11111111u, memory.ReadUInt32(ComRig.Data(0x360)));          // failure: out value not copied
        Assert.Equal(0x09090909u, memory.ReadUInt32(ComRig.Data(0x380)));          // failure: in/out struct untouched
        Assert.Equal((nint)(-2), native.Calls[^1].A);                              // LONG sign-extended
        Assert.Equal(5, native.Calls[^1].B);

        // a NULL where the table requires a pointer never reaches the native method
        var before = native.Calls.Count;
        Assert.Equal(ComHr.InvalidArg, g.Vt(obj.Address, 3, 0, ComRig.Data(0x320), ComRig.Data(0x340), ComRig.Data(0x360)));
        Assert.Equal(before, native.Calls.Count);
    }
}
