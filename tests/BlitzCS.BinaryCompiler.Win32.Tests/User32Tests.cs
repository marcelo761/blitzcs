using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>The sandbox user32 subset (HostWindowing = None; the default on a non-Windows host). The host bridge has its own tests (HostWindowBridgeTests).</summary>
public sealed class User32Tests
{
    private const string U = "USER32.dll";
    private static readonly Win32Options Sandbox = new() { HostWindowing = HostWindowingKind.None };

    [Fact]
    public void DesktopWindowIsAStableDocumentedHandle()
    {
        using var w = new World(Sandbox);
        var h = w.Call(U, "GetDesktopWindow");
        Assert.Equal(User32BasicService.DesktopWindow, h);
        Assert.Equal(h, w.Call(U, "GetDesktopWindow"));
        Assert.Equal(1u, w.Call(U, "IsWindow", h));
        Assert.Equal(0u, w.Call(U, "IsWindow", 0));
        Assert.Equal(0u, w.Call(U, "IsWindow", 0x12345));
        Assert.Equal(1u, w.Call(U, "IsWindowVisible", h));
        Assert.Equal(0u, w.Call(U, "IsWindowVisible", 0x12345));
        Assert.Equal(1400u, w.LastError);
    }

    [Fact]
    public void UnimplementedWindowApisAreAbsentNotFaked()
    {
        using var w = new World(Sandbox);
        Assert.Throws<KeyNotFoundException>(() => w.Call(U, "ShowWindow", w.Call(U, "GetDesktopWindow"), 1));
        Assert.Throws<KeyNotFoundException>(() => w.Call(U, "CreateWindowExA", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void MessageBoxFaultsWithoutAHostHandler()
    {
        using var w = new World(Sandbox);
        var e = Assert.Throws<Win32UsageException>(() => w.Call(U, "MessageBoxA", 0, w.Str("t"), w.Str("c"), 0));
        Assert.Contains("user32!MessageBoxA", e.Message);
    }

    [Fact]
    public void MessageBoxReportsThroughTheHostHandler()
    {
        (uint Hwnd, string Text, string Caption, uint Type)? seen = null;
        using var w = new World(new Win32Options { HostWindowing = HostWindowingKind.None, MessageBoxHandler = (h, t, c, ty) => { seen = (h, t, c, ty); return 7; } });
        Assert.Equal(7u, w.Call(U, "MessageBoxA", 0, w.Str("body"), w.Str("title"), 4));
        Assert.Equal((0u, "body", "title", 4u), seen);
        Assert.Equal(1u, w.Call(U, "MessageBoxA", User32BasicService.DesktopWindow, w.Str("x"), 0, 0) == 7 ? 1u : 0u);
        Assert.Equal("Error", seen!.Value.Caption);
        Assert.Equal(0u, w.Call(U, "MessageBoxA", 0x9999, w.Str("x"), w.Str("y"), 0)); // invalid HWND
        Assert.Equal(1400u, w.LastError);
    }
}
