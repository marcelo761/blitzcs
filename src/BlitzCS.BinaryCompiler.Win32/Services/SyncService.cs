using System.Diagnostics;

namespace BlitzCS.BinaryCompiler.Win32;

internal sealed class CriticalSection { public readonly object Gate = new(); public int Owner; public int Depth; }

internal sealed class KernelEvent(bool manualReset, bool signaled)
{
    private readonly object gate = new();
    private bool signaled = signaled;
    public void Set() { lock (gate) { signaled = true; Monitor.PulseAll(gate); } }
    public void Reset() { lock (gate) signaled = false; }

    public bool Wait(uint milliseconds)
    {
        var clock = Stopwatch.StartNew();
        lock (gate)
        {
            while (!signaled)
            {
                if (milliseconds == 0) return false;
                if (milliseconds == uint.MaxValue) { Monitor.Wait(gate); continue; }
                var left = milliseconds - clock.ElapsedMilliseconds;
                if (left <= 0 || !Monitor.Wait(gate, (int)left)) { if (!signaled) return false; }
            }
            if (!manualReset) signaled = false;
            return true;
        }
    }
}

internal sealed class KernelMutex
{
    private readonly object gate = new();
    private int owner, count;

    public bool Acquire(uint milliseconds)
    {
        var me = Environment.CurrentManagedThreadId;
        var clock = Stopwatch.StartNew();
        lock (gate)
        {
            while (count != 0 && owner != me)
            {
                if (milliseconds == 0) return false;
                if (milliseconds == uint.MaxValue) { Monitor.Wait(gate); continue; }
                var left = milliseconds - clock.ElapsedMilliseconds;
                if (left <= 0 || !Monitor.Wait(gate, (int)left)) { if (count != 0 && owner != me) return false; }
            }
            owner = me; count++;
            return true;
        }
    }

    public bool Release()
    {
        lock (gate)
        {
            if (count == 0 || owner != Environment.CurrentManagedThreadId) return false;
            if (--count == 0) Monitor.PulseAll(gate);
            return true;
        }
    }
}

internal sealed class SyncState
{
    public readonly object AtomicGate = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, CriticalSection> CriticalSections = new();
    public readonly Dictionary<string, object> Named = [];
}

/// <summary>
/// KERNEL32 synchronisation (Microsoft Learn, "Synchronization Functions").
/// Critical sections: the CRITICAL_SECTION structure stays in guest memory (24 bytes; LockCount/RecursionCount/OwningThread are mirrored for
/// guests that read them) while the lock is a host monitor keyed by the structure's address - recursive, owner-checked. Usage errors Windows
/// answers with a crash or corruption (Enter/Leave of an uninitialised section, leaving a section the thread does not own, deleting a held
/// section) raise <see cref="Win32UsageException"/>. Interlocked operations are atomic with respect to each other only (one gate), NOT to guest
/// code using LOCK-prefixed instructions on the same word; that needs a runtime atomic-memory hook. Events and mutexes are in-process, named
/// objects share one object (new handle, ERROR_ALREADY_EXISTS); abandoned-mutex semantics are not modelled. Semaphores, timers and waitable
/// threads are absent.
/// </summary>
public sealed class SyncService : Win32Service
{
    public const uint WaitObject0 = 0, WaitTimeout = 0x102, WaitFailed = 0xFFFFFFFF;
    public override string Name => "Kernel32.Sync";

    private static CriticalSection Get(Win32Call c, uint address, string api) =>
        c.Ctx.Sync.CriticalSections.TryGetValue(address, out var cs) ? cs
            : throw new Win32UsageException($"{api} on 0x{address:X8}, which is not an initialised CRITICAL_SECTION.");

    private static void Mirror(Win32Call c, uint address, CriticalSection cs)
    {
        c.Mem.WriteUInt32(address + 4, (uint)(cs.Depth - 1));
        c.Mem.WriteUInt32(address + 8, (uint)cs.Depth);
        c.Mem.WriteUInt32(address + 12, cs.Depth == 0 ? 0 : c.Ctx.CurrentThreadId);
    }

    private static uint Init(Win32Call c, uint address)
    {
        if (address == 0) throw new Win32UsageException("InitializeCriticalSection(NULL).");
        c.Mem.Write(address, new byte[24]);
        c.Mem.WriteUInt32(address + 4, 0xFFFFFFFF);
        if (c.Ctx.Sync.CriticalSections.TryGetValue(address, out var existing) && existing.Depth != 0)
            throw new Win32UsageException($"Re-initialising the held CRITICAL_SECTION 0x{address:X8}.");
        c.Ctx.Sync.CriticalSections[address] = new CriticalSection();
        return 0;
    }

