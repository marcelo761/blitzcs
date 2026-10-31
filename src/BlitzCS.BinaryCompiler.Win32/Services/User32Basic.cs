namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Host callback for MessageBoxA: returns the button identifier the host chose (IDOK = 1, IDCANCEL = 2, IDYES = 6, IDNO = 7, ...).</summary>
public delegate int Win32MessageBoxHandler(uint hwnd, string text, string caption, uint type);

/// <summary>
/// USER32 subset that is honest without a window system. There is no window manager: the only window that exists is the desktop, whose HWND is the documented
/// opaque sandbox handle <see cref="DesktopWindow"/> (0x00010010). Every API taking an HWND validates it: NULL or the desktop handle are accepted where the API allows,
/// anything else fails with ERROR_INVALID_WINDOW_HANDLE (1400). All other user32 APIs are unresolved, so a guest that uses an HWND for them faults naming the API.
/// MessageBoxA is a real function only when the host supplies <see cref="Win32Options.MessageBoxHandler"/>; otherwise it faults with user32!MessageBoxA - the component
/// never pretends the user clicked a button.
/// </summary>
/// <remarks>With the host window bridge (<see cref="HostUser32Service"/>, <paramref name="messageBoxOnly"/>) only MessageBoxA stays here: a modal box would block a headless run, so the
/// <see cref="Win32Options.MessageBoxHandler"/> policy is kept, and HWNDs are validated by the host's IsWindow.</remarks>
public sealed class User32BasicService(bool messageBoxOnly = false) : Win32Service
{
    public const uint DesktopWindow = 0x00010010;
    public const uint ErrorInvalidWindowHandle = 1400;

    public override string Name => "User32.Basic";

    protected override void Define()
    {
        const string U = "USER32.dll";
        if (!messageBoxOnly)
        {
            Std(U, "GetDesktopWindow", 0, _ => DesktopWindow);
            Std(U, "GetForegroundWindow", 0, _ => 0);   // no window system, so no window has the focus: NULL is the real answer
            Std(U, "IsWindow", 1, c => c[0] == DesktopWindow ? 1u : 0u);
            Std(U, "IsWindowVisible", 1, c => c[0] == DesktopWindow ? 1u : c.Ctx.Fail(ErrorInvalidWindowHandle));
        }
        Std(U, "MessageBoxA", 4, c =>
        {
            var handler = c.Ctx.Options.MessageBoxHandler
                ?? throw new Win32UsageException("user32!MessageBoxA was called but Win32Options.MessageBoxHandler is not set: this component has no window system and will not pretend the user answered.");
            var valid = c[0] == 0 || (messageBoxOnly ? HostBridge.HostInvoke.Call(HostBridge.HostExports.Get("user32.dll", "IsWindow"), [HostBridge.HostHandles.ToHost(c[0])]) != 0 : c[0] == DesktopWindow);
            if (!valid) return c.Ctx.Fail(ErrorInvalidWindowHandle);
            var text = c[1] == 0 ? "" : c.Mem.ReadAnsiString(c[1], 65536);
            var caption = c[2] == 0 ? "Error" : c.Mem.ReadAnsiString(c[2], 65536); // a NULL caption shows "Error"
            return (uint)handler(c[0], text, caption, c[3]);
        });
    }
}
