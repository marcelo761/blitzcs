using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32.HostBridge;

/// <summary>Guest code that the host re-entered (a window procedure) failed; the bridged import that made the host call fails with it as soon as the host returns.</summary>
public sealed class HostCallbackException(string message, Exception inner) : Exception(message, inner);

/// <summary>A guest window class registered on the host: the host class runs <see cref="WindowProcBridge"/>'s native procedure, which enters the guest procedure.</summary>
internal sealed class GuestWindowClass
{
    public required ushort Atom { get; init; }
    public required string Name { get; init; }
    public required uint GuestProc { get; init; }
    public required uint GuestInstance { get; init; }
    public required GuestModule Module { get; init; }
    public required WindowState Owner { get; init; }
}

/// <summary>A host window whose procedure is guest code (created from a guest class). <see cref="Proc"/> changes with SetWindowLongA(GWL_WNDPROC).</summary>
internal sealed class GuestWindow(nint hwnd, GuestWindowClass cls)
{
    public nint Hwnd { get; } = hwnd;
    public GuestWindowClass Class { get; } = cls;
    public uint Proc = cls.GuestProc;
    public uint Instance = cls.GuestInstance;
}

/// <summary>Window-bridge state of one emulated process (one <see cref="Win32Context"/>): its classes and windows, and the guest scratch heap that message copies live in.</summary>
internal sealed class WindowState : IDisposable
{
    private static readonly ConditionalWeakTable<Win32Context, WindowState> states = new();
    private readonly object gate = new();
    private GuestHeap? scratch;
    private IWin32Memory? memory;

    private WindowState(Win32Context ctx) { Ctx = ctx; }
    public Win32Context Ctx { get; }
    public bool Disposed { get; private set; }

    public static WindowState For(Win32Context ctx) => states.GetValue(ctx, c => { var s = new WindowState(c); c.AddDisposable(s); return s; });

    /// <summary>Binds the memory used for guest copies (the first module that registers a class; in a shared process state allocations come from the process anchor anyway).</summary>
    public void Bind(IWin32Memory mem) { lock (gate) memory ??= mem; }
    public IWin32Memory Memory => memory ?? throw new Win32UsageException("The window bridge has no guest memory yet.");
    public GuestHeap Scratch { get { lock (gate) return scratch ??= new GuestHeap(Memory, 0, 0, 0); } }

    public readonly ConcurrentDictionary<string, GuestWindowClass> ClassesByName = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        Disposed = true;   // later messages to windows of this process go to the host's DefWindowProcA: the guest code is gone
        foreach (var w in WindowProcBridge.WindowsOf(this))
            if (WindowProcBridge.DestroyIfOwnThread(w.Hwnd)) WindowProcBridge.Forget(w.Hwnd);
        foreach (var c in ClassesByName.Values)
            try { HostInvoke.Call(HostExports.Get("user32.dll", "UnregisterClassA"), [(nint)c.Atom, HostExports.HostInstance]); } catch (Win32UsageException) { }
        try { scratch?.Dispose(); } catch (ObjectDisposedException) { } catch (GuestException) { }
    }
}

/// <summary>
/// Calls guest window procedures from host messages. Every guest window class is registered on the host with <see cref="NativeProcedure"/> as its procedure; that native entry point finds the
/// guest procedure of the window (by HWND, or by class atom for the first messages of CreateWindowEx), converts the message (<see cref="MessageThunks"/>), enters the guest with
/// <see cref="GuestModule.CallAddress"/> on the calling thread (the nested-call path: same TEB, the guest stack continues below the import that is running, exactly as the stack discipline
/// of <c>GuestModule.Seh.cs</c> expects) and converts the result back.
///
/// A managed exception must never unwind through user32's native frames, so a fault inside the guest procedure is recorded per thread, the host gets a neutral answer, further messages on that
/// thread go to the host's DefWindowProcA, and the bridged import that made the host call (<see cref="HostFunction.CallNative"/>) rethrows it as soon as the host returns.
/// </summary>
public static unsafe class WindowProcBridge
{
    private static readonly ConcurrentDictionary<ushort, GuestWindowClass> classes = new();
    private static readonly ConcurrentDictionary<nint, GuestWindow> windows = new();

    [ThreadStatic] private static Exception? pendingFault;
    [ThreadStatic] private static List<InFlight>? inFlight;
    [ThreadStatic] private static uint creatingInstance;

    /// <summary>A message the guest procedure is handling now: the guest copy, the host original and the thunk that relates them.</summary>
    private sealed record InFlight(nint Hwnd, uint Msg, uint GuestW, uint GuestL, nint HostW, nint HostL, MessageThunk Thunk, MessageFrame Frame);

