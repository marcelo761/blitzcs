using System.Globalization;
using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// MSVCRT.dll C runtime subset (cdecl). Semantics follow the ISO C library as documented for the Microsoft CRT (Microsoft Learn, "C runtime library reference"):
/// malloc/calloc/realloc/free use a private guest heap (separate from GetProcessHeap); free/realloc of a pointer this heap does not own raises
/// <see cref="Win32UsageException"/> (the CRT would corrupt its heap); memcpy handles overlap like memmove (a safe superset of the contract);
/// string functions read guest memory byte-wise and fault on unmapped memory instead of crashing the host. sprintf/_snprintf support
/// %d %i %u %x %X %o %c %s %p %f %F %% with flags - + space 0 #, width, precision, '*' and the h/l/ll/I64 size prefixes; any other conversion
/// (%e %g %a %n ...) raises <see cref="Win32UsageException"/> naming the specifier - never a wrong guess. NOT provided (need runtime hooks):
/// _ftol / _ftol2 (x87 stack argument), qsort and bsearch (callbacks into guest code), setjmp/longjmp, atexit (needs the exit-time guest call).
/// </summary>
public sealed class CrtService : Win32Service
{
    public override string Name => "Msvcrt";
    private const string M = "MSVCRT.dll";

    private static uint Malloc(Win32Call c, uint size)
    {
        if (size > c.Ctx.Options.MaxAllocation) return 0;
        return c.Ctx.Heaps.Crt(c.Mem).Alloc(size, false);
    }

    private static int Compare(Win32Call c, uint a, uint b, uint n)
    {
        var x = new byte[(int)Math.Min(n, 65536u)]; var y = new byte[x.Length];
        for (uint done = 0; done < n;)
        {
            var len = (int)Math.Min(x.Length, n - done);
            c.Mem.Read(a + done, x.AsSpan(0, len)); c.Mem.Read(b + done, y.AsSpan(0, len));
            for (var i = 0; i < len; i++) if (x[i] != y[i]) return x[i] - y[i];
            done += (uint)len;
        }
        return 0;
    }

    private static uint Cmp(int v) => (uint)Math.Sign(v);

    private static int StrCompare(Win32Call c, uint a, uint b, long max, bool ignoreCase)
    {
        for (long i = 0; i < max; i++)
        {
            int x = c.Mem.ReadByte(a + (uint)i), y = c.Mem.ReadByte(b + (uint)i);
            if (ignoreCase) { x = ToLower(x); y = ToLower(y); }
            if (x != y) return x < y ? -1 : 1;
            if (x == 0) return 0;
        }
        return 0;
    }

    private static int ToLower(int ch) => ch is >= 'A' and <= 'Z' ? ch + 32 : ch;
    private static int ToUpper(int ch) => ch is >= 'a' and <= 'z' ? ch - 32 : ch;

