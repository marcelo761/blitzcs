using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.DirectX;
using BlitzCS.BinaryCompiler.Win32.HostBridge;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>[Fact] that needs a Windows host whose ddraw.dll loads (under Wine the prefix needs the vkd3d DLLs: BLITZCS_WINE_GRAPHICS=1, docs/wine-testing.md).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresHostDirectDrawFactAttribute : FactAttribute
{
    public RequiresHostDirectDrawFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "needs a Windows host (Windows or Wine) with ddraw.dll; this host is not Windows";
        else if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("ddraw.dll", out _)) Skip = "the host's ddraw.dll cannot be loaded (under Wine: a prefix with the vkd3d DLLs, BLITZCS_WINE_GRAPHICS=1)";
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresHostDirectXFileFactAttribute : FactAttribute
{
    public RequiresHostDirectXFileFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "needs a Windows host (Windows or Wine) with d3dxof.dll; this host is not Windows";
        else if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("d3dxof.dll", out _)) Skip = "the host's d3dxof.dll cannot be loaded";
    }
}

/// <summary>32-bit/64-bit structure conversions of the bridge. Pure computation: runs on every host.</summary>
public sealed class HostBridgeLayoutTests
{
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s[o..]);

    private static readonly long Proc = 0x7FF6_1234_5678, Inst = 0x1_4000_0000, Name = 0x2_0000_0010, Str = 0x2_0000_0000;

    [Fact]
    public void WndClassA_moves_every_field_to_its_64_bit_offset_and_sign_extends_handles()
    {
        var g = new byte[Win32Layouts.GuestWndClassA];
        uint[] words = [0x23, 0x60001000, 4, 8, 0x60000000, 0x00010003, 0xFFFF0011, 6, 0, 0x60002000];
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4 * i), words[i]);
        var h = new byte[Win32Layouts.HostWndClassA];
        Win32Layouts.WndClassToHost(g, h, (nint)Proc, (nint)Inst, 0, (nint)Name);
        Assert.Equal(0x23u, U32(h, 0));
        Assert.Equal(0x7FF6_1234_5678L, I64(h, 8));                 // the native procedure, never the guest address
        Assert.Equal((4u, 8u), (U32(h, 16), U32(h, 20)));
        Assert.Equal(0x1_4000_0000L, I64(h, 24));                    // the host HINSTANCE
        Assert.Equal(0x00010003L, I64(h, 32));
        Assert.Equal(unchecked((long)0xFFFF_FFFF_FFFF_0011UL), I64(h, 40));   // a handle with bit 31 set is sign-extended
        Assert.Equal(6L, I64(h, 48));                                // COLOR_xxx + 1 stays a small value
        Assert.Equal(0x2_0000_0010L, I64(h, 64));
    }

    [Fact]
    public void Msg_round_trips_and_a_64_bit_lparam_is_refused_by_name()
    {
        var g = new byte[Win32Layouts.GuestMsg];
        uint[] words = [0x000A0B0C, 0x8002, 7, 0xFFFFFFFE, 123456, 0xFFFFFFF0, 77];
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4 * i), words[i]);
        var h = new byte[Win32Layouts.HostMsg];
        Win32Layouts.MsgToHost(g, h);
        Assert.Equal(7L, I64(h, 16));                                 // WPARAM zero-extended
        Assert.Equal(-2L, I64(h, 24));                                // LPARAM sign-extended
        var back = new byte[Win32Layouts.GuestMsg];
        Win32Layouts.MsgToGuest(h, back);
        Assert.Equal(g, back);

        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), 0x7FFF_0000_1000);
        var e = Assert.Throws<Win32UsageException>(() => Win32Layouts.MsgToGuest(h, back));
        Assert.Contains("MSG.lParam of message 0x8002", e.Message);
    }

    [Fact]
    public void CreateStruct_and_WindowPos_round_trip_with_the_pointer_fields_converted_by_the_caller()
    {
        var host = new byte[Win32Layouts.HostCreateStructA];
        BinaryPrimitives.WriteInt64LittleEndian(host, 0xCAFE);                     // lpCreateParams
        BinaryPrimitives.WriteInt64LittleEndian(host.AsSpan(8), 0x1_4000_0000);    // hInstance (host)
        BinaryPrimitives.WriteInt64LittleEndian(host.AsSpan(16), 0x50);            // hMenu
        BinaryPrimitives.WriteInt64LittleEndian(host.AsSpan(24), -1);              // hwndParent
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(host.AsSpan(32 + 4 * i), 10 * (i + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(host.AsSpan(48), 0x10CF0000);
        BinaryPrimitives.WriteInt64LittleEndian(host.AsSpan(56), 0x2_0000_0000);
        BinaryPrimitives.WriteInt64LittleEndian(host.AsSpan(64), 0xC123);          // class atom
        BinaryPrimitives.WriteUInt32LittleEndian(host.AsSpan(72), 0x40000);
        var guest = new byte[Win32Layouts.GuestCreateStructA];
        Win32Layouts.CreateStructToGuest(host, guest, (offset, v) => offset switch { 8 => 0x50000000u, 56 => 0x7000u, _ => (uint)v });
        Assert.Equal([0xCAFEu, 0x50000000u, 0x50u, 0xFFFFFFFFu, 10u, 20u, 30u, 40u, 0x10CF0000u, 0x7000u, 0xC123u, 0x40000u],
            Enumerable.Range(0, 12).Select(i => U32(guest, 4 * i)));
        var again = new byte[Win32Layouts.HostCreateStructA];
        Win32Layouts.CreateStructToHost(guest, again, (offset, v) => offset switch { 4 => (nint)Inst, 36 => (nint)Str, _ => (nint)v });
        Assert.Equal(host, again);

        var pos = new byte[Win32Layouts.HostWindowPos];
        BinaryPrimitives.WriteInt64LittleEndian(pos, 0x000A0B0C);
        BinaryPrimitives.WriteInt64LittleEndian(pos.AsSpan(8), -2);               // HWND_NOTOPMOST
        for (var i = 0; i < 5; i++) BinaryPrimitives.WriteInt32LittleEndian(pos.AsSpan(16 + 4 * i), -5 + i);
        var g = new byte[Win32Layouts.GuestWindowPos];
        Win32Layouts.WindowPosToGuest(pos, g);
        Assert.Equal(0xFFFFFFFEu, U32(g, 4));
        var h2 = new byte[Win32Layouts.HostWindowPos];
        Win32Layouts.WindowPosToHost(g, h2);
        Assert.Equal(pos, h2);
    }

    [Fact]
    public void PaintStruct_round_trips_and_a_handle_that_is_not_32_bit_significant_faults()
    {
        var h = new byte[Win32Layouts.HostPaintStruct];
        BinaryPrimitives.WriteInt64LittleEndian(h, 0x0123_4567);
        for (var i = 8; i < 68; i++) h[i] = (byte)i;
        var g = new byte[Win32Layouts.GuestPaintStruct];
        Win32Layouts.PaintStructToGuest(h, g);
        Assert.Equal(0x01234567u, U32(g, 0));
        var back = new byte[Win32Layouts.HostPaintStruct];
        Win32Layouts.PaintStructToHost(g, back);
        Assert.Equal(h.AsSpan(0, 68).ToArray(), back.AsSpan(0, 68).ToArray());
        BinaryPrimitives.WriteInt64LittleEndian(h, 0x1_0000_0000);
        Assert.Contains("PAINTSTRUCT.hdc", Assert.Throws<Win32UsageException>(() => Win32Layouts.PaintStructToGuest(h, g)).Message);
    }

    [Fact]
    public void SurfaceDesc2_moves_the_tail_by_eight_bytes_drops_host_pointers_and_bridges_client_memory()
    {
        var g = new byte[DdLayouts.GuestSurfaceDesc2];
        for (var i = 0; i < g.Length; i++) g[i] = (byte)(i + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(g, DdLayouts.GuestSurfaceDesc2);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), 0x1007);            // DDSD_CAPS | HEIGHT | WIDTH | PIXELFORMAT
        var h = new byte[DdLayouts.HostSurfaceDesc2];
        DdLayouts.SurfaceDescToHost(g, h);
        Assert.Equal((uint)DdLayouts.HostSurfaceDesc2, U32(h, 0));
        Assert.Equal(g.AsSpan(4, 32).ToArray(), h.AsSpan(4, 32).ToArray());
        Assert.Equal(0L, I64(h, 40));
        Assert.Equal(g.AsSpan(40, 84).ToArray(), h.AsSpan(48, 84).ToArray());     // color keys, DDPIXELFORMAT, DDSCAPS2, dwTextureStage
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 0x7FFF_1234_0000);   // a host lpSurface
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 0x1807);
        var back = new byte[DdLayouts.GuestSurfaceDesc2];
        DdLayouts.SurfaceDescToGuest(h, back);
        Assert.Equal(0u, U32(back, 36));                                           // never handed over
        Assert.Equal(0x1007u, U32(back, 4));
        Assert.Equal(g.AsSpan(40).ToArray(), back.AsSpan(40).ToArray());

        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), 0x1807);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(36), 0x12345678);
        DdLayouts.SurfaceDescToHost(g, h);
        Assert.Equal(0x12345678L, I64(h, 40));
        BinaryPrimitives.WriteUInt32LittleEndian(g, 108);
        Assert.Throws<ComArgumentException>(() => DdLayouts.SurfaceDescToHost(g, h));
    }

    [Fact]
    public void BltFx_places_each_dword_member_of_the_pointer_unions_at_its_aligned_64_bit_offset()
    {
        var g = new byte[DirectDrawSurfaces.GuestBltFx];
        for (var i = 4; i < g.Length; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(i), (uint)(1000 + i));
        BinaryPrimitives.WriteUInt32LittleEndian(g, DirectDrawSurfaces.GuestBltFx);
        var h = new byte[DirectDrawSurfaces.HostBltFx];
        DirectDrawSurfaces.BltFxToHost(g, h);
        // guest offset -> host offset (ddraw.h DDBLTFX, natural alignment of a 64-bit build)
        (int G, int H)[] map = [(4, 4), (36, 36), (40, 40), (44, 48), (48, 56), (52, 64), (64, 76), (68, 80), (72, 88), (76, 96), (80, 104), (84, 112), (96, 124)];
        foreach (var (go, ho) in map) Assert.Equal(U32(g, go), U32(h, ho));
        Assert.Equal((uint)DirectDrawSurfaces.HostBltFx, U32(h, 0));
    }

    [Fact]
    public void DataFormat_becomes_the_64_bit_image_with_copied_guids()
    {
        var mem = new FakeMemory();
        var block = mem.Allocate(0x200).Address;
        var guid = new Guid("6F1D2B60-D5A0-11CF-BFC7-444553540000");   // GUID_SysKeyboard (any GUID works)
        mem.Write(block + 0x100, guid.ToByteArray());
        uint objects = block + 0x40;
        mem.Write(block, [.. BitConverter.GetBytes(24u), .. BitConverter.GetBytes(16u), .. BitConverter.GetBytes(2u), .. BitConverter.GetBytes(256u), .. BitConverter.GetBytes(2u), .. BitConverter.GetBytes(objects)]);
        mem.Write(objects, [.. BitConverter.GetBytes(block + 0x100), .. BitConverter.GetBytes(4u), .. BitConverter.GetBytes(0x8000000Cu), .. BitConverter.GetBytes(0u)]);
        mem.Write(objects + 16, [.. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(8u), .. BitConverter.GetBytes(0x00FFFF0Cu), .. BitConverter.GetBytes(1u)]);
        using var scope = new NativeScope();
        var h = DirectInputAdapters.DataFormatToHost(mem, scope, block);
        Assert.Equal((32, 24, 2, 256, 2), (Marshal.ReadInt32(h), Marshal.ReadInt32(h, 4), Marshal.ReadInt32(h, 8), Marshal.ReadInt32(h, 12), Marshal.ReadInt32(h, 16)));
        var array = Marshal.ReadIntPtr(h, 24);
        var g0 = Marshal.ReadIntPtr(array);
        var bytes = new byte[16]; Marshal.Copy(g0, bytes, 0, 16);
        Assert.Equal(guid, new Guid(bytes));
        Assert.Equal((4, unchecked((int)0x8000000C), 0), (Marshal.ReadInt32(array, 8), Marshal.ReadInt32(array, 12), Marshal.ReadInt32(array, 16)));
        Assert.Equal(IntPtr.Zero, Marshal.ReadIntPtr(array, 24));
        Assert.Equal((8, 0x00FFFF0C, 1), (Marshal.ReadInt32(array, 32), Marshal.ReadInt32(array, 36), Marshal.ReadInt32(array, 40)));
    }

    [Fact]
    public void FvfStride_computes_vertex_size_for_standard_and_multi_texture_formats_and_refuses_invalid_fvf()
    {
        Assert.Equal(32, Direct3DAdapters.FvfStride(0x112));   // D3DFVF_VERTEX (XYZ | NORMAL | TEX1)
        Assert.Equal(32, Direct3DAdapters.FvfStride(0x1E2));   // D3DFVF_LVERTEX (XYZ | RESERVED1 | DIFFUSE | SPECULAR | TEX1)
        Assert.Equal(32, Direct3DAdapters.FvfStride(0x1C4));   // D3DFVF_TLVERTEX (XYZRHW | DIFFUSE | SPECULAR | TEX1)
        Assert.Equal(44, Direct3DAdapters.FvfStride(0x252));   // Blitz3D vertex (XYZ | NORMAL | DIFFUSE | TEX2)
        Assert.Equal(44, Direct3DAdapters.FvfStride(0x002u | 0x300u | (3u << 16) | (1u << 18) | (2u << 20)));   // XYZ (12) + 1D (4) + 3D (12) + 4D (16)
        Assert.Throws<ComArgumentException>(() => Direct3DAdapters.FvfStride(0));          // no position
        Assert.Throws<ComArgumentException>(() => Direct3DAdapters.FvfStride(0x014));      // XYZRHW | NORMAL
        Assert.Throws<ComArgumentException>(() => Direct3DAdapters.FvfStride(0x902));      // 9 texture sets
    }
}

