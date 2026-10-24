using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class CriticalSectionTests
{
    private static uint NewCs(World w) { var p = w.Buf(64); w.Kernel("InitializeCriticalSection", p); return p; }

    [Fact]
    public void InitMirrorsLockFieldsAndEnterLeaveRecurse()
    {
        using var w = new World();
        var cs = NewCs(w);
        Assert.Equal(0xFFFFFFFFu, w.Mem.ReadUInt32(cs + 4));
        w.Kernel("EnterCriticalSection", cs);
        w.Kernel("EnterCriticalSection", cs);
        Assert.Equal(2u, w.Mem.ReadUInt32(cs + 8));
        Assert.Equal(w.Kernel("GetCurrentThreadId"), w.Mem.ReadUInt32(cs + 12));
        w.Kernel("LeaveCriticalSection", cs);
        Assert.Equal(1u, w.Mem.ReadUInt32(cs + 8));
        w.Kernel("LeaveCriticalSection", cs);
        Assert.Equal(0u, w.Mem.ReadUInt32(cs + 12));
        w.Kernel("DeleteCriticalSection", cs);
    }

    [Fact]
    public void UsageErrorsRaiseInsteadOfCorrupting()
    {
        using var w = new World();
        var raw = w.Buf(64);
        Assert.Throws<Win32UsageException>(() => w.Kernel("EnterCriticalSection", raw));
        Assert.Throws<Win32UsageException>(() => w.Kernel("InitializeCriticalSection", 0));
        var cs = NewCs(w);
        Assert.Throws<Win32UsageException>(() => w.Kernel("LeaveCriticalSection", cs)); // not owned
        w.Kernel("EnterCriticalSection", cs);
        Assert.Throws<Win32UsageException>(() => w.Kernel("InitializeCriticalSection", cs));
        w.Kernel("LeaveCriticalSection", cs);
        w.Kernel("DeleteCriticalSection", cs);
        Assert.Throws<Win32UsageException>(() => w.Kernel("EnterCriticalSection", cs)); // deleted
    }

    [Fact]
    public void DeletingASectionTheCallingThreadStillOwnsIsToleratedButAnotherThreadsIsRefused()
    {
        using var w = new World();
        var mine = NewCs(w);
        w.Kernel("EnterCriticalSection", mine);
        w.Kernel("EnterCriticalSection", mine);
        w.Kernel("DeleteCriticalSection", mine);                                          // the original FMOD does this while closing DirectSound; Windows tolerates it
        Assert.Throws<Win32UsageException>(() => w.Kernel("EnterCriticalSection", mine)); // deleted

        var theirs = NewCs(w);
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var holder = new Thread(() => { w.Kernel("EnterCriticalSection", theirs); entered.Set(); release.Wait(); w.Kernel("LeaveCriticalSection", theirs); });
        holder.Start();
        entered.Wait();
        var error = Assert.Throws<Win32UsageException>(() => w.Kernel("DeleteCriticalSection", theirs));
        Assert.Contains("another thread", error.Message);
        release.Set(); holder.Join();
        w.Kernel("DeleteCriticalSection", theirs);
    }

    [Fact]
    public void TryEnterFailsWhileAnotherThreadHoldsTheSection()
    {
        using var w = new World();
        var cs = NewCs(w);
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var t = new Thread(() => { w.Kernel("EnterCriticalSection", cs); held.Set(); release.Wait(); w.Kernel("LeaveCriticalSection", cs); });
        t.Start(); held.Wait();
        Assert.Equal(0u, w.Kernel("TryEnterCriticalSection", cs));
        Assert.Throws<Win32UsageException>(() => w.Kernel("LeaveCriticalSection", cs)); // a thread cannot leave another's section
        release.Set(); t.Join();
        Assert.Equal(1u, w.Kernel("TryEnterCriticalSection", cs));
        w.Kernel("LeaveCriticalSection", cs);
        Assert.Equal(1u, w.Kernel("InitializeCriticalSectionAndSpinCount", w.Buf(64), 4000));
    }

    [Fact]
    public void ProvidesMutualExclusion()
    {
        using var w = new World();
        var cs = NewCs(w);
        var counter = w.Buf(4);
        Parallel.For(0, 6, _ =>
        {
            for (var i = 0; i < 3000; i++)
            {
                w.Kernel("EnterCriticalSection", cs);
                var v = w.Mem.ReadUInt32(counter); // deliberately non-atomic read-modify-write
                if (i % 97 == 0) Thread.Yield();
                w.Mem.WriteUInt32(counter, v + 1);
                w.Kernel("LeaveCriticalSection", cs);
            }
        });
        Assert.Equal(18000u, w.Mem.ReadUInt32(counter));
    }
}

