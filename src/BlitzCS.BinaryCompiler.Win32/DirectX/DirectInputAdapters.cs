using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32.DirectX;

/// <summary>
/// DirectInput 8 (ANSI) over the host's dinput8.dll. Structures without pointers (DIDEVCAPS, DIDEVICEINSTANCEA, DIDEVICEOBJECTINSTANCEA, DIPROPDWORD/DIPROPRANGE, device state) are copied with
/// their guest dwSize. Converted explicitly: DIDATAFORMAT (24 vs 32 bytes, with an array of DIOBJECTDATAFORMAT of 16 vs 24 bytes whose GUID pointers are copied) and DIDEVICEOBJECTDATA
/// (20 vs 24 bytes: uAppData is pointer-sized). A property GUID may be a MAKEDIPROP integer and passes as that value. Event notification handles, force feedback, action maps and the
/// control panel are named faults.
/// </summary>
public static unsafe class DirectInputAdapters
{
    public static readonly Guid IidDirectInput8A = new("BF798030-483A-4DA2-AA99-5D64ED369700");
    public static readonly Guid IidDirectInputDevice8A = new("54D41080-DC15-4833-A41B-748F73A38179");
    public const int GuestDataFormat = 24, HostDataFormat = 32, GuestObjectFormat = 16, HostObjectFormat = 24, GuestObjectData = 20, HostObjectData = 24, ObjectDataDx3 = 16;

    private static GuestComInterface? input, device;
    public static GuestComInterface? Table(Guid iid) =>
        iid == IidDirectInput8A ? input ??= Input() :
        iid == IidDirectInputDevice8A ? device ??= Device() :
        null;

    private static Func<GuestComObject, HostComInterface> HostOf(Guid iid) => o => ((HostComProxy)o.State!).Host(iid);
    private static ComArg OutObject(Guid iid) => ComArgs.OutInterface((s, native) => HostComRegistry.For(s.Call.Host, DirectXTables.Find).Wrap(native, iid));

    /// <summary>A REFGUID that may be a small integer (MAKEDIPROP).</summary>
    private static readonly ComArg GuidOrId = new DelegateComArg((s, v) => v < 0x10000 ? (nint)v : s.Native.Copy(s.Mem.ReadBytes(v, 16)));

    /// <summary>A structure whose first DWORD is its size: that many bytes (4..<paramref name="max"/>) in and back on success.</summary>
    private static ComArg Sized(int max, bool inOut = true) => new DelegateComArg((s, v) =>
    {
        if (v == 0) throw new ComArgumentException("A structure pointer must not be NULL.");
        var size = (int)s.Mem.ReadUInt32(v);
        if (size < 4 || size > max) throw new ComArgumentException($"A structure size of {size} bytes is not valid here (at most {max}).");
        return inOut ? ComArgs.InOutStruct(size).ToHost(s, v) : ComArgs.InStruct(size).ToHost(s, v);
    });