/// <summary>Which hosts get the bridge (the Linux target must not gain these APIs).</summary>
public sealed class HostBridgeCompositionTests
{
    private static readonly (string Dll, string Name)[] Bridged =
        [("user32.dll", "CreateWindowExA"), ("user32.dll", "DispatchMessageA"), ("gdi32.dll", "ExtTextOutA"), ("ddraw.dll", "DirectDrawCreateEx"), ("dinput8.dll", "DirectInput8Create")];

    [Fact]
    public void The_bridge_is_part_of_the_default_composition_exactly_on_a_Windows_host()
    {
        using var r = new Win32ImportResolver();
        foreach (var (dll, name) in Bridged)
            if (OperatingSystem.IsWindows()) Assert.NotNull(r.Resolve(dll, name, null));
            else Assert.Null(r.Resolve(dll, name, null));   // absent: the loader / the guest faults naming dll!name
        Assert.Equal(OperatingSystem.IsWindows(), r.Services.Any(s => s is HostUser32Service));
    }

    [Fact]
    public void None_keeps_the_sandbox_subset_and_Host_is_refused_off_Windows()
    {
        using var r = new Win32ImportResolver(new Win32Options { HostWindowing = HostWindowingKind.None });
        foreach (var (dll, name) in Bridged) Assert.Null(r.Resolve(dll, name, null));
        Assert.NotNull(r.Resolve("user32.dll", "GetDesktopWindow", null));
        if (!OperatingSystem.IsWindows())
            Assert.Contains("needs a Windows host", Assert.Throws<Win32UsageException>(() => new Win32ImportResolver(new Win32Options { HostWindowing = HostWindowingKind.Host })).Message);
    }

