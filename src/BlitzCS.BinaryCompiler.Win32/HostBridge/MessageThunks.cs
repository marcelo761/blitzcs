using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Win32.Com;

namespace BlitzCS.BinaryCompiler.Win32.HostBridge;

/// <summary>Scratch of one message crossing: guest blocks (freed at the end of the crossing) and native blocks.</summary>
internal sealed class MessageFrame(WindowState owner, IWin32Memory mem) : IDisposable
{
    private readonly List<uint> guestBlocks = [];
    public IWin32Memory Mem { get; } = mem;
    public WindowState Owner { get; } = owner;
    public NativeScope Native { get; } = new();
    /// <summary>The guest HINSTANCE that a host CREATESTRUCT.hInstance (always the host's own module) stands for.</summary>
    public uint GuestInstance { get; init; }

    public uint AllocGuest(int size)
    {
        var a = Owner.Scratch.Alloc((uint)Math.Max(size, 4), true);
        if (a == 0) throw new Win32UsageException("The guest scratch heap of the window bridge is exhausted.");
        guestBlocks.Add(a);
        return a;
    }

    public uint GuestString(nint host)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(Marshal.PtrToStringAnsi(host) ?? "");
        var a = AllocGuest(bytes.Length + 1);
        Mem.Write(a, [.. bytes, 0]);
        return a;
    }

    public void Dispose()
    {
        foreach (var b in guestBlocks) Owner.Scratch.Free(b);
        Native.Dispose();
    }
}

/// <summary>
/// How one window message crosses between the 64-bit host and the 32-bit guest. Messages whose wParam/lParam are values or handles use <see cref="ValueThunk"/> (a host value that does not
/// fit 32 bits faults by name); messages that carry pointers get an explicit converter, or <see cref="Unsupported"/> which faults naming the message. The host's structure is the original:
/// the guest works on a copy, and the guest's changes are copied back before the host sees the result (including when the guest calls DefWindowProc on the same message).
/// </summary>
internal abstract class MessageThunk
{
    public abstract void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl);
    /// <summary>After the guest handled the message: its changes to the copy go back into the host structure.</summary>
    public virtual void BackToHost(MessageFrame f, uint gw, uint gl, nint w, nint l) { }
    /// <summary>The guest passes a message to the host (DefWindowProcA, a message it built itself).</summary>
    public abstract void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l);
    /// <summary>After the host handled a guest-built message: its changes go back into the guest's structure.</summary>
    public virtual void BackToGuest(MessageFrame f, uint gw, uint gl, nint w, nint l) { }
    /// <summary>A pointer message: PostMessage cannot carry it (ERROR_MESSAGE_SYNC_ONLY).</summary>
    public virtual bool CarriesPointer => true;
}

internal sealed class ValueThunk : MessageThunk
{
    public static readonly ValueThunk Instance = new();
    public override bool CarriesPointer => false;
    public override void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl)
    {
        gw = Win32Layouts.Value(w, $"wParam of message 0x{msg:X4}");
        gl = Win32Layouts.Value(l, $"lParam of message 0x{msg:X4}");
    }
    public override void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l) { w = (nint)gw; l = (int)gl; }
}

internal sealed class Unsupported(string name) : MessageThunk
{
    private Win32UsageException Fault(uint msg) => new($"Window message {name} (0x{msg:X4}) carries a pointer and the window bridge has no marshaller for it.");
    public override void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl) => throw Fault(msg);
    public override void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l) => throw Fault(msg);
}

/// <summary>lParam points to a structure (wParam is a value). <paramref name="toGuest"/>/<paramref name="toHost"/> convert one image into the other in place.</summary>
internal sealed unsafe class StructThunk(int guestSize, int hostSize, bool inOut, Action<MessageFrame, nint, uint> toGuest, Action<MessageFrame, uint, nint> toHost, bool optional = false) : MessageThunk
{
    /// <summary>A structure whose layouts are identical (only 32-bit fields).</summary>
    public static StructThunk Same(int size, bool inOut) => new(size, size, inOut,
        (f, h, g) => f.Mem.Write(g, new ReadOnlySpan<byte>((void*)h, size)),
        (f, g, h) => f.Mem.Read(g, new Span<byte>((void*)h, size)));

