using System.Text;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// GDI32 bridged to the HOST's gdi32.dll (see <see cref="HostUser32Service"/>; registered only on a Windows host). GDI handles are 32-bit significant and sign-extended; TEXTMETRICA, SIZE,
/// ABC and RECT have identical 32/64-bit layouts and are copied; counted strings are copied with their length argument. Font files (AddFontResourceA/RemoveFontResourceA) are translated
/// into the sandbox before passing to the host.
/// </summary>
public sealed class HostGdi32Service : Win32Service
{
    public override string Name => "Gdi32.Host";
    private const string G = "GDI32.dll", H = "gdi32.dll";

    private void Bridge(string name, HostResult result, params ComArg[] args) => Bridge(new HostFunction(H, name, result, args));
    private void Bridge(HostFunction f) => Std(G, f.Name, f.Args.Count, f.Invoke);

    protected override void Define()
    {
        var always = (Func<nint, bool>)(_ => true);
        var h = HostArgs.Handle;
        Bridge("GetStockObject", HostResult.Handle, ComArgs.Signed);
        Bridge("CreateCompatibleDC", HostResult.Handle, h);
        Bridge("DeleteDC", HostResult.Int32, h);
        Bridge("CreateFontA", HostResult.Handle, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed, ComArgs.Signed,
            ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.Value, ComArgs.AnsiString(optional: true));
        Bridge(new HostFunction(H, "SelectObject", HostResult.Handle, h, h) { Succeeded = always });
        Bridge("DeleteObject", HostResult.Int32, h);
        Bridge(new HostFunction(H, "SetTextColor", HostResult.Int32, h, ComArgs.Value) { Succeeded = always });
        Bridge(new HostFunction(H, "SetBkColor", HostResult.Int32, h, ComArgs.Value) { Succeeded = always });
        Bridge("GetTextMetricsA", HostResult.Int32, h, ComArgs.OutBytes(56));
        Bridge("GetTextExtentPoint32A", HostResult.Int32, h, HostArgs.CountedAnsi(2), ComArgs.Signed, ComArgs.OutBytes(8));
        Bridge("GetCharABCWidthsA", HostResult.Int32, h, ComArgs.Value, ComArgs.Value, HostArgs.OutArray(a => (int)(a[2] - a[1] + 1), 12));
        Bridge("ExtTextOutA", HostResult.Int32, h, ComArgs.Signed, ComArgs.Signed, ComArgs.Value, ComArgs.InStruct(16, optional: true), HostArgs.CountedAnsi(6), ComArgs.Value, HostArgs.CountedArray(6, 4));
        Std(G, "AddFontResourceA", 1, AddFontResourceA);
        Std(G, "RemoveFontResourceA", 1, RemoveFontResourceA);
    }

    private static uint AddFontResourceA(Win32Call c)
    {
        if (c[0] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var path = c.Mem.ReadAnsiString(c[0], 1024);
        var (host, error) = c.Ctx.Files.Translate(path);
        if (host is null) return c.Ctx.Fail(error);
        using var scope = new NativeScope();
        var hostPath = scope.Copy(Encoding.Unicode.GetBytes(host + "\0"));
        return (uint)HostFunction.CallNative(c.Ctx, HostExports.Get(H, "AddFontResourceW"), [hostPath]);
    }

    private static uint RemoveFontResourceA(Win32Call c)
    {
        if (c[0] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var path = c.Mem.ReadAnsiString(c[0], 1024);
        var (host, error) = c.Ctx.Files.Translate(path);
        if (host is null) return c.Ctx.Fail(error);
        using var scope = new NativeScope();
        var hostPath = scope.Copy(Encoding.Unicode.GetBytes(host + "\0"));
        return (uint)HostFunction.CallNative(c.Ctx, HostExports.Get(H, "RemoveFontResourceW"), [hostPath]);
    }
}