    [Fact]
    public void HostFunction_caches_resolved_native_export_and_HostExports_is_case_insensitive()
    {
        var fn = new HostFunction("kernel32.dll", "GetModuleHandleW", HostResult.Pointer);
        if (OperatingSystem.IsWindows())
        {
            var p1 = fn.NativeExport;
            Assert.NotEqual(0, p1);
            var p2 = fn.NativeExport;
            Assert.Equal(p1, p2);
            var p3 = HostExports.Get("KERNEL32.DLL", "getmodulehandlew");
            Assert.Equal(p1, p3);
        }
        else
        {
            var ex = Assert.Throws<Win32UsageException>(() => _ = fn.NativeExport);
            Assert.Contains("kernel32.dll", ex.Message);
            Assert.Contains("GetModuleHandleW", ex.Message);
        }
    }
}

/// <summary>
/// A hand-assembled IA-32 guest for the window bridge: callptr0..12(fn, a1..) = fn(a1..) (stdcall through a function pointer, from cdecl wrappers), loadlib/getproc, and a window procedure
/// <c>wndproc</c> that records what it receives and tail-jumps to the imported DefWindowProcA. For message 0x8001 it jumps to the import NOPE.dll!Missing instead (no service provides it),
/// so a fault inside a host-delivered message can be observed.
/// Records at <see cref="Rec"/>: +0 message count, +4 last message, +8 CREATESTRUCT.lpCreateParams, +0xC CREATESTRUCT.lpszClass, +0x10 CREATESTRUCT.hInstance, +0x14 1 when WM_NCCREATE
/// arrived, +0x18 last HWND.
/// </summary>
internal sealed class WindowRig : IDisposable
{
    public static readonly uint Base = Win32PeBuilder.ImageBase;
    public static uint Data(uint offset) => Base + Win32PeBuilder.DataRva + offset;
    public static readonly uint Rec = Data(0x800);

    public readonly GuestModule Module;
    public readonly Win32ImportResolver Resolver;
    public readonly uint WndProc;

    public WindowRig()
    {
        Resolver = new Win32ImportResolver(new Win32Options());
        var pe = new Win32PeBuilder();
        var loadLibrary = pe.Import("KERNEL32.dll", "LoadLibraryA");
        var getProc = pe.Import("KERNEL32.dll", "GetProcAddress");
        var defProc = pe.Import("USER32.dll", "DefWindowProcA");
        var missing = pe.Import("NOPE.dll", "Missing");
        var code = new List<byte>();
        var labels = new Dictionary<string, uint>();
        void B(params byte[] b) => code.AddRange(b);
        void D(uint v) => code.AddRange(BitConverter.GetBytes(v));
        void Label(string n) => labels[n] = (uint)code.Count;
        void IfEqual(uint value, Action body) { B(0x3D); D(value); B(0x75, 0); var at = code.Count; body(); code[at - 1] = checked((byte)(code.Count - at)); }   // cmp eax,imm32; jne skip
        for (var n = 0; n <= 12; n++)
        {
            Label("callptr" + n);
            B(0x8B, 0x44, 0x24, 0x04);
            for (var a = 0; a < n; a++) B(0xFF, 0x74, 0x24, (byte)(4 * (n + 1)));
            B(0xFF, 0xD0, 0xC3);
        }
        Label("loadlib");
        B(0xFF, 0x74, 0x24, 0x04, 0xFF, 0x15); D(Base + loadLibrary); B(0xC3);
        Label("getproc");
        B(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08, 0xFF, 0x15); D(Base + getProc); B(0xC3);
        Label("wndproc");
        B(0x8B, 0x44, 0x24, 0x08);                 // mov eax,[esp+8]   (msg)
        B(0xFF, 0x05); D(Rec);                     // inc dword [Rec]
        B(0xA3); D(Rec + 4);                       // mov [Rec+4],eax
        B(0x8B, 0x4C, 0x24, 0x04);                 // mov ecx,[esp+4]   (hwnd)
        B(0x89, 0x0D); D(Rec + 0x18);              // mov [Rec+0x18],ecx
        IfEqual(0x0001, () =>                      // WM_CREATE: record CREATESTRUCT fields
        {
            B(0x8B, 0x4C, 0x24, 0x10);             // mov ecx,[esp+16]  (lParam)
            B(0x8B, 0x11); B(0x89, 0x15); D(Rec + 8);          // lpCreateParams
            B(0x8B, 0x51, 0x28); B(0x89, 0x15); D(Rec + 0xC);  // lpszClass
            B(0x8B, 0x51, 0x04); B(0x89, 0x15); D(Rec + 0x10); // hInstance
        });
        IfEqual(0x0081, () => { B(0xC7, 0x05); D(Rec + 0x14); D(1); });   // WM_NCCREATE
        IfEqual(0x8001, () => { B(0xFF, 0x25); D(Base + missing); });     // jmp [NOPE.dll!Missing]
        B(0xFF, 0x25); D(Base + defProc);          // jmp [DefWindowProcA]
        pe.WithCode([.. code]).WithData(new byte[0x1000]);
        foreach (var (n, o) in labels) pe.Export(n, o);
        var image = PeReader.Read(pe.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release) { DynamicStubs = 256 }, "window-rig");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Module = GuestModule.Load(artifact, image, Resolver, new GuestModuleOptions { RunEntryPoint = false, AllowUnboundImports = true });
        WndProc = Base + Win32PeBuilder.TextRva + labels["wndproc"];
    }

    public uint F(string name, params uint[] args) => Module.Bind(name, GuestCallingConvention.Cdecl, args.Length).Call(args);
    public uint Str(uint dataOffset, string text) { Module.Memory.Write(Data(dataOffset), [.. System.Text.Encoding.ASCII.GetBytes(text), (byte)0]); return Data(dataOffset); }
    public uint Api(string dll, string name)
    {
        var h = F("loadlib", Str(0x400, dll));
        var f = h == 0 ? 0 : F("getproc", h, Str(0x440, name));
        Assert.NotEqual(0u, f);
        return f;
    }
    /// <summary>Calls a bridged stdcall API through its guest-callable address with exactly these arguments.</summary>
    public uint Call(string dll, string name, params uint[] args) => F("callptr" + args.Length, [Api(dll, name), .. args]);
    public uint Word(uint address) => Module.Memory.ReadUInt32(address);

    public void Dispose() { Module.Dispose(); Resolver.Dispose(); }
}

[Collection("GuestRuntime")]
public sealed class HostWindowBridgeTests
{
    private const string U = "user32.dll";
    private static bool HostIsWindow(uint hwnd) => HostInvoke.Call(HostExports.Get("user32.dll", "IsWindow"), [HostHandles.ToHost(hwnd)]) != 0;