    public override void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl)
    {
        gw = Win32Layouts.Value(w, $"wParam of message 0x{msg:X4}");
        if (l == 0 && optional) { gl = 0; return; }
        if (l == 0) throw new Win32UsageException($"Message 0x{msg:X4} arrived with a NULL structure pointer.");
        gl = f.AllocGuest(guestSize);
        toGuest(f, l, gl);
    }
    public override void BackToHost(MessageFrame f, uint gw, uint gl, nint w, nint l) { if (inOut && gl != 0) toHost(f, gl, l); }
    public override void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l)
    {
        w = (nint)gw;
        if (gl == 0) { l = optional ? 0 : throw new Win32UsageException($"Message 0x{msg:X4} needs a structure pointer."); return; }
        l = f.Native.AllocZeroed(hostSize);
        toHost(f, gl, l);
    }
    public override void BackToGuest(MessageFrame f, uint gw, uint gl, nint w, nint l) { if (inOut && gl != 0) toGuest(f, l, gl); }
}

/// <summary>lParam is an input ANSI string (WM_SETTEXT, WM_SETTINGCHANGE ...), possibly NULL.</summary>
internal sealed class StringInThunk : MessageThunk
{
    public static readonly StringInThunk Instance = new();
    public override void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl)
    {
        gw = Win32Layouts.Value(w, $"wParam of message 0x{msg:X4}");
        gl = l == 0 ? 0 : f.GuestString(l);
    }
    public override void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l)
    {
        w = (nint)gw;
        l = gl == 0 ? 0 : f.Native.Copy([.. f.Mem.ReadCString(gl), 0]);
    }
}

/// <summary>WM_GETTEXT: wParam = buffer size in characters, lParam = output buffer.</summary>
internal sealed unsafe class GetTextThunk : MessageThunk
{
    public override void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl)
    {
        gw = Win32Layouts.Value(w, "WM_GETTEXT size");
        gl = gw == 0 ? 0 : f.AllocGuest((int)Math.Min(gw, 1u << 20));
    }
    public override void BackToHost(MessageFrame f, uint gw, uint gl, nint w, nint l)
    {
        if (gl == 0 || l == 0) return;
        var n = (int)Math.Min(gw, 1u << 20);
        var bytes = f.Mem.ReadBytes(gl, n);
        if (bytes.AsSpan().IndexOf((byte)0) < 0) bytes[n - 1] = 0;   // the host buffer is always terminated
        bytes.CopyTo(new Span<byte>((void*)l, n));
    }
    public override void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l)
    {
        w = (nint)gw;
        l = gw == 0 ? 0 : f.Native.AllocZeroed((int)Math.Min(gw, 1u << 20));
    }
    public override void BackToGuest(MessageFrame f, uint gw, uint gl, nint w, nint l)
    {
        if (gl != 0 && l != 0) f.Mem.Write(gl, new ReadOnlySpan<byte>((void*)l, (int)Math.Min(gw, 1u << 20)));
    }
}

internal static unsafe class MessageThunks
{
    public const uint WM_CREATE = 0x0001, WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_SETTINGCHANGE = 0x001A, WM_DEVMODECHANGE = 0x001B, WM_GETMINMAXINFO = 0x0024,
        WM_WINDOWPOSCHANGING = 0x0046, WM_WINDOWPOSCHANGED = 0x0047, WM_STYLECHANGING = 0x007C, WM_STYLECHANGED = 0x007D, WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082,
        WM_NCCALCSIZE = 0x0083, WM_GETDLGCODE = 0x0087, WM_TIMER = 0x0113, WM_SIZING = 0x0214, WM_MOVING = 0x0216;

    private static readonly Dictionary<uint, MessageThunk> table = Build();

    public static MessageThunk For(uint msg) => msg < 0x0400 && table.TryGetValue(msg, out var t) ? t : ValueThunk.Instance;

