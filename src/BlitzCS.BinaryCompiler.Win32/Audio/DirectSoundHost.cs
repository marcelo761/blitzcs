using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Win32.Com;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Which DirectSound implementation a <see cref="Win32Context"/> reaches. <see cref="Default"/>: the host's native dsound.dll on a Windows host (including Wine), none elsewhere.</summary>
public enum DirectSoundBackendKind { Default, Host, None }

/// <summary>dsound.h constants (HRESULTs are the real facility-0x878 values).</summary>
public static class DsConst
{
    public const uint DsErrNoDriver = 0x88780078, DsErrInvalidCall = 0x88780032, DsErrBufferLost = 0x88780096, DsErrPrioLevelNeeded = 0x88780046,
        DsErrBadFormat = 0x88780064, DsErrUninitialized = 0x887800AA, DsErrAllocated = 0x8878000A, DsErrControlUnavail = 0x8878001E,
        DsErrNoAggregation = 0x80040110, DsErrUnsupported = 0x80004001, DsErrInvalidParam = 0x80070057, DsErrOutOfMemory = 0x8007000E;

    public const uint DsbcapsPrimaryBuffer = 0x1, DsbcapsCtrl3D = 0x10, DsbcapsCtrlFrequency = 0x20, DsbcapsCtrlPan = 0x40, DsbcapsCtrlVolume = 0x80,
        DsbcapsCtrlPositionNotify = 0x100, DsbcapsGetCurrentPosition2 = 0x10000, DsbcapsGlobalFocus = 0x8000, DsbcapsStickyFocus = 0x4000,
        DsbcapsLocSoftware = 0x8, DsbcapsLocHardware = 0x4;
    public const uint DsblockFromWriteCursor = 1, DsblockEntireBuffer = 2;
    public const uint DsbstatusBufferLost = 0x2;
    public const uint DsSclNormal = 1, DsSclPriority = 2, DsSclExclusive = 3, DsSclWritePrimary = 4;

    public static readonly Guid IidDirectSound = new("279AFA83-4981-11CE-A521-0020AF0BE560");
    public static readonly Guid IidDirectSoundBuffer = new("279AFA85-4981-11CE-A521-0020AF0BE560");
}

/// <summary>P/Invoke surface of the host's dsound.dll / user32.dll. Only reachable on a Windows host (the entry points are resolved on first call).</summary>
internal static class DsNative
{
    [DllImport("dsound.dll", EntryPoint = "DirectSoundCreate")]
    public static extern int DirectSoundCreate(nint guid, out nint directSound, nint outer);

    [DllImport("dsound.dll", EntryPoint = "DirectSoundEnumerateA")]
    public static extern int DirectSoundEnumerateA(nint callback, nint context);

    [DllImport("user32.dll", EntryPoint = "GetDesktopWindow")]
    public static extern nint GetDesktopWindow();
}

/// <summary>
/// Decides whether DirectSound is available and maps guest window handles to host windows.
///
/// HWND: the guest's windows are the component's opaque sandbox handles (there is no window system behind them). A guest HWND of <see cref="User32BasicService.DesktopWindow"/> (GetDesktopWindow)
/// is mapped to the host's real desktop window, which dsound accepts as the focus window of SetCooperativeLevel (verified under Wine, docs/binary-com.md). NULL stays NULL (the host answers
/// for it) and any other value is not a window this component created: the call answers E_INVALIDARG instead of handing the host a meaningless handle.
/// </summary>
internal static class DsHost
{
    public static bool IsAvailable(Win32Options options)
    {
        if (options.DirectSound == DirectSoundBackendKind.None) return false;
        if (OperatingSystem.IsWindows()) return true;
        if (options.DirectSound == DirectSoundBackendKind.Host) throw new Win32UsageException("Win32Options.DirectSound = Host needs a Windows host (Windows or Wine).");
        return false;
    }

    public static nint MapWindow(Win32Options options, uint guestHwnd)
    {
        if (guestHwnd == 0) return 0;
        if (options.UsesHostWindowing)
        {
            // with the window bridge the guest's HWNDs are the host's own (32-bit significant, sign-extended)
            var host = HostBridge.HostHandles.ToHost(guestHwnd);
            return HostBridge.HostInvoke.Call(HostBridge.HostExports.Get("user32.dll", "IsWindow"), [host]) != 0 ? host
                : throw new ComArgumentException($"HWND 0x{guestHwnd:X8} is not a window of the host.");
        }
        if (guestHwnd == User32BasicService.DesktopWindow) return DsNative.GetDesktopWindow();
        throw new ComArgumentException($"HWND 0x{guestHwnd:X8} is not a window of this component (only the desktop window and NULL exist).");
    }
}

/// <summary>Read-only access for tests and host tools to the native interface behind a guest DirectSound object (the objects <c>DirectSoundCreate</c> / <c>CreateSoundBuffer</c> returned).</summary>
public static class DirectSoundInspect
{
    /// <summary>The host's native IDirectSound / IDirectSoundBuffer behind <paramref name="obj"/>, or null when the object is not a DirectSound object.</summary>
    public static HostComInterface? HostInterface(GuestComObject obj) => obj.State switch
    {
        DirectSoundAdapters.DsDevice d => d.Host,
        DirectSoundAdapters.DsBuffer b => b.Host,
        _ => null,
    };

    /// <summary>Size of the buffer's guest-side shadow in bytes (0 before the first Lock).</summary>
    public static int ShadowBytes(GuestComObject obj) => obj.State is DirectSoundAdapters.DsBuffer { Shadow: { } s } ? s.Length : 0;
}