    [RequiresWindowsFact("needs the host's user32.dll (Windows or Wine)")]
    public void A_guest_window_procedure_is_reentered_from_host_messages_with_converted_structures()
    {
        using var g = new WindowRig();
        var className = $"BcsBridge{Environment.ProcessId}x{Environment.TickCount64}";
        var name = g.Str(0x200, className);
        var title = g.Str(0x240, "bridge test");
        // WNDCLASSA at Data(0x100): style, lpfnWndProc, cbClsExtra, cbWndExtra, hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName
        uint[] wc = [0, g.WndProc, 0, 16, WindowRig.Base, 0, g.Call(U, "LoadCursorA", 0, 32512), 0, 0, name];
        for (var i = 0; i < wc.Length; i++) g.Module.Memory.WriteUInt32(WindowRig.Data(0x100 + 4u * (uint)i), wc[i]);
        var atom = g.Call(U, "RegisterClassA", WindowRig.Data(0x100));
        Assert.NotEqual(0u, atom);

        var hwnd = g.Call(U, "CreateWindowExA", 0, name, title, 0x00CF0000, 10, 10, 200, 120, 0, 0, WindowRig.Base, 0xCAFEBABE);
        Assert.NotEqual(0u, hwnd);
        Assert.True(HostIsWindow(hwnd));
        Assert.Equal(1u, g.Word(WindowRig.Rec + 0x14));                        // WM_NCCREATE reached the guest procedure
        Assert.Equal(0xCAFEBABEu, g.Word(WindowRig.Rec + 8));                  // lpCreateParams: the guest's own value back
        Assert.Equal(WindowRig.Base, g.Word(WindowRig.Rec + 0x10));            // hInstance: the guest's HINSTANCE, not the host's
        Assert.Equal(className, g.Module.Memory.ReadAnsiString(g.Word(WindowRig.Rec + 0xC)));   // lpszClass: a guest copy of the host string
        Assert.Equal(hwnd, g.Word(WindowRig.Rec + 0x18));
        Assert.True(g.Word(WindowRig.Rec) >= 4);

        Assert.Equal(g.WndProc, g.Call(U, "GetWindowLongA", hwnd, unchecked((uint)-4)));   // GWL_WNDPROC: the guest procedure
        Assert.Equal(WindowRig.Base, g.Call(U, "GetWindowLongA", hwnd, unchecked((uint)-6)));
        g.Call(U, "SetWindowLongA", hwnd, unchecked((uint)-21), 0x12345678);
        Assert.Equal(0x12345678u, g.Call(U, "GetWindowLongA", hwnd, unchecked((uint)-21)));
        g.Call(U, "SetWindowLongA", hwnd, 12, 0x87654321);                    // window extra bytes (cbWndExtra 16)
        Assert.Equal(0x87654321u, g.Call(U, "GetWindowLongA", hwnd, 12));

        // a posted message: MSG conversion both ways, then DispatchMessageA re-enters the guest
        Assert.Equal(1u, g.Call(U, "PostMessageA", hwnd, 0x8002, 7, 0xFFFFFFFE));
        var msg = WindowRig.Data(0x300);
        Assert.Equal(1u, g.Call(U, "PeekMessageA", msg, hwnd, 0x8002, 0x8002, 1));
        Assert.Equal((hwnd, 0x8002u, 7u, 0xFFFFFFFEu), (g.Word(msg), g.Word(msg + 4), g.Word(msg + 8), g.Word(msg + 12)));
        var before = g.Word(WindowRig.Rec);
        g.Call(U, "DispatchMessageA", msg);
        Assert.Equal(before + 1, g.Word(WindowRig.Rec));
        Assert.Equal(0x8002u, g.Word(WindowRig.Rec + 4));

        // a pointer message cannot be posted (ERROR_MESSAGE_SYNC_ONLY), as on Windows
        Assert.Equal(0u, g.Call(U, "PostMessageA", hwnd, 0x000C, 0, title));
        Assert.Equal(1159u, g.Resolver.Context.LastError);

        // a fault inside a host-delivered message ends the guest call that dispatched it, by name
        Assert.Equal(1u, g.Call(U, "PostMessageA", hwnd, 0x8001, 0, 0));
        Assert.Equal(1u, g.Call(U, "PeekMessageA", msg, hwnd, 0x8001, 0x8001, 1));
        var dispatch = g.Api(U, "DispatchMessageA");
        var fault = Assert.Throws<GuestImportException>(() => g.F("callptr1", dispatch, msg));
        Assert.Contains("NOPE.dll!Missing", fault.Message + fault.InnerException?.Message);

        // the module keeps working; destruction delivers WM_NCDESTROY to the guest last
        Assert.Equal(1u, g.Call(U, "DestroyWindow", hwnd));
        Assert.Equal(0x0082u, g.Word(WindowRig.Rec + 4));
        Assert.False(HostIsWindow(hwnd));
        Assert.NotEqual(0u, g.Call(U, "UnregisterClassA", name, WindowRig.Base));
    }

    [RequiresWindowsFact("needs the host's user32.dll (Windows or Wine)")]
    public void Handles_from_the_host_are_32_bit_significant_and_return_unchanged()
    {
        using var g = new WindowRig();
        var desktop = g.Call(U, "GetDesktopWindow");
        Assert.True(HostIsWindow(desktop));
        Assert.Equal(1u, g.Call(U, "IsWindow", desktop));
        var rect = WindowRig.Data(0x500);
        Assert.Equal(1u, g.Call(U, "GetWindowRect", desktop, rect));
        Assert.True((int)g.Word(rect + 8) > (int)g.Word(rect));               // right > left: the RECT came back
        var stock = g.Call("gdi32.dll", "GetStockObject", 4);                 // BLACK_BRUSH
        Assert.NotEqual(0u, stock);
        Assert.Equal(stock, g.Call("gdi32.dll", "GetStockObject", 4));
        var dc = g.Call("gdi32.dll", "CreateCompatibleDC", 0);
        Assert.NotEqual(0u, dc);
        var tm = WindowRig.Data(0x600);
        Assert.Equal(1u, g.Call("gdi32.dll", "GetTextMetricsA", dc, tm));
        Assert.True((int)g.Word(tm) > 0);                                      // tmHeight
        var size = WindowRig.Data(0x680);
        Assert.Equal(1u, g.Call("gdi32.dll", "GetTextExtentPoint32A", dc, g.Str(0x6A0, "hello"), 5, size));
        Assert.True((int)g.Word(size) > 0);
        Assert.Equal(1u, g.Call("gdi32.dll", "DeleteDC", dc));
    }
}

[Collection("GuestRuntime")]
public sealed class HostDirectDrawBridgeTests
{
    private static uint D(uint o) => ComRig.Data(o);

    [Theory]
    [InlineData(0x31545844u, 8)]  // DXT1
    [InlineData(0x33545844u, 16)] // DXT3
    [InlineData(0x35545844u, 16)] // DXT5
    public void DirectDrawSurface7_Lock_copies_compressed_linear_surface_bytes(uint fourCc, int blockBytes)
    {
        using var native = new FakeNativeSurface(8, 8, fourCc, blockBytes);
        using var g = new ComRig(64);
        var surface = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find)
            .Wrap(native.Pointer, DdConst.IidDirectDrawSurface7);
        var desc = D(0x500);
        g.Module.Memory.Write(desc, new byte[DdLayouts.GuestSurfaceDesc2]);
        g.Module.Memory.WriteUInt32(desc, DdLayouts.GuestSurfaceDesc2);

        Assert.Equal(0u, g.Vt(surface, 25, 0, desc, 0, 0));
        var guestSize = checked((int)g.Module.Memory.ReadUInt32(desc + 16));
        var guestBits = g.Module.Memory.ReadUInt32(desc + 36);
        var linearBytes = native.Bytes.Length;
        Assert.True(g.Module.Memory.IsMapped(guestBits, (uint)linearBytes));
        Assert.Equal(linearBytes, guestSize);
        Assert.Equal(native.Bytes, g.Module.Memory.ReadBytes(guestBits, linearBytes));

