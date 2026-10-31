using System.Diagnostics;
using System.Runtime.InteropServices;
using Blitz3DCS.Windowing;
using Xunit;

namespace Blitz3DCS.Windowing.Tests;

public sealed class SdlWindowBackendTests
{
    [Fact]
    public void Event_union_matches_SDL3_header_offsets()
    {
        Assert.Equal(128, Marshal.SizeOf<SdlWindowBackend.SdlEvent>());
        Assert.Equal((nint)8, Marshal.OffsetOf<SdlWindowBackend.SdlEvent>(nameof(SdlWindowBackend.SdlEvent.Timestamp)));
        Assert.Equal((nint)16, Marshal.OffsetOf<SdlWindowBackend.SdlEvent>(nameof(SdlWindowBackend.SdlEvent.WindowId)));
        Assert.Equal((nint)24, Marshal.OffsetOf<SdlWindowBackend.SdlEvent>(nameof(SdlWindowBackend.SdlEvent.ScanCode)));
        Assert.Equal((nint)37, Marshal.OffsetOf<SdlWindowBackend.SdlEvent>(nameof(SdlWindowBackend.SdlEvent.Repeat)));
        Assert.Equal((nint)28, Marshal.OffsetOf<SdlWindowBackend.SdlEvent>(nameof(SdlWindowBackend.SdlEvent.X)));
        Assert.Equal((nint)(IntPtr.Size == 8 ? 24 : 20), Marshal.OffsetOf<SdlWindowBackend.TextEvent>(nameof(SdlWindowBackend.TextEvent.Text)));
    }

    [Fact]
    public void Invalid_dimensions_fail_before_initializing_native_video()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdlWindowBackend(0, 64, "test"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdlWindowBackend(64, -1, "test"));
    }

    [Fact]
    public void Windows_native_handle_probe_is_opt_in()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BLITZCS_TEST_SDL_WINDOW_NATIVE_HWND") != "1") return;
        using var backend = new SdlWindowBackend(160, 120, "BlitzCS HWND probe");
        var hwnd = backend.GetWin32WindowHandle();
        Assert.NotEqual(0, hwnd);
        Assert.True(IsWindow(hwnd));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [Fact]
    public async Task Dummy_driver_validates_native_event_routing_lifecycle_and_audio_independence()
    {
        await RunChild("dummy", null);
    }

    [Fact]
    public async Task Unavailable_video_driver_reports_failure_and_can_retry()
    {
        await RunChild("failure", null);
    }

    [Fact]
    public async Task Missing_X11_display_reports_failure_without_falling_back_and_can_retry()
    {
        await RunChild("no-display", ":65534");
    }

    [Fact]
    public async Task Xvfb_creates_real_X11_window_and_pumps_native_events()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = Process.Start(new ProcessStartInfo("Xvfb", "-displayfd 1 -screen 0 640x480x24 -nolisten tcp")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start Xvfb.");
        try
        {
            var displayNumber = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(string.IsNullOrWhiteSpace(displayNumber));
            await RunChild("x11", ":" + displayNumber);
        }
        finally { if (!server.HasExited) server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
    }

    private static async Task RunChild(string scenario, string? display)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SdlWindowBackendTests).Assembly.Location);
        start.ArgumentList.Add(scenario);
        start.Environment["SDL_VIDEO_DRIVER"] = scenario switch
        {
            "failure" => "blitzcs-unavailable-test-driver",
            "no-display" => "x11",
            _ => scenario
        };
        start.Environment["SDL_AUDIO_DRIVER"] = "dummy";
        if (display is not null) start.Environment["DISPLAY"] = display;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start native backend probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, $"Native {scenario} probe failed ({process.ExitCode}):\n{await stdout}\n{await stderr}");
    }
}

