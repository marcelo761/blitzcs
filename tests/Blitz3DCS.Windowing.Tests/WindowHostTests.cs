using Xunit;

namespace Blitz3DCS.Windowing.Tests;

public sealed class WindowHostTests
{
    [Theory]
    [InlineData(4,30)] [InlineData(29,44)] [InlineData(40,28)] [InlineData(58,59)]
    [InlineData(69,88)] [InlineData(79,205)] [InlineData(82,200)] [InlineData(88,156)]
    [InlineData(224,29)] [InlineData(228,157)] [InlineData(230,184)] [InlineData(-1,0)] [InlineData(700,0)]
    public void Physical_codes_map_to_PC_codes(int sdl, int legacy)
    {
        Assert.Equal(legacy, LegacyScanCodes.FromSdl(sdl));
        if (legacy != 0) Assert.Equal(sdl, LegacyScanCodes.ToSdl(legacy));
    }

    [Fact]
    public void Every_input_query_pumps_and_flush_preserves_held_state()
    {
        var backend = new FakeBackend();
        using var host = new WindowHost(backend);
        backend.Events.Enqueue(new(WindowInputEventKind.KeyDown, ScanCode: 4));
        Assert.Equal(1, host.KeyDown(30));
        host.FlushKeys();
        Assert.Equal(1, host.KeyDown(30));
        Assert.Equal(0, host.KeyHit(30));
        Assert.Equal(0, host.GetKey());
        backend.Events.Enqueue(new(WindowInputEventKind.MouseDown, Button: 3));
        Assert.Equal(1, host.MouseDown(2));
        host.FlushMouse();
        Assert.Equal(1, host.MouseDown(2));
        Assert.Equal(0, host.GetMouse());
        Assert.Equal(9, backend.Pumps);
    }

