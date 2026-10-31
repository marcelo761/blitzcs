namespace Blitz3DCS.Windowing;

internal interface IWindowBackend : IDisposable
{
    int ClientWidth { get; }
    int ClientHeight { get; }
    bool HasFocus { get; }
    void PumpEvents(Action<WindowInputEvent> dispatch);
    void WarpMouse(float x, float y);
    uint TranslateScanCode(int scanCode, bool shift, bool ctrl, bool alt);
    nint GetWin32WindowHandle() => throw new PlatformNotSupportedException("This window backend does not expose a Win32 HWND.");
    void SetTitle(string title) => throw new NotSupportedException("This window backend cannot change the window title.");
    void SetSize(int width, int height) => throw new NotSupportedException("This window backend cannot resize the window.");
    void Show() => throw new NotSupportedException("This window backend cannot show the window.");
    void Hide() => throw new NotSupportedException("This window backend cannot hide the window.");
    void ShowPointer() => throw new NotSupportedException("This window backend cannot show the pointer.");
    void HidePointer() => throw new NotSupportedException("This window backend cannot hide the pointer.");
}

/// <summary>Owns a window and the polling keyboard/mouse behavior of legacy windowed mode 2.</summary>
public sealed class WindowHost : IDisposable
{
    private readonly IWindowBackend _backend;
    private readonly LegacyInputState _input = new();
    private readonly Action<WindowInputEvent> _dispatch;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed, _closeRequested, _hasFocus;
    private double _wheelRemainder;
    private const string Cp1252Special = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008dŽ\u008f\u0090‘’“”•–—˜™š›œ\u009džŸ";

    private readonly long _pollIntervalTicks;
    private readonly Func<long> _clock;
    private long _lastPump;
    private bool _pumped;

    /// <summary>Minimum wall time between SDL pumps triggered by input polls.</summary>
    internal const long DefaultPollIntervalMicros = 300;

    public WindowHost(int width, int height, string title) : this(new SdlWindowBackend(width, height, title), DefaultPollIntervalMicros) { }

    internal static WindowHost CreateHiddenApplicationWindow(string title) =>
        new(new SdlWindowBackend(1, 1, title, hidden: true), DefaultPollIntervalMicros);

    internal WindowHost(IWindowBackend backend, long pollIntervalMicros = 0, Func<long>? clock = null)
    {
        _clock = clock ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _pollIntervalTicks = clock is null ? pollIntervalMicros * System.Diagnostics.Stopwatch.Frequency / 1_000_000 : pollIntervalMicros;
        _backend = backend;
        _dispatch = ProcessEvent;
        _hasFocus = backend.HasFocus;
    }

    public bool CloseRequested { get { CheckAccess(); return _closeRequested; } }
    public bool HasFocus { get { CheckAccess(); return _hasFocus; } }
    public int ClientWidth { get { CheckAccess(); return _backend.ClientWidth; } }
    public int ClientHeight { get { CheckAccess(); return _backend.ClientHeight; } }

    /// <summary>Returns this window's native HWND for a Windows renderer sharing the SDL-owned window.</summary>
    public nint GetWin32WindowHandle() { CheckAccess(); return _backend.GetWin32WindowHandle(); }

