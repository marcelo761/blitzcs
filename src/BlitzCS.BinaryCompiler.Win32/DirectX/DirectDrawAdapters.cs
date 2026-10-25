using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32.DirectX;

/// <summary>ddraw.h / d3d.h identifiers and the HRESULTs the adapters produce themselves.</summary>
public static class DdConst
{
    public static readonly Guid IidDirectDraw7 = new("15E65EC0-3B9C-11D2-B92F-00609797EA5B");
    public static readonly Guid IidDirectDraw = new("6C14DB80-A733-11CE-A521-0020AF0BE560");
    public static readonly Guid IidDirectDrawSurface7 = new("06675A80-3B9B-11D2-B92F-00609797EA5B");
    public static readonly Guid IidDirectDrawClipper = new("6C14DB85-A733-11CE-A521-0020AF0BE560");
    public static readonly Guid IidDirect3D7 = new("F5049E77-4861-11D2-A407-00A0C90629A8");
    public static readonly Guid IidDirect3DDevice7 = new("F5049E79-4861-11D2-A407-00A0C90629A8");
    public const uint DdErrInvalidParams = 0x80070057, DdErrNoDirectDrawHw = 0x887601FF;
    public const uint DdsdLpSurface = 0x800;
}

/// <summary>
/// DDSURFACEDESC2: 124 bytes on the 32-bit guest, 136 on the 64-bit host (lpSurface is a pointer at 36 / 40, everything after it moves by 8). Every other field is a DWORD-sized value, a
/// DDCOLORKEY, a DDPIXELFORMAT or a DDSCAPS2 (all 32-bit fields), so they are copied as bytes.
/// </summary>
public static class DdLayouts
{
    public const int GuestSurfaceDesc2 = 124, HostSurfaceDesc2 = 136, GuestDdCapsMax = 380, DeviceIdentifier2 = 1072, D3dDeviceDesc7 = 236, PixelFormat = 32;

    /// <summary>Host to guest. lpSurface is never handed over (a host address): it reads 0.</summary>
    public static void SurfaceDescToGuest(ReadOnlySpan<byte> h, Span<byte> g)
    {
        h[..36].CopyTo(g);
        BinaryPrimitives.WriteUInt32LittleEndian(g[0..], GuestSurfaceDesc2);
        BinaryPrimitives.WriteUInt32LittleEndian(g[36..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(g[4..], BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) & ~DdConst.DdsdLpSurface);
        h.Slice(48, 84).CopyTo(g[40..]);
    }

    /// <summary>Guest to host. A guest that supplies its own surface memory (DDSD_LPSURFACE) is refused: the host would write to a 32-bit guest address it cannot see as such.</summary>
    public static void SurfaceDescToHost(ReadOnlySpan<byte> g, Span<byte> h)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(g) != GuestSurfaceDesc2) throw new ComArgumentException($"DDSURFACEDESC2.dwSize is {BinaryPrimitives.ReadUInt32LittleEndian(g)}, not {GuestSurfaceDesc2}.");
        if ((BinaryPrimitives.ReadUInt32LittleEndian(g[4..]) & DdConst.DdsdLpSurface) != 0) throw new Win32UsageException("DDSURFACEDESC2 with DDSD_LPSURFACE (client surface memory) is not bridged.");
        h[..HostSurfaceDesc2].Clear();
        g[..36].CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h, HostSurfaceDesc2);
        g.Slice(40, 84).CopyTo(h[48..]);
    }
}

/// <summary>
/// The DirectDraw 7 interfaces as adapter tables over the host's ddraw.dll (Windows, or Wine's ddraw over wined3d). Every slot is listed in vtable order: a bridged row converts each argument
/// explicitly, an <c>Absent</c> row faults naming the method and why. Objects the host returns are wrapped through <see cref="HostComRegistry"/> (one guest identity per host object).
/// </summary>
public static unsafe class DirectDrawAdapters
{
    private static GuestComInterface? dd7, dd1, clipper, d3d7;