    internal static uint Format(IWin32Memory mem, Func<int, uint> va, uint format, int firstVarArg, out byte[] output)
    {
        var fmt = mem.ReadCString(format);
        var sb = new List<byte>();
        var arg = firstVarArg;
        for (var i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%') { sb.Add(fmt[i]); continue; }
            i++;
            bool left = false, plus = false, space = false, zero = false, alt = false;
            for (; i < fmt.Length; i++)
            {
                switch ((char)fmt[i])
                {
                    case '-': left = true; continue;
                    case '+': plus = true; continue;
                    case ' ': space = true; continue;
                    case '0': zero = true; continue;
                    case '#': alt = true; continue;
                }
                break;
            }
            var width = 0;
            if (i < fmt.Length && fmt[i] == '*') { var w = (int)va(arg++); if (w < 0) { left = true; w = -w; } width = w; i++; }
            else while (i < fmt.Length && fmt[i] is >= (byte)'0' and <= (byte)'9') width = width * 10 + fmt[i++] - '0';
            var precision = -1;
            if (i < fmt.Length && fmt[i] == '.')
            {
                i++; precision = 0;
                if (i < fmt.Length && fmt[i] == '*') { precision = (int)va(arg++); i++; }
                else while (i < fmt.Length && fmt[i] is >= (byte)'0' and <= (byte)'9') precision = precision * 10 + fmt[i++] - '0';
            }
            var size = 4;
            if (i < fmt.Length && fmt[i] == 'I' && i + 2 < fmt.Length && fmt[i + 1] == '6' && fmt[i + 2] == '4') { size = 8; i += 3; }
            else if (i < fmt.Length && fmt[i] == 'l' && i + 1 < fmt.Length && fmt[i + 1] == 'l') { size = 8; i += 2; }
            else if (i < fmt.Length && fmt[i] is (byte)'l' or (byte)'L' or (byte)'w') i++;
            else if (i < fmt.Length && fmt[i] == 'h') { size = 2; i++; }
            if (i >= fmt.Length) throw new Win32UsageException("sprintf: format string ends inside a conversion.");
            var conv = (char)fmt[i];
            string body; var numeric = false;
            switch (conv)
            {
                case '%': body = "%"; break;
                case 'c': body = ((char)(byte)va(arg++)).ToString(); break;
                case 's':
                {
                    var p = va(arg++);
                    var bytes = p == 0 ? "(null)"u8.ToArray() : mem.ReadCString(p);
                    if (precision >= 0 && bytes.Length > precision) bytes = bytes[..precision];
                    body = Encoding.Latin1.GetString(bytes); break;
                }
                case 'd': case 'i':
                {
                    long v = size == 8 ? (long)(va(arg++) | (ulong)va(arg++) << 32) : size == 2 ? (short)va(arg++) : (int)va(arg++);
                    var digits = Math.Abs((decimal)v).ToString(CultureInfo.InvariantCulture);
                    if (precision >= 0) digits = digits.PadLeft(precision, '0');
                    if (precision == 0 && v == 0) digits = "";
                    body = (v < 0 ? "-" : plus ? "+" : space ? " " : "") + digits; numeric = precision < 0; break;
                }
                case 'u': case 'x': case 'X': case 'o': case 'p':
                {
                    ulong v = size == 8 ? va(arg++) | (ulong)va(arg++) << 32 : size == 2 ? (ushort)va(arg++) : va(arg++);
                    if (conv == 'p') { precision = 8; conv = 'X'; alt = false; }
                    var digits = conv switch { 'u' => v.ToString(CultureInfo.InvariantCulture), 'x' => v.ToString("x"), 'X' => v.ToString("X"), _ => Convert.ToString((long)v, 8) };
                    if (precision >= 0) digits = digits.PadLeft(precision, '0');
                    if (precision == 0 && v == 0) digits = "";
                    var prefix = alt && v != 0 ? conv switch { 'x' => "0x", 'X' => "0X", 'o' => "0", _ => "" } : "";
                    body = prefix + digits; numeric = precision < 0; break;
                }
                case 'f': case 'F':
                {
                    var lo = va(arg++); var hi = va(arg++);
                    var v = BitConverter.UInt64BitsToDouble(lo | (ulong)hi << 32);
                    if (!double.IsFinite(v)) throw new Win32UsageException("sprintf: %f of NaN/Infinity (the CRT prints 1.#INF00 style text) is not supported.");
                    {
                        body = Math.Abs(v).ToString("F" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture);
                        var negative = v < 0 || (v == 0 && double.IsNegative(v));
                        body = (negative ? "-" : plus ? "+" : space ? " " : "") + body;
                        numeric = true;
                    }
                    break;
                }
                default: throw new Win32UsageException($"sprintf: conversion specifier '%{conv}' is not supported by the Win32 CRT service.");
            }
            var pad = width - body.Length;
            if (pad > 0)
            {
                if (left) body += new string(' ', pad);
                else if (zero && numeric)
                {
                    var signLen = body.Length > 0 && body[0] is '-' or '+' or ' ' ? 1 : 0;
                    if (body.StartsWith("0x", StringComparison.Ordinal) || body.StartsWith("0X", StringComparison.Ordinal)) signLen = 2;
                    body = body[..signLen] + new string('0', pad) + body[signLen..];
                }
                else body = new string(' ', pad) + body;
            }
            sb.AddRange(Encoding.Latin1.GetBytes(body));
        }
        output = [.. sb];
        return (uint)output.Length;
    }

    private static uint Format(Win32Call c, uint format, int firstVarArg, out byte[] output) => Format(c.Mem, c.VarArgs(), format, firstVarArg, out output);