        var changed = Enumerable.Repeat((byte)0xA5, linearBytes).ToArray();
        g.Module.Memory.Write(guestBits, changed);
        Assert.Equal(0u, g.Vt(surface, 32, 0));
        Assert.Equal(changed, native.Bytes);
    }

    [Theory]
    [InlineData(0x31545844u, 8)]
    [InlineData(0x35545844u, 16)]
    public void DirectDrawSurface7_Lock_handles_a_tiny_compressed_mip_as_one_block(uint fourCc, int blockBytes)
    {
        using var native = new FakeNativeSurface(1, 1, fourCc, blockBytes);
        using var g = new ComRig(64);
        var surface = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find)
            .Wrap(native.Pointer, DdConst.IidDirectDrawSurface7);
        var desc = D(0x500);
        g.Module.Memory.Write(desc, new byte[DdLayouts.GuestSurfaceDesc2]);
        g.Module.Memory.WriteUInt32(desc, DdLayouts.GuestSurfaceDesc2);

        Assert.Equal(0u, g.Vt(surface, 25, 0, desc, 0, 0));
        var guestBits = g.Module.Memory.ReadUInt32(desc + 36);
        Assert.Equal(blockBytes, native.Bytes.Length);
        Assert.Equal(native.Bytes, g.Module.Memory.ReadBytes(guestBits, blockBytes));
        Assert.Equal(0u, g.Vt(surface, 32, 0));
    }

    [Theory]
    [InlineData(0x10u, false)] // DDLOCK_READONLY copies in, not back
    [InlineData(0x20u, true)]  // DDLOCK_WRITEONLY copies back, not in
    public void DirectDrawSurface7_CompressedLock_obeys_readonly_and_writeonly(uint flags, bool startsZero)
    {
        using var native = new FakeNativeSurface(4, 4, 0x31545844, 8);
        using var g = new ComRig(64);
        var surface = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find)
            .Wrap(native.Pointer, DdConst.IidDirectDrawSurface7);
        var desc = D(0x500);
        g.Module.Memory.Write(desc, new byte[DdLayouts.GuestSurfaceDesc2]);
        g.Module.Memory.WriteUInt32(desc, DdLayouts.GuestSurfaceDesc2);

        Assert.Equal(0u, g.Vt(surface, 25, 0, desc, flags, 0));
        var guestBits = g.Module.Memory.ReadUInt32(desc + 36);
        var surfaceBytes = native.Bytes.Length;
        var before = g.Module.Memory.ReadBytes(guestBits, surfaceBytes);
        Assert.Equal(startsZero, before.All(b => b == 0));
        var replacement = Enumerable.Repeat((byte)0x6C, surfaceBytes).ToArray();
        g.Module.Memory.Write(guestBits, replacement);
        Assert.Equal(0u, g.Vt(surface, 32, 0));
        Assert.Equal(flags == 0x20 ? replacement : native.OriginalBytes, native.Bytes);
    }

    [Fact]
    public void DirectDrawSurface7_Lock_rejects_unknown_fourcc_with_a_named_error()
    {
        using var native = new FakeNativeSurface(8, 8, 0x12345678, 8);
        using var g = new ComRig(64);
        var surface = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find)
            .Wrap(native.Pointer, DdConst.IidDirectDrawSurface7);
        var desc = D(0x500);
        g.Module.Memory.Write(desc, new byte[DdLayouts.GuestSurfaceDesc2]);
        g.Module.Memory.WriteUInt32(desc, DdLayouts.GuestSurfaceDesc2);

        var error = Assert.Throws<GuestImportException>(() => g.Vt(surface, 25, 0, desc, 0, 0));
        Assert.Contains("IDirectDrawSurface7::Lock", error.Message + error.InnerException?.Message);
        Assert.Contains("0x4", error.Message + error.InnerException?.Message);
    }

    [Fact]
    public void DirectDrawSurface7_Lock_rejects_a_too_small_linear_size_and_unlocks_before_reading_bits()
    {
        using var native = new FakeNativeSurface(8, 8, 0x31545844, 8, linearSizeOverride: 31, returnNullBits: true);
        using var g = new ComRig(64);
        var surface = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find)
            .Wrap(native.Pointer, DdConst.IidDirectDrawSurface7);
        var desc = D(0x500);
        g.Module.Memory.Write(desc, new byte[DdLayouts.GuestSurfaceDesc2]);
        g.Module.Memory.WriteUInt32(desc, DdLayouts.GuestSurfaceDesc2);

        var error = Assert.Throws<GuestImportException>(() => g.Vt(surface, 25, 0, desc, 0, 0));
        Assert.Contains("unusable compressed layout", error.Message + error.InnerException?.Message);
        Assert.Equal(1, native.UnlockCalls);
    }

    [Fact]
    public void DirectDrawSurface7_Lock_rejects_a_partial_compressed_rect_and_unlocks_before_reading_bits()
    {
        using var native = new FakeNativeSurface(8, 8, 0x31545844, 8, returnNullBits: true);
        using var g = new ComRig(64);
        var surface = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find)
            .Wrap(native.Pointer, DdConst.IidDirectDrawSurface7);
        var desc = D(0x500);
        var rect = D(0x600);
        g.Module.Memory.Write(desc, new byte[DdLayouts.GuestSurfaceDesc2]);
        g.Module.Memory.WriteUInt32(desc, DdLayouts.GuestSurfaceDesc2);
        foreach (var (i, value) in new[] { 0, 0, 4, 4 }.Select((value, i) => (i, value)))
            g.Module.Memory.WriteUInt32(rect + (uint)(i * 4), (uint)value);

        var error = Assert.Throws<GuestImportException>(() => g.Vt(surface, 25, rect, desc, 0, 0));
        Assert.Contains("whole-surface lock", error.Message + error.InnerException?.Message);
        Assert.Equal(1, native.UnlockCalls);
    }

    [Fact]
    public void DirectDrawSurface7_Root_releases_attached_native_surfaces_before_its_own_native_reference()
    {
        var releaseOrder = new List<string>();
        using var child = new FakeNativeSurface(4, 4, 0x31545844, 8, releaseEvents: releaseOrder, releaseName: "child", initialReferences: 0);
        using var root = new FakeNativeSurface(4, 4, 0x31545844, 8, releaseEvents: releaseOrder, releaseName: "root", attachedChild: child);
        using var g = new ComRig(64);
        var registry = HostComRegistry.For(GuestComHost.For(g.Resolver.Context, g.Module), DirectXTables.Find);
        var rootGuest = registry.Wrap(root.Pointer, DdConst.IidDirectDrawSurface7);
        var caps = D(0x500);
        var outSurface = D(0x520);
        g.Module.Memory.Write(caps, new byte[16]);

        Assert.Equal(0u, g.Vt(rootGuest, 12, caps, outSurface));
        var childGuest = g.Module.Memory.ReadUInt32(outSurface);
        Assert.NotEqual(0u, childGuest);
        Assert.Equal(1u, g.Release(childGuest)); // leave only the root's attached-surface reference
        Assert.Equal(0u, g.Release(rootGuest));

        Assert.Equal(new[] { "child", "root" }, releaseOrder);
    }

    private sealed unsafe class FakeNativeSurface : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Query(nint self, nint iid, nint result);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Ref(nint self);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Method(nint self, nint a, nint b, nint c, nint d);
        private readonly List<Delegate> keep = [];
        private readonly nint block;
        private readonly nint bits;
        private readonly List<string>? releaseEvents;
        private readonly string? releaseName;
        private readonly FakeNativeSurface? attachedChild;
        private int references = 1;
        public nint Pointer => block;
        public int Pitch { get; }
        public byte[] Bytes { get; }
        public byte[] OriginalBytes { get; }
        public int UnlockCalls { get; private set; }

        public FakeNativeSurface(int width, int height, uint fourCc, int blockBytes, int? linearSizeOverride = null, bool returnNullBits = false,
            List<string>? releaseEvents = null, string? releaseName = null, FakeNativeSurface? attachedChild = null, int initialReferences = 1)
        {
            this.releaseEvents = releaseEvents;
            this.releaseName = releaseName;
            this.attachedChild = attachedChild;
            references = initialReferences;
            int blockRows = (height + 3) / 4, blockColumns = (width + 3) / 4;
            Pitch = checked(blockColumns * blockRows * blockBytes);
            Bytes = Enumerable.Range(0, Pitch).Select(i => (byte)(i + 1)).ToArray();
            OriginalBytes = [.. Bytes];
            bits = (nint)NativeMemory.Alloc((nuint)Bytes.Length);
            Marshal.Copy(Bytes, 0, bits, Bytes.Length);

            var vtbl = (nint*)NativeMemory.AllocZeroed(33, (nuint)sizeof(nint));
            Query qi = (self, _, result) => { *(nint*)result = self; Interlocked.Increment(ref references); return 0; };
            Ref add = _ => (uint)Interlocked.Increment(ref references);
            Ref release = _ =>
            {
                var value = Interlocked.Decrement(ref references);
                if (value == 0 && releaseName is not null) releaseEvents!.Add(releaseName);
                return (uint)value;
            };
            Method lockMethod = (_, _, desc, _, _) =>
            {
                Marshal.WriteInt32(desc, 136);
                Marshal.WriteInt32(desc, 8, height);
                Marshal.WriteInt32(desc, 12, width);
                Marshal.WriteInt32(desc, 16, linearSizeOverride ?? Pitch);
                Marshal.WriteInt64(desc, 40, returnNullBits ? 0 : (long)bits);
                Marshal.WriteInt32(desc, 4, 0x81007); // DDSD_CAPS|HEIGHT|WIDTH|PIXELFORMAT|LINEARSIZE; union at offset 16 is a byte count
                Marshal.WriteInt32(desc, 84, 0x4); // DDPF_FOURCC
                Marshal.WriteInt32(desc, 88, unchecked((int)fourCc));
                Marshal.WriteInt32(desc, 92, 0); // compressed formats have no RGB bit count
                return 0;
            };
            Method unlockMethod = (_, _, _, _, _) =>
            {
                UnlockCalls++;
                Marshal.Copy(bits, Bytes, 0, Bytes.Length);
                return 0;
            };
            Method getAttachedMethod = (_, _, outSurface, _, _) =>
            {
                if (attachedChild is null) return unchecked((int)0x80004005);
                Interlocked.Increment(ref attachedChild.references); // GetAttachedSurface returns an owned native reference
                *(nint*)outSurface = attachedChild.Pointer;
                return 0;
            };
            keep.AddRange([qi, add, release, lockMethod, unlockMethod, getAttachedMethod]);
            vtbl[0] = Marshal.GetFunctionPointerForDelegate(qi);
            vtbl[1] = Marshal.GetFunctionPointerForDelegate(add);
            vtbl[2] = Marshal.GetFunctionPointerForDelegate(release);
            vtbl[12] = Marshal.GetFunctionPointerForDelegate(getAttachedMethod);
            vtbl[25] = Marshal.GetFunctionPointerForDelegate(lockMethod);
            vtbl[32] = Marshal.GetFunctionPointerForDelegate(unlockMethod);
            block = (nint)NativeMemory.Alloc((nuint)sizeof(nint));
            *(nint**)block = vtbl;
        }

        public void Dispose()
        {
            NativeMemory.Free(*(void**)block);
            NativeMemory.Free((void*)block);
            NativeMemory.Free((void*)bits);
        }
    }

    [RequiresHostDirectDrawFact]
    public void One_guest_identity_per_host_object_lifetimes_follow_the_guest_and_Lock_uses_a_shadow()
    {
        using var g = new ComRig(256);
        var mem = g.Module.Memory;
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var create = g.GetProc("ddraw.dll", "DirectDrawCreateEx");
        Assert.NotEqual(0u, create);
        mem.Write(D(0x500), DdConst.IidDirectDraw7.ToByteArray());
        Assert.Equal(0u, g.F("callptr4", create, 0, D(0x520), D(0x500), 0));
        var dd = mem.ReadUInt32(D(0x520));

        Assert.Equal(0u, g.QueryInterface(dd, DdConst.IidDirect3D7, out var d3d));
        Assert.NotEqual(dd, d3d);
        Assert.Equal(0u, g.QueryInterface(d3d, ComIids.IUnknown, out var unknown));
        Assert.Equal(dd, unknown);                                              // QI(IUnknown) from any face: the same pointer
        Assert.Equal(0u, g.QueryInterface(d3d, DdConst.IidDirectDraw7, out var again));
        Assert.Equal(dd, again);
        g.Release(unknown); g.Release(again);
        Assert.Equal(0x80004002u, g.QueryInterface(dd, new Guid("11111111-2222-3333-4444-555555555555"), out var none));   // the host refuses: E_NOINTERFACE
        Assert.Equal(0u, none);

        Assert.Equal(0u, g.Vt(dd, 20, 0, 8));                                  // SetCooperativeLevel(NULL, DDSCL_NORMAL)
        var desc = D(0x600);
        mem.Write(desc, new byte[124]);
        uint[] head = [124, 0x1007, 16, 16];                                    // dwSize, DDSD_CAPS|HEIGHT|WIDTH|PIXELFORMAT, height, width
        for (var i = 0; i < head.Length; i++) mem.WriteUInt32(desc + 4u * (uint)i, head[i]);
        uint[] pf = [32, 0x40, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0];               // DDPIXELFORMAT: DDPF_RGB, 32 bits
        for (var i = 0; i < pf.Length; i++) mem.WriteUInt32(desc + 72 + 4u * (uint)i, pf[i]);
        mem.WriteUInt32(desc + 104, 0x40 | 0x800);                              // DDSCAPS_OFFSCREENPLAIN | DDSCAPS_SYSTEMMEMORY
        Assert.Equal(0u, g.Vt(dd, 6, desc, D(0x6C0), 0));
        var surface = mem.ReadUInt32(D(0x6C0));
        Assert.Equal(0u, g.Vt(surface, 36, D(0x6C4)));                          // GetDDInterface: the existing DirectDraw object, not a new wrapper
        Assert.Equal(dd, mem.ReadUInt32(D(0x6C4)));
        g.Release(dd);

        uint Lock(uint flags)
        {
            mem.Write(D(0x700), new byte[124]);
            mem.WriteUInt32(D(0x700), 124);
            Assert.Equal(0u, g.Vt(surface, 25, 0, D(0x700), flags | 1, 0));      // DDLOCK_WAIT
            var bits = mem.ReadUInt32(D(0x700) + 36);
            Assert.True(bits != 0 && mem.IsMapped(bits, 16 * 4));               // lpSurface: guest memory
            return bits;
        }
        var bits = Lock(0);
        var pitch = mem.ReadUInt32(D(0x700) + 16);
        mem.WriteUInt32(bits + 3 * pitch + 5 * 4, 0x00ABCDEF);
        Assert.Equal(0u, g.Vt(surface, 32, 0));
        Assert.Equal(0x887601AAu, g.Vt(surface, 32, 0));                       // DDERR_NOTLOCKED
        bits = Lock(0x10);                                                      // DDLOCK_READONLY: a fresh shadow filled from the host's memory
        Assert.Equal(0x00ABCDEFu, mem.ReadUInt32(bits + 3 * pitch + 5 * 4));
        Assert.Equal(0u, g.Vt(surface, 32, 0));

        var fx = D(0x800);
        mem.Write(fx, new byte[100]);
        mem.WriteUInt32(fx, 100); mem.WriteUInt32(fx + 80, 0x00112233);         // dwFillColor
        Assert.Equal(0u, g.Vt(surface, 5, 0, 0, 0, 0x400 | 0x1000000, fx));      // Blt(DDBLT_COLORFILL | DDBLT_WAIT)
        bits = Lock(0x10);
        Assert.Equal(0x00112233u, mem.ReadUInt32(bits + 3 * pitch + 5 * 4));
        Assert.Equal(0u, g.Vt(surface, 32, 0));

        var absent = Assert.Throws<GuestImportException>(() => g.Vt(dd, 5, 0, 0, D(0x900), 0));   // CreatePalette: a named fault, not an invented HRESULT
        Assert.Contains("IDirectDraw7::CreatePalette is not bridged", absent.Message + absent.InnerException?.Message);

        var live = com.LiveObjects;
        Assert.Equal(0u, g.Release(surface));
        Assert.Equal(1u, g.Release(d3d));                                     // the DirectDraw object's last reference is dd's
        Assert.Equal(0u, g.Release(dd));
        Assert.Equal(live - 2, com.LiveObjects);
        var dead = Assert.Throws<GuestImportException>(() => g.AddRef(dd));
        Assert.Contains("after the object was destroyed", dead.Message + dead.InnerException?.Message);
    }

    [RequiresHostDirectDrawFact]
    public void DirectDrawSurface7_AttachedSurfaces_LifetimeTiedToRoot()
    {
        using var g = new ComRig(256);
        var mem = g.Module.Memory;
        var com = GuestComHost.For(g.Resolver.Context, g.Module);
        var create = g.GetProc("ddraw.dll", "DirectDrawCreateEx");
        mem.Write(D(0x500), DdConst.IidDirectDraw7.ToByteArray());
        Assert.Equal(0u, g.F("callptr4", create, 0, D(0x520), D(0x500), 0));
        var dd = mem.ReadUInt32(D(0x520));
        Assert.Equal(0u, g.Vt(dd, 20, 0, 8)); // SetCooperativeLevel(NULL, DDSCL_NORMAL)

        // Create 32x32 surface with 2 mipmap levels:
        var desc = D(0x600);
        mem.Write(desc, new byte[124]);
        uint[] head = [124, 0x21007, 32, 32];
        for (var i = 0; i < head.Length; i++) mem.WriteUInt32(desc + 4u * (uint)i, head[i]);
        uint[] pf = [32, 0x40, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0];
        for (var i = 0; i < pf.Length; i++) mem.WriteUInt32(desc + 72 + 4u * (uint)i, pf[i]);
        mem.WriteUInt32(desc + 104, 0x8 | 0x1000 | 0x400000 | 0x800); // DDSCAPS_COMPLEX | DDSCAPS_TEXTURE | DDSCAPS_MIPMAP | DDSCAPS_SYSTEMMEMORY
        mem.WriteUInt32(desc + 28, 2); // dwMipMapCount = 2
        Assert.Equal(0u, g.Vt(dd, 6, desc, D(0x6C0), 0));
        var rootSurf = mem.ReadUInt32(D(0x6C0));
        Assert.NotEqual(0u, rootSurf);

        // GetAttachedSurface(DDSCAPS_TEXTURE | DDSCAPS_MIPMAP, &dest)
        var caps = D(0x700);
        mem.WriteUInt32(caps, 0x1000 | 0x400000); // DDSCAPS_TEXTURE | DDSCAPS_MIPMAP
        mem.WriteUInt32(caps + 4, 0);
        mem.WriteUInt32(caps + 8, 0);
        mem.WriteUInt32(caps + 12, 0);
        Assert.Equal(0u, g.Vt(rootSurf, 12, caps, D(0x6C4)));
        var childSurf = mem.ReadUInt32(D(0x6C4));
        Assert.NotEqual(0u, childSurf);
        Assert.NotEqual(rootSurf, childSurf);

        // Releasing childSurf from the guest decrements its COM reference,
        // but because childSurf is attached to rootSurf, its lifetime is bound to rootSurf.
        // It must remain alive while rootSurf is alive.
        g.Release(childSurf);
        Assert.True(com.Find(childSurf)?.IsAlive == true);

        // Calling a method on the child surface works without "after the object was destroyed".
        var outCaps = D(0x750);
        Assert.Equal(0u, g.Vt(childSurf, 14, outCaps)); // GetCaps

        // Destroying rootSurf destroys both root and attached children.
        g.Release(rootSurf);
        Assert.False(com.Find(childSurf)?.IsAlive == true);
        var dead = Assert.Throws<GuestImportException>(() => g.Vt(childSurf, 14, outCaps));
        Assert.Contains("after the object was destroyed", dead.Message + dead.InnerException?.Message);

        g.Release(dd);
    }

    [RequiresHostDirectDrawFact]
    public void Direct3D7_creates_a_device_and_vertex_buffer_locks_with_a_shadow_clears_with_float_z_and_draws()
    {
        using var g = new ComRig(256);
        var mem = g.Module.Memory;
        var create = g.GetProc("ddraw.dll", "DirectDrawCreateEx");
        mem.Write(D(0x500), DdConst.IidDirectDraw7.ToByteArray());
        Assert.Equal(0u, g.F("callptr4", create, 0, D(0x520), D(0x500), 0));
        var dd = mem.ReadUInt32(D(0x520));
        Assert.Equal(0u, g.Vt(dd, 20, 0, 8));                                  // SetCooperativeLevel(NULL, DDSCL_NORMAL)
        Assert.Equal(0u, g.QueryInterface(dd, DdConst.IidDirect3D7, out var d3d));

        // 32x32 32-bit RGB 3D-device render target in system memory
        var desc = D(0x600);
        mem.Write(desc, new byte[124]);
        uint[] head = [124, 0x1007, 32, 32];
        for (var i = 0; i < head.Length; i++) mem.WriteUInt32(desc + 4u * (uint)i, head[i]);
        uint[] pf = [32, 0x40, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0];
        for (var i = 0; i < pf.Length; i++) mem.WriteUInt32(desc + 72 + 4u * (uint)i, pf[i]);
        mem.WriteUInt32(desc + 104, 0x40 | 0x800 | 0x2000);                    // DDSCAPS_OFFSCREENPLAIN | DDSCAPS_SYSTEMMEMORY | DDSCAPS_3DDEVICE
        Assert.Equal(0u, g.Vt(dd, 6, desc, D(0x6C0), 0));
        var target = mem.ReadUInt32(D(0x6C0));

        // CreateDevice: try IID_IDirect3DRGBDevice, then IID_IDirect3DHALDevice
        Guid[] clsidCandidates = [new("A4665C60-2673-11CF-A31A-00AA00B93356"), new("84E63DE0-46AA-11CF-816F-0000C020156E")];
        uint device = 0, hr = 1;
        foreach (var clsid in clsidCandidates)
        {
            mem.Write(D(0x540), clsid.ToByteArray());
            hr = g.Vt(d3d, 4, D(0x540), target, D(0x560));
            if (hr == 0) { device = mem.ReadUInt32(D(0x560)); break; }
        }
        Assert.Equal(0u, hr);
        Assert.NotEqual(0u, device);

        // Identity preserved for GetDirect3D and GetRenderTarget
        Assert.Equal(0u, g.Vt(device, 7, D(0x564)));
        Assert.Equal(d3d, mem.ReadUInt32(D(0x564)));
        g.Release(d3d);
        Assert.Equal(0u, g.Vt(device, 9, D(0x568)));
        Assert.Equal(target, mem.ReadUInt32(D(0x568)));
        g.Release(target);

        // CreateVertexBuffer (3 TLVERTEX vertices = 96 bytes) + Lock/Unlock shadow round-trip
        var vbDesc = D(0x700);
        mem.WriteUInt32(vbDesc, 16);
        mem.WriteUInt32(vbDesc + 4, 0x10000);                                  // D3DVBCAPS_SYSTEMMEMORY
        mem.WriteUInt32(vbDesc + 8, 0x1C4);                                    // D3DFVF_TLVERTEX
        mem.WriteUInt32(vbDesc + 12, 3);
        Assert.Equal(0u, g.Vt(d3d, 5, vbDesc, D(0x720), 0));
        var vb = mem.ReadUInt32(D(0x720));
        Assert.Equal(0u, g.Vt(vb, 3, 1, D(0x724), D(0x728)));                  // Lock(DDLOCK_WAIT, &ptr, &size)
        var vbShadow = mem.ReadUInt32(D(0x724));
        var vbSize = mem.ReadUInt32(D(0x728));
        Assert.Equal(96u, vbSize);
        Assert.True(vbShadow != 0 && mem.IsMapped(vbShadow, 96));
        for (uint i = 0; i < 24; i++) mem.WriteUInt32(vbShadow + i * 4, 0x1000u + i);
        Assert.Equal(0u, g.Vt(vb, 4));                                         // Unlock
        Assert.Equal(0x887601AAu, g.Vt(vb, 4));                                // DDERR_NOTLOCKED
        Assert.Equal(0u, g.Vt(vb, 3, 0x11, D(0x724), 0));                      // Lock(DDLOCK_READONLY | DDLOCK_WAIT, &ptr, NULL)
        var vbShadow2 = mem.ReadUInt32(D(0x724));
        for (uint i = 0; i < 24; i++) Assert.Equal(0x1000u + i, mem.ReadUInt32(vbShadow2 + i * 4));
        Assert.Equal(0u, g.Vt(vb, 4));

        // SetViewport + Clear(count=0, rects=NULL, flags=D3DCLEAR_TARGET(1), color=0x00224466, z=1.0f, stencil=0) + BeginScene/EndScene
        var vp = D(0x740);
        mem.WriteUInt32(vp, 0); mem.WriteUInt32(vp + 4, 0); mem.WriteUInt32(vp + 8, 32); mem.WriteUInt32(vp + 12, 32);
        mem.WriteUInt32(vp + 16, BitConverter.SingleToUInt32Bits(0.0f));
        mem.WriteUInt32(vp + 20, BitConverter.SingleToUInt32Bits(1.0f));
        Assert.Equal(0u, g.Vt(device, 13, vp));
        Assert.Equal(0u, g.Vt(device, 10, 0, 0, 1, 0x00224466, BitConverter.SingleToUInt32Bits(1.0f), 0));
        Assert.Equal(0u, g.Vt(device, 5));                                     // BeginScene
        Assert.Equal(0u, g.Vt(device, 6));                                     // EndScene

        // Verify target surface was cleared to 0x00224466
        mem.Write(D(0x800), new byte[124]);
        mem.WriteUInt32(D(0x800), 124);
        Assert.Equal(0u, g.Vt(target, 25, 0, D(0x800), 0x11, 0));
        var bits = mem.ReadUInt32(D(0x800) + 36);
        Assert.Equal(0x00224466u, mem.ReadUInt32(bits) & 0x00FFFFFFu);
        Assert.Equal(0u, g.Vt(target, 32, 0));

        g.Release(vb); g.Release(device); g.Release(target); g.Release(d3d); g.Release(dd);
    }
}