    /// <summary>The native window procedure every bridged class uses.</summary>
    public static nint NativeProcedure => (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc;

    internal static void RegisterClass(GuestWindowClass c) { classes[c.Atom] = c; c.Owner.ClassesByName[c.Name] = c; }
    internal static void UnregisterClass(GuestWindowClass c) { classes.TryRemove(c.Atom, out _); c.Owner.ClassesByName.TryRemove(c.Name, out _); }
    internal static GuestWindowClass? ClassOf(ushort atom) => classes.GetValueOrDefault(atom);
    internal static GuestWindow? Window(nint hwnd) => windows.GetValueOrDefault(hwnd);
    internal static IEnumerable<GuestWindow> WindowsOf(WindowState owner) => windows.Values.Where(w => w.Class.Owner == owner).ToList();
    internal static void Forget(nint hwnd) => windows.TryRemove(hwnd, out _);

    /// <summary>The guest HINSTANCE of the CreateWindowExA call running on this thread (what CREATESTRUCT.hInstance means to the guest).</summary>
    internal static uint CreatingInstance { get => creatingInstance; set => creatingInstance = value; }

    internal static bool DestroyIfOwnThread(nint hwnd)
    {
        uint pid;
        var thread = (uint)HostInvoke.Call(HostExports.Get("user32.dll", "GetWindowThreadProcessId"), [hwnd, (nint)(&pid)]);
        var self = (uint)HostInvoke.Call(HostExports.Get("kernel32.dll", "GetCurrentThreadId"), []);
        return thread == self && HostInvoke.Call(HostExports.Get("user32.dll", "DestroyWindow"), [hwnd]) != 0;
    }

    /// <summary>Rethrows (once) a fault that guest code raised while the host was calling a window procedure on this thread.</summary>
    public static void ThrowPendingCallbackFault()
    {
        // Every level rethrows: a nested bridged call (DefWindowProcA inside a guest procedure) fails its own guest call, which makes the enclosing procedure fail in turn, up to the outermost import.
        if (pendingFault is not { } f) return;
        pendingFault = null;
        throw new HostCallbackException($"The guest window procedure failed while the host delivered a message: {f.GetType().Name}: {f.Message}", f);
    }

    internal static GuestWindow? Bind(nint hwnd)
    {
        if (windows.TryGetValue(hwnd, out var w)) return w;
        var atom = (ushort)HostInvoke.Call(HostExports.Get("user32.dll", "GetClassLongPtrA"), [hwnd, -32]);   // GCW_ATOM
        if (!classes.TryGetValue(atom, out var cls)) return null;
        w = new GuestWindow(hwnd, cls);
        if (creatingInstance != 0) w.Instance = creatingInstance;
        return windows.GetOrAdd(hwnd, w);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        try
        {
            var window = Bind(hwnd);
            if (window is null || pendingFault is not null || window.Class.Owner.Disposed || window.Class.Module.IsDisposed)
                return HostDefWindowProc(hwnd, msg, w, l);
            return Deliver(window, msg, w, l);
        }
        catch (Exception e)
        {
            pendingFault ??= e;
            try { return HostDefWindowProc(hwnd, msg, w, l); } catch { return 0; }
        }
        finally
        {
            if (msg == MessageThunks.WM_NCDESTROY) windows.TryRemove(hwnd, out _);
        }
    }

    private static nint HostDefWindowProc(nint hwnd, uint msg, nint w, nint l) => HostInvoke.Call(HostExports.Get("user32.dll", "DefWindowProcA"), [hwnd, (nint)msg, w, l]);

    private static nint Deliver(GuestWindow window, uint msg, nint w, nint l)
    {
        var owner = window.Class.Owner;
        var ctx = owner.Ctx;
        var thunk = MessageThunks.For(msg);
        using var frame = new MessageFrame(owner, owner.Memory) { GuestInstance = window.Instance };
        thunk.ToGuest(frame, msg, w, l, out var gw, out var gl);
        var stack = inFlight ??= [];
        stack.Add(new InFlight(window.Hwnd, msg, gw, gl, w, l, thunk, frame));
        HostLastError.After(ctx);
        uint r;
        try { r = window.Class.Module.CallAddress(window.Proc, HostHandles.ToGuest(window.Hwnd, "HWND"), msg, gw, gl); }
        finally { stack.RemoveAt(stack.Count - 1); }
        HostLastError.Before(ctx);
        thunk.BackToHost(frame, gw, gl, w, l);
        return (int)r;   // LRESULT: sign-extended (WM_NCHITTEST's HTERROR, WM_CREATE's -1, handles)
    }

    /// <summary>
    /// The guest passes a message to a host procedure (DefWindowProcA). When it is the message the guest procedure is handling right now (same HWND, message and parameters), the host
    /// gets its ORIGINAL structure with the guest's changes applied, and the guest copy is refreshed afterwards, so the default processing works on the real data. Otherwise the guest's
    /// parameters are converted like any guest-built message.
    /// </summary>
    internal static uint PassToHost(Win32Context ctx, IWin32Memory mem, nint function, uint hwnd, uint msg, uint gw, uint gl)
    {
        var host = HostHandles.ToHost(hwnd);
        if (inFlight is { Count: > 0 } stack && stack[^1] is var top && top.Hwnd == host && top.Msg == msg && top.GuestW == gw && top.GuestL == gl)
        {
            top.Thunk.BackToHost(top.Frame, gw, gl, top.HostW, top.HostL);
            var r = HostFunction.CallNative(ctx, function, [host, (nint)msg, top.HostW, top.HostL]);
            top.Thunk.BackToGuest(top.Frame, gw, gl, top.HostW, top.HostL);
            return Result(r, msg);
        }
        var thunk = MessageThunks.For(msg);
        var owner = WindowState.For(ctx);
        owner.Bind(mem);
        using var frame = new MessageFrame(owner, mem);
        thunk.ToHost(frame, msg, gw, gl, out var w, out var l);
        var result = HostFunction.CallNative(ctx, function, [host, (nint)msg, w, l]);
        thunk.BackToGuest(frame, gw, gl, w, l);
        return Result(result, msg);
    }

    private static uint Result(nint r, uint msg) => Win32Layouts.Value(r, $"the result of message 0x{msg:X4}");
}