    private static Dictionary<uint, MessageThunk> Build()
    {
        var t = new Dictionary<uint, MessageThunk>();
        var createStruct = new StructThunk(Win32Layouts.GuestCreateStructA, Win32Layouts.HostCreateStructA, false, CreateStructToGuest, CreateStructToHost);
        t[WM_CREATE] = createStruct; t[WM_NCCREATE] = createStruct;
        foreach (var m in new[] { WM_SETTEXT, WM_SETTINGCHANGE, WM_DEVMODECHANGE }) t[m] = StringInThunk.Instance;
        t[WM_GETTEXT] = new GetTextThunk();
        t[WM_GETMINMAXINFO] = StructThunk.Same(40, true);
        var windowPos = new StructThunk(Win32Layouts.GuestWindowPos, Win32Layouts.HostWindowPos, true,
            (f, h, g) => { Span<byte> b = stackalloc byte[Win32Layouts.GuestWindowPos]; Win32Layouts.WindowPosToGuest(new ReadOnlySpan<byte>((void*)h, Win32Layouts.HostWindowPos), b); f.Mem.Write(g, b); },
            (f, g, h) => Win32Layouts.WindowPosToHost(f.Mem.ReadBytes(g, Win32Layouts.GuestWindowPos), new Span<byte>((void*)h, Win32Layouts.HostWindowPos)));
        t[WM_WINDOWPOSCHANGING] = windowPos; t[WM_WINDOWPOSCHANGED] = windowPos;
        t[WM_STYLECHANGING] = StructThunk.Same(8, true); t[WM_STYLECHANGED] = StructThunk.Same(8, false);
        t[WM_SIZING] = StructThunk.Same(16, true); t[WM_MOVING] = StructThunk.Same(16, true);
        t[WM_NCCALCSIZE] = new NcCalcSizeThunk();
        t[WM_GETDLGCODE] = new StructThunk(Win32Layouts.GuestMsg, Win32Layouts.HostMsg, false,
            (f, h, g) => { Span<byte> b = stackalloc byte[Win32Layouts.GuestMsg]; Win32Layouts.MsgToGuest(new ReadOnlySpan<byte>((void*)h, Win32Layouts.HostMsg), b); f.Mem.Write(g, b); },
            (f, g, h) => Win32Layouts.MsgToHost(f.Mem.ReadBytes(g, Win32Layouts.GuestMsg), new Span<byte>((void*)h, Win32Layouts.HostMsg)), optional: true);
        foreach (var (m, n) in new (uint, string)[]
        {
            (0x002B, "WM_DRAWITEM"), (0x002C, "WM_MEASUREITEM"), (0x002D, "WM_DELETEITEM"), (0x0039, "WM_COMPAREITEM"), (0x004A, "WM_COPYDATA"), (0x004E, "WM_NOTIFY"),
            (0x0053, "WM_HELP"), (0x0213, "WM_NEXTMENU"), (0x0220, "WM_MDICREATE"), (0x0309, "WM_PAINTCLIPBOARD"), (0x030B, "WM_SIZECLIPBOARD"), (0x030C, "WM_ASKCBFORMATNAME"),
            (0x033F, "WM_GETTITLEBARINFOEX"),
        }) t[m] = new Unsupported(n);
        return t;
    }

    private static void CreateStructToGuest(MessageFrame f, nint h, uint g)
    {
        Span<byte> b = stackalloc byte[Win32Layouts.GuestCreateStructA];
        Win32Layouts.CreateStructToGuest(new ReadOnlySpan<byte>((void*)h, Win32Layouts.HostCreateStructA), b, (offset, value) => offset switch
        {
            0 => Win32Layouts.Value(value, "CREATESTRUCT.lpCreateParams"),   // the guest's own lpParam, zero-extended on the way in
            8 => value == 0 ? 0 : value == HostExports.HostInstance ? f.GuestInstance : throw new Win32UsageException($"CREATESTRUCT.hInstance 0x{value:X} is not the HINSTANCE guest windows are created under."),
            64 when value < 0x10000 => (uint)value,                               // class atom
            _ => value == 0 ? 0 : f.GuestString(value),
        });
        f.Mem.Write(g, b);
    }

