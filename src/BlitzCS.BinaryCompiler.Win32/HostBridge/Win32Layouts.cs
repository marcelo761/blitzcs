using System.Buffers.Binary;

namespace BlitzCS.BinaryCompiler.Win32.HostBridge;

/// <summary>
/// The 32-bit (guest) and 64-bit (host) images of the user32 structures that contain pointers or pointer-sized fields, and their field-by-field conversion. Structures made only of
/// 32-bit fields (RECT, POINT, SIZE, MINMAXINFO, STYLESTRUCT, TEXTMETRICA, ABC) have identical layouts and are copied as bytes by the adapter tables instead.
/// Handles convert with <see cref="HostHandles"/>; values that are addresses on one side (lpCreateParams, HINSTANCE, string pointers) are converted by the caller through the delegates.
/// </summary>
public static class Win32Layouts
{
    public const int GuestWndClassA = 40, HostWndClassA = 72;
    public const int GuestMsg = 28, HostMsg = 48;
    public const int GuestCreateStructA = 48, HostCreateStructA = 80;
    public const int GuestWindowPos = 28, HostWindowPos = 40;
    public const int GuestNcCalcSizeParams = 52, HostNcCalcSizeParams = 56;
    public const int GuestPaintStruct = 64, HostPaintStruct = 72;

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static void U32(Span<byte> s, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s[o..], v);
    private static nint P(ReadOnlySpan<byte> s, int o) => (nint)BinaryPrimitives.ReadInt64LittleEndian(s[o..]);
    private static void P(Span<byte> s, int o, nint v) => BinaryPrimitives.WriteInt64LittleEndian(s[o..], v);

    /// <summary>A WPARAM/LPARAM-style value the host produced, as a guest word: it must fit 32 bits (zero- or sign-extended), otherwise it is a 64-bit pointer and <paramref name="what"/> faults.</summary>
    public static uint Value(nint host, string what) =>
        HostHandles.Fits(host) ? (uint)host : throw new Win32UsageException($"{what}: the host value 0x{host:X} does not fit a 32-bit word (it is a 64-bit pointer with no marshaller).");

    // ---- WNDCLASSA ----

    /// <summary>Guest WNDCLASSA (40 bytes) to the host layout (72 bytes). The window procedure, HINSTANCE and string pointers are supplied by the caller (they are not convertible values).</summary>
    public static void WndClassToHost(ReadOnlySpan<byte> g, Span<byte> h, nint wndProc, nint instance, nint menuName, nint className)
    {
        h[..HostWndClassA].Clear();
        U32(h, 0, U32(g, 0));                               // style
        P(h, 8, wndProc);
        U32(h, 16, U32(g, 8)); U32(h, 20, U32(g, 12));     // cbClsExtra, cbWndExtra
        P(h, 24, instance);
        P(h, 32, HostHandles.ToHost(U32(g, 20)));           // hIcon
        P(h, 40, HostHandles.ToHost(U32(g, 24)));           // hCursor
        P(h, 48, HostHandles.ToHost(U32(g, 28)));           // hbrBackground (a handle or COLOR_xxx + 1)
        P(h, 56, menuName);
        P(h, 64, className);
    }

    // ---- MSG ----

    public static void MsgToGuest(ReadOnlySpan<byte> h, Span<byte> g)
    {
        var msg = U32(h, 8);
        U32(g, 0, HostHandles.ToGuest(P(h, 0), "MSG.hwnd"));
        U32(g, 4, msg);
        U32(g, 8, Value(P(h, 16), $"MSG.wParam of message 0x{msg:X4}"));
        U32(g, 12, Value(P(h, 24), $"MSG.lParam of message 0x{msg:X4}"));
        U32(g, 16, U32(h, 32));                                     // time
        U32(g, 20, U32(h, 36)); U32(g, 24, U32(h, 40));             // pt
    }

    public static void MsgToHost(ReadOnlySpan<byte> g, Span<byte> h)
    {
        h[..HostMsg].Clear();
        P(h, 0, HostHandles.ToHost(U32(g, 0)));
        U32(h, 8, U32(g, 4));
        P(h, 16, (nint)U32(g, 8));                      // WPARAM: zero-extended
        P(h, 24, (int)U32(g, 12));                      // LPARAM: sign-extended
        U32(h, 32, U32(g, 16));
        U32(h, 36, U32(g, 20)); U32(h, 40, U32(g, 24));
    }