[Collection("GuestRuntime")]
public sealed class HostDirectXFileBridgeTests
{
    private static uint D(uint o) => ComRig.Data(o);

    [Fact]
    public void DirectXFile_table_contains_all_interfaces_and_resolves_through_DirectXTables()
    {
        var file = DirectXTables.Find(DirectXFileAdapters.IidDirectXFile);
        Assert.NotNull(file);
        Assert.Equal("IDirectXFile", file.Name);

        var enumObj = DirectXTables.Find(DirectXFileAdapters.IidDirectXFileEnumObject);
        Assert.NotNull(enumObj);
        Assert.Equal("IDirectXFileEnumObject", enumObj.Name);

        var data = DirectXTables.Find(DirectXFileAdapters.IidDirectXFileData);
        Assert.NotNull(data);
        Assert.Equal("IDirectXFileData", data.Name);
        Assert.Contains(DirectXFileAdapters.IidDirectXFileObject, data.AlsoAnswers);

        var obj = DirectXTables.Find(DirectXFileAdapters.IidDirectXFileObject);
        Assert.NotNull(obj);
        Assert.Equal("IDirectXFileObject", obj.Name);

        var dataRef = DirectXTables.Find(DirectXFileAdapters.IidDirectXFileDataReference);
        Assert.NotNull(dataRef);
        Assert.Equal("IDirectXFileDataReference", dataRef.Name);
        Assert.Contains(DirectXFileAdapters.IidDirectXFileObject, dataRef.AlsoAnswers);

        var binary = DirectXTables.Find(DirectXFileAdapters.IidDirectXFileBinary);
        Assert.NotNull(binary);
        Assert.Equal("IDirectXFileBinary", binary.Name);
        Assert.Contains(DirectXFileAdapters.IidDirectXFileObject, binary.AlsoAnswers);

        var saveObj = DirectXTables.Find(DirectXFileAdapters.IidDirectXFileSaveObject);
        Assert.NotNull(saveObj);
        Assert.Equal("IDirectXFileSaveObject", saveObj.Name);
    }