    private static void CreateStructToHost(MessageFrame f, uint g, nint h) =>
        Win32Layouts.CreateStructToHost(f.Mem.ReadBytes(g, Win32Layouts.GuestCreateStructA), new Span<byte>((void*)h, Win32Layouts.HostCreateStructA), (offset, value) => offset switch
        {
            0 => (nint)value,
            4 => value == 0 ? 0 : HostExports.HostInstance,
            40 when value < 0x10000 => (nint)value,
            _ => value == 0 ? 0 : f.Native.Copy([.. f.Mem.ReadCString(value), 0]),
        });

    /// <summary>WM_NCCALCSIZE: wParam FALSE = RECT*, TRUE = NCCALCSIZE_PARAMS* (three RECTs and a WINDOWPOS pointer, whose WINDOWPOS is converted too).</summary>
    private sealed class NcCalcSizeThunk : MessageThunk
    {
        private const int Rects = 48;
        public override void ToGuest(MessageFrame f, uint msg, nint w, nint l, out uint gw, out uint gl)
        {
            gw = Win32Layouts.Value(w, "WM_NCCALCSIZE wParam");
            if (gw == 0) { gl = f.AllocGuest(16); f.Mem.Write(gl, new ReadOnlySpan<byte>((void*)l, 16)); return; }
            gl = f.AllocGuest(Win32Layouts.GuestNcCalcSizeParams + Win32Layouts.GuestWindowPos);
            HostToGuest(f, l, gl);
        }
        private static void HostToGuest(MessageFrame f, nint l, uint gl)
        {
            f.Mem.Write(gl, new ReadOnlySpan<byte>((void*)l, Rects));
            var pos = *(nint*)(l + Rects);
            var guestPos = pos == 0 ? 0 : gl + Win32Layouts.GuestNcCalcSizeParams;
            f.Mem.WriteUInt32(gl + Rects, guestPos);
            if (pos != 0)
            {
                Span<byte> b = stackalloc byte[Win32Layouts.GuestWindowPos];
                Win32Layouts.WindowPosToGuest(new ReadOnlySpan<byte>((void*)pos, Win32Layouts.HostWindowPos), b);
                f.Mem.Write(guestPos, b);
            }
        }
        private static void GuestToHost(MessageFrame f, uint gl, nint l)
        {
            f.Mem.Read(gl, new Span<byte>((void*)l, Rects));
            var guestPos = f.Mem.ReadUInt32(gl + Rects);
            var pos = *(nint*)(l + Rects);
            if (guestPos != 0 && pos != 0) Win32Layouts.WindowPosToHost(f.Mem.ReadBytes(guestPos, Win32Layouts.GuestWindowPos), new Span<byte>((void*)pos, Win32Layouts.HostWindowPos));
        }
        public override void BackToHost(MessageFrame f, uint gw, uint gl, nint w, nint l)
        {
            if (gw == 0) f.Mem.Read(gl, new Span<byte>((void*)l, 16));
            else GuestToHost(f, gl, l);
        }
        public override void ToHost(MessageFrame f, uint msg, uint gw, uint gl, out nint w, out nint l)
        {
            w = (nint)gw;
            if (gw == 0) { l = f.Native.Copy(f.Mem.ReadBytes(gl, 16)); return; }
            l = f.Native.AllocZeroed(Win32Layouts.HostNcCalcSizeParams + Win32Layouts.HostWindowPos);
            var guestPos = f.Mem.ReadUInt32(gl + Rects);
            *(nint*)(l + Rects) = guestPos == 0 ? 0 : l + Win32Layouts.HostNcCalcSizeParams;
            GuestToHost(f, gl, l);
        }
        public override void BackToGuest(MessageFrame f, uint gw, uint gl, nint w, nint l)
        {
            if (gw == 0) { f.Mem.Write(gl, new ReadOnlySpan<byte>((void*)l, 16)); return; }
            f.Mem.Write(gl, new ReadOnlySpan<byte>((void*)l, Rects));
            var guestPos = f.Mem.ReadUInt32(gl + Rects);
            var pos = *(nint*)(l + Rects);
            if (guestPos != 0 && pos != 0)
            {
                Span<byte> b = stackalloc byte[Win32Layouts.GuestWindowPos];
                Win32Layouts.WindowPosToGuest(new ReadOnlySpan<byte>((void*)pos, Win32Layouts.HostWindowPos), b);
                f.Mem.Write(guestPos, b);
            }
        }
    }
}