    // ---- CREATESTRUCTA ----

    /// <summary>Host CREATESTRUCTA (80 bytes) to guest (48 bytes). <paramref name="pointer"/> converts lpCreateParams/hInstance/lpszName/lpszClass (field offset in the host layout, value) to guest words.</summary>
    public static void CreateStructToGuest(ReadOnlySpan<byte> h, Span<byte> g, Func<int, nint, uint> pointer)
    {
        U32(g, 0, pointer(0, P(h, 0)));                                  // lpCreateParams
        U32(g, 4, pointer(8, P(h, 8)));                                  // hInstance
        U32(g, 8, HostHandles.ToGuest(P(h, 16), "CREATESTRUCT.hMenu"));
        U32(g, 12, HostHandles.ToGuest(P(h, 24), "CREATESTRUCT.hwndParent"));
        for (var i = 0; i < 4; i++) U32(g, 16 + 4 * i, U32(h, 32 + 4 * i));   // cy, cx, y, x
        U32(g, 32, U32(h, 48));                                          // style
        U32(g, 36, pointer(56, P(h, 56)));                               // lpszName
        U32(g, 40, pointer(64, P(h, 64)));                               // lpszClass
        U32(g, 44, U32(h, 72));                                          // dwExStyle
    }

    /// <summary>Guest CREATESTRUCTA to host; <paramref name="pointer"/> converts the guest words at guest offsets 0, 4, 36, 40.</summary>
    public static void CreateStructToHost(ReadOnlySpan<byte> g, Span<byte> h, Func<int, uint, nint> pointer)
    {
        h[..HostCreateStructA].Clear();
        P(h, 0, pointer(0, U32(g, 0)));
        P(h, 8, pointer(4, U32(g, 4)));
        P(h, 16, HostHandles.ToHost(U32(g, 8)));
        P(h, 24, HostHandles.ToHost(U32(g, 12)));
        for (var i = 0; i < 4; i++) U32(h, 32 + 4 * i, U32(g, 16 + 4 * i));
        U32(h, 48, U32(g, 32));
        P(h, 56, pointer(36, U32(g, 36)));
        P(h, 64, pointer(40, U32(g, 40)));
        U32(h, 72, U32(g, 44));
    }

    // ---- WINDOWPOS ----

    public static void WindowPosToGuest(ReadOnlySpan<byte> h, Span<byte> g)
    {
        U32(g, 0, HostHandles.ToGuest(P(h, 0), "WINDOWPOS.hwnd"));
        U32(g, 4, HostHandles.ToGuest(P(h, 8), "WINDOWPOS.hwndInsertAfter"));   // HWND_TOP 0, HWND_BOTTOM 1, HWND_TOPMOST -1, HWND_NOTOPMOST -2 survive sign extension
        for (var i = 0; i < 5; i++) U32(g, 8 + 4 * i, U32(h, 16 + 4 * i));      // x, y, cx, cy, flags
    }

    public static void WindowPosToHost(ReadOnlySpan<byte> g, Span<byte> h)
    {
        h[..HostWindowPos].Clear();
        P(h, 0, HostHandles.ToHost(U32(g, 0)));
        P(h, 8, HostHandles.ToHost(U32(g, 4)));
        for (var i = 0; i < 5; i++) U32(h, 16 + 4 * i, U32(g, 8 + 4 * i));
    }

    // ---- PAINTSTRUCT ----

    public static void PaintStructToGuest(ReadOnlySpan<byte> h, Span<byte> g)
    {
        U32(g, 0, HostHandles.ToGuest(P(h, 0), "PAINTSTRUCT.hdc"));
        h.Slice(8, 4 + 16 + 4 + 4 + 32).CopyTo(g[4..]);    // fErase, rcPaint, fRestore, fIncUpdate, rgbReserved
    }

    public static void PaintStructToHost(ReadOnlySpan<byte> g, Span<byte> h)
    {
        h[..HostPaintStruct].Clear();
        P(h, 0, HostHandles.ToHost(U32(g, 0)));
        g.Slice(4, 60).CopyTo(h[8..]);
    }
}