    [RequiresHostDirectXFileFact]
    public void DirectXFileCreate_creates_IDirectXFile_registers_templates_and_enumerates_memory_source()
    {
        using var g = new ComRig(256);
        var mem = g.Module.Memory;
        var create = g.GetProc("d3dxof.dll", "DirectXFileCreate");
        Assert.NotEqual(0u, create);

        mem.WriteUInt32(D(0x500), 0);
        Assert.Equal(0u, g.F("callptr1", create, D(0x500)));
        var dxf = mem.ReadUInt32(D(0x500));
        Assert.NotEqual(0u, dxf);

        // Minimal DirectX X-file text template and data
        const string xfile =
            "xof 0302txt 0064\n" +
            "template Header { <3D82AB43-62DA-11CF-AB39-0020AF71E433> WORD major; WORD minor; DWORD flags; }\n" +
            "Header root { 1; 0; 0; }\n";
        var xbytes = System.Text.Encoding.ASCII.GetBytes(xfile);

        // Memory load structure: { LPVOID lpMemory; DWORD dSize; }
        var memData = D(0x600);
        mem.Write(memData, xbytes);
        var memDesc = D(0x520);
        mem.WriteUInt32(memDesc, memData);
        mem.WriteUInt32(memDesc + 4, (uint)xbytes.Length);

        // IDirectXFile::CreateEnumObject(pvSource, DXFILELOAD_FROMMEMORY=2, &enumObj)
        mem.WriteUInt32(D(0x530), 0);
        Assert.Equal(0u, g.Vt(dxf, 3, memDesc, 2, D(0x530)));
        var enumObj = mem.ReadUInt32(D(0x530));
        Assert.NotEqual(0u, enumObj);

        // IDirectXFileEnumObject::GetNextDataObject(&dataObj)
        mem.WriteUInt32(D(0x540), 0);
        Assert.Equal(0u, g.Vt(enumObj, 3, D(0x540)));
        var dataObj = mem.ReadUInt32(D(0x540));
        Assert.NotEqual(0u, dataObj);

        // IDirectXFileData::GetType(&pTypeGuid)
        mem.WriteUInt32(D(0x550), 0);
        Assert.Equal(0u, g.Vt(dataObj, 6, D(0x550)));
        var pTypeGuid = mem.ReadUInt32(D(0x550));
        Assert.NotEqual(0u, pTypeGuid);
        var typeGuid = new Guid(mem.ReadBytes(pTypeGuid, 16));
        Assert.Equal(DirectXFileAdapters.IidDirectXFileObject, typeGuid);

        // IDirectXFileData::GetData(NULL, &size, &pData)
        mem.WriteUInt32(D(0x560), 0);
        mem.WriteUInt32(D(0x564), 0);
        Assert.Equal(0u, g.Vt(dataObj, 5, 0, D(0x560), D(0x564)));
        var dataSize = mem.ReadUInt32(D(0x560));
        var pData = mem.ReadUInt32(D(0x564));
        Assert.Equal(8u, dataSize);
        Assert.NotEqual(0u, pData);
        Assert.Equal(1u, (uint)mem.ReadByte(pData) | ((uint)mem.ReadByte(pData + 1) << 8));
        Assert.Equal(0u, (uint)mem.ReadByte(pData + 2) | ((uint)mem.ReadByte(pData + 3) << 8));
        Assert.Equal(0u, mem.ReadUInt32(pData + 4));

        // IDirectXFileData::GetName(name, &len)
        var nameBuf = D(0x570);
        mem.WriteUInt32(D(0x580), 32);
        Assert.Equal(0u, g.Vt(dataObj, 3, nameBuf, D(0x580)));
        var name = mem.ReadAnsiString(nameBuf);
        Assert.Equal("root", name);

        // Release
        g.Release(dataObj);
        g.Release(enumObj);
        g.Release(dxf);
    }
}
