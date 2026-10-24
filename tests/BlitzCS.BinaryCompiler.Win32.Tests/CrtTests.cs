using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class CrtTests
{
    [Fact]
    public void MallocCallocReallocFree()
    {
        using var w = new World();
        var p = w.Crt("malloc", 40);
        Assert.NotEqual(0u, p);
        w.Mem.Fill(p, 0xEE, 40);
        var q = w.Crt("realloc", p, 4000);
        Assert.All(w.Bytes(q, 40), x => Assert.Equal(0xEE, x));
        var c = w.Crt("calloc", 10, 10);
        Assert.All(w.Bytes(c, 100), x => Assert.Equal(0, x));
        Assert.Equal(0u, w.Crt("calloc", 0xFFFFFFFF, 0xFFFFFFFF)); // overflow
        Assert.NotEqual(0u, w.Crt("realloc", 0, 8));               // realloc(NULL) = malloc
        Assert.Equal(0u, w.Crt("realloc", q, 0));                   // realloc(p,0) frees
        Assert.Throws<Win32UsageException>(() => w.Crt("free", q)); // already freed
        Assert.Equal(0u, w.Crt("free", 0));
        Assert.Equal(0u, w.Crt("free", c));
        Assert.Throws<Win32UsageException>(() => w.Crt("free", c));
        Assert.Throws<Win32UsageException>(() => w.Crt("realloc", 0x1234, 4));
        Assert.NotEqual(0u, w.Crt("malloc", 0)); // valid minimal block
        Assert.Equal(0u, w.Crt("malloc", 0xF0000000));
    }

    [Fact]
    public void CrtHeapIsSeparateFromProcessHeap()
    {
        using var w = new World();
        var p = w.Crt("malloc", 8);
        Assert.Equal(0u, w.Kernel("HeapFree", w.Kernel("GetProcessHeap"), 0, p));
        var h = w.Kernel("HeapAlloc", w.Kernel("GetProcessHeap"), 0, 8);
        Assert.Throws<Win32UsageException>(() => w.Crt("free", h));
    }

    [Fact]
    public void MemoryFunctions()
    {
        using var w = new World();
        var a = w.Buf(300000); var b = w.Buf(300000);
        var pattern = new byte[300000]; new Random(3).NextBytes(pattern);
        w.Mem.Write(a, pattern);
        Assert.Equal(b, w.Crt("memcpy", b, a, 300000));
        Assert.Equal(0u, w.Crt("memcmp", a, b, 300000));
        w.Mem.WriteByte(b + 150000, (byte)(pattern[150000] + 1));
        Assert.Equal(unchecked((uint)-1), w.Crt("memcmp", a, b, 300000));
        Assert.Equal(1u, w.Crt("memcmp", b, a, 300000));
        Assert.Equal(0u, w.Crt("memcmp", a, b, 150000));
        w.Crt("memset", a, 0x41, 10);
        Assert.Equal(Enumerable.Repeat((byte)0x41, 10), w.Bytes(a, 10));
        Assert.Equal(a, w.Crt("memchr", a, 0x41, 10));
        Assert.Equal(0u, w.Crt("memchr", a, 0x42, 10));
        // overlapping moves in both directions
        w.Mem.Write(a, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        w.Crt("memmove", a + 10, a, 50);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (byte)i), w.Bytes(a + 10, 50));
        w.Mem.Write(a, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        w.Crt("memmove", a, a + 10, 50);
        Assert.Equal(Enumerable.Range(10, 50).Select(i => (byte)i), w.Bytes(a, 50));
        w.Mem.Write(a, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        w.Crt("memcpy", a + 10, a, 50); // msvcrt memcpy tolerates overlap
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (byte)i), w.Bytes(a + 10, 50));
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestMemoryException>(() => w.Crt("memcpy", 0, a, 4));
        Assert.Equal(0u, w.Crt("memcpy", a, a, 0) - a);
    }

    [Fact]
    public void StringFunctions()
    {
        using var w = new World();
        var buf = w.Buf(64);
        Assert.Equal(5u, w.Crt("strlen", w.Str("hello")));
        Assert.Equal(0u, w.Crt("strcmp", w.Str("abc"), w.Str("abc")));
        Assert.Equal(unchecked((uint)-1), w.Crt("strcmp", w.Str("abc"), w.Str("abd")));
        Assert.Equal(1u, w.Crt("strcmp", w.Str("abc"), w.Str("ab")));
        Assert.Equal(1u, w.Crt("strcmp", w.Str("\u00FF"), w.Str("a"))); // unsigned comparison
        Assert.Equal(0u, w.Crt("_stricmp", w.Str("HeLLo"), w.Str("hello")));
        Assert.Equal(0u, w.Crt("strncmp", w.Str("abcX"), w.Str("abcY"), 3));
        Assert.Equal(1u, w.Crt("strncmp", w.Str("abcX"), w.Str("abcA"), 4));
        Assert.Equal(buf, w.Crt("strcpy", buf, w.Str("foo")));
        Assert.Equal("foo", w.Cstr(buf));
        w.Crt("strcat", buf, w.Str("bar"));
        Assert.Equal("foobar", w.Cstr(buf));
        w.Mem.Fill(buf, 0x7F, 16);
        w.Crt("strncpy", buf, w.Str("ab"), 6);
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0, 0, 0, 0, 0x7F }, w.Bytes(buf, 7)); // zero padding, no write beyond count
        w.Mem.Write(buf, "a.b.c\0"u8.ToArray());
        Assert.Equal(buf + 1, w.Crt("strchr", buf, '.'));
        Assert.Equal(buf + 3, w.Crt("strrchr", buf, '.'));
        Assert.Equal(0u, w.Crt("strchr", buf, 'z'));
        Assert.Equal(buf + 5, w.Crt("strchr", buf, 0));
        Assert.Equal(buf + 2, w.Crt("strstr", buf, w.Str("b.c")));
        Assert.Equal(0u, w.Crt("strstr", buf, w.Str("zz")));
        Assert.Equal(buf, w.Crt("strstr", buf, w.Str("")));
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestMemoryException>(() => w.Crt("strlen", 0));
    }

    [Fact]
    public void NumericHelpers()
    {
        using var w = new World();
        Assert.Equal(5u, w.Crt("abs", unchecked((uint)-5)));
        Assert.Equal(7u, w.Crt("labs", 7));
        Assert.Equal(0x80000000u, w.Crt("abs", 0x80000000)); // abs(INT_MIN) = INT_MIN
        Assert.Equal('A', (int)w.Crt("toupper", 'a'));
        Assert.Equal('a', (int)w.Crt("tolower", 'A'));
        Assert.Equal('1', (int)w.Crt("toupper", '1'));
        Assert.Equal(unchecked((uint)-42), w.Crt("atoi", w.Str("  -42xyz")));
        Assert.Equal(0u, w.Crt("atoi", w.Str("zz")));
        Assert.Equal(int.MaxValue, (int)w.Crt("atoi", w.Str("99999999999")));
    }

    private static string Sprintf(World w, string fmt, params uint[] args)
    {
        var buf = w.Buf(512);
        var n = w.Crt("sprintf", [buf, w.Str(fmt), .. args]);
        var s = w.Cstr(buf);
        Assert.Equal((uint)s.Length, n);
        return s;
    }

    [Fact]
    public void SprintfIntegersStringsAndFlags()
    {
        using var w = new World();
        Assert.Equal("42|-7|   42|42   |00042|+5", Sprintf(w, "%d|%d|%5d|%-5d|%05d|%+d", 42, unchecked((uint)-7), 42, 42, 42, 5));
        Assert.Equal("ff|FF|0xff|0XFF|377|4294967295", Sprintf(w, "%x|%X|%#x|%#X|%o|%u", 255, 255, 255, 255, 255, 0xFFFFFFFF));
        Assert.Equal("a|ab|  ab|ab  |(null)|%", Sprintf(w, "%c|%.2s|%4.2s|%-4.2s|%s|%%", 'a', w.Str("abc"), w.Str("abc"), w.Str("abc"), 0));
        Assert.Equal("[  7][7  ][0007]", Sprintf(w, "[%*d][%-*d][%.*d]", 3, 7, 3, 7, 4, 7));
        Assert.Equal("00ABCDEF", Sprintf(w, "%p", 0xABCDEF));
        Assert.Equal("-0012", Sprintf(w, "%05d", unchecked((uint)-12)));
        Assert.Equal("|", Sprintf(w, "%.0d|", 0));
        Assert.Equal("12345678901", Sprintf(w, "%I64d", 0xDFDC1C35, 2));
        Assert.Equal("65535", Sprintf(w, "%hu", 0x1FFFF));
        Assert.Equal("-1", Sprintf(w, "%hd", 0xFFFF));
        Assert.Equal("tail", Sprintf(w, "%stail", w.Str("")));
    }

    [Fact]
    public void SprintfFloatsAndUnsupportedConversions()
    {
        using var w = new World();
        var lo = World.Dbl(3.14159265, out var hi);
        Assert.Equal("3.141593|3.14|   3.1|-2.50", Sprintf(w, "%f|%.2f|%6.1f|%.2f", lo, hi, lo, hi, lo, hi, World.Dbl(-2.5, out var h2), h2));
        Assert.Equal("0", Sprintf(w, "%.0f", World.Dbl(0.4, out var h3), h3));
        foreach (var bad in new[] { "%e", "%g", "%n", "%a", "%Z" })
        {
            var ex = Assert.Throws<Win32UsageException>(() => Sprintf(w, bad, 0, 0));
            Assert.Contains(bad, ex.Message);
        }
        Assert.Throws<Win32UsageException>(() => Sprintf(w, "%"));
        Assert.Throws<Win32UsageException>(() => Sprintf(w, "%f", World.Dbl(double.NaN, out var h4), h4));
    }

    [Fact]
    public void SnprintfTruncationFollowsMsvcrt()
    {
        using var w = new World();
        var buf = w.Buf(32); w.Mem.Fill(buf, 0x7F, 32);
        Assert.Equal(5u, w.Crt("_snprintf", buf, 16, w.Str("%s!"), w.Str("abcd")));
        Assert.Equal("abcd!", w.Cstr(buf));
        w.Mem.Fill(buf, 0x7F, 32);
        Assert.Equal(5u, w.Crt("_snprintf", buf, 5, w.Str("%s!"), w.Str("abcd")));   // exactly fits: no terminator
        Assert.Equal(Encoding.Latin1.GetBytes("abcd!\u007F"), w.Bytes(buf, 6));
        w.Mem.Fill(buf, 0x7F, 32);
        Assert.Equal(0xFFFFFFFFu, w.Crt("_snprintf", buf, 3, w.Str("%s!"), w.Str("abcd"))); // truncated: -1
        Assert.Equal(Encoding.Latin1.GetBytes("abc\u007F"), w.Bytes(buf, 4));
    }

    [Fact]
    public void FunctionsThatNeedRuntimeHooksAreAbsent()
    {
        foreach (var name in new[] { "_ftol", "_ftol2", "qsort", "bsearch", "setjmp", "longjmp", "atexit", "printf" })
            Assert.DoesNotContain(Win32ImportResolver.Coverage.Implemented, a => a.Dll == "msvcrt.dll" && a.Name == name);
    }
}