    [Fact]
    public void Current_modifiers_translate_buffered_key_and_zero_key_consumes_only_one_entry()
    {
        var backend = new FakeBackend();
        using var host = new WindowHost(backend);
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 4));
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 225));
        Assert.Equal(65, host.GetKey());
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 5));
        Assert.Equal(0, host.GetKey()); // Shift's own FIFO entry.
        host.ProcessEvent(new(WindowInputEventKind.KeyUp, ScanCode: 225));
        Assert.Equal(98, host.GetKey());
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 224));
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 4));
        Assert.Equal(0, host.GetKey());
        Assert.Equal(1, host.GetKey());
    }

    [Theory]
    [InlineData(74,1)] [InlineData(77,2)] [InlineData(73,3)] [InlineData(76,4)]
    [InlineData(75,5)] [InlineData(78,6)] [InlineData(82,28)] [InlineData(81,29)]
    [InlineData(79,30)] [InlineData(80,31)]
    public void Navigation_has_original_control_character_values(int scan, int expected)
    {
        using var host = new WindowHost(new FakeBackend());
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: scan));
        Assert.Equal(expected, host.GetKey());
    }

    [Theory]
    [InlineData(87,43)]
    [InlineData(86,45)]
    public void Keypad_add_and_subtract_return_ASCII_even_when_SDL_returns_masked_keycodes(int sdlScan, int expected)
    {
        using var host = new WindowHost(new FakeBackend { Translation = 0x40000000u | (uint)sdlScan });
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: sdlScan));
        Assert.Equal(expected, host.GetKey());
    }

    [Theory]
    [InlineData(0x20ac,128)] [InlineData(0x201c,147)] [InlineData(0xe9,233)]
    [InlineData(0x4000003a,0)] [InlineData(0x1f600,0)] [InlineData(0x3bb,0)]
    public void Unicode_layout_output_is_limited_to_CP1252(uint code, int expected)
    {
        using var host = new WindowHost(new FakeBackend { Translation = code });
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 4));
        Assert.Equal(expected, host.GetKey());
    }

    [Fact]
    public void Focus_loss_preserves_mode2_keyboard_mouse_and_speed_state()
    {
        using var host = new WindowHost(new FakeBackend());
        host.ProcessEvent(new(WindowInputEventKind.KeyDown, ScanCode: 4));
        host.ProcessEvent(new(WindowInputEventKind.MouseDown, Button: 2));
        host.ProcessEvent(new(WindowInputEventKind.MouseMove, X: 73,Y: 91));
        host.ProcessEvent(new(WindowInputEventKind.FocusLost));
        Assert.False(host.HasFocus);
        Assert.Equal(1, host.KeyDown(30));
        Assert.Equal(1, host.MouseDown(3));
        Assert.Equal(73, host.MouseX());
        Assert.Equal(91, host.MouseY());
        Assert.Equal(73, host.MouseXSpeed());
        Assert.Equal(0, host.MouseXSpeed());
        host.ProcessEvent(new(WindowInputEventKind.FocusGained));
        host.ProcessEvent(new(WindowInputEventKind.KeyUp, ScanCode: 4));
        Assert.True(host.HasFocus);
        Assert.Equal(0, host.KeyDown(30));
    }

    [Fact]
    public void Motion_clamps_client_coordinates_and_warp_resets_speed_baselines()
    {
        var backend = new FakeBackend();
        using var host = new WindowHost(backend);
        host.ProcessEvent(new(WindowInputEventKind.MouseMove, X: -17,Y: 999));
        Assert.Equal(0, host.MouseX());
        Assert.Equal(119, host.MouseY());
        host.MoveMouse(37, 53);
        Assert.Equal((37f,53f), backend.Warp);
        Assert.Equal(0, host.MouseXSpeed());
        Assert.Equal(0, host.MouseYSpeed());
        host.ProcessEvent(new(WindowInputEventKind.MouseMove, X: 73,Y: 91));
        Assert.Equal(36, host.MouseXSpeed());
        Assert.Equal(38, host.MouseYSpeed());
        host.ProcessEvent(new(WindowInputEventKind.MouseWheel, WheelY: 0.5f));
        host.ProcessEvent(new(WindowInputEventKind.MouseWheel, WheelY: 1.5f));
        Assert.Equal(2, host.MouseZ());
        Assert.Equal(2, host.MouseZSpeed());
        host.FlushMouse();
        Assert.Equal(2, host.MouseZ());
        Assert.Equal(0, host.MouseZSpeed());
    }

    [Fact]
    public void Wait_skips_noncharacters_and_close_or_cancellation_throws()
    {
        var backend = new FakeBackend();
        using var host = new WindowHost(backend);
        backend.Events.Enqueue(new(WindowInputEventKind.KeyDown, ScanCode: 58));
        backend.Events.Enqueue(new(WindowInputEventKind.KeyDown, ScanCode: 5));
        Assert.Equal(98, host.WaitKey());
        backend.Events.Enqueue(new(WindowInputEventKind.MouseDown, Button: 3));
        Assert.Equal(2, host.MouseWait());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => host.WaitMouse(canceled.Token));
        backend.Events.Enqueue(new(WindowInputEventKind.CloseRequested));
        Assert.Throws<OperationCanceledException>(() => host.WaitKey());
        Assert.True(host.CloseRequested);
    }

    [Fact]
    public void Host_enforces_creator_thread_and_disposal_for_cached_properties()
    {
        var backend = new FakeBackend();
        var host = new WindowHost(backend);
        Exception? error = null;
        var thread = new Thread(() => { try { _ = host.HasFocus; } catch(Exception ex) { error = ex; } });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        host.Dispose(); host.Dispose();
        Assert.Equal(1, backend.Disposals);
        Assert.Throws<ObjectDisposedException>(() => host.CloseRequested);
        Assert.Throws<ObjectDisposedException>(() => host.HasFocus);
        Assert.Throws<ObjectDisposedException>(() => host.KeyDown(30));
    }

    [Fact]
    public void Native_window_handle_is_delegated_to_backend()
    {
        var expected = (nint)0x12345678;
        using var host = new WindowHost(new FakeBackend { NativeWindowHandle = expected });

        Assert.Equal(expected, host.GetWin32WindowHandle());
    }

    [Fact]
    public void Backend_without_a_native_handle_fails_explicitly()
    {
        using var host = new WindowHost(new HandlelessBackend());

        Assert.Throws<PlatformNotSupportedException>(() => host.GetWin32WindowHandle());
    }

    [Fact]
    public void Native_window_handle_obeys_thread_and_disposal_affinity()
    {
        var host = new WindowHost(new FakeBackend { NativeWindowHandle = (nint)0x1234 });
        Exception? error = null;
        var thread = new Thread(() => { try { _ = host.GetWin32WindowHandle(); } catch (Exception ex) { error = ex; } });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(error);

        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => host.GetWin32WindowHandle());
    }

    [Fact]
    public void Window_mutation_methods_forward_to_backend_on_the_owner_thread()
    {
        var backend = new FakeBackend();
        using var host = new WindowHost(backend);

        host.SetTitle("SCP runtime");
        host.SetSize(640, 480);
        host.Hide();
        host.Show();
        host.HidePointer();
        host.ShowPointer();

        Assert.Equal("SCP runtime", backend.Title);
        Assert.Equal((640, 480), backend.Size);
        Assert.Equal(1, backend.HideCalls);
        Assert.Equal(1, backend.ShowCalls);
        Assert.Equal(1, backend.HidePointerCalls);
        Assert.Equal(1, backend.ShowPointerCalls);
    }

    private sealed class FakeBackend : IWindowBackend
    {
        public int ClientWidth => 160;
        public int ClientHeight => 120;
        public bool HasFocus => true;
        public nint NativeWindowHandle;
        public int Pumps, Disposals;
        public uint? Translation;
        public (float X,float Y) Warp;
        public string? Title;
        public (int Width, int Height) Size;
        public int ShowCalls, HideCalls;
        public int ShowPointerCalls, HidePointerCalls;
        public Queue<WindowInputEvent> Events { get; } = new();
        public void PumpEvents(Action<WindowInputEvent> dispatch)
        {
            Pumps++;
            while (Events.TryDequeue(out var e)) dispatch(e);
        }
        public void WarpMouse(float x, float y) => Warp = (x,y);
        public uint TranslateScanCode(int scanCode, bool shift, bool ctrl, bool alt) => Translation ??
            (scanCode is 4 or 5 ? (uint)(scanCode + (shift ? 'A' : 'a') - 4) : 0);
        public nint GetWin32WindowHandle() => NativeWindowHandle;
        public void SetTitle(string title) => Title = title;
        public void SetSize(int width, int height) => Size = (width, height);
        public void Show() => ShowCalls++;
        public void Hide() => HideCalls++;
        public void ShowPointer() => ShowPointerCalls++;
        public void HidePointer() => HidePointerCalls++;
        public void Dispose() => Disposals++;
    }

    private sealed class HandlelessBackend : IWindowBackend
    {
        public int ClientWidth => 1;
        public int ClientHeight => 1;
        public bool HasFocus => false;
        public void PumpEvents(Action<WindowInputEvent> dispatch) { }
        public void WarpMouse(float x, float y) { }
        public uint TranslateScanCode(int scanCode, bool shift, bool ctrl, bool alt) => 0;
        public void ShowPointer() { }
        public void HidePointer() { }
        public void Dispose() { }
    }
}
