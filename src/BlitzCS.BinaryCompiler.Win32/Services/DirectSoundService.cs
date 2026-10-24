using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32.Com;
using static BlitzCS.BinaryCompiler.Win32.DirectSoundAdapters;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// dsound.dll: <c>DirectSoundCreate</c> and <c>DirectSoundEnumerateA</c>, reachable both as static imports and through GetProcAddress (which hands out a dynamic stub of the guest module).
/// Both go to the HOST's native dsound.dll (Windows or Wine, see <see cref="DirectSoundAdapters"/>); on a host without one (Linux, or Win32Options.DirectSound = None) they answer the real
/// DSERR_NODRIVER (0x88780078) with *ppDS cleared and call no enumeration callback. DirectSoundCreate8, DirectSoundCapture* and DirectSoundFullDuplexCreate are not exported (GetProcAddress
/// returns NULL for them, as on a system that lacks them): IDirectSound8 and capture are not adapted.
/// Requires the guest module to have been translated with a dynamic stub pool (the COM objects' vtables are stubs).
/// </summary>
public sealed unsafe class DirectSoundService : Win32Service
{
    public override string Name => "DirectSound";
    private const string D = "dsound.dll";

    private static GuestComInterface? device;
    private static GuestComInterface Device => device ??= DeviceInterface();

    protected override void Define()
    {
        Std(D, "DirectSoundCreate", 3, Create);
        Std(D, "DirectSoundEnumerateA", 2, Enumerate);
    }

    private static uint Create(Win32Call c)
    {
        uint guidPtr = c[0], outPtr = c[1], outer = c[2];
        if (outPtr == 0) return DsConst.DsErrInvalidParam;
        c.Mem.WriteUInt32(outPtr, 0);
        if (outer != 0) return DsConst.DsErrNoAggregation;
        if (!DsHost.IsAvailable(c.Ctx.Options)) return DsConst.DsErrNoDriver;
        var module = GuestModule.Current ?? throw new Win32UsageException("dsound!DirectSoundCreate needs a running guest module (its COM objects live in the guest's memory).");
        var com = GuestComHost.For(c.Ctx, module);
        using var scope = new NativeScope();
        var nativeGuid = guidPtr == 0 ? 0 : scope.Copy(c.Mem.ReadBytes(guidPtr, 16));
        nint ds;
        int hr;
        try { hr = DsNative.DirectSoundCreate(nativeGuid, out ds, 0); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            c.Ctx.Options.DebugOutput?.Invoke("DirectSoundCreate: the host has no usable dsound.dll (" + e.Message + ")");
            return DsConst.DsErrNoDriver;
        }
        if (hr < 0) return (uint)hr;
        if (ds == 0) return ComHr.Fail;
        var host = new HostComInterface(ds, DeviceName);
        var state = new DsDevice(host);
        var obj = com.Create(DeviceName, state, Device, o => state.GuestReleased());
        c.Mem.WriteUInt32(outPtr, obj.Address);
        return ComHr.Ok;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumCallback(nint guid, nint description, nint module, nint context);

    private static uint Enumerate(Win32Call c)
    {
        uint callback = c[0], context = c[1];
        if (callback == 0) return DsConst.DsErrInvalidParam;
        if (!DsHost.IsAvailable(c.Ctx.Options)) return DsConst.DsErrNoDriver;
        var callGuest = c.GuestCaller();
        var mem = c.Mem;
        Exception? failure = null;
        // The host calls back on this same thread, inside DirectSoundEnumerateA, so the guest can be entered from here.
        EnumCallback native = (guid, description, module, ctx) =>
        {
            try
            {
                static byte[] CString(nint p) => p == 0 ? [0] : [.. Marshal.PtrToStringAnsi(p) is { } s ? System.Text.Encoding.Latin1.GetBytes(s) : [], (byte)0];
                var desc = CString(description); var mod = CString(module);
                using var scratch = mem.Allocate(16 + desc.Length + mod.Length);
                var at = scratch.Address;
                uint guestGuid = 0;
                if (guid != 0) { guestGuid = at; mem.Write(at, new ReadOnlySpan<byte>((void*)guid, 16)); }
                var descAt = at + 16; var modAt = descAt + (uint)desc.Length;
                mem.Write(descAt, desc); mem.Write(modAt, mod);
                return callGuest(callback, [guestGuid, descAt, modAt, context]) != 0 ? 1 : 0;
            }
            catch (Exception e) { failure ??= e; return 0; }
        };
        var pointer = Marshal.GetFunctionPointerForDelegate(native);
        int hr;
        try { hr = DsNative.DirectSoundEnumerateA(pointer, 0); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return DsConst.DsErrNoDriver; }
        GC.KeepAlive(native);
        if (failure is not null) throw new Win32UsageException("A DirectSoundEnumerate callback failed: " + failure.Message);
        return (uint)hr;
    }
}
