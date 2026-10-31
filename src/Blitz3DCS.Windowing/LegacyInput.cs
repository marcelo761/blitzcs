namespace Blitz3DCS.Windowing;

/// <summary>Historical keyboard/mouse bindings backed by an explicitly owned SDL window.</summary>
/// <remarks>OpenWindow is a host bootstrap extension, not an implementation of Graphics3D.</remarks>
public static class LegacyInput
{
    private static WindowHost? _host;
    private static bool _applicationWindow;
    private static bool _pointerVisible = true;
    private static WindowHost Host => _host ?? throw new InvalidOperationException(
        "Input requires an active window. Open the Blitz3DCS window host first.");

    /// <summary>The currently open SDL window, for renderer components that must share its native window.</summary>
    public static WindowHost CurrentWindow
    {
        get
        {
            var host = Host;
            _ = host.CloseRequested; // Validate that the window is live and accessed from its owning thread.
            return host;
        }
    }

    public static void OpenWindow(int width, int height, string title)
    {
        if (_host is not null) throw new InvalidOperationException("A window host is already open.");
        _host = new WindowHost(width, height, title);
        _applicationWindow = false;
        ApplyPointerVisibility();
    }

    public static void CloseWindow()
    {
        if (_host is null) return;
        _host.Dispose();
        _host = null;
        _applicationWindow = false;
    }

    internal static WindowHost EnsureApplicationWindow(string title)
    {
        if (_host is null)
        {
            _host = WindowHost.CreateHiddenApplicationWindow(title);
            _applicationWindow = true;
            ApplyPointerVisibility();
        }
        return _host;
    }

    internal static void HideApplicationWindow()
    {
        if (_host is not null && _applicationWindow) _host.Hide();
    }

    internal static bool TrySetWindowTitle(string title)
    {
        if (_host is null) return false;
        _host.SetTitle(title);
        return true;
    }

    public static void PumpEvents() => Host.PumpEvents();
    public static int WindowOpen()
    {
        if (_host is null) return 0;
        _host.PumpEvents();
        return _host.CloseRequested ? 0 : 1;
    }
    public static int WindowFocused() { Host.PumpEvents(); return Host.HasFocus ? 1 : 0; }
    public static int KeyDown(int key) => Host.KeyDown(key);
    public static int KeyHit(int key) => Host.KeyHit(key);
    public static int GetKey() => Host.GetKey();
    public static int WaitKey() => Host.WaitKey();
    public static void FlushKeys() => Host.FlushKeys();
    public static int MouseDown(int button) => Host.MouseDown(button);
    public static int MouseHit(int button) => Host.MouseHit(button);
    public static int GetMouse() => Host.GetMouse();
    public static int WaitMouse() => Host.WaitMouse();
    public static int MouseWait() => Host.WaitMouse();
    public static int MouseX() => Host.MouseX();
    public static int MouseY() => Host.MouseY();
    public static int MouseZ() => Host.MouseZ();
    public static int MouseXSpeed() => Host.MouseXSpeed();
    public static int MouseYSpeed() => Host.MouseYSpeed();
    public static int MouseZSpeed() => Host.MouseZSpeed();
    public static void FlushMouse() => Host.FlushMouse();
    public static void MoveMouse(int x, int y) => Host.MoveMouse(x, y);

    public static void HidePointer()
    {
        _pointerVisible = false;
        _host?.HidePointer();
    }

    public static void ShowPointer()
    {
        _pointerVisible = true;
        _host?.ShowPointer();
    }

    private static void ApplyPointerVisibility()
    {
        if (_pointerVisible) _host!.ShowPointer();
        else _host!.HidePointer();
    }
}

/// <summary>Host lifecycle helpers for the shared application window; not BASIC command bindings.</summary>
public static class ApplicationWindowBootstrap
{
    public static WindowHost EnsureApplicationWindow(string title = "Blitz3DCS") => LegacyInput.EnsureApplicationWindow(title);
    public static void HideApplicationWindow() => LegacyInput.HideApplicationWindow();
    public static bool TrySetWindowTitle(string title) => LegacyInput.TrySetWindowTitle(title);
}
