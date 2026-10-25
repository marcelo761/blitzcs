using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class StringTests
{
    private static string WideAt(World w, uint p, int n) => new string(w.Bytes(p, n * 2).Chunk(2).Select(b => (char)(b[0] | b[1] << 8)).ToArray());

    [Fact]
    public void MultiByteToWideCharCodePages()
    {
        using var w = new World();
        var src = w.Buf(8); w.Mem.Write(src, [(byte)'h', 0xE9, (byte)'l', 0x80, 0]);
        var dst = w.Buf(64);
        Assert.Equal(5u, w.Kernel("MultiByteToWideChar", 1252, 0, src, 0xFFFFFFFF, dst, 32)); // includes the terminator
        Assert.Equal("h\u00E9l\u20AC", WideAt(w, dst, 4));
        Assert.Equal(4u, w.Kernel("MultiByteToWideChar", 0, 0, src, 4, 0, 0));               // CP_ACP, size query
        Assert.Equal(4u, w.Kernel("MultiByteToWideChar", 1252, 0, src, 4, dst, 4));
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 1252, 0, src, 4, dst, 3));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        Assert.Equal(1u, w.Kernel("MultiByteToWideChar", 437, 0, src, 1, dst, 4));
        Assert.Equal(4u, w.Kernel("MultiByteToWideChar", 1, 0, w.Str("abcd"), 4, dst, 4));    // CP_OEMCP = 437
    }

    [Fact]
    public void MultiByteToWideCharUtf8AndErrors()
    {
        using var w = new World();
        var dst = w.Buf(64);
        var euro = w.Str("\u00E2\u0082\u00AC"); // E2 82 AC
        Assert.Equal(1u, w.Kernel("MultiByteToWideChar", 65001, 0, euro, 3, dst, 8));
        Assert.Equal(0x20ACu, w.Mem.ReadUInt16(dst));
        var bad = w.Str("a\u00FFb");
        Assert.Equal(3u, w.Kernel("MultiByteToWideChar", 65001, 0, bad, 3, dst, 8));
        Assert.Equal(0xFFFDu, w.Mem.ReadUInt16(dst + 2));
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 65001, 8, bad, 3, dst, 8));
        Assert.Equal(Win32Error.NoUnicodeTranslation, w.LastError);
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 65001, 4, euro, 3, dst, 8));
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 932, 0, euro, 3, dst, 8));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 1252, 0, 0, 3, dst, 8));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 1252, 0, euro, 0, dst, 8));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 1252, 2, euro, 3, dst, 8)); // MB_COMPOSITE: explicit refusal
        Assert.Equal(Win32Error.NotSupported, w.LastError);
        Assert.Equal(0u, w.Kernel("MultiByteToWideChar", 1252, 3, euro, 3, dst, 8));
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);
    }

    [Fact]
    public void WideCharToMultiByteCodePages()
    {
        using var w = new World();
        var wide = w.Wide("A\u20ACB");
        var dst = w.Buf(32);
        Assert.Equal(4u, w.Kernel("WideCharToMultiByte", 1252, 0, wide, 0xFFFFFFFF, dst, 16, 0, 0));
        Assert.Equal(new byte[] { (byte)'A', 0x80, (byte)'B', 0 }, w.Bytes(dst, 4));
        Assert.Equal(4u, w.Kernel("WideCharToMultiByte", 1252, 0, wide, 0xFFFFFFFF, 0, 0, 0, 0));
        Assert.Equal(0u, w.Kernel("WideCharToMultiByte", 1252, 0, wide, 3, dst, 2, 0, 0));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        var used = w.Buf(4); var def = w.Buf(1); w.Mem.WriteByte(def, (byte)'#');
        var unmappable = w.Wide("A\u0100\uD83D\uDE00");
        Assert.Equal(3u, w.Kernel("WideCharToMultiByte", 1252, 0, unmappable, 4, dst, 16, def, used));
        Assert.Equal(new byte[] { (byte)'A', (byte)'#', (byte)'#' }, w.Bytes(dst, 3));
        Assert.Equal(1u, w.Mem.ReadUInt32(used));
        Assert.Equal(3u, w.Kernel("WideCharToMultiByte", 1252, 0, unmappable, 4, dst, 16, 0, used));
        Assert.Equal((byte)'?', w.Mem.ReadByte(dst + 1));
        Assert.Equal(1u, w.Kernel("WideCharToMultiByte", 1252, 0, w.Wide("Z"), 1, dst, 16, 0, used));
        Assert.Equal(0u, w.Mem.ReadUInt32(used));
        Assert.Equal(1u, w.Kernel("WideCharToMultiByte", 437, 0, w.Wide("\u00E9"), 1, dst, 4, 0, 0));
        Assert.Equal(0x82, w.Mem.ReadByte(dst));
    }

    [Fact]
    public void WideCharToMultiByteUtf8()
    {
        using var w = new World();
        var dst = w.Buf(32);
        Assert.Equal(3u, w.Kernel("WideCharToMultiByte", 65001, 0, w.Wide("\u20AC"), 1, dst, 8, 0, 0));
        Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC }, w.Bytes(dst, 3));
        Assert.Equal(4u, w.Kernel("WideCharToMultiByte", 65001, 0, w.Wide("\uD83D\uDE00"), 2, dst, 8, 0, 0));
        Assert.Equal(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, w.Bytes(dst, 4));
        var lone = w.Wide("\uD83D");
        Assert.Equal(3u, w.Kernel("WideCharToMultiByte", 65001, 0, lone, 1, dst, 8, 0, 0));
        Assert.Equal(new byte[] { 0xEF, 0xBF, 0xBD }, w.Bytes(dst, 3));
        Assert.Equal(0u, w.Kernel("WideCharToMultiByte", 65001, 0x80, lone, 1, dst, 8, 0, 0));
        Assert.Equal(Win32Error.NoUnicodeTranslation, w.LastError);
        Assert.Equal(0u, w.Kernel("WideCharToMultiByte", 65001, 0, lone, 1, dst, 8, dst, 0));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("WideCharToMultiByte", 65001, 0x40, lone, 1, dst, 8, 0, 0));
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);
        Assert.Equal(0u, w.Kernel("WideCharToMultiByte", 1252, 0x80, lone, 1, dst, 8, 0, 0)); // WC_ERR_INVALID_CHARS is UTF-8 only
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);
        Assert.Equal(0u, w.Kernel("WideCharToMultiByte", 1252, 0, 0, 1, dst, 8, 0, 0));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void LcMapStringCaseMapping()
    {
        using var w = new World();
        var dst = w.Buf(32);
        Assert.Equal(4u, w.Kernel("LCMapStringW", 0, 0x100, w.Wide("AbC"), 0xFFFFFFFF, dst, 16));
        Assert.Equal("abc\0", WideAt(w, dst, 4));
        Assert.Equal(3u, w.Kernel("LCMapStringW", 0x400, 0x200, w.Wide("abc"), 3, dst, 3));
        Assert.Equal("ABC", WideAt(w, dst, 3));
        Assert.Equal(3u, w.Kernel("LCMapStringW", 0, 0x200, w.Wide("abc"), 3, 0, 0));
        Assert.Equal(0u, w.Kernel("LCMapStringW", 0, 0x200, w.Wide("abc"), 3, dst, 2));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        Assert.Equal(0u, w.Kernel("LCMapStringW", 0, 0x400, w.Wide("abc"), 3, dst, 16)); // SORTKEY unsupported
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);
        Assert.Equal(0u, w.Kernel("LCMapStringW", 0, 0x300, w.Wide("abc"), 3, dst, 16));
        Assert.Equal(3u, w.Kernel("LCMapStringW", 0, 0, w.Wide("abc"), 3, dst, 16)); // no flag: copy
        Assert.Equal(4u, w.Kernel("LCMapStringA", 0, 0x200, w.Str("ab\u00E9"), 0xFFFFFFFF, dst, 16));
        Assert.Equal(new byte[] { (byte)'A', (byte)'B', 0xC9, 0 }, w.Bytes(dst, 4)); // é -> É in CP1252
        Assert.Equal(0u, w.Kernel("LCMapStringA", 0, 0x200, 0, 3, dst, 16));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void GetStringTypeClassifiesCType1()
    {
        using var w = new World();
        var out16 = w.Buf(64);
        var text = w.Str("Aa1 \n\t!\u00E9");
        Assert.Equal(1u, w.Kernel("GetStringTypeA", 0, 1, text, 8, out16));
        var types = Enumerable.Range(0, 8).Select(i => w.Mem.ReadUInt16(out16 + (uint)i * 2)).ToArray();
        Assert.Equal(new ushort[] { 0x181, 0x182, 0x84, 0x48, 0x28, 0x68, 0x10, 0x102 }, types);
        Assert.Equal(1u, w.Kernel("GetStringTypeW", 1, w.Wide("\u00A0\u0378"), 0xFFFFFFFF, out16));
        Assert.Equal(0x48, w.Mem.ReadUInt16(out16));
        Assert.Equal(0u, w.Mem.ReadUInt16(out16 + 2)); // unassigned
        Assert.Equal(0x20u, w.Mem.ReadUInt16(out16 + 4)); // the terminator is classified too: a control
        Assert.Equal(0u, w.Kernel("GetStringTypeW", 2, w.Wide("a"), 1, out16));
        Assert.Equal(Win32Error.NotSupported, w.LastError);
        Assert.Equal(0u, w.Kernel("GetStringTypeW", 3, w.Wide("a"), 1, out16));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("GetStringTypeW", 1, w.Wide("a"), 0, out16));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void CodePageQueries()
    {
        using var w = new World();
        Assert.Equal(1252u, w.Kernel("GetACP"));
        Assert.Equal(437u, w.Kernel("GetOEMCP"));
        var info = w.Buf(20);
        Assert.Equal(1u, w.Kernel("GetCPInfo", 1252, info));
        Assert.Equal(1u, w.Mem.ReadUInt32(info)); Assert.Equal((byte)'?', w.Mem.ReadByte(info + 4));
        Assert.Equal(1u, w.Kernel("GetCPInfo", 65001, info));
        Assert.Equal(4u, w.Mem.ReadUInt32(info));
        Assert.Equal(1u, w.Kernel("GetCPInfo", 0, info));
        Assert.Equal(0u, w.Kernel("GetCPInfo", 932, info));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        using var other = new World(new Win32Options { AnsiCodePage = 850, OemCodePage = 850 });
        Assert.Equal(850u, other.Kernel("GetACP"));
    }

    [Fact]
    public void LstrFunctions()
    {
        using var w = new World();
        Assert.Equal(0u, w.Kernel("lstrlenA", 0));
        Assert.Equal(5u, w.Kernel("lstrlenA", w.Str("hello")));
        var buf = w.Buf(16);
        Assert.Equal(buf, w.Kernel("lstrcpyA", buf, w.Str("abc")));
        Assert.Equal("abc", w.Cstr(buf));
        Assert.Equal(buf, w.Kernel("lstrcatA", buf, w.Str("def")));
        Assert.Equal("abcdef", w.Cstr(buf));
        Assert.Equal(buf, w.Kernel("lstrcpynA", buf, w.Str("123456789"), 4));
        Assert.Equal("123", w.Cstr(buf));
        Assert.Equal(0u, w.Kernel("lstrcpyA", 0, w.Str("x")));
        Assert.Equal(0u, w.Kernel("lstrcatA", buf, 0));
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestMemoryException>(() => w.Kernel("lstrlenA", 0x10));
    }
}
