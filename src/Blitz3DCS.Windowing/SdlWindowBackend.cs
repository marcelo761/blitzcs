using System.Reflection;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Windowing;

public enum WindowInputEventKind
{
    KeyDown, KeyUp, MouseMove, MouseDown, MouseUp, MouseWheel,
    FocusGained, FocusLost, CloseRequested, Quit, TextInput
}

/// <summary>Window input in SDL physical scancodes, SDL button indices and client coordinates.</summary>
public readonly record struct WindowInputEvent(
    WindowInputEventKind Kind, int ScanCode = 0, int Button = 0,
    float X = 0, float Y = 0, float WheelX = 0, float WheelY = 0,
    bool Repeat = false, string? Text = null);

/// <summary>
/// A single SDL3 window and process event pump. Construct and use on the application's main
/// thread. Owns video/events subsystem references independently of SDL audio.
/// </summary>
public sealed class SdlWindowBackend : IWindowBackend
{
    private static readonly object LifecycleLock = new();
    private static bool _active;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private nint _window;
    private bool _pumping;
    private const uint Subsystems = 0x20 | 0x4000; // SDL_INIT_VIDEO | SDL_INIT_EVENTS

    public uint WindowId { get { CheckAccess(); return _windowId; } }
    public int ClientWidth { get { CheckAccess(); return _width; } }
    public int ClientHeight { get { CheckAccess(); return _height; } }
    public bool HasFocus { get { CheckAccess(); return _hasFocus; } }
    private readonly uint _windowId;
    private int _width, _height;
    private bool _hasFocus;

    static SdlWindowBackend() => NativeLibrary.SetDllImportResolver(typeof(SdlWindowBackend).Assembly, ResolveLibrary);

