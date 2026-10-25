using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Whether user32/gdi32 (and the DirectX COM creation functions) reach the host's own DLLs. <see cref="Default"/>: the host bridge on a Windows host (including Wine), absent elsewhere.</summary>
public enum HostWindowingKind { Default, Host, None }

/// <summary>
/// USER32 bridged to the HOST's user32.dll (Windows x64, natively or under Wine; docs/binary-user32-bridge.md). Registered only on a Windows host: on Linux these APIs stay absent and fault by name.
/// Most exports are rows of the adapter table (<see cref="HostFunction"/>: one conversion per argument, structures with identical layouts copied, handles sign-extended). Hand-written adapters
/// cover what crosses with a different layout or carries a guest code address: window classes and procedures (<see cref="WindowProcBridge"/>), MSG, PAINTSTRUCT, window longs.
/// Not bridged (absent, or a named fault for an unsupported argument): menus and resources of guest images (LoadCursorA with a guest HINSTANCE, a class menu name), timer procedures in WM_TIMER,
/// host window procedures handed to the guest (GWL_WNDPROC of a window the guest did not create), message boxes (<see cref="Win32Options.MessageBoxHandler"/> stays the policy).
/// </summary>
public sealed unsafe class HostUser32Service : Win32Service
{
    public override string Name => "User32.Host";
    private const string U = "USER32.dll", H = "user32.dll";
    public const uint ErrorMessageSyncOnly = 1159, ErrorInvalidWindowHandle = 1400;
    private const int GwlWndProc = -4, GwlHInstance = -6, GwlHwndParent = -8, GwlId = -12, GwlUserData = -21;

