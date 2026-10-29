using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.DirectX;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// DDRAW.dll creation functions bridged to the HOST's ddraw.dll (registered with the window bridge only, i.e. on a Windows host). The objects are guest COM objects over the native ones
/// (<see cref="DirectDrawAdapters"/>, <see cref="DirectInputAdapters"/>, <see cref="HostComRegistry"/>); the module needs a dynamic stub pool for their vtables. DINPUT8.dll!DirectInput8Create
/// creates IDirectInput8A objects. d3dxof.dll!DirectXFileCreate creates IDirectXFile objects.
/// </summary>
public sealed unsafe class HostDirectXService : Win32Service
{
    public override string Name => "DirectX.Host";
    private const string D = "DDRAW.dll", H = "ddraw.dll";

    protected override void Define()
    {
        Std(D, "DirectDrawCreateEx", 4, DirectDrawCreateEx);
        Std(D, "DirectDrawEnumerateExA", 3, DirectDrawEnumerateEx);
        Std("DINPUT8.dll", "DirectInput8Create", 5, DirectInput8Create);
        Std("DINPUT.dll", "DirectInputCreateEx", 5, DirectInputCreateEx);
        Std("DINPUT.dll", "DirectInputCreateA", 4, DirectInputCreateA);
        Std("d3dxof.dll", "DirectXFileCreate", 1, DirectXFileCreate);
    }

    /// <summary>DirectXFileCreate(lplpDirectXFile): creates IDirectXFile object over d3dxof.dll.</summary>
    private static uint DirectXFileCreate(Win32Call c)
    {
        uint ppDirectXFile = c[0];
        if (ppDirectXFile == 0) return ComHr.InvalidArg;
        c.Mem.WriteUInt32(ppDirectXFile, 0);

        var module = GuestModule.Current ?? throw new Win32UsageException("d3dxof!DirectXFileCreate needs a running guest module (its COM objects live in the guest's memory).");
        nint dxf = 0;
        var hr = (uint)HostFunction.CallNative(c.Ctx, HostExports.Get("d3dxof.dll", "DirectXFileCreate"), [(nint)(&dxf)]);
        if (!ComHr.Succeeded(hr) || dxf == 0) return ComHr.Succeeded(hr) ? ComHr.Fail : hr;

        var com = GuestComHost.For(c.Ctx, module);
        c.Mem.WriteUInt32(ppDirectXFile, HostComRegistry.For(com, DirectXTables.Find).Wrap(dxf, DirectXFileAdapters.IidDirectXFile));
        return ComHr.Ok;
    }

    /// <summary>DirectInputCreateEx(hinst, version, riid, ppv, outer): IDirectInput7A and IDirectInput8A.</summary>
    private static uint DirectInputCreateEx(Win32Call c)
    {
        uint version = c[1], iid = c[2], ppv = c[3], outer = c[4];
        if (ppv == 0 || iid == 0) return ComHr.InvalidArg;
        c.Mem.WriteUInt32(ppv, 0);
        if (outer != 0) return 0x80040110;   // CLASS_E_NOAGGREGATION
        var requested = new Guid(c.Mem.ReadBytes(iid, 16));
        if (requested != DirectInputAdapters.IidDirectInput7A && requested != DirectInputAdapters.IidDirectInput8A)
            throw new Win32UsageException($"dinput!DirectInputCreateEx for interface {requested}: only IDirectInput7A and IDirectInput8A have adapter tables.");
        var module = GuestModule.Current ?? throw new Win32UsageException("dinput!DirectInputCreateEx needs a running guest module (its COM objects live in the guest's memory).");
        using var scope = new NativeScope();
        nint di = 0;
        var hr = (uint)HostFunction.CallNative(c.Ctx, HostExports.Get("dinput.dll", "DirectInputCreateEx"),
            [c[0] == 0 ? 0 : HostExports.HostInstance, (nint)version, scope.Copy(requested.ToByteArray()), (nint)(&di), 0]);
        if (!ComHr.Succeeded(hr) || di == 0) return ComHr.Succeeded(hr) ? ComHr.Fail : hr;
        c.Mem.WriteUInt32(ppv, HostComRegistry.For(GuestComHost.For(c.Ctx, module), DirectXTables.Find).Wrap(di, requested));
        return ComHr.Ok;
    }

    /// <summary>DirectInputCreateA(hinst, version, ppv, outer): IDirectInput7A.</summary>
    private static uint DirectInputCreateA(Win32Call c)
    {
        uint version = c[1], ppv = c[2], outer = c[3];
        if (ppv == 0) return ComHr.InvalidArg;
        c.Mem.WriteUInt32(ppv, 0);
        if (outer != 0) return 0x80040110;   // CLASS_E_NOAGGREGATION
        var module = GuestModule.Current ?? throw new Win32UsageException("dinput!DirectInputCreateA needs a running guest module (its COM objects live in the guest's memory).");
        nint di = 0;
        var hr = (uint)HostFunction.CallNative(c.Ctx, HostExports.Get("dinput.dll", "DirectInputCreateA"),
            [c[0] == 0 ? 0 : HostExports.HostInstance, (nint)version, (nint)(&di), 0]);
        if (!ComHr.Succeeded(hr) || di == 0) return ComHr.Succeeded(hr) ? ComHr.Fail : hr;
        c.Mem.WriteUInt32(ppv, HostComRegistry.For(GuestComHost.For(c.Ctx, module), DirectXTables.Find).Wrap(di, DirectInputAdapters.IidDirectInput7A));
        return ComHr.Ok;
    }