public sealed class WinsockTests
{
    [Theory]
    [InlineData("192.168.1.1", 0x0101A8C0u)]
    [InlineData("1.2.3", 0x03000201u)]
    [InlineData("1.2", 0x02000001u)]
    [InlineData("0x7f.1", 0x0100007Fu)]
    [InlineData("0177.0.0.1", 0x0100007Fu)]
    [InlineData("16909060", 0x04030201u)]
    [InlineData("256.1.1.1", 0xFFFFFFFFu)]
    [InlineData("1.2.3.4.5", 0xFFFFFFFFu)]
    [InlineData("1..2", 0xFFFFFFFFu)]
    [InlineData("abc", 0xFFFFFFFFu)]
    [InlineData("1.2.3.256", 0xFFFFFFFFu)]
    [InlineData("08.1", 0xFFFFFFFFu)]
    public void InetAddr(string text, uint expected)
    {
        using var w = new World();
        Assert.Equal(expected, w.Call("wsock32.dll", "inet_addr", w.Str(text)));
        Assert.Equal(expected, w.Call("WS2_32.dll", "inet_addr", w.Str(text)));
    }

    [Fact]
    public void InetNtoaUsesAPerThreadBufferAndBindsByWsock32Ordinal()
    {
        using var w = new World();
        var p = w.Call("wsock32.dll", "inet_ntoa", 0x0101A8C0);
        Assert.Equal("192.168.1.1", w.Cstr(p));
        Assert.Equal(p, w.Call("wsock32.dll", "inet_ntoa", 0x0100007F));
        Assert.Equal("127.0.0.1", w.Cstr(p));
        uint other = p;
        var t = new Thread(() => other = w.Call("wsock32.dll", "inet_ntoa", 1));
        t.Start(); t.Join();
        Assert.NotEqual(p, other);
        Assert.NotNull(w.R.Resolve("WSOCK32.dll", null, 11));
        Assert.NotNull(w.R.Resolve("WSOCK32.dll", null, 10));
        Assert.Null(w.R.Resolve("WSOCK32.dll", null, 12));
    }
}