    public static GuestComInterface? Table(Guid iid) =>
        iid == DdConst.IidDirectDraw7 ? dd7 ??= DirectDraw7() :
        iid == DdConst.IidDirectDraw ? dd1 ??= DirectDraw1() :
        iid == DdConst.IidDirectDrawClipper ? clipper ??= Clipper() :
        iid == DdConst.IidDirect3D7 ? d3d7 ??= Direct3D7() :
        iid == DdConst.IidDirectDrawSurface7 ? DirectDrawSurfaces.Table :
        null;

    private static Func<GuestComObject, HostComInterface> HostOf(Guid iid) => o => ((HostComProxy)o.State!).Host(iid);

    private static HostComRegistry Registry(ComCallScope s) => HostComRegistry.For(s.Call.Host, DirectXTables.Find);

    /// <summary>An output interface pointer of the given IID, wrapped with identity.</summary>
    internal static ComArg OutObject(Guid iid) => ComArgs.OutInterface((s, native) => Registry(s).Wrap(native, iid));

    /// <summary>A guest interface pointer passed IN (a surface given to a method): the native interface behind the guest object, or NULL.</summary>
    internal static ComArg InObject(Guid iid, bool optional) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("An interface pointer must not be NULL.");
        var obj = s.Call.Host.Find(v) ?? throw new ComArgumentException($"0x{v:X8} is not a COM object of this process.");
        return obj.State is HostComProxy p && p.Has(iid) ? p.Host(iid).Pointer : throw new ComArgumentException($"{obj} has no native {iid} interface.");
    });

    /// <summary>A structure whose first DWORD is its size (DDCAPS): that many bytes (at most <paramref name="max"/>) in and back out; optional.</summary>
    private static ComArg SizedInOut(int max) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return 0;
        var size = (int)s.Mem.ReadUInt32(v);
        if (size < 4 || size > max) throw new ComArgumentException($"A structure size of {size} bytes is not valid here (at most {max}).");
        return ComArgs.InOutStruct(size).ToHost(s, v);
    });

    /// <summary>DDSURFACEDESC2 in (and back out on success when <paramref name="inOut"/>), converted between the 124- and 136-byte layouts.</summary>
    internal static ComArg SurfaceDesc(bool inOut, bool optional = false) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("A DDSURFACEDESC2 pointer must not be NULL.");
        var native = s.Native.AllocZeroed(DdLayouts.HostSurfaceDesc2);
        DdLayouts.SurfaceDescToHost(s.Mem.ReadBytes(v, DdLayouts.GuestSurfaceDesc2), new Span<byte>((void*)native, DdLayouts.HostSurfaceDesc2));
        if (inOut)
            s.OnCompleted(hr =>
            {
                if (!ComHr.Succeeded(hr)) return;
                Span<byte> g = stackalloc byte[DdLayouts.GuestSurfaceDesc2];
                DdLayouts.SurfaceDescToGuest(new ReadOnlySpan<byte>((void*)native, DdLayouts.HostSurfaceDesc2), g);
                s.Mem.Write(v, g);
            });
        return native;
    });

    // ---- IDirectDraw7 ----

    private static GuestComInterface DirectDraw7()
    {
        var i = new GuestComInterface("IDirectDraw7", DdConst.IidDirectDraw7);
        var host = HostOf(DdConst.IidDirectDraw7);
        i.Add(new HostComMethod("Compact", 3).ToGuest(host));
        i.Add(new HostComMethod("CreateClipper", 4, ComArgs.Value, OutObject(DdConst.IidDirectDrawClipper), ComArgs.MustBeNull("pUnkOuter")).ToGuest(host));
        i.Absent("CreatePalette", 4, "palettes (8-bit display modes) have no adapter table");
        i.Add(new HostComMethod("CreateSurface", 6, SurfaceDesc(inOut: false), OutObject(DdConst.IidDirectDrawSurface7), ComArgs.MustBeNull("pUnkOuter")).ToGuest(host));
        i.Add(new HostComMethod("DuplicateSurface", 7, InObject(DdConst.IidDirectDrawSurface7, false), OutObject(DdConst.IidDirectDrawSurface7)).ToGuest(host));
        i.Add(new GuestComMethod("EnumDisplayModes", 4, c => EnumDisplayModes(c, host(c.Object))));
        i.Absent("EnumSurfaces", 4, "the surface enumeration callback has no adapter row");
        i.Add(new HostComMethod("FlipToGDISurface", 10).ToGuest(host));
        i.Add(new HostComMethod("GetCaps", 11, SizedInOut(DdLayouts.GuestDdCapsMax), SizedInOut(DdLayouts.GuestDdCapsMax)).ToGuest(host));
        i.Add(new HostComMethod("GetDisplayMode", 12, SurfaceDesc(inOut: true)).ToGuest(host));
        i.Absent("GetFourCCCodes", 2, "the in/out count protocol has no adapter row");
        i.Add(new HostComMethod("GetGDISurface", 14, OutObject(DdConst.IidDirectDrawSurface7)).ToGuest(host));
        i.Add(new HostComMethod("GetMonitorFrequency", 15, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("GetScanLine", 16, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("GetVerticalBlankStatus", 17, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("Initialize", 18, ComArgs.Guid(optional: true)).ToGuest(host));
        i.Add(new HostComMethod("RestoreDisplayMode", 19).ToGuest(host));
        i.Add(new HostComMethod("SetCooperativeLevel", 20, HostArgs.Handle, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("SetDisplayMode", 21, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("WaitForVerticalBlank", 22, ComArgs.Value, ComArgs.MustBeNull("hEvent")).ToGuest(host));
        i.Add(new HostComMethod("GetAvailableVidMem", 23, ComArgs.InStruct(16), ComArgs.OutU32(optional: true), ComArgs.OutU32(optional: true)).ToGuest(host));
        i.Add(new HostComMethod("GetSurfaceFromDC", 24, HostArgs.Handle, OutObject(DdConst.IidDirectDrawSurface7)).ToGuest(host));
        i.Add(new HostComMethod("RestoreAllSurfaces", 25).ToGuest(host));
        i.Add(new HostComMethod("TestCooperativeLevel", 26).ToGuest(host));
        i.Add(new HostComMethod("GetDeviceIdentifier", 27, ComArgs.OutBytes(DdLayouts.DeviceIdentifier2), ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("StartModeTest", 28, HostArgs.CountedArray(1, 8), ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("EvaluateMode", 29, ComArgs.Value, ComArgs.OutU32()).ToGuest(host));
        return i;
    }

    // ---- IDirectDraw (version 1: what a program keeps for DirectShow or other version-1 consumers) ----

    private static GuestComInterface DirectDraw1()
    {
        const string noV1 = "DDSURFACEDESC and the version-1 surface interfaces have no adapter table";
        var i = new GuestComInterface("IDirectDraw", DdConst.IidDirectDraw);
        var host = HostOf(DdConst.IidDirectDraw);
        i.Add(new HostComMethod("Compact", 3).ToGuest(host));
        i.Add(new HostComMethod("CreateClipper", 4, ComArgs.Value, OutObject(DdConst.IidDirectDrawClipper), ComArgs.MustBeNull("pUnkOuter")).ToGuest(host));
        i.Absent("CreatePalette", 4, "palettes have no adapter table");
        i.Absent("CreateSurface", 3, noV1);
        i.Absent("DuplicateSurface", 2, noV1);
        i.Absent("EnumDisplayModes", 4, noV1);
        i.Absent("EnumSurfaces", 4, noV1);
        i.Add(new HostComMethod("FlipToGDISurface", 10).ToGuest(host));
        i.Add(new HostComMethod("GetCaps", 11, SizedInOut(DdLayouts.GuestDdCapsMax), SizedInOut(DdLayouts.GuestDdCapsMax)).ToGuest(host));
        i.Absent("GetDisplayMode", 1, noV1);
        i.Absent("GetFourCCCodes", 2, "the in/out count protocol has no adapter row");
        i.Absent("GetGDISurface", 1, noV1);
        i.Add(new HostComMethod("GetMonitorFrequency", 15, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("GetScanLine", 16, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("GetVerticalBlankStatus", 17, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("Initialize", 18, ComArgs.Guid(optional: true)).ToGuest(host));
        i.Add(new HostComMethod("RestoreDisplayMode", 19).ToGuest(host));
        i.Add(new HostComMethod("SetCooperativeLevel", 20, HostArgs.Handle, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("SetDisplayMode", 21, ComArgs.Value, ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("WaitForVerticalBlank", 22, ComArgs.Value, ComArgs.MustBeNull("hEvent")).ToGuest(host));
        return i;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ModesCallback(nint desc, nint context);

    /// <summary>EnumDisplayModes(flags, filter, context, callback): the host's callback receives a host DDSURFACEDESC2, the guest's gets a 124-byte copy; DDENUMRET_CANCEL ends it.</summary>
    private static uint EnumDisplayModes(GuestComCall c, HostComInterface host)
    {
        uint flags = c.Arg(0), filter = c.Arg(1), context = c.Arg(2), callback = c.Arg(3);
        if (callback == 0) return DdConst.DdErrInvalidParams;
        var mem = c.Mem;
        var call = c;
        Exception? failure = null;
        using var scope = new NativeScope();
        nint nativeFilter = 0;
        if (filter != 0)
        {
            nativeFilter = scope.AllocZeroed(DdLayouts.HostSurfaceDesc2);
            try { DdLayouts.SurfaceDescToHost(mem.ReadBytes(filter, DdLayouts.GuestSurfaceDesc2), new Span<byte>((void*)nativeFilter, DdLayouts.HostSurfaceDesc2)); }
            catch (ComArgumentException) { return DdConst.DdErrInvalidParams; }
        }
        using var scratch = mem.Allocate(DdLayouts.GuestSurfaceDesc2);
        ModesCallback native = (desc, _) =>
        {
            if (failure is not null) return 0;
            try
            {
                Span<byte> g = stackalloc byte[DdLayouts.GuestSurfaceDesc2];
                DdLayouts.SurfaceDescToGuest(new ReadOnlySpan<byte>((void*)desc, DdLayouts.HostSurfaceDesc2), g);
                mem.Write(scratch.Address, g);
                return (int)call.CallGuest(callback, scratch.Address, context);
            }
            catch (Exception e) { failure = e; return 0; }
        };
        var hr = host.Invoke(8, (nint)flags, nativeFilter, 0, Marshal.GetFunctionPointerForDelegate(native));
        GC.KeepAlive(native);
        if (failure is not null) throw new HostCallbackException("An IDirectDraw7::EnumDisplayModes callback failed: " + failure.Message, failure);
        return hr;
    }

    // ---- IDirectDrawClipper ----

    private static GuestComInterface Clipper()
    {
        var i = new GuestComInterface("IDirectDrawClipper", DdConst.IidDirectDrawClipper);
        var host = HostOf(DdConst.IidDirectDrawClipper);
        i.Absent("GetClipList", 3, "RGNDATA has no adapter row");
        i.Add(new GuestComMethod("GetHWnd", 1, c =>
        {
            if (c.Arg(0) == 0) return DdConst.DdErrInvalidParams;
            nint hwnd = 0;
            var hr = host(c.Object).Invoke(4, (nint)(&hwnd));
            if (ComHr.Succeeded(hr)) c.Mem.WriteUInt32(c.Arg(0), HostHandles.ToGuest(hwnd, "IDirectDrawClipper::GetHWnd"));
            return hr;
        }));
        i.Absent("Initialize", 2, "a clipper made by CreateClipper is already initialised");
        i.Add(new HostComMethod("IsClipListChanged", 6, ComArgs.OutU32()).ToGuest(host));
        i.Absent("SetClipList", 2, "RGNDATA has no adapter row");
        i.Add(new HostComMethod("SetHWnd", 8, ComArgs.Value, HostArgs.Handle).ToGuest(host));
        return i;
    }

    // ---- IDirect3D7 ----

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DevicesCallback(nint description, nint name, nint desc, nint context);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FormatsCallback(nint pixelFormat, nint context);

    private static GuestComInterface Direct3D7()
    {
        var i = new GuestComInterface("IDirect3D7", DdConst.IidDirect3D7);
        var host = HostOf(DdConst.IidDirect3D7);
        i.Add(new GuestComMethod("EnumDevices", 2, c => EnumDevices(c, host(c.Object))));
        i.Absent("CreateDevice", 3, "IDirect3DDevice7 has no adapter table yet");
        i.Absent("CreateVertexBuffer", 3, "IDirect3DVertexBuffer7 has no adapter table");
        i.Add(new GuestComMethod("EnumZBufferFormats", 3, c => EnumZBufferFormats(c, host(c.Object))));
        i.Add(new HostComMethod("EvictManagedTextures", 7).ToGuest(host));
        return i;
    }

    private static uint EnumDevices(GuestComCall c, HostComInterface host)
    {
        uint callback = c.Arg(0), context = c.Arg(1);
        if (callback == 0) return DdConst.DdErrInvalidParams;
        var mem = c.Mem; var call = c;
        Exception? failure = null;
        DevicesCallback native = (description, name, desc, _) =>
        {
            if (failure is not null) return 0;
            try
            {
                var d = CString(description); var n = CString(name);
                using var scratch = mem.Allocate(DdLayouts.D3dDeviceDesc7 + d.Length + n.Length);
                var at = scratch.Address;
                mem.Write(at, new ReadOnlySpan<byte>((void*)desc, DdLayouts.D3dDeviceDesc7));   // D3DDEVICEDESC7: DWORDs, floats and a GUID, identical layouts
                mem.Write(at + DdLayouts.D3dDeviceDesc7, d);
                mem.Write(at + DdLayouts.D3dDeviceDesc7 + (uint)d.Length, n);
                return (int)call.CallGuest(callback, at + DdLayouts.D3dDeviceDesc7, at + DdLayouts.D3dDeviceDesc7 + (uint)d.Length, at, context);
            }
            catch (Exception e) { failure = e; return 0; }
        };
        var hr = host.Invoke(3, Marshal.GetFunctionPointerForDelegate(native), 0);
        GC.KeepAlive(native);
        if (failure is not null) throw new HostCallbackException("An IDirect3D7::EnumDevices callback failed: " + failure.Message, failure);
        return hr;
    }

    private static uint EnumZBufferFormats(GuestComCall c, HostComInterface host)
    {
        uint device = c.Arg(0), callback = c.Arg(1), context = c.Arg(2);
        if (device == 0 || callback == 0) return DdConst.DdErrInvalidParams;
        var mem = c.Mem; var call = c;
        Exception? failure = null;
        using var scope = new NativeScope();
        var clsid = scope.Copy(mem.ReadBytes(device, 16));
        using var scratch = mem.Allocate(DdLayouts.PixelFormat);
        FormatsCallback native = (format, _) =>
        {
            if (failure is not null) return 0;
            try
            {
                mem.Write(scratch.Address, new ReadOnlySpan<byte>((void*)format, DdLayouts.PixelFormat));
                return (int)call.CallGuest(callback, scratch.Address, context);
            }
            catch (Exception e) { failure = e; return 0; }
        };
        var hr = host.Invoke(6, clsid, Marshal.GetFunctionPointerForDelegate(native), 0);
        GC.KeepAlive(native);
        if (failure is not null) throw new HostCallbackException("An IDirect3D7::EnumZBufferFormats callback failed: " + failure.Message, failure);
        return hr;
    }

    internal static byte[] CString(nint p) => p == 0 ? [0] : [.. System.Text.Encoding.Latin1.GetBytes(Marshal.PtrToStringAnsi(p) ?? ""), 0];
}
