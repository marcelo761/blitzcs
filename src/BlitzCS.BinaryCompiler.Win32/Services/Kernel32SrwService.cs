using System.Diagnostics;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>State of one slim reader/writer lock. The lock word in guest memory is mirrored (non-zero while held) for guests that peek at it.</summary>
internal sealed class SrwLockState { public int Readers; public int Writer; }

/// <summary>One thread parked in SleepConditionVariable*.</summary>
internal sealed class CvWaiter { public bool Woken; }

/// <summary>Host-side state of every SRWLOCK and CONDITION_VARIABLE, keyed by the address of the guest's pointer-sized word (both are statically initialisable to zero, so entries appear lazily).</summary>
internal sealed class SrwState
{
    public readonly object Gate = new();
    public readonly Dictionary<uint, SrwLockState> Locks = [];
    public readonly Dictionary<uint, List<CvWaiter>> Conditions = [];
    public bool Aborted;
    internal void Abort() { lock (Gate) { Aborted = true; Monitor.PulseAll(Gate); } }
}

/// <summary>
/// KERNEL32 slim reader/writer locks and condition variables (Microsoft Learn, "Slim Reader/Writer (SRW) Locks" and "Condition Variables").
/// The guest's SRWLOCK/CONDITION_VARIABLE word stays in guest memory (zero = free / no waiters, SRWLOCK_INIT and CONDITION_VARIABLE_INIT); the real state is a host table keyed by its address.
/// <list type="bullet">
/// <item>Acquire* block; exclusive excludes everything, shared admits any number of readers while no writer holds the lock. Not fair (like Windows, no ordering is promised).</item>
/// <item>TryAcquire* return TRUE or FALSE without blocking. Release of a lock that is not held in that mode, and an exclusive re-acquire by the thread that already holds it (a certain deadlock on
/// Windows), raise <see cref="Win32UsageException"/> (Windows raises a fatal exception) rather than hanging the host.</item>
/// <item>SleepConditionVariableCS/SRW atomically release the lock (a critical section must be held exactly once; SRW in the mode given by CONDITION_VARIABLE_LOCKMODE_SHARED), wait for a wake or the
/// timeout, and always re-acquire the lock before returning. A timeout returns FALSE with ERROR_TIMEOUT; Wake/WakeAll wake the oldest waiter or every waiter, and are lost when nobody waits.</item>
/// <item>Spurious wake-ups never happen here; callers must still loop on their predicate as documented.</item>
/// </list>
/// </summary>
public sealed class Kernel32SrwService : Win32Service
{
    public override string Name => "Kernel32.Srw";
    private const uint LockModeShared = 1, Infinite = 0xFFFFFFFF;

    private static void Mirror(Win32Call c, uint address, SrwLockState s) => c.Mem.WriteUInt32(address, s.Writer != 0 ? 1u : s.Readers != 0 ? (uint)s.Readers << 4 | 1u : 0u);

    private static SrwLockState Lock(Win32Call c, uint address)
    {
        if (address == 0) throw new Win32UsageException("SRW lock operation on NULL.");
        var table = c.Ctx.Sync.Srw.Locks;
        if (!table.TryGetValue(address, out var s)) table[address] = s = new SrwLockState();
        return s;
    }

    private static void Drop(Win32Call c, uint address, SrwLockState s)
    {
        if (s.Readers == 0 && s.Writer == 0) c.Ctx.Sync.Srw.Locks.Remove(address);
    }

    private static void Acquire(Win32Call c, uint address, bool shared)
    {
        var srw = c.Ctx.Sync.Srw;
        var me = Environment.CurrentManagedThreadId;
        lock (srw.Gate)
        {
            var s = Lock(c, address);
            if (!shared && s.Writer == me)
                throw new Win32UsageException($"AcquireSRWLockExclusive on 0x{address:X8}, which the calling thread already holds exclusively (certain deadlock).");
            while (shared ? s.Writer != 0 : s.Writer != 0 || s.Readers != 0)
            {
                if (srw.Aborted && c.Ctx.IsExitingOnAnotherThread) c.Ctx.ThrowIfPoisoned();
                Monitor.Wait(srw.Gate);
                s = Lock(c, address);
            }
            if (shared) s.Readers++; else s.Writer = me;
            Mirror(c, address, s);
        }
    }