public sealed class InterlockedAndKernelObjectTests
{
    [Fact]
    public void InterlockedSemantics()
    {
        using var w = new World();
        var p = w.Buf(4);
        w.Mem.WriteUInt32(p, 5);
        Assert.Equal(6u, w.Kernel("InterlockedIncrement", p));
        Assert.Equal(5u, w.Kernel("InterlockedDecrement", p));
        Assert.Equal(5u, w.Kernel("InterlockedExchange", p, 9));
        Assert.Equal(9u, w.Kernel("InterlockedExchangeAdd", p, 3));
        Assert.Equal(12u, w.Mem.ReadUInt32(p));
        Assert.Equal(12u, w.Kernel("InterlockedCompareExchange", p, 100, 12)); // returns the initial value
        Assert.Equal(100u, w.Mem.ReadUInt32(p));
        Assert.Equal(100u, w.Kernel("InterlockedCompareExchange", p, 7, 1));    // no match: unchanged
        Assert.Equal(100u, w.Mem.ReadUInt32(p));
        w.Mem.WriteUInt32(p, 0);
        Assert.Equal(0xFFFFFFFFu, w.Kernel("InterlockedDecrement", p)); // wraps
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestMemoryException>(() => w.Kernel("InterlockedIncrement", 0));
    }

    [Fact]
    public void InterlockedIncrementIsAtomicAcrossThreads()
    {
        using var w = new World();
        var p = w.Buf(4);
        Parallel.For(0, 8, _ => { for (var i = 0; i < 5000; i++) w.Kernel("InterlockedIncrement", p); });
        Assert.Equal(40000u, w.Mem.ReadUInt32(p));
    }

    [Fact]
    public void EventsAutoAndManualReset()
    {
        using var w = new World();
        var auto = w.Kernel("CreateEventA", 0, 0, 1, 0);
        Assert.Equal(SyncService.WaitObject0, w.Kernel("WaitForSingleObject", auto, 0));
        Assert.Equal(SyncService.WaitTimeout, w.Kernel("WaitForSingleObject", auto, 0)); // auto-reset consumed it
        var manual = w.Kernel("CreateEventA", 0, 1, 0, 0);
        Assert.Equal(SyncService.WaitTimeout, w.Kernel("WaitForSingleObject", manual, 10));
        Assert.Equal(1u, w.Kernel("SetEvent", manual));
        Assert.Equal(SyncService.WaitObject0, w.Kernel("WaitForSingleObject", manual, 0));
        Assert.Equal(SyncService.WaitObject0, w.Kernel("WaitForSingleObject", manual, 0));
        Assert.Equal(1u, w.Kernel("ResetEvent", manual));
        Assert.Equal(SyncService.WaitTimeout, w.Kernel("WaitForSingleObject", manual, 0));
    }

    [Fact]
    public void WaitIsWokenByAnotherThread()
    {
        using var w = new World();
        var ev = w.Kernel("CreateEventA", 0, 1, 0, 0);
        var t = new Thread(() => { Thread.Sleep(50); w.Kernel("SetEvent", ev); });
        t.Start();
        Assert.Equal(SyncService.WaitObject0, w.Kernel("WaitForSingleObject", ev, 0xFFFFFFFF));
        t.Join();
    }