    private void Bridge(string name, HostResult result, params ComArg[] args) => Bridge(new HostFunction(H, name, result, args));
    private void Bridge(HostFunction f) => Std(U, f.Name, f.Args.Count, f.Invoke);

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);

    protected override void Define()
    {
        var always = (Func<nint, bool>)(_ => true);
        var handle = HostArgs.Handle;
        // windows and geometry
        Bridge("GetDesktopWindow", HostResult.Handle);
        Bridge("GetForegroundWindow", HostResult.Handle);
        Bridge("IsWindow", HostResult.Int32, handle);
        Bridge("IsWindowVisible", HostResult.Int32, handle);
        Bridge(new HostFunction(H, "ShowWindow", HostResult.Int32, handle, ComArgs.Value) { Succeeded = always });
        Bridge("UpdateWindow", HostResult.Int32, handle);
        Bridge("DestroyWindow", HostResult.Int32, handle);
        Bridge("MoveWindow", HostResult.Int32, handle, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Value);
        Bridge("SetWindowPos", HostResult.Int32, handle, handle, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Value);
        Bridge("SetForegroundWindow", HostResult.Int32, handle);
        Bridge("SetWindowTextA", HostResult.Int32, handle, ComArgs.AnsiString(optional: true));
        Bridge("GetClientRect", HostResult.Int32, handle, ComArgs.OutBytes(16));
        Bridge("GetWindowRect", HostResult.Int32, handle, ComArgs.OutBytes(16));
        Bridge("ClientToScreen", HostResult.Int32, handle, ComArgs.InOutStruct(8));
        Bridge("ScreenToClient", HostResult.Int32, handle, ComArgs.InOutStruct(8));
        Bridge("InvalidateRect", HostResult.Int32, handle, ComArgs.InStruct(16, optional: true), ComArgs.Value);
        Bridge(new HostFunction(H, "GetSystemMetrics", HostResult.Int32, ComArgs.Signed) { Succeeded = always });
        // input
        Bridge(new HostFunction(H, "SetCapture", HostResult.Handle, handle) { Succeeded = always });
        Bridge("ReleaseCapture", HostResult.Int32);
        Bridge("GetCursorPos", HostResult.Int32, ComArgs.OutBytes(8));
        Bridge("SetCursorPos", HostResult.Int32, ComArgs.Signed, ComArgs.Signed);
        Bridge(new HostFunction(H, "ShowCursor", HostResult.Int32, ComArgs.Value) { Succeeded = always });
        Bridge(new HostFunction(H, "SetCursor", HostResult.Handle, handle) { Succeeded = always });
        Bridge(new HostFunction(H, "MapVirtualKeyA", HostResult.Int32, ComArgs.Value, ComArgs.Value) { Succeeded = always });
        Bridge(new HostFunction(H, "ToAscii", HostResult.Int32, ComArgs.Value, ComArgs.Value, ComArgs.InStruct(256, optional: true), ComArgs.OutBytes(2), ComArgs.Value) { Succeeded = r => (int)r > 0 });

        Std(U, "LoadCursorA", 2, c => c[0] != 0
            ? throw new Win32UsageException($"user32!LoadCursorA with hInstance 0x{c[0]:X8}: cursors from a guest image's resources are not bridged (only the system cursors, hInstance NULL).")
            : new HostFunction(H, "LoadCursorA", HostResult.Handle, ComArgs.Value, HostArgs.AnsiOrId()).Invoke(c));
        Std(U, "SystemParametersInfoA", 4, SystemParametersInfo);

        // classes, windows and their procedures
        Std(U, "RegisterClassA", 1, RegisterClass);
        Std(U, "UnregisterClassA", 2, UnregisterClass);
        Std(U, "CreateWindowExA", 12, CreateWindow);
        Std(U, "DefWindowProcA", 4, c => WindowProcBridge.PassToHost(c.Ctx, c.Mem, HostExports.Get(H, "DefWindowProcA"), c[0], c[1], c[2], c[3]));
        Std(U, "GetWindowLongA", 2, GetWindowLong);
        Std(U, "SetWindowLongA", 3, SetWindowLong);

        // messages
        Std(U, "GetMessageA", 4, c => ReceiveMessage(c, "GetMessageA", [HostArgs.Handle, ComArgs.Value, ComArgs.Value], r => (int)r != -1));
        Std(U, "PeekMessageA", 5, c => ReceiveMessage(c, "PeekMessageA", [HostArgs.Handle, ComArgs.Value, ComArgs.Value, ComArgs.Value], r => (int)r != 0));
        Std(U, "DispatchMessageA", 1, DispatchMessage);
        Std(U, "PostMessageA", 4, c => MessageThunks.For(c[1]).CarriesPointer
            ? c.Ctx.Fail(ErrorMessageSyncOnly)
            : new HostFunction(H, "PostMessageA", HostResult.Int32, handle, ComArgs.Value, HostArgs.WParam, HostArgs.LParam).Invoke(c));

        // painting
        Std(U, "BeginPaint", 2, BeginPaint);
        Std(U, "EndPaint", 2, EndPaint);
    }

    private static uint RegisterClass(Win32Call c)
    {
        if (c[0] == 0) return c.Ctx.Fail(Win32Error.NoAccess);
        var g = c.Mem.ReadBytes(c[0], Win32Layouts.GuestWndClassA);
        if (U32(g, 32) != 0) throw new Win32UsageException("user32!RegisterClassA: WNDCLASSA.lpszMenuName is set; menus from a guest image's resources are not bridged.");
        var guestProc = U32(g, 4);
        if (guestProc == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var module = GuestModule.Current ?? throw new Win32UsageException("user32!RegisterClassA needs a running guest module (the window procedure is guest code).");
        var nameWord = U32(g, 36);
        if (nameWord == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var state = WindowState.For(c.Ctx);
        state.Bind(c.Mem);
        using var scope = new NativeScope();
        var name = nameWord < 0x10000 ? "#" + nameWord : c.Mem.ReadAnsiString(nameWord);
        var hostName = nameWord < 0x10000 ? (nint)nameWord : scope.Copy([.. c.Mem.ReadCString(nameWord), 0]);
        var h = scope.AllocZeroed(Win32Layouts.HostWndClassA);
        Win32Layouts.WndClassToHost(g, new Span<byte>((void*)h, Win32Layouts.HostWndClassA), WindowProcBridge.NativeProcedure, HostExports.HostInstance, 0, hostName);
        var atom = (ushort)HostFunction.CallNative(c.Ctx, HostExports.Get(H, "RegisterClassA"), [h]);
        if (atom == 0) return 0;
        WindowProcBridge.RegisterClass(new GuestWindowClass { Atom = atom, Name = name, GuestProc = guestProc, GuestInstance = U32(g, 16), Module = module, Owner = state });
        return atom;
    }

    private static uint UnregisterClass(Win32Call c)
    {
        var state = WindowState.For(c.Ctx);
        var cls = c[0] < 0x10000 ? WindowProcBridge.ClassOf((ushort)c[0]) : state.ClassesByName.GetValueOrDefault(c.Mem.ReadAnsiString(c[0]));
        var r = new HostFunction(H, "UnregisterClassA", HostResult.Int32, HostArgs.AnsiOrId(), new DelegateComArg((_, v) => v == 0 ? 0 : HostExports.HostInstance)).Invoke(c);
        if (r != 0 && cls is not null) WindowProcBridge.UnregisterClass(cls);
        return r;
    }

    private static readonly HostFunction createWindow = new(H, "CreateWindowExA", HostResult.Handle,
        ComArgs.Value, HostArgs.AnsiOrId(), ComArgs.AnsiString(optional: true), ComArgs.Value, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed,
        HostArgs.Handle, HostArgs.Handle, new DelegateComArg((_, v) => v == 0 ? 0 : HostExports.HostInstance), ComArgs.Value);

    private static uint CreateWindow(Win32Call c)
    {
        WindowState.For(c.Ctx).Bind(c.Mem);
        var previous = WindowProcBridge.CreatingInstance;
        WindowProcBridge.CreatingInstance = c[10];
        try
        {
            var hwnd = createWindow.Invoke(c);
            if (hwnd != 0 && WindowProcBridge.Bind(HostHandles.ToHost(hwnd)) is { } w) w.Instance = c[10];
            return hwnd;
        }
        finally { WindowProcBridge.CreatingInstance = previous; }
    }

    private static GuestWindow OwnWindow(Win32Call c, string api) =>
        WindowProcBridge.Bind(HostHandles.ToHost(c[0])) ?? throw new Win32UsageException($"user32!{api}(0x{c[0]:X8}, {(int)c[1]}): the window was not created from a guest class, so its window procedure / instance is a host address that 32-bit code cannot hold.");

    private static uint GetWindowLong(Win32Call c)
    {
        var index = (int)c[1];
        switch (index)
        {
            case GwlWndProc: return OwnWindow(c, "GetWindowLongA").Proc;
            case GwlHInstance: return OwnWindow(c, "GetWindowLongA").Instance;
            case GwlHwndParent: return new HostFunction(H, "GetWindowLongPtrA", HostResult.Handle, HostArgs.Handle, ComArgs.Signed) { Succeeded = _ => true }.Invoke(c);
            case GwlUserData or GwlId: return new HostFunction(H, "GetWindowLongPtrA", HostResult.Pointer, HostArgs.Handle, ComArgs.Signed) { Succeeded = _ => true }.Invoke(c);
            default: return new HostFunction(H, "GetWindowLongA", HostResult.Int32, HostArgs.Handle, ComArgs.Signed) { Succeeded = _ => true }.Invoke(c);
        }
    }

    private static uint SetWindowLong(Win32Call c)
    {
        var index = (int)c[1];
        switch (index)
        {
            case GwlWndProc:
            {
                var w = OwnWindow(c, "SetWindowLongA");
                if (c[2] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
                var old = w.Proc; w.Proc = c[2];
                return old;
            }
            case GwlHInstance: throw new Win32UsageException("user32!SetWindowLongA(GWL_HINSTANCE) is not bridged.");
            case GwlHwndParent: return new HostFunction(H, "SetWindowLongPtrA", HostResult.Handle, HostArgs.Handle, ComArgs.Signed, HostArgs.Handle) { Succeeded = _ => true }.Invoke(c);
            case GwlUserData or GwlId: return new HostFunction(H, "SetWindowLongPtrA", HostResult.Pointer, HostArgs.Handle, ComArgs.Signed, ComArgs.Value) { Succeeded = _ => true }.Invoke(c);
            default: return new HostFunction(H, "SetWindowLongA", HostResult.Int32, HostArgs.Handle, ComArgs.Signed, ComArgs.Value) { Succeeded = _ => true }.Invoke(c);
        }
    }

    private static uint ReceiveMessage(Win32Call c, string api, ComArg[] rest, Func<nint, bool> filled)
    {
        if (c[0] == 0) return c.Ctx.Fail(Win32Error.NoAccess, api == "GetMessageA" ? uint.MaxValue : 0);
        using var scope = new NativeScope();
        var msg = scope.AllocZeroed(Win32Layouts.HostMsg);
        Span<nint> args = stackalloc nint[1 + rest.Length];
        args[0] = msg;
        using (var conv = new ComCallScope(c.Mem, c.Ctx, c.Args.ToArray()))
            for (var i = 0; i < rest.Length; i++) args[1 + i] = rest[i].ToHost(conv, c[1 + i]);
        var r = HostFunction.CallNative(c.Ctx, HostExports.Get(H, api), args);
        if (filled(r))
        {
            Span<byte> g = stackalloc byte[Win32Layouts.GuestMsg];
            Win32Layouts.MsgToGuest(new ReadOnlySpan<byte>((void*)msg, Win32Layouts.HostMsg), g);
            c.Mem.Write(c[0], g);
        }
        return (uint)r;
    }

    private static uint DispatchMessage(Win32Call c)
    {
        if (c[0] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var g = c.Mem.ReadBytes(c[0], Win32Layouts.GuestMsg);
        if (U32(g, 4) == MessageThunks.WM_TIMER && U32(g, 12) != 0)
            throw new Win32UsageException("user32!DispatchMessageA: WM_TIMER with a timer procedure (a guest code address) is not bridged.");
        using var scope = new NativeScope();
        var h = scope.AllocZeroed(Win32Layouts.HostMsg);
        Win32Layouts.MsgToHost(g, new Span<byte>((void*)h, Win32Layouts.HostMsg));
        var r = HostFunction.CallNative(c.Ctx, HostExports.Get(H, "DispatchMessageA"), [h]);
        return Win32Layouts.Value(r, "user32!DispatchMessageA result");
    }

    private static uint BeginPaint(Win32Call c)
    {
        if (c[1] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        using var scope = new NativeScope();
        var ps = scope.AllocZeroed(Win32Layouts.HostPaintStruct);
        var hdc = HostFunction.CallNative(c.Ctx, HostExports.Get(H, "BeginPaint"), [HostHandles.ToHost(c[0]), ps]);
        if (hdc == 0) return 0;
        Span<byte> g = stackalloc byte[Win32Layouts.GuestPaintStruct];
        Win32Layouts.PaintStructToGuest(new ReadOnlySpan<byte>((void*)ps, Win32Layouts.HostPaintStruct), g);
        c.Mem.Write(c[1], g);
        return HostHandles.ToGuest(hdc, "user32!BeginPaint");
    }

    private static uint EndPaint(Win32Call c)
    {
        if (c[1] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        using var scope = new NativeScope();
        var ps = scope.AllocZeroed(Win32Layouts.HostPaintStruct);
        Win32Layouts.PaintStructToHost(c.Mem.ReadBytes(c[1], Win32Layouts.GuestPaintStruct), new Span<byte>((void*)ps, Win32Layouts.HostPaintStruct));
        return (uint)HostFunction.CallNative(c.Ctx, HostExports.Get(H, "EndPaint"), [HostHandles.ToHost(c[0]), ps]);
    }

    /// <summary>SystemParametersInfoA: each action has its own pvParam type, so only actions with a known parameter are bridged; any other action faults naming it.</summary>
    private static uint SystemParametersInfo(Win32Call c)
    {
        var action = c[0];
        ComArg pv = action switch
        {
            0x0030 => ComArgs.OutBytes(16),                  // SPI_GETWORKAREA: RECT
            0x0010 => ComArgs.OutU32(),                      // SPI_GETSCREENSAVEACTIVE: BOOL
            0x0011 => new DelegateComArg((_, v) => (nint)v), // SPI_SETSCREENSAVEACTIVE: pvParam unused (value)
            0x004A => ComArgs.OutU32(),                      // SPI_GETFONTSMOOTHING: BOOL
            0x004B => new DelegateComArg((_, v) => (nint)v), // SPI_SETFONTSMOOTHING: the value is uiParam, pvParam unused
            _ => throw new Win32UsageException($"user32!SystemParametersInfoA action 0x{action:X4} is not bridged (its pvParam type is not in the adapter table)."),
        };
        return new HostFunction(H, "SystemParametersInfoA", HostResult.Int32, ComArgs.Value, ComArgs.Value, pv, ComArgs.Value).Invoke(c);
    }
}