    /// <summary>Guest DIDATAFORMAT (and its object array) to a native image.</summary>
    public static nint DataFormatToHost(IWin32Memory mem, NativeScope native, uint guest)
    {
        var g = mem.ReadBytes(guest, GuestDataFormat);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(g), objSize = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(4)), count = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(16)), objects = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(20));
        if (size != GuestDataFormat || objSize != GuestObjectFormat) throw new ComArgumentException($"DIDATAFORMAT sizes {size}/{objSize} are not the 32-bit {GuestDataFormat}/{GuestObjectFormat}.");
        if (count > 1024) throw new ComArgumentException($"DIDATAFORMAT with {count} objects is not plausible.");
        var h = native.AllocZeroed(HostDataFormat);
        var array = count == 0 ? 0 : native.AllocZeroed((int)count * HostObjectFormat);
        *(uint*)h = HostDataFormat; *(uint*)(h + 4) = HostObjectFormat;
        *(uint*)(h + 8) = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(8));    // dwFlags
        *(uint*)(h + 12) = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(12));  // dwDataSize
        *(uint*)(h + 16) = count;
        *(nint*)(h + 24) = array;
        for (uint i = 0; i < count; i++)
        {
            var o = mem.ReadBytes(objects + i * GuestObjectFormat, GuestObjectFormat);
            var guid = BinaryPrimitives.ReadUInt32LittleEndian(o);
            var at = array + (nint)(i * HostObjectFormat);
            *(nint*)at = guid == 0 ? 0 : native.Copy(mem.ReadBytes(guid, 16));
            new ReadOnlySpan<byte>(o, 4, 12).CopyTo(new Span<byte>((void*)(at + 8), 12));   // dwOfs, dwType, dwFlags
        }
        return h;
    }

    // ---- IDirectInput8A ----

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DevicesCallback(nint instance, nint context);

    private static GuestComInterface Input()
    {
        var i = new GuestComInterface("IDirectInput8A", IidDirectInput8A);
        var host = HostOf(IidDirectInput8A);
        i.Add(new HostComMethod("CreateDevice", 3, ComArgs.Guid(optional: false), OutObject(IidDirectInputDevice8A), ComArgs.MustBeNull("pUnkOuter")).ToGuest(host));
        i.Add(new GuestComMethod("EnumDevices", 4, c => EnumDevices(c, host(c.Object))));
        i.Add(new HostComMethod("GetDeviceStatus", 5, ComArgs.Guid(optional: false)).ToGuest(host));
        i.Absent("RunControlPanel", 2, "it opens the host's control panel");
        i.Absent("Initialize", 2, "an object made by DirectInput8Create is already initialised");
        i.Add(new HostComMethod("FindDevice", 8, ComArgs.Guid(optional: false), ComArgs.AnsiString(optional: false), ComArgs.OutBytes(16)).ToGuest(host));
        i.Absent("EnumDevicesBySemantics", 5, "action maps (DIACTIONFORMAT) have no adapter row");
        i.Absent("ConfigureDevices", 4, "it opens the host's configuration UI");
        return i;
    }

    private static uint EnumDevices(GuestComCall c, HostComInterface host)
    {
        uint type = c.Arg(0), callback = c.Arg(1), context = c.Arg(2), flags = c.Arg(3);
        if (callback == 0) return ComHr.InvalidArg;
        var call = c; var mem = c.Mem;
        Exception? failure = null;
        IWin32Allocation? scratch = null;
        DevicesCallback native = (instance, _) =>
        {
            if (failure is not null) return 0;
            try
            {
                var size = *(uint*)instance;   // DIDEVICEINSTANCEA: no pointers, identical layouts
                if (size is < 4 or > 1024) throw new Win32UsageException($"DIDEVICEINSTANCEA from the host has size {size}.");
                scratch ??= mem.Allocate(1024);
                mem.Write(scratch.Address, new ReadOnlySpan<byte>((void*)instance, (int)size));
                return (int)call.CallGuest(callback, scratch.Address, context);
            }
            catch (Exception e) { failure = e; return 0; }
        };
        try
        {
            var hr = host.Invoke(4, (nint)type, Marshal.GetFunctionPointerForDelegate(native), 0, (nint)flags);
            GC.KeepAlive(native);
            if (failure is not null) throw new HostCallbackException("An IDirectInput8A::EnumDevices callback failed: " + failure.Message, failure);
            return hr;
        }
        finally { scratch?.Dispose(); }
    }

    // ---- IDirectInputDevice8A ----

    private static GuestComInterface Device()
    {
        var i = new GuestComInterface("IDirectInputDevice8A", IidDirectInputDevice8A);
        var host = HostOf(IidDirectInputDevice8A);
        const string ff = "force feedback has no adapter rows";
        i.Add(new HostComMethod("GetCapabilities", 3, Sized(64)).ToGuest(host));
        i.Absent("EnumObjects", 3, "the object enumeration callback has no adapter row yet");
        i.Add(new HostComMethod("GetProperty", 5, GuidOrId, Sized(256)).ToGuest(host));
        i.Add(new HostComMethod("SetProperty", 6, GuidOrId, Sized(256, inOut: false)).ToGuest(host));
        i.Add(new HostComMethod("Acquire", 7).ToGuest(host));
        i.Add(new HostComMethod("Unacquire", 8).ToGuest(host));
        i.Add(new GuestComMethod("GetDeviceState", 2, c =>
        {
            uint size = c.Arg(0), data = c.Arg(1);
            if (data == 0 || size == 0 || size > 65536) return ComHr.InvalidArg;
            return new HostComMethod("GetDeviceState", 9, ComArgs.Value, ComArgs.OutBytes((int)size)).Invoke(host(c.Object), c);
        }));
        i.Add(new GuestComMethod("GetDeviceData", 4, c => GetDeviceData(c, host(c.Object))));
        i.Add(new GuestComMethod("SetDataFormat", 1, c =>
        {
            if (c.Arg(0) == 0) return ComHr.InvalidArg;
            using var scope = new NativeScope();
            nint format;
            try { format = DataFormatToHost(c.Mem, scope, c.Arg(0)); }
            catch (ComArgumentException e) { c.Ctx.Options.DebugOutput?.Invoke("IDirectInputDevice8A::SetDataFormat: " + e.Message); return ComHr.InvalidArg; }
            var hr = host(c.Object).Invoke(11, format);
            WindowProcBridge.ThrowPendingCallbackFault();
            return hr;
        }));
        i.Add(new HostComMethod("SetEventNotification", 12, ComArgs.MustBeNull("hEvent (an event of the emulated kernel32 is not a host event)")).ToGuest(host));
        i.Add(new HostComMethod("SetCooperativeLevel", 13, HostArgs.Handle, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetObjectInfo", 14, Sized(1024), ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetDeviceInfo", 15, Sized(1024)).ToGuest(host));
        i.Absent("RunControlPanel", 2, "it opens the host's control panel");
        i.Absent("Initialize", 3, "a device made by CreateDevice is already initialised");
        i.Absent("CreateEffect", 4, ff);
        i.Absent("EnumEffects", 3, ff);
        i.Absent("GetEffectInfo", 2, ff);
        i.Absent("GetForceFeedbackState", 1, ff);
        i.Absent("SendForceFeedbackCommand", 1, ff);
        i.Absent("EnumCreatedEffectObjects", 3, ff);
        i.Absent("Escape", 1, "driver escapes (DIEFFESCAPE with a buffer pointer) have no adapter row");
        i.Add(new HostComMethod("Poll", 25).ToGuest(host));
        i.Absent("SendDeviceData", 4, "output device data has no adapter row");
        i.Absent("EnumEffectsInFile", 4, ff);
        i.Absent("WriteEffectToFile", 4, ff);
        i.Absent("BuildActionMap", 3, "action maps (DIACTIONFORMAT) have no adapter row");
        i.Absent("SetActionMap", 3, "action maps (DIACTIONFORMAT) have no adapter row");
        i.Absent("GetImageInfo", 1, "DIDEVICEIMAGEINFOHEADER holds pointers and has no adapter row");
        return i;
    }

    /// <summary>GetDeviceData(cbObjectData, rgdod, pdwInOut, flags): buffered input. The host fills 24-byte (or 16-byte DX3) records; the guest gets 20-byte (or 16-byte) records.</summary>
    private static uint GetDeviceData(GuestComCall c, HostComInterface host)
    {
        uint guestSize = c.Arg(0), records = c.Arg(1), inOut = c.Arg(2), flags = c.Arg(3);
        if (inOut == 0 || guestSize is not (GuestObjectData or ObjectDataDx3)) return ComHr.InvalidArg;
        var hostSize = guestSize == GuestObjectData ? HostObjectData : ObjectDataDx3;
        var count = c.Mem.ReadUInt32(inOut);
        if (records != 0 && count > 1 << 16) return ComHr.InvalidArg;
        using var scope = new NativeScope();
        var buffer = records == 0 ? 0 : scope.AllocZeroed((int)count * hostSize);
        var n = count;
        var hr = host.Invoke(10, (nint)hostSize, buffer, (nint)(&n), (nint)flags);
        WindowProcBridge.ThrowPendingCallbackFault();
        if (!ComHr.Succeeded(hr)) return hr;
        Span<byte> g = stackalloc byte[(int)guestSize];
        if (buffer != 0)
            for (uint i = 0; i < n; i++)
            {
                var at = buffer + (nint)(i * hostSize);
                new ReadOnlySpan<byte>((void*)at, 16).CopyTo(g);   // dwOfs, dwData, dwTimeStamp, dwSequence
                if (guestSize == GuestObjectData) BinaryPrimitives.WriteUInt32LittleEndian(g[16..], Win32Layouts.Value(*(nint*)(at + 16), "DIDEVICEOBJECTDATA.uAppData"));
                c.Mem.Write(records + i * guestSize, g);
            }
        c.Mem.WriteUInt32(inOut, n);
        return hr;
    }
}