    private static bool TryAcquire(Win32Call c, uint address, bool shared)
    {
        var srw = c.Ctx.Sync.Srw;
        lock (srw.Gate)
        {
            var s = Lock(c, address);
            if (shared ? s.Writer != 0 : s.Writer != 0 || s.Readers != 0) { Drop(c, address, s); return false; }
            if (shared) s.Readers++; else s.Writer = Environment.CurrentManagedThreadId;
            Mirror(c, address, s);
            return true;
        }
    }

    private static void Release(Win32Call c, uint address, bool shared)
    {
        var srw = c.Ctx.Sync.Srw;
        lock (srw.Gate)
        {
            var s = Lock(c, address);
            if (shared ? s.Readers == 0 : s.Writer == 0)
            {
                Drop(c, address, s);
                throw new Win32UsageException($"ReleaseSRWLock{(shared ? "Shared" : "Exclusive")} on 0x{address:X8}, which is not held in that mode.");
            }
            if (shared) s.Readers--; else s.Writer = 0;
            Mirror(c, address, s);
            Drop(c, address, s);
            Monitor.PulseAll(srw.Gate);
        }
    }

    /// <summary>Parks the thread on the condition variable after <paramref name="release"/> ran (registration precedes the release, so a wake in between is not lost), then re-acquires.</summary>
    private static uint Sleep(Win32Call c, uint cv, uint milliseconds, uint lockAddr, bool shared, CriticalSection? cs)
    {
        if (cv == 0) throw new Win32UsageException("SleepConditionVariable on a NULL condition variable.");
        var srw = c.Ctx.Sync.Srw;
        var waiter = new CvWaiter();
        lock (srw.Gate)
        {
            if (!srw.Conditions.TryGetValue(cv, out var list)) srw.Conditions[cv] = list = [];
            list.Add(waiter);
            c.Mem.WriteUInt32(cv, 1);
        }
        try { if (cs is null) Release(c, lockAddr, shared); else LeaveCs(c, lockAddr, cs); }
        catch
        {
            lock (srw.Gate)
                if (srw.Conditions.TryGetValue(cv, out var pending) && pending.Remove(waiter) && pending.Count == 0) { srw.Conditions.Remove(cv); c.Mem.WriteUInt32(cv, 0); }
            throw;
        }
        var woken = false;
        var clock = Stopwatch.StartNew();
        lock (srw.Gate)
        {
            while (!waiter.Woken)
            {
                if (srw.Aborted && c.Ctx.IsExitingOnAnotherThread) c.Ctx.ThrowIfPoisoned();
                if (milliseconds == Infinite)
                {
                    using (c.Ctx.SuspendGuestMemoryWriterForHostWait())
                        Monitor.Wait(srw.Gate);
                    continue;
                }
                var left = milliseconds - clock.ElapsedMilliseconds;
                if (left <= 0) break;
                bool timedOut;
                using (c.Ctx.SuspendGuestMemoryWriterForHostWait())
                    timedOut = !Monitor.Wait(srw.Gate, (int)Math.Min(left, int.MaxValue));
                if (timedOut)
                {
                    if (srw.Aborted && c.Ctx.IsExitingOnAnotherThread) c.Ctx.ThrowIfPoisoned();
                    if (!waiter.Woken) break;
                }
            }
            woken = waiter.Woken;
            if (!woken && srw.Conditions.TryGetValue(cv, out var list))
            {
                list.Remove(waiter);
                if (list.Count == 0) { srw.Conditions.Remove(cv); c.Mem.WriteUInt32(cv, 0); }
            }
        }
        if (cs is null) Acquire(c, lockAddr, shared); else EnterCs(c, lockAddr, cs);
        return woken ? 1u : c.Ctx.Fail(Win32Error.Timeout);
    }

    private static void LeaveCs(Win32Call c, uint address, CriticalSection cs)
    {
        cs.Depth--;
        c.Mem.WriteUInt32(address + 4, 0xFFFFFFFF); c.Mem.WriteUInt32(address + 8, 0); c.Mem.WriteUInt32(address + 12, 0);
        Monitor.Exit(cs.Gate);
    }