internal static class BackendProbe
{
    private const string Sdl = "libSDL3.so.0";
    public static int Main(string[] args)
    {
        try
        {
            if (args[0] is "failure" or "no-display")
            {
                var error = Assert.Throws<InvalidOperationException>(() => new SdlWindowBackend(64, 64, "failure"));
                Assert.Contains("Initialize SDL video/events", error.Message);
                if (args[0] == "no-display") Assert.Contains("x11", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True(SDL_SetHintWithPriority("SDL_VIDEO_DRIVER", "dummy", 2));
            }
            RunProbe();
            RunHostProbe();
            RunLegacyInputHostProbe();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RunLegacyInputHostProbe()
    {
        Assert.Throws<InvalidOperationException>(() => _ = LegacyInput.CurrentWindow);
        LegacyInput.OpenWindow(64, 64, "BlitzCS shared-window probe");
        var expected = LegacyInput.CurrentWindow;
        Assert.NotNull(expected);
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => expected.GetWin32WindowHandle());
        Exception? wrongThread = null;
        var thread = new Thread(() => { try { _ = LegacyInput.CurrentWindow; } catch (Exception ex) { wrongThread = ex; } });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(wrongThread);
        LegacyInput.CloseWindow();
        Assert.Throws<InvalidOperationException>(() => _ = LegacyInput.CurrentWindow);
    }

    private static void RunHostProbe()
    {
        var backend = new SdlWindowBackend(160, 120, "BlitzCS host probe");
        using var host = new WindowHost(backend);
        host.PumpEvents();
        Push(new() { Type = 0x300, WindowId = backend.WindowId, ScanCode = 4 });
        Push(new() { Type = 0x300, WindowId = backend.WindowId, ScanCode = 225 });
        Assert.Equal(65, host.GetKey());
        Assert.Equal(1, host.KeyDown(30));
        Assert.Equal(1, host.KeyHit(30));
        Assert.Equal(0, host.GetKey());
        Push(new() { Type = 0x301, WindowId = backend.WindowId, ScanCode = 225 });
        Push(new() { Type = 0x300, WindowId = backend.WindowId, ScanCode = 88 });
        Assert.Equal(13, host.GetKey());
        Push(new() { Type = 0x300, WindowId = backend.WindowId, ScanCode = 87 });
        Assert.Equal(43, host.GetKey());
        Push(new() { Type = 0x300, WindowId = backend.WindowId, ScanCode = 86 });
        Assert.Equal(45, host.GetKey());
        Push(new() { Type = 0x401, WindowId = backend.WindowId, Button = 3 });
        Assert.Equal(1, host.MouseDown(2));
        Assert.Equal(2, host.GetMouse());
        Push(new() { Type = 0x400, WindowId = backend.WindowId, X = 999, Y = -5 });
        Assert.Equal(159, host.MouseX());
        Assert.Equal(0, host.MouseY());
        Push(new() { Type = 0x403, WindowId = backend.WindowId, WheelY = 2 });
        Assert.Equal(2, host.MouseZ());
        Assert.Equal(2, host.MouseZSpeed());
        Assert.Equal(0, host.MouseZSpeed());
        Push(new() { Type = 0x210, WindowId = backend.WindowId });
        Assert.Throws<OperationCanceledException>(() => host.WaitMouse());
        Assert.True(host.CloseRequested);
    }

    private static void RunProbe()
    {
        Assert.True(SDL_InitSubSystem(0x10));
        try
        {
            var window = new SdlWindowBackend(160, 120, "BlitzCS backend probe");
            try
            {
                Assert.NotEqual(0u, window.WindowId);
                Assert.Equal(160, window.ClientWidth);
                Assert.Equal(120, window.ClientHeight);
                Assert.Throws<InvalidOperationException>(() => new SdlWindowBackend(64, 64, "second"));
                var events = new List<WindowInputEvent>();
                window.PumpEvents(events.Add);
                events.Clear();
                Push(new() { Type = 0x300, WindowId = window.WindowId + 100, ScanCode = 4 });
                Push(new() { Type = 0x300, WindowId = window.WindowId, ScanCode = 4, Repeat = 1 });
                Push(new() { Type = 0x301, WindowId = window.WindowId, ScanCode = 4 });
                Push(new() { Type = 0x400, WindowId = window.WindowId, X = 12.5f, Y = 17.5f });
                Push(new() { Type = 0x401, WindowId = window.WindowId, Button = 3, X = 10, Y = 20 });
                Push(new() { Type = 0x402, WindowId = window.WindowId, Button = 3, X = 10, Y = 20 });
                Push(new() { Type = 0x403, WindowId = window.WindowId, WheelX = 2, WheelY = -3, WheelDirection = 1 });
                Push(new() { Type = 0x20f, WindowId = window.WindowId });
                Push(new() { Type = 0x20e, WindowId = window.WindowId });
                Push(new() { Type = 0x210, WindowId = window.WindowId });
                Push(new() { Type = 0x100 });
                window.PumpEvents(events.Add);
                Assert.Equal(10, events.Count);
                Assert.Equal(new(WindowInputEventKind.KeyDown, ScanCode: 4, Repeat: true), events[0]);
                Assert.Equal(WindowInputEventKind.KeyUp, events[1].Kind);
                Assert.Equal(new(WindowInputEventKind.MouseMove, X: 12.5f, Y: 17.5f), events[2]);
                Assert.Equal(3, events[3].Button);
                Assert.Equal(WindowInputEventKind.MouseUp, events[4].Kind);
                Assert.Equal(-2, events[5].WheelX);
                Assert.Equal(3, events[5].WheelY);
                Assert.Equal(WindowInputEventKind.FocusLost, events[6].Kind);
                Assert.True(window.HasFocus);
                Assert.Equal(WindowInputEventKind.CloseRequested, events[8].Kind);
                Assert.Equal(WindowInputEventKind.Quit, events[9].Kind);
                Assert.Equal((uint)'a', window.TranslateScanCode(4, false, false, false));
                Assert.Equal((uint)'A', window.TranslateScanCode(4, true, false, false));
                Assert.Equal((uint)'a', window.TranslateScanCode(4, false, true, false));
                Assert.Equal((uint)'a', window.TranslateScanCode(4, false, false, true));
                var text = Marshal.StringToCoTaskMemUTF8("é€");
                try
                {
                    Push(new() { Type = 0x303, WindowId = window.WindowId,
                        Text = new() { Type = 0x303, WindowId = window.WindowId, Text = text } });
                    events.Clear();
                    window.PumpEvents(events.Add);
                    Assert.Contains(events, e => e.Kind == WindowInputEventKind.TextInput && e.Text == "é€");
                }
                finally { Marshal.FreeCoTaskMem(text); }
                window.WarpMouse(32, 45);
                _ = window.GetMousePosition();
                window.StartTextInput();
                window.StopTextInput();
                Action[] threadBoundOperations = [
                    () => window.PumpEvents(_ => { }), () => window.WarpMouse(0, 0),
                    () => window.GetMousePosition(), () => window.TranslateScanCode(4, false, false, false),
                    () => window.StartTextInput(), () => window.StopTextInput(), () => window.Dispose()
                ];
                foreach (var operation in threadBoundOperations)
                {
                    Exception? wrongThread = null;
                    var thread = new Thread(() => { try { operation(); } catch (Exception ex) { wrongThread = ex; } });
                    thread.Start(); thread.Join();
                    Assert.IsType<InvalidOperationException>(wrongThread);
                }
                Push(new() { Type = 0x300, WindowId = window.WindowId, ScanCode = 5 });
                window.PumpEvents(_ => Assert.Throws<InvalidOperationException>(() => window.PumpEvents(_ => { })));
            }
            finally { window.Dispose(); }
            window.Dispose();
            Assert.Throws<ObjectDisposedException>(() => window.GetMousePosition());
            Assert.Equal(0x10u, SDL_WasInit(0x10));
            for (var cycle = 0; cycle < 10; cycle++)
            {
                using (var reopened = new SdlWindowBackend(64, 64, $"reopened {cycle}"))
                {
                    Assert.NotEqual(0u, reopened.WindowId);
                    reopened.PumpEvents(_ => { });
                }
                Assert.Equal(0u, SDL_WasInit(0x20));
                Assert.Equal(0x10u, SDL_WasInit(0x10));
            }
        }
        finally { SDL_QuitSubSystem(0x10); }
    }

    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetHintWithPriority(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int priority);

    private static void Push(SdlWindowBackend.SdlEvent e) => Assert.True(SDL_PushEvent(ref e));
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_WasInit(uint flags);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_PushEvent(ref SdlWindowBackend.SdlEvent e);
}