    protected override void Define()
    {
        Cdecl(M, "malloc", 1, c => Malloc(c, c[0]));
        Cdecl(M, "calloc", 2, c =>
        {
            var total = (ulong)c[0] * c[1];
            if (total > (ulong)c.Ctx.Options.MaxAllocation) return 0;
            return c.Ctx.Heaps.Crt(c.Mem).Alloc((uint)total, true);
        });
        Cdecl(M, "free", 1, c =>
        {
            if (c[0] == 0) return 0;
            if (!c.Ctx.Heaps.Crt(c.Mem).Free(c[0])) throw new Win32UsageException($"free(0x{c[0]:X8}): not a live block of the CRT heap (double free or foreign pointer).");
            return 0;
        });
        Cdecl(M, "realloc", 2, c =>
        {
            var heap = c.Ctx.Heaps.Crt(c.Mem);
            if (c[0] == 0) return Malloc(c, c[1]);
            if (c[1] == 0) { if (!heap.Free(c[0])) throw new Win32UsageException($"realloc(0x{c[0]:X8}, 0): not a live block of the CRT heap."); return 0; }
            if (c[1] > c.Ctx.Options.MaxAllocation) return 0;
            return heap.Realloc(c[0], c[1], false, false, out var moved) switch
            {
                GuestHeap.ReallocResult.Ok => moved,
                GuestHeap.ReallocResult.NotOwned => throw new Win32UsageException($"realloc(0x{c[0]:X8}): not a live block of the CRT heap."),
                _ => 0,
            };
        });
        Cdecl(M, "memcpy", 3, c => { c.Mem.Copy(c[0], c[1], c[2]); return c[0]; });
        Cdecl(M, "memmove", 3, c => { c.Mem.Copy(c[0], c[1], c[2]); return c[0]; });
        Cdecl(M, "memset", 3, c => { c.Mem.Fill(c[0], (byte)c[1], c[2]); return c[0]; });
        Cdecl(M, "memcmp", 3, c => (uint)Compare(c, c[0], c[1], c[2]));
        Cdecl(M, "memchr", 3, c =>
        {
            for (uint i = 0; i < c[2]; i++) if (c.Mem.ReadByte(c[0] + i) == (byte)c[1]) return c[0] + i;
            return 0;
        });
        Cdecl(M, "strlen", 1, c => (uint)c.Mem.StrLen(c[0]));
        Cdecl(M, "strcmp", 2, c => Cmp(StrCompare(c, c[0], c[1], long.MaxValue, false)));
        Cdecl(M, "_stricmp", 2, c => Cmp(StrCompare(c, c[0], c[1], long.MaxValue, true)));
        Cdecl(M, "strncmp", 3, c => Cmp(StrCompare(c, c[0], c[1], c[2], false)));
        Cdecl(M, "strcpy", 2, c => { c.Mem.Write(c[0], c.Mem.ReadBytes(c[1], c.Mem.StrLen(c[1]) + 1)); return c[0]; });
        Cdecl(M, "strncpy", 3, c =>
        {
            var n = (int)Math.Min(c[2], int.MaxValue);
            var buffer = new byte[n]; var len = 0;
            while (len < n && c.Mem.ReadByte(c[1] + (uint)len) != 0) len++;
            c.Mem.ReadBytes(c[1], len).CopyTo(buffer, 0); // the rest stays zero: strncpy pads with NULs
            c.Mem.Write(c[0], buffer);
            return c[0];
        });
        Cdecl(M, "strcat", 2, c => { c.Mem.Write(c[0] + (uint)c.Mem.StrLen(c[0]), c.Mem.ReadBytes(c[1], c.Mem.StrLen(c[1]) + 1)); return c[0]; });
        Cdecl(M, "strchr", 2, c =>
        {
            for (uint i = 0; ; i++)
            {
                var b = c.Mem.ReadByte(c[0] + i);
                if (b == (byte)c[1]) return c[0] + i;
                if (b == 0) return 0;
            }
        });
        Cdecl(M, "strrchr", 2, c =>
        {
            uint found = 0;
            for (uint i = 0; ; i++)
            {
                var b = c.Mem.ReadByte(c[0] + i);
                if (b == (byte)c[1]) found = c[0] + i;
                if (b == 0) return found;
            }
        });
        Cdecl(M, "strstr", 2, c =>
        {
            var hay = c.Mem.ReadCString(c[0]); var needle = c.Mem.ReadCString(c[1]);
            var at = hay.AsSpan().IndexOf(needle);
            return at < 0 ? 0 : c[0] + (uint)at;
        });
        Cdecl(M, "abs", 1, c => { var v = (int)c[0]; return (uint)(v < 0 ? unchecked(-v) : v); });
        Cdecl(M, "labs", 1, c => { var v = (int)c[0]; return (uint)(v < 0 ? unchecked(-v) : v); });
        Cdecl(M, "toupper", 1, c => (uint)ToUpper((int)c[0]));
        Cdecl(M, "tolower", 1, c => (uint)ToLower((int)c[0]));
        Cdecl(M, "atoi", 1, c =>
        {
            var s = c.Mem.ReadCString(c[0]); var i = 0;
            while (i < s.Length && (s[i] == ' ' || (s[i] >= 9 && s[i] <= 13))) i++;
            var neg = false;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) neg = s[i++] == '-';
            long v = 0;
            while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9') { v = v * 10 + s[i++] - '0'; if (v > 0x80000000L) v = 0x80000000L; } // saturates like strtol clamped to int
            if (neg) v = -v;
            return (uint)(int)Math.Clamp(v, int.MinValue, int.MaxValue);
        });
        CdeclVar(M, "sprintf", 2, c =>
        {
            var n = Format(c, c[1], 2, out var bytes);
            c.Mem.Write(c[0], [.. bytes, (byte)0]);
            return n;
        });
        CdeclVar(M, "_snprintf", 3, c =>
        {
            Format(c, c[2], 3, out var bytes);
            var cap = c[1];
            if (bytes.Length < cap) { c.Mem.Write(c[0], [.. bytes, (byte)0]); return (uint)bytes.Length; }
            if (cap > 0) c.Mem.Write(c[0], bytes.AsSpan(0, (int)cap)); // documented: exactly count bytes, no terminator
            return bytes.Length == cap ? (uint)bytes.Length : 0xFFFFFFFF;
        });
    }
}