    /// <summary>DirectInput8Create(hinst, version, riid, ppv, outer): the guest's HINSTANCE stands for the host's own module (DirectInput only checks it is a module); IDirectInput8A only.</summary>
    private static uint DirectInput8Create(Win32Call c)
    {
        uint version = c[1], iid = c[2], ppv = c[3], outer = c[4];
        if (ppv == 0 || iid == 0) return ComHr.InvalidArg;
        c.Mem.WriteUInt32(ppv, 0);
        if (outer != 0) return 0x80040110;   // CLASS_E_NOAGGREGATION
        var requested = new Guid(c.Mem.ReadBytes(iid, 16));
        if (requested != DirectInputAdapters.IidDirectInput8A)
            throw new Win32UsageException($"dinput8!DirectInput8Create for interface {requested}: only IDirectInput8A has an adapter table.");
        var module = GuestModule.Current ?? throw new Win32UsageException("dinput8!DirectInput8Create needs a running guest module (its COM objects live in the guest's memory).");
        using var scope = new NativeScope();
        nint di = 0;
        var hr = (uint)HostFunction.CallNative(c.Ctx, HostExports.Get("dinput8.dll", "DirectInput8Create"),
            [c[0] == 0 ? 0 : HostExports.HostInstance, (nint)version, scope.Copy(requested.ToByteArray()), (nint)(&di), 0]);
        if (!ComHr.Succeeded(hr) || di == 0) return ComHr.Succeeded(hr) ? ComHr.Fail : hr;
        c.Mem.WriteUInt32(ppv, HostComRegistry.For(GuestComHost.For(c.Ctx, module), DirectXTables.Find).Wrap(di, DirectInputAdapters.IidDirectInput8A));
        return ComHr.Ok;
    }

    /// <summary>A GUID* argument of DirectDraw: NULL, the DDCREATE_* pseudo pointers (1, 2) passed as values, or a native copy.</summary>
    private static nint DriverGuid(NativeScope scope, IWin32Memory mem, uint v) => v < 0x10000 ? (nint)v : scope.Copy(mem.ReadBytes(v, 16));

    private static uint DirectDrawCreateEx(Win32Call c)
    {
        uint guid = c[0], ppv = c[1], iid = c[2], outer = c[3];
        if (ppv == 0 || iid == 0) return DdConst.DdErrInvalidParams;
        c.Mem.WriteUInt32(ppv, 0);
        if (outer != 0) return 0x80040110;   // CLASS_E_NOAGGREGATION
        var requested = new Guid(c.Mem.ReadBytes(iid, 16));
        if (requested != DdConst.IidDirectDraw7) return DdConst.DdErrInvalidParams;   // DirectDrawCreateEx only creates IDirectDraw7 objects
        var module = GuestModule.Current ?? throw new Win32UsageException("ddraw!DirectDrawCreateEx needs a running guest module (its COM objects live in the guest's memory).");
        using var scope = new NativeScope();
        var nativeIid = scope.Copy(requested.ToByteArray());
        nint dd = 0;
        var hr = (uint)HostFunction.CallNative(c.Ctx, HostExports.Get(H, "DirectDrawCreateEx"), [DriverGuid(scope, c.Mem, guid), (nint)(&dd), nativeIid, 0]);
        if (!ComHr.Succeeded(hr) || dd == 0) return ComHr.Succeeded(hr) ? ComHr.Fail : hr;
        var com = GuestComHost.For(c.Ctx, module);
        c.Mem.WriteUInt32(ppv, HostComRegistry.For(com, DirectXTables.Find).Wrap(dd, DdConst.IidDirectDraw7));
        return ComHr.Ok;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumCallbackEx(nint guid, nint description, nint name, nint context, nint monitor);

    /// <summary>DirectDrawEnumerateExA(callback, context, flags): the host calls back on this thread; each driver is handed to the guest callback with guest copies of the GUID and strings.</summary>
    private static uint DirectDrawEnumerateEx(Win32Call c)
    {
        uint callback = c[0], context = c[1], flags = c[2];
        if (callback == 0) return DdConst.DdErrInvalidParams;
        var callGuest = c.GuestCaller();
        var mem = c.Mem;
        Exception? failure = null;
        EnumCallbackEx native = (guid, description, name, _, monitor) =>
        {
            if (failure is not null) return 0;
            try
            {
                var d = DirectDrawAdapters.CString(description); var n = DirectDrawAdapters.CString(name);
                using var scratch = mem.Allocate(16 + d.Length + n.Length);
                var at = scratch.Address;
                uint guestGuid = 0;
                if (guid != 0) { guestGuid = at; mem.Write(at, new ReadOnlySpan<byte>((void*)guid, 16)); }
                mem.Write(at + 16, d); mem.Write(at + 16 + (uint)d.Length, n);
                return callGuest(callback, [guestGuid, at + 16, at + 16 + (uint)d.Length, context, HostHandles.ToGuest(monitor, "HMONITOR")]) != 0 ? 1 : 0;
            }
            catch (Exception e) { failure = e; return 0; }
        };
        var hr = (uint)HostFunction.CallNative(c.Ctx, HostExports.Get(H, "DirectDrawEnumerateExA"), [Marshal.GetFunctionPointerForDelegate(native), 0, (nint)flags]);
        GC.KeepAlive(native);
        if (failure is not null) throw new HostCallbackException("A DirectDrawEnumerateExA callback failed: " + failure.Message, failure);
        return hr;
    }
}