    private static void EnterCs(Win32Call c, uint address, CriticalSection cs)
    {
        Monitor.Enter(cs.Gate);
        cs.Owner = Environment.CurrentManagedThreadId; cs.Depth++;
        c.Mem.WriteUInt32(address + 4, 0); c.Mem.WriteUInt32(address + 8, 1); c.Mem.WriteUInt32(address + 12, c.Ctx.CurrentThreadId);
    }

    private static void Wake(Win32Call c, uint cv, bool all)
    {
        if (cv == 0) throw new Win32UsageException("WakeConditionVariable on a NULL condition variable.");
        var srw = c.Ctx.Sync.Srw;
        lock (srw.Gate)
        {
            if (!srw.Conditions.TryGetValue(cv, out var list)) return;
            var n = all ? list.Count : 1;
            for (var i = 0; i < n; i++) list[i].Woken = true;
            list.RemoveRange(0, n);
            if (list.Count == 0) { srw.Conditions.Remove(cv); c.Mem.WriteUInt32(cv, 0); }
            Monitor.PulseAll(srw.Gate);
        }
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "InitializeSRWLock", 1, c =>
        {
            lock (c.Ctx.Sync.Srw.Gate)
            {
                if (c.Ctx.Sync.Srw.Locks.TryGetValue(c[0], out var held) && (held.Readers != 0 || held.Writer != 0))
                    throw new Win32UsageException($"Re-initialising the held SRWLOCK 0x{c[0]:X8}.");
                c.Ctx.Sync.Srw.Locks.Remove(c[0]);
                c.Mem.WriteUInt32(c[0], 0);
            }
            return 0;
        });
        Std(K, "AcquireSRWLockExclusive", 1, c => { Acquire(c, c[0], false); return 0; });
        Std(K, "AcquireSRWLockShared", 1, c => { Acquire(c, c[0], true); return 0; });
        Std(K, "ReleaseSRWLockExclusive", 1, c => { Release(c, c[0], false); return 0; });
        Std(K, "ReleaseSRWLockShared", 1, c => { Release(c, c[0], true); return 0; });
        Std(K, "TryAcquireSRWLockExclusive", 1, c => TryAcquire(c, c[0], false) ? 1u : 0u);
        Std(K, "TryAcquireSRWLockShared", 1, c => TryAcquire(c, c[0], true) ? 1u : 0u);

        Std(K, "InitializeConditionVariable", 1, c =>
        {
            lock (c.Ctx.Sync.Srw.Gate)
            {
                if (c.Ctx.Sync.Srw.Conditions.ContainsKey(c[0])) throw new Win32UsageException($"Re-initialising the CONDITION_VARIABLE 0x{c[0]:X8}, which has waiting threads.");
                c.Mem.WriteUInt32(c[0], 0);
            }
            return 0;
        });
        Std(K, "WakeConditionVariable", 1, c => { Wake(c, c[0], false); return 0; });
        Std(K, "WakeAllConditionVariable", 1, c => { Wake(c, c[0], true); return 0; });
        Std(K, "SleepConditionVariableSRW", 4, c =>
        {
            if ((c[3] & ~LockModeShared) != 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            var shared = (c[3] & LockModeShared) != 0;
            return Sleep(c, c[0], c[2], c[1], shared, null);
        });
        Std(K, "SleepConditionVariableCS", 3, c =>
        {
            var csAddr = c[1];
            if (!c.Ctx.Sync.CriticalSections.TryGetValue(csAddr, out var cs))
                throw new Win32UsageException($"SleepConditionVariableCS on 0x{csAddr:X8}, which is not an initialised CRITICAL_SECTION.");
            if (!Monitor.IsEntered(cs.Gate) || cs.Depth != 1)
                throw new Win32UsageException($"SleepConditionVariableCS needs the CRITICAL_SECTION 0x{csAddr:X8} held exactly once by the calling thread.");
            return Sleep(c, c[0], c[2], csAddr, false, cs);
        });
    }
}
