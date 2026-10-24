using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>A guest thread: a host thread running a guest start routine. The handle is waitable (signalled when the routine returns).</summary>
internal sealed class KernelThread : IDisposable
{
    public readonly KernelEvent Done = new(true, false);
    public readonly ManualResetEventSlim Resume = new(true);
    public int Priority;
    public int HostThreadId;
    public volatile uint ExitCode = 259; // STILL_ACTIVE
    public volatile bool Finished;
    public void Dispose() { } // closing a handle never kills the thread
}

/// <summary>
/// KERNEL32 additions needed by DllMain-time code. CreateThread runs the guest start routine (stdcall, one argument) on a NEW HOST THREAD of the calling guest module through
/// <c>GuestModule.CallAddress</c>, so the thread gets its own TEB and guest stack; the requested stack size is ignored (the module's stack size applies), security attributes are
/// ignored, CREATE_SUSPENDED is honoured (ResumeThread returns the previous suspend count; SuspendThread is not provided). The handle is waitable by WaitForSingleObject; the routine's EAX
/// is the exit code. A fault inside the thread ends it with exit code 0xFFFFFFFF and is recorded in <see cref="ThreadFaults"/> (never silently dropped). Disposing the module while
/// guest threads run is the host's responsibility. SetThreadPriority/GetThreadPriority validate the documented values (-15, -2..2, 15) and record the value: Windows priorities are
/// scheduling hints, the host scheduler is not changed. DisableThreadLibraryCalls accepts the running module (or a registered module) and records it; this runtime never delivers
/// DLL_THREAD_ATTACH/DETACH to DllMain, so the flag has no further effect. GetSystemDirectoryA/GetWindowsDirectoryA report the virtual machine's directories ("C:\WINDOWS\system32",
/// "C:\WINDOWS"; they exist only inside the sandbox mapping). GetDriveTypeA: the virtual machine has exactly one drive, C: (DRIVE_FIXED); other letters give DRIVE_NO_ROOT_DIR, a path
/// without a root gives DRIVE_UNKNOWN, so no optical drive is ever reported. OutputDebugStringA forwards to <see cref="Win32Options.DebugOutput"/>; with no debugger attached the text is dropped.
/// </summary>
public sealed class Kernel32ExtraService : Win32Service
{
    public override string Name => "Kernel32.Extra";

    private sealed class Faults { public readonly ConcurrentQueue<Exception> Queue = new(); }
    private static readonly ConditionalWeakTable<Win32Context, Faults> FaultTable = new();
    private static readonly ConcurrentDictionary<int, int> PseudoPriority = new();

    /// <summary>Exceptions that ended guest threads created by CreateThread.</summary>
    public static IReadOnlyCollection<Exception> ThreadFaults(Win32Context ctx) => FaultTable.GetValue(ctx, _ => new Faults()).Queue;

    internal static void RecordFault(Win32Context ctx, Exception e) => FaultTable.GetValue(ctx, _ => new Faults()).Queue.Enqueue(e);

    private static bool ValidPriority(int p) => p is -15 or >= -2 and <= 2 or 15;

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "DisableThreadLibraryCalls", 1, c =>
        {
            var module = GuestModule.Current;
            if ((module is not null && module.ImageBase == c[0]) || c.Ctx.Process.Find(c[0]) is not null || c[0] == c.Ctx.Options.MainModuleBase) return 1;
            return c.Ctx.Fail(Win32Error.InvalidHandle);
        });
        Std(K, "GetSystemDirectoryA", 2, c => WriteDirectory(c, "C:\\WINDOWS\\system32"));
        Std(K, "GetWindowsDirectoryA", 2, c => WriteDirectory(c, "C:\\WINDOWS"));
        Std(K, "GetDriveTypeA", 1, c =>
        {
            if (c[0] == 0) return 1; // NULL means the current directory's drive in Windows; the sandbox working drive is C:
            var root = c.Mem.ReadAnsiString(c[0], 4096);
            if (root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0])) return 0; // DRIVE_UNKNOWN
            return char.ToUpperInvariant(root[0]) == 'C' ? 3u : 1u; // DRIVE_FIXED / DRIVE_NO_ROOT_DIR
        });
        Std(K, "OutputDebugStringA", 1, c =>
        {
            var text = c[0] == 0 ? "" : c.Mem.ReadAnsiString(c[0], 65536);
            c.Ctx.Options.DebugOutput?.Invoke(text);
            return 0;
        });
        Std(K, "CreateThread", 6, CreateThread);
        Std(K, "ResumeThread", 1, c =>
        {
            if (c.Ctx.GetHandle<KernelThread>(c[0]) is not { } t) return c.Ctx.Fail(Win32Error.InvalidHandle, 0xFFFFFFFF);
            var was = t.Resume.IsSet ? 0u : 1u;
            t.Resume.Set();
            return was;
        });
        Std(K, "GetExitCodeThread", 2, c =>
        {
            if (c.Ctx.GetHandle<KernelThread>(c[0]) is not { } t) return c.Ctx.Fail(Win32Error.InvalidHandle);
            c.Mem.WriteUInt32(c[1], t.ExitCode);
            return 1;
        });
        Std(K, "SetThreadPriority", 2, c =>
        {
            var p = (int)c[1];
            if (!ValidPriority(p)) return c.Ctx.Fail(Win32Error.InvalidParameter);
            if (c[0] == 0xFFFFFFFE) { PseudoPriority[Environment.CurrentManagedThreadId] = p; return 1; }
            if (c.Ctx.GetHandle<KernelThread>(c[0]) is not { } t) return c.Ctx.Fail(Win32Error.InvalidHandle);
            t.Priority = p;
            return 1;
        });
        Std(K, "GetThreadPriority", 1, c =>
        {
            if (c[0] == 0xFFFFFFFE) return (uint)PseudoPriority.GetValueOrDefault(Environment.CurrentManagedThreadId, 0);
            return c.Ctx.GetHandle<KernelThread>(c[0]) is { } t ? (uint)t.Priority : c.Ctx.Fail(Win32Error.InvalidHandle, 0x7FFFFFFF);
        });
    }

    private static uint WriteDirectory(Win32Call c, string path)
    {
        if (c[1] <= path.Length) return (uint)path.Length + 1; // too small: required size including the terminator
        c.Mem.WriteCString(c[0], path);
        return (uint)path.Length;
    }

    private static uint CreateThread(Win32Call c)
    {
        var module = GuestModule.Current;
        if (module is null) throw new Win32UsageException("CreateThread needs a running guest module.");
        uint start = c[2], parameter = c[3], flags = c[4];
        if ((flags & ~4u) != 0) return c.Ctx.Fail(Win32Error.InvalidParameter); // STACK_SIZE_PARAM_IS_A_RESERVATION and others are not modelled
        if (start == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var ctx = c.Ctx;
        var state = new KernelThread();
        if ((flags & 4) != 0) state.Resume.Reset();
        var thread = new Thread(() =>
        {
            state.Resume.Wait();
            try { state.ExitCode = module.CallAddress(start, parameter); }
            catch (Exception e)
            {
                state.ExitCode = 0xFFFFFFFF;
                FaultTable.GetValue(ctx, _ => new Faults()).Queue.Enqueue(e);
            }
            finally { state.Finished = true; state.Done.Set(); }
        }) { IsBackground = true, Name = "guest-thread" };
        state.HostThreadId = thread.ManagedThreadId;
        var handle = ctx.NewHandle(state);
        if (c[5] != 0) c.Mem.WriteUInt32(c[5], 1000u + 4u * (uint)thread.ManagedThreadId);
        thread.Start();
        return handle;
    }
}