    public SdlWindowBackend(int width, int height, string title)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(title);
        lock (LifecycleLock)
        {
            if (_active) throw new InvalidOperationException("Only one SDL window event host may be active in this process.");
            if (!Native.SDL_InitSubSystem(Subsystems)) throw Error("Initialize SDL video/events");
            try
            {
                _window = Native.SDL_CreateWindow(title, width, height, 0);
                if (_window == 0) throw Error("Create SDL window");
                _windowId = Native.SDL_GetWindowID(_window);
                if (_windowId == 0) throw Error("Get SDL window ID");
                if (!Native.SDL_GetWindowSize(_window, out _width, out _height)) throw Error("Get SDL window size");
                _hasFocus = (Native.SDL_GetWindowFlags(_window) & 0x200) != 0;
                _active = true;
            }
            catch
            {
                if (_window != 0) Native.SDL_DestroyWindow(_window);
                _window = 0;
                Native.SDL_QuitSubSystem(Subsystems);
                throw;
            }
        }
    }

    /// <summary>Drains SDL's process queue; dispatches only this window's supported events and global quit.</summary>
    public void PumpEvents(Action<WindowInputEvent> dispatch)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(dispatch);
        if (_pumping) throw new InvalidOperationException("The SDL event pump cannot be reentered.");
        _pumping = true;
        try
        {
            while (Native.SDL_PollEvent(out var e))
            {
                if (e.Type == 0x100) { dispatch(new(WindowInputEventKind.Quit)); }
                else if (e.WindowId == _windowId)
                {
                    switch (e.Type)
                    {
                        case 0x206: _width = e.Data1; _height = e.Data2; break;
                        case 0x20e: _hasFocus = true; dispatch(new(WindowInputEventKind.FocusGained)); break;
                        case 0x20f: _hasFocus = false; dispatch(new(WindowInputEventKind.FocusLost)); break;
                        case 0x210: dispatch(new(WindowInputEventKind.CloseRequested)); break;
                        case 0x300: dispatch(new(WindowInputEventKind.KeyDown, ScanCode: e.ScanCode, Repeat: e.Repeat != 0)); break;
                        case 0x301: dispatch(new(WindowInputEventKind.KeyUp, ScanCode: e.ScanCode)); break;
                        case 0x303: dispatch(new(WindowInputEventKind.TextInput, Text: Marshal.PtrToStringUTF8(e.Text.Text))); break;
                        case 0x400: dispatch(new(WindowInputEventKind.MouseMove, X: e.X, Y: e.Y)); break;
                        case 0x401: dispatch(new(WindowInputEventKind.MouseDown, Button: e.Button, X: e.X, Y: e.Y)); break;
                        case 0x402: dispatch(new(WindowInputEventKind.MouseUp, Button: e.Button, X: e.X, Y: e.Y)); break;
                        case 0x403:
                            var sign = e.WheelDirection == 1 ? -1 : 1;
                            dispatch(new(WindowInputEventKind.MouseWheel, X: e.WheelMouseX, Y: e.WheelMouseY,
                                WheelX: e.WheelX * sign, WheelY: e.WheelY * sign));
                            break;
                    }
                }
                // A callback may dispose this backend; never poll the subsystem again afterward.
                if (_window == 0) break;
            }
        }
        finally { _pumping = false; }
    }

    /// <summary>Translates using the active SDL keyboard layout; lock modifiers are intentionally omitted.</summary>
    public uint TranslateScanCode(int scanCode, bool shift, bool ctrl, bool alt)
    {
        CheckAccess();
        ushort modifiers = (ushort)((shift ? 0x0001 : 0) | (ctrl ? 0x0040 : 0) | (alt ? 0x0100 : 0));
        return Native.SDL_GetKeyFromScancode(scanCode, modifiers, false);
    }

    public (float X, float Y) GetMousePosition()
    {
        CheckAccess();
        Native.SDL_GetMouseState(out var x, out var y);
        return (x, y);
    }

    public void WarpMouse(float x, float y)
    {
        CheckAccess();
        if (!float.IsFinite(x) || !float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x), "Mouse coordinates must be finite.");
        Native.SDL_WarpMouseInWindow(_window, x, y);
    }

    public void StartTextInput()
    {
        CheckAccess();
        if (!Native.SDL_StartTextInput(_window)) throw Error("Start SDL text input");
    }

    public void StopTextInput()
    {
        CheckAccess();
        if (!Native.SDL_StopTextInput(_window)) throw Error("Stop SDL text input");
    }

    public void Dispose()
    {
        CheckThread();
        if (_window == 0) return;
        lock (LifecycleLock)
        {
            Native.SDL_DestroyWindow(_window);
            _window = 0;
            Native.SDL_QuitSubSystem(Subsystems);
            _active = false;
        }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("SDL window operations must run on the creating main thread.");
    }

    private void CheckAccess() { CheckThread(); ObjectDisposedException.ThrowIf(_window == 0, this); }
    private static InvalidOperationException Error(string operation) =>
        new($"{operation}: {Marshal.PtrToStringUTF8(Native.SDL_GetError()) ?? "Unknown SDL3 error."}");

    private static nint ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "BlitzCS.SDL3.Windowing") return 0;
        string library = OperatingSystem.IsWindows() ? "SDL3.dll" :
            OperatingSystem.IsMacOS() ? "libSDL3.0.dylib" : "libSDL3.so.0";
        return NativeLibrary.Load(library, assembly, path);
    }

    // SDL3 SDL_Event is an ABI-stable 128-byte union. Explicit overlaps mirror SDL_events.h;
    // text uses native pointer alignment (offset 24 on 64-bit; 20 on 32-bit).
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextEvent
    {
        public uint Type, Reserved;
        public ulong Timestamp;
        public uint WindowId;
        public nint Text;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    internal struct SdlEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ulong Timestamp;
        [FieldOffset(16)] public uint WindowId;
        [FieldOffset(20)] public int Data1;
        [FieldOffset(24)] public int Data2;
        [FieldOffset(24)] public int ScanCode;
        [FieldOffset(37)] public byte Repeat;
        [FieldOffset(24)] public byte Button;
        [FieldOffset(28)] public float X;
        [FieldOffset(32)] public float Y;
        [FieldOffset(24)] public float WheelX;
        [FieldOffset(28)] public float WheelY;
        [FieldOffset(32)] public uint WheelDirection;
        [FieldOffset(36)] public float WheelMouseX;
        [FieldOffset(40)] public float WheelMouseY;
        [FieldOffset(0)] public TextEvent Text;
    }

    private static class Native
    {
        private const string Library = "BlitzCS.SDL3.Windowing";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_InitSubSystem(uint flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_QuitSubSystem(uint flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int width, int height, ulong flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyWindow(nint window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetWindowID(nint window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SDL_GetWindowFlags(nint window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GetWindowSize(nint window, out int width, out int height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_PollEvent(out SdlEvent e);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_WarpMouseInWindow(nint window, float x, float y);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_StartTextInput(nint window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_StopTextInput(nint window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint SDL_GetError();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetKeyFromScancode(int scanCode, ushort modifiers, [MarshalAs(UnmanagedType.I1)] bool keyEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetMouseState(out float x, out float y);
    }
}