/// <summary>
/// Winsock helpers imported by real binaries. wsock32 ordinals verified against a Windows wsock32 export table: #10 inet_addr, #11 inet_ntoa.
/// inet_addr follows the BSD rules (1-4 parts; decimal, 0-octal, 0x-hex; last part fills the remaining bytes) and returns INADDR_NONE (0xFFFFFFFF) on error;
/// inet_ntoa returns a per-thread static buffer in guest memory (valid until the next call on the same thread, as documented).
/// </summary>
public sealed class WinsockService : Win32Service
{
    public override string Name => "Winsock";

    internal static uint ParseInetAddr(string text)
    {
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4) return 0xFFFFFFFF;
        var values = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) return 0xFFFFFFFF;
            try
            {
                values[i] = p.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(p[2..], 16)
                    : p.Length > 1 && p[0] == '0' ? Convert.ToUInt64(p, 8) : ulong.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { return 0xFFFFFFFF; }
        }
        ulong host = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (values[i] > 255) return 0xFFFFFFFF;
            host |= values[i] << (24 - 8 * i);
        }
        var lastBits = 8 * (4 - (parts.Length - 1));
        if (values[^1] >> lastBits != 0) return 0xFFFFFFFF;
        host |= values[^1];
        return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)host); // network order as a little-endian load
    }

    protected override void Define()
    {
        Win32Handler addr = c => ParseInetAddr(c.Mem.ReadAnsiString(c[0], 4096));
        Win32Handler ntoa = c =>
        {
            var buffer = c.Ctx.Process.ThreadBuffer(c.Mem, "inet_ntoa", 16);
            c.Mem.WriteCString(buffer, $"{c[0] & 0xFF}.{c[0] >> 8 & 0xFF}.{c[0] >> 16 & 0xFF}.{c[0] >> 24}");
            return buffer;
        };
        StdOrdinal("WSOCK32.dll", "inet_addr", 10, 1, addr);
        StdOrdinal("WSOCK32.dll", "inet_ntoa", 11, 1, ntoa);
        Std("WS2_32.dll", "inet_addr", 1, addr);
        Std("WS2_32.dll", "inet_ntoa", 1, ntoa);
    }
}
