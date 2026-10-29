using System.Globalization;
using System.Runtime.InteropServices;
using BlitzCS.LegacyRuntime;
using Blitz3DCS.Runtime;
using Blitz3DCS.Windowing;

namespace Blitz3DCS.Engine.Windows;

/// <summary>Legacy display and application-window commands backed by SDL3 and Win32.</summary>
public static partial class LegacyGraphics
{
    private static readonly object DisplayModesGate = new();
    private static SdlDisplayModeInfo[]? _displayModes;
    private static string _applicationTitle = "Blitz3DCS";
    private static string _applicationErrorTitle = "";

    /// <summary>Initial title for the persistent application window.</summary>
    internal static string InitialWindowTitle => _applicationTitle;
    internal static string ApplicationErrorTitle => _applicationErrorTitle;

    /// <summary>Sets the application title; the optional error title is retained for legacy compatibility.</summary>
    public static void AppTitle(string title, string errorTitle = "")
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(errorTitle);
        _applicationTitle = title;
        _applicationErrorTitle = errorTitle;
        ApplicationWindowBootstrap.TrySetWindowTitle(title);
    }

    /// <summary>Returns the available graphics backends implemented by this runtime.</summary>
    public static int CountGfxDrivers() => Blitz3DCS.Graphics.D3D11.D3D11Capabilities.SupportsWindowed3D ? 1 : 0;

    /// <summary>Returns the one-based name of an available graphics backend.</summary>
    public static string GfxDriverName(int driver)
    {
        if (CountGfxDrivers() == 0 || driver != 1)
            throw new ArgumentOutOfRangeException(nameof(driver), driver, "Graphics driver index must be 1 when a D3D11 backend is available.");
        return "Direct3D 11";
    }

    /// <summary>Hides the SDL cursor, retaining the request until the application window is created.</summary>
    public static void HidePointer() => LegacyInput.HidePointer();

    /// <summary>Shows the SDL cursor, retaining the request until the application window is created.</summary>
    public static void ShowPointer() => LegacyInput.ShowPointer();

    /// <summary>Returns the number of real fullscreen modes reported for the primary display.</summary>
    public static int CountGfxModes3D() => DisplayModes().Length;

    /// <summary>Returns a one-based fullscreen mode width, preserving SDL's reported mode order.</summary>
    public static int GfxModeWidth(int mode) => DisplayModes()[ValidateDisplayMode(mode)].Width;

    /// <summary>Returns a one-based fullscreen mode height, preserving SDL's reported mode order.</summary>
    public static int GfxModeHeight(int mode) => DisplayModes()[ValidateDisplayMode(mode)].Height;

    private static SdlDisplayModeInfo[] DisplayModes()
    {
        lock (DisplayModesGate) return _displayModes ??= SdlWindowBackend.GetPrimaryDisplayModes();
    }

    private static int ValidateDisplayMode(int mode)
    {
        var modes = DisplayModes();
        if (mode <= 0 || mode > modes.Length)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Display mode index must be between 1 and {modes.Length}.");
        return mode - 1;
    }
}

/// <summary>Host-specific system property and Win32 user-library commands used by legacy scripts.</summary>
public static class LegacyWindowBindings
{
    private static readonly object HandlesGate = new();
    private static readonly Dictionary<int, nint> Handles = [];

    /// <summary>Returns the actual stable SDL-owned application HWND as a decimal string.</summary>
    public static string SystemProperty(string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!string.Equals(property, "AppHWND", StringComparison.OrdinalIgnoreCase))
            return LegacySystem.SystemProperty(property);
        EnsureWindows();
        var window = ApplicationWindowBootstrap.EnsureApplicationWindow(LegacyGraphics.InitialWindowTitle);
        var hwnd = window.GetWin32WindowHandle();
        RegisterHandle(hwnd);
        return hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the real desktop HWND encoded as the legacy 32-bit handle token.</summary>
    public static int api_GetDesktopWindow()
    {
        EnsureWindows();
        var hwnd = Native.GetDesktopWindow();
        return RegisterHandle(hwnd);
    }

    /// <summary>Returns the keyboard-focus HWND of the calling thread's queue as a legacy handle token, or 0.</summary>
    /// <remarks>The application window is owned and pumped by the BASIC thread, so focus elsewhere yields 0.</remarks>
    public static int api_GetFocus()
    {
        EnsureWindows();
        return RegisterHandle(Native.GetFocus());
    }

    /// <summary>Writes a native RECT into the payload of a Blitz bank, never its BBBank header.</summary>
    public static int api_GetClientRect(int hwnd, int rectBank)
    {
        EnsureWindows();
        var nativeHwnd = ResolveHandle(hwnd);
        return LegacyBanks.WithPointer(rectBank, 0, 16, (pointer, _) =>
        {
            if (!Native.GetClientRect(nativeHwnd, out var rect)) return 0;
            Marshal.WriteInt32(pointer, 0, rect.Left);
            Marshal.WriteInt32(pointer, 4, rect.Top);
            Marshal.WriteInt32(pointer, 8, rect.Right);
            Marshal.WriteInt32(pointer, 12, rect.Bottom);
            return 1;
        }, alignment: 4);
    }

    public static int api_SetWindowLong(int hwnd, int index, int value)
    {
        EnsureWindows();
        var nativeHwnd = ResolveHandle(hwnd);
        nint previous = IntPtr.Size == 8
            ? Native.SetWindowLongPtr64(nativeHwnd, index, (nint)value)
            : (nint)Native.SetWindowLong32(nativeHwnd, index, value);
        return unchecked((int)previous.ToInt64());
    }

    public static int api_SetWindowPos(int hwnd, int insertAfter, int x, int y, int width, int height, int flags)
    {
        EnsureWindows();
        var nativeHwnd = ResolveHandle(hwnd);
        var nativeInsertAfter = insertAfter is 0 or 1 or -1 or -2 ? (nint)insertAfter : ResolveHandle(insertAfter);
        return Native.SetWindowPos(nativeHwnd, nativeInsertAfter, x, y, width, height, unchecked((uint)flags)) ? 1 : 0;
    }

    private static int RegisterHandle(nint handle)
    {
        if (handle == 0) return 0;
        var token = unchecked((int)handle.ToInt64());
        lock (HandlesGate)
        {
            if (Handles.TryGetValue(token, out var existing) && existing != handle)
                throw new InvalidOperationException("Two live native window handles share the same 32-bit legacy token.");
            Handles[token] = handle;
        }
        return token;
    }

    private static nint ResolveHandle(int token)
    {
        lock (HandlesGate)
            if (Handles.TryGetValue(token, out var handle)) return handle;
        // Legacy user32 declarations carry 32-bit integers even in a 64-bit process. Preserve
        // arbitrary Win32 sentinel/handle bit patterns by zero-extending unknown tokens.
        return unchecked((nint)(nuint)(uint)token);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This command requires the Windows window host.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static class Native
    {
        [DllImport("user32.dll", ExactSpelling = true)] public static extern nint GetDesktopWindow();
        [DllImport("user32.dll", ExactSpelling = true)] public static extern nint GetFocus();
        [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
        public static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
        public static extern int SetWindowLong32(nint hwnd, int index, int value);
        [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    }
}