    private static uint CreateNamed<T>(Win32Call c, uint nameArg, Func<T> create) where T : class
    {
        var name = nameArg == 0 ? null : c.Mem.ReadAnsiString(nameArg, 260);
        if (name is null) return c.Ctx.NewHandle(create());
        lock (c.Ctx.Sync.Named)
        {
            if (c.Ctx.Sync.Named.TryGetValue(name, out var existing))
            {
                if (existing is not T found) return c.Ctx.Fail(Win32Error.InvalidHandle);
                var h = c.Ctx.NewHandle(found);
                c.Ctx.LastError = Win32Error.AlreadyExists;
                return h;
            }
            var created = create();
            c.Ctx.Sync.Named[name] = created;
            c.Ctx.LastError = Win32Error.Success;
            return c.Ctx.NewHandle(created);
        }
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "InitializeCriticalSection", 1, c => Init(c, c[0]));
        Std(K, "InitializeCriticalSectionAndSpinCount", 2, c => { Init(c, c[0]); return 1; });
        Std(K, "DeleteCriticalSection", 1, c =>
        {
            var cs = Get(c, c[0], "DeleteCriticalSection");
            // Windows defines deleting a section that ANOTHER thread owns or waits for as undefined (refused here, before blocking on its gate); deleting one the calling thread
            // itself still owns is harmless there and the original FMOD does exactly that while closing DirectSound, so the ownership the emulation recorded is simply dropped.
            if (Volatile.Read(ref cs.Depth) != 0 && Volatile.Read(ref cs.Owner) != Environment.CurrentManagedThreadId)
                throw new Win32UsageException($"DeleteCriticalSection on the CRITICAL_SECTION 0x{c[0]:X8}, which another thread owns.");
            lock (cs.Gate)
            {
                if (cs.Depth != 0)
                {
                    for (; cs.Depth > 0; cs.Depth--) Monitor.Exit(cs.Gate);
                    cs.Owner = 0;
                }
                c.Ctx.Sync.CriticalSections.TryRemove(c[0], out _);
            }
            return 0;
        });
        Std(K, "EnterCriticalSection", 1, c =>
        {
            var cs = Get(c, c[0], "EnterCriticalSection");
            Monitor.Enter(cs.Gate);
            cs.Owner = Environment.CurrentManagedThreadId; cs.Depth++;
            Mirror(c, c[0], cs);
            return 0;
        });
        Std(K, "TryEnterCriticalSection", 1, c =>
        {
            var cs = Get(c, c[0], "TryEnterCriticalSection");
            if (!Monitor.TryEnter(cs.Gate)) return 0;
            cs.Owner = Environment.CurrentManagedThreadId; cs.Depth++;
            Mirror(c, c[0], cs);
            return 1;
        });
        Std(K, "LeaveCriticalSection", 1, c =>
        {
            var cs = Get(c, c[0], "LeaveCriticalSection");
            if (!Monitor.IsEntered(cs.Gate)) throw new Win32UsageException($"LeaveCriticalSection on 0x{c[0]:X8}, which the calling thread does not own.");
            cs.Depth--;
            Mirror(c, c[0], cs);
            Monitor.Exit(cs.Gate);
            return 0;
        });

        Std(K, "InterlockedIncrement", 1, c => { lock (c.Ctx.Sync.AtomicGate) { var v = c.Mem.ReadUInt32(c[0]) + 1; c.Mem.WriteUInt32(c[0], v); return v; } });
        Std(K, "InterlockedDecrement", 1, c => { lock (c.Ctx.Sync.AtomicGate) { var v = c.Mem.ReadUInt32(c[0]) - 1; c.Mem.WriteUInt32(c[0], v); return v; } });
        Std(K, "InterlockedExchange", 2, c => { lock (c.Ctx.Sync.AtomicGate) { var old = c.Mem.ReadUInt32(c[0]); c.Mem.WriteUInt32(c[0], c[1]); return old; } });
        Std(K, "InterlockedExchangeAdd", 2, c => { lock (c.Ctx.Sync.AtomicGate) { var old = c.Mem.ReadUInt32(c[0]); c.Mem.WriteUInt32(c[0], old + c[1]); return old; } });
        Std(K, "InterlockedCompareExchange", 3, c =>
        {
            lock (c.Ctx.Sync.AtomicGate)
            {
                var old = c.Mem.ReadUInt32(c[0]);
                if (old == c[2]) c.Mem.WriteUInt32(c[0], c[1]);
                return old;
            }
        });

        Std(K, "CreateEventA", 4, c =>
        {
            bool manual = c[1] != 0, initial = c[2] != 0;
            return CreateNamed(c, c[3], () => new KernelEvent(manual, initial));
        });
        Std(K, "SetEvent", 1, c => c.Ctx.GetHandle<KernelEvent>(c[0]) is { } e ? Do(e.Set) : c.Ctx.Fail(Win32Error.InvalidHandle));
        Std(K, "ResetEvent", 1, c => c.Ctx.GetHandle<KernelEvent>(c[0]) is { } e ? Do(e.Reset) : c.Ctx.Fail(Win32Error.InvalidHandle));
        Std(K, "CreateMutexA", 3, c =>
        {
            var h = CreateNamed(c, c[2], () => new KernelMutex());
            if (h != 0 && c[1] != 0) c.Ctx.GetHandle<KernelMutex>(h)!.Acquire(0);
            return h;
        });
        Std(K, "ReleaseMutex", 1, c => c.Ctx.GetHandle<KernelMutex>(c[0]) is not { } m ? c.Ctx.Fail(Win32Error.InvalidHandle) : m.Release() ? 1u : c.Ctx.Fail(Win32Error.NotOwner));
        Std(K, "WaitForSingleObject", 2, c =>
        {
            var target = c.Ctx.GetHandle<object>(c[0]);
            return target switch
            {
                KernelEvent e => e.Wait(c[1]) ? WaitObject0 : WaitTimeout,
                KernelMutex m => m.Acquire(c[1]) ? WaitObject0 : WaitTimeout,
                KernelThread t => t.Done.Wait(c[1]) ? WaitObject0 : WaitTimeout,
                _ => c.Ctx.Fail(Win32Error.InvalidHandle, WaitFailed),
            };
        });
    }

    private static uint Do(Action a) { a(); return 1; }
}