    public void SetTitle(string title)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(title);
        _backend.SetTitle(title);
    }

    public void SetSize(int width, int height)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _backend.SetSize(width, height);
    }

    public void Show() { CheckAccess(); _backend.Show(); }
    public void Hide() { CheckAccess(); _backend.Hide(); }
    public void ShowPointer() { CheckAccess(); _backend.ShowPointer(); }
    public void HidePointer() { CheckAccess(); _backend.HidePointer(); }

    /// <summary>Always pumps (boundary calls: Flip/Delay/Flush/Wait/WindowOpen).</summary>
    public void PumpEvents()
    {
        CheckAccess();
        _backend.PumpEvents(_dispatch);
        if (_pollIntervalTicks > 0) { _lastPump = _clock(); _pumped = true; }
    }

    /// <summary>Pump for read-only polls: skipped when the previous pump is younger than the poll interval.</summary>
    private void PollPump()
    {
        if (_pollIntervalTicks > 0 && _pumped)
        {
            CheckAccess();
            if (_clock() - _lastPump < _pollIntervalTicks) return;
        }
        PumpEvents();
    }
    public int KeyDown(int scan) { PollPump(); return _input.KeyDown(scan); }
    public int KeyHit(int scan) { PollPump(); return _input.KeyHit(scan); }
    public void FlushKeys() { PumpEvents(); _input.FlushKeys(); }
    public int GetKey() { PollPump(); return Translate(_input.GetScanCode()); }
    public int MouseDown(int button) { PollPump(); return _input.MouseDown(button); }
    public int MouseHit(int button) { PollPump(); return _input.MouseHit(button); }
    public int GetMouse() { PollPump(); return _input.GetMouse(); }
    public void FlushMouse() { PumpEvents(); _input.FlushMouse(); }
    public int MouseX() { PollPump(); return _input.MouseX; }
    public int MouseY() { PollPump(); return _input.MouseY; }
    public int MouseZ() { PollPump(); return _input.MouseZ; }
    public int MouseXSpeed() { PollPump(); return _input.MouseXSpeed(); }
    public int MouseYSpeed() { PollPump(); return _input.MouseYSpeed(); }
    public int MouseZSpeed() { PollPump(); return _input.MouseZSpeed(); }

    public void MoveMouse(int x, int y)
    {
        PumpEvents();
        _input.MoveMouse(x, y);
        _backend.WarpMouse(x, y);
    }

    /// <summary>Waits for a translated key; closing or cancellation throws OperationCanceledException.</summary>
    public int WaitKey(CancellationToken cancellationToken = default) => Wait(true, cancellationToken);
    public int WaitMouse(CancellationToken cancellationToken = default) => Wait(false, cancellationToken);
    public int MouseWait(CancellationToken cancellationToken = default) => WaitMouse(cancellationToken);

    private int Wait(bool keyboard, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PumpEvents();
            if (_closeRequested) throw new OperationCanceledException("The input window was closed.", cancellationToken);
            var value = keyboard ? Translate(_input.GetScanCode()) : _input.GetMouse();
            if (value != 0) return value;
            if (cancellationToken.CanBeCanceled) cancellationToken.WaitHandle.WaitOne(20);
            else Thread.Sleep(20);
        }
    }

    internal void ProcessEvent(WindowInputEvent e)
    {
        CheckAccess();
        switch (e.Kind)
        {
            case WindowInputEventKind.KeyDown: _input.SetKey(LegacyScanCodes.FromSdl(e.ScanCode), true, e.Repeat); break;
            case WindowInputEventKind.KeyUp: _input.SetKey(LegacyScanCodes.FromSdl(e.ScanCode), false); break;
            case WindowInputEventKind.MouseDown: _input.SetMouseButton(MouseButton(e.Button), true); break;
            case WindowInputEventKind.MouseUp: _input.SetMouseButton(MouseButton(e.Button), false); break;
            case WindowInputEventKind.MouseMove:
                _input.SetMousePosition(ClampAxis(e.X, _backend.ClientWidth), ClampAxis(e.Y, _backend.ClientHeight));
                break;
            case WindowInputEventKind.MouseWheel:
                if (float.IsFinite(e.WheelY))
                {
                    var raw = _wheelRemainder + e.WheelY * 120d;
                    var units = (int)Math.Clamp(raw, int.MinValue, int.MaxValue);
                    _wheelRemainder = raw - units;
                    _input.AddMouseWheel(units);
                }
                break;
            case WindowInputEventKind.FocusLost: _hasFocus = false; break;
            case WindowInputEventKind.FocusGained: _hasFocus = true; break;
            case WindowInputEventKind.CloseRequested:
            case WindowInputEventKind.Quit: _closeRequested = true; break;
        }
    }

    private int Translate(int scan)
    {
        var navigation = scan switch { 199 => 1, 207 => 2, 210 => 3, 211 => 4, 201 => 5, 209 => 6,
            200 => 28, 208 => 29, 205 => 30, 203 => 31, _ => 0 };
        if (navigation != 0 || scan == 0) return navigation;
        // Original keypad add/subtract translate to ASCII regardless of NumLock state.
        if (scan == 78) return 43;
        if (scan == 74) return 45;
        var shift = _input.KeyDown(42) != 0 || _input.KeyDown(54) != 0;
        var ctrl = _input.KeyDown(29) != 0 || _input.KeyDown(157) != 0;
        var alt = _input.KeyDown(56) != 0 || _input.KeyDown(184) != 0;
        var code = _backend.TranslateScanCode(LegacyScanCodes.ToSdl(scan & 127), shift, ctrl, alt);
        if (ctrl && !alt)
        {
            if (code is >= 'a' and <= 'z') return (int)code - 'a' + 1;
            if (code is >= '@' and <= '_') return (int)code & 31;
        }
        if (code <= 127 || code is >= 160 and <= 255) return (int)code;
        if (code > char.MaxValue) return 0;
        var special = Cp1252Special.IndexOf((char)code);
        return special < 0 ? 0 : special + 128;
    }

    private static int MouseButton(int button) => button switch { 1 => 1, 3 => 2, 2 => 3, _ => 0 };
    private static int ClampAxis(float value, int size) => float.IsNaN(value) ? 0 : (int)Math.Clamp(value, 0, Math.Max(0, size - 1));
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Window input operations must run on the creating main thread.");
    }
    private void CheckAccess() { CheckThread(); ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _backend.Dispose();
        _disposed = true;
    }
}