    [Fact]
    public void NamedObjectsShareStateAndMismatchedTypesFail()
    {
        using var w = new World();
        var name = w.Str("blitz-test");
        var e1 = w.Kernel("CreateEventA", 0, 1, 0, name);
        Assert.Equal(Win32Error.Success, w.LastError);
        var e2 = w.Kernel("CreateEventA", 0, 1, 0, name);
        Assert.Equal(Win32Error.AlreadyExists, w.LastError);
        Assert.NotEqual(e1, e2);
        w.Kernel("SetEvent", e1);
        Assert.Equal(SyncService.WaitObject0, w.Kernel("WaitForSingleObject", e2, 0));
        Assert.Equal(0u, w.Kernel("CreateMutexA", 0, 0, name));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void MutexOwnershipAndRecursion()
    {
        using var w = new World();
        var m = w.Kernel("CreateMutexA", 0, 1, 0);
        Assert.Equal(SyncService.WaitObject0, w.Kernel("WaitForSingleObject", m, 0)); // recursive acquire
        uint other = 99;
        var t = new Thread(() => other = w.Kernel("WaitForSingleObject", m, 20));
        t.Start(); t.Join();
        Assert.Equal(SyncService.WaitTimeout, other);
        Assert.Equal(1u, w.Kernel("ReleaseMutex", m));
        Assert.Equal(1u, w.Kernel("ReleaseMutex", m));
        Assert.Equal(0u, w.Kernel("ReleaseMutex", m));
        Assert.Equal(Win32Error.NotOwner, w.LastError);
    }

    [Fact]
    public void InvalidHandlesAndDoubleClose()
    {
        using var w = new World();
        Assert.Equal(0xFFFFFFFFu, w.Kernel("WaitForSingleObject", 0x4444, 0));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(0u, w.Kernel("SetEvent", 0x4444));
        var e = w.Kernel("CreateEventA", 0, 0, 0, 0);
        Assert.Equal(1u, w.Kernel("CloseHandle", e));
        Assert.Equal(0u, w.Kernel("CloseHandle", e));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(0u, w.Kernel("CloseHandle", 0xFFFFFFFF));
    }
}

public sealed class TlsTests
{
    [Fact]
    public void SlotsStartAtZeroHoldPerThreadValuesAndClearLastError()
    {
        using var w = new World();
        var i = w.Kernel("TlsAlloc");
        Assert.True(i < 64);
        w.Kernel("SetLastError", 77);
        Assert.Equal(0u, w.Kernel("TlsGetValue", i));
        Assert.Equal(0u, w.LastError); // success is signalled through ERROR_SUCCESS
        Assert.Equal(1u, w.Kernel("TlsSetValue", i, 0xCAFE));
        Assert.Equal(0xCAFEu, w.Kernel("TlsGetValue", i));
        uint seen = 1;
        var t = new Thread(() => { seen = w.Kernel("TlsGetValue", i); w.Kernel("TlsSetValue", i, 5); });
        t.Start(); t.Join();
        Assert.Equal(0u, seen);
        Assert.Equal(0xCAFEu, w.Kernel("TlsGetValue", i));
    }

    [Fact]
    public void InvalidIndexesFailWithInvalidParameter()
    {
        using var w = new World();
        Assert.Equal(0u, w.Kernel("TlsGetValue", 63)); // never allocated
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("TlsSetValue", 64, 1));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("TlsFree", 5));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("TlsFree", 0xFFFFFFFF));
    }

    [Fact]
    public void FreeInvalidatesValuesEverywhereAndSlotsAreReusedAsZero()
    {
        using var w = new World();
        var i = w.Kernel("TlsAlloc");
        w.Kernel("TlsSetValue", i, 123);
        Assert.Equal(1u, w.Kernel("TlsFree", i));
        Assert.Equal(0u, w.Kernel("TlsGetValue", i));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("TlsFree", i)); // double free
        Assert.Equal(i, w.Kernel("TlsAlloc"));    // lowest free index
        Assert.Equal(0u, w.Kernel("TlsGetValue", i)); // stale value is gone
    }

    [Fact]
    public void ExhaustionReturnsOutOfIndexes()
    {
        using var w = new World();
        var all = Enumerable.Range(0, 64).Select(_ => w.Kernel("TlsAlloc")).ToList();
        Assert.Equal(64, all.Distinct().Count());
        Assert.Equal(0xFFFFFFFFu, w.Kernel("TlsAlloc"));
        Assert.Equal(Win32Error.NoMoreItems, w.LastError);
    }
}
