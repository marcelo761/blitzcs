using System.Globalization;
using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Code page helpers: CP437, CP850, CP1252 (single byte, tables from the .NET code page provider) and UTF-8 (65001).</summary>
internal static class Nls
{
    public const int CpAcp = 0, CpOemcp = 1, CpThreadAcp = 3, CpUtf8 = 65001;
    private static readonly Dictionary<int, (char[] Decode, Dictionary<char, byte> Encode)> Tables = [];

    public static int Resolve(Win32Options o, uint cp) => cp switch
    {
        CpAcp or CpThreadAcp => o.AnsiCodePage,
        CpOemcp => o.OemCodePage,
        _ => (int)cp,
    };

    /// <summary>Exact UTF-16 transcoding that keeps unpaired surrogates (Encoding.Unicode would replace them).</summary>
    public static string FromUtf16(byte[] bytes) => new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1)));
    public static byte[] ToUtf16(string text) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan()).ToArray();

    public static bool IsSupported(int cp) => cp is 437 or 850 or 1252 or CpUtf8;

    /// <summary>Byte to UTF-16 table and its inverse for a single-byte code page.</summary>
    public static (char[] Decode, Dictionary<char, byte> Encode) Table(int cp)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(cp, out var t)) return t;
            var enc = CodePagesEncodingProvider.Instance.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
                      ?? throw new NotSupportedException($"Code page {cp} is unavailable.");
            var decode = new char[256];
            var encode = new Dictionary<char, byte>();
            for (var b = 0; b < 256; b++)
            {
                var s = enc.GetString([(byte)b]);
                decode[b] = s.Length == 1 ? s[0] : '�';
                encode.TryAdd(decode[b], (byte)b);
            }
            return Tables[cp] = (decode, encode);
        }
    }

    public static string DecodeBytes(int cp, ReadOnlySpan<byte> bytes, bool strict, out bool invalid)
    {
        invalid = false;
        if (cp == CpUtf8)
        {
            var dec = new UTF8Encoding(false, strict);
            try { return dec.GetString(bytes); }
            catch (DecoderFallbackException) { invalid = true; return ""; }
        }
        var (decode, _) = Table(cp);
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = decode[bytes[i]];
        return new string(chars);
    }

    public static byte[] EncodeChars(int cp, string text, byte defaultChar, bool strict, out bool usedDefault, out bool invalid)
    {
        usedDefault = false; invalid = false;
        var output = new List<byte>(text.Length);
        if (cp == CpUtf8)
        {
            Span<byte> tmp = stackalloc byte[4];
            for (var i = 0; i < text.Length; i++)
            {
                Rune rune;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { rune = new Rune(text[i], text[i + 1]); i++; }
                else if (char.IsSurrogate(text[i])) { if (strict) { invalid = true; return []; } rune = Rune.ReplacementChar; }
                else rune = new Rune(text[i]);
                var n = rune.EncodeToUtf8(tmp);
                for (var k = 0; k < n; k++) output.Add(tmp[k]);
            }
            return [.. output];
        }
        var (_, encode) = Table(cp);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; ch = '￿'; }
            if (encode.TryGetValue(ch, out var b)) output.Add(b);
            else { output.Add(defaultChar); usedDefault = true; }
        }
        return [.. output];
    }

    public const ushort Upper = 1, Lower = 2, Digit = 4, Space = 8, Punct = 0x10, Cntrl = 0x20, Blank = 0x40, Xdigit = 0x80, Alpha = 0x100, Defined = 0x200;

    /// <summary>CT_CTYPE1 classification of one UTF-16 code unit.</summary>
    public static ushort CType1(char ch)
    {
        ushort bits = 0;
        var cat = char.GetUnicodeCategory(ch);
        if (cat == UnicodeCategory.UppercaseLetter) bits |= Upper | Alpha;
        else if (cat == UnicodeCategory.LowercaseLetter) bits |= Lower | Alpha;
        else if (char.IsLetter(ch) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) bits |= Alpha;
        if (cat == UnicodeCategory.DecimalDigitNumber || ch is '²' or '³' or '¹') bits |= Digit;
        if (char.IsWhiteSpace(ch)) bits |= Space;
        if (ch is ' ' or '\t' or ' ' || cat == UnicodeCategory.SpaceSeparator) bits |= Blank;
        if (char.IsControl(ch)) bits |= Cntrl;
        if (char.IsPunctuation(ch) || char.IsSymbol(ch)) bits |= Punct;
        if (ch is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f') bits |= Xdigit;
        if (bits == 0 && cat != UnicodeCategory.OtherNotAssigned) bits |= Defined;
        return bits;
    }
}

/// <summary>
/// KERNEL32 string and NLS functions (Microsoft Learn, "Unicode and Character Sets" / "National Language Support Functions").
/// Code pages: ACP (default 1252), OEM (default 437), 850, 1252, 65001; others fail with ERROR_INVALID_PARAMETER. Documented approximations:
/// WideCharToMultiByte never best-fit maps (behaves as WC_NO_BEST_FIT_CHARS) and a surrogate pair becomes one default character in single-byte
/// pages; MB_COMPOSITE is refused (ERROR_NOT_SUPPORTED); LCMapString supports LCMAP_LOWERCASE/LCMAP_UPPERCASE (and flag 0 = copy) with
/// invariant simple case mapping regardless of locale; GetStringType supports CT_CTYPE1 only (CTYPE2/CTYPE3 fail with ERROR_NOT_SUPPORTED);
/// the locale argument is ignored. lstrcpyA/lstrcatA/lstrcpynA keep the documented NULL-pointer behaviour (return NULL).
/// </summary>
public sealed class StringService : Win32Service
{
    public override string Name => "Kernel32.String";
    private const uint InvalidSize = 0xFFFFFFFF;

    private static uint MultiByteToWide(Win32Call c)
    {
        uint cpArg = c[0], flags = c[1], src = c[2], dst = c[4];
        var cb = (int)c[3]; var ccWide = (int)c[5];
        var cp = Nls.Resolve(c.Ctx.Options, cpArg);
        if (!Nls.IsSupported(cp) || src == 0 || cb == 0 || cb < -1 || ccWide < 0 || (dst == 0 && ccWide != 0))
            return c.Ctx.Fail(Win32Error.InvalidParameter);
        if ((flags & ~0xFu) != 0 || (flags & 3) == 3 || (cp == Nls.CpUtf8 && (flags & ~8u) != 0)) return c.Ctx.Fail(Win32Error.InvalidFlags);
        if ((flags & 2) != 0) return c.Ctx.Fail(Win32Error.NotSupported);
        var bytes = cb == -1 ? c.Mem.ReadBytes(src, c.Mem.StrLen(src) + 1) : c.Mem.ReadBytes(src, cb);
        var text = Nls.DecodeBytes(cp, bytes, (flags & 8) != 0, out var invalid);
        if (invalid) return c.Ctx.Fail(Win32Error.NoUnicodeTranslation);
        if (ccWide == 0) return (uint)text.Length;
        if (text.Length > ccWide) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        c.Mem.Write(dst, Nls.ToUtf16(text));
        return (uint)text.Length;
    }

    private static uint WideToMultiByte(Win32Call c)
    {
        uint cpArg = c[0], flags = c[1], src = c[2], dst = c[4], defChar = c[6], usedDefault = c[7];
        var cch = (int)c[3]; var cb = (int)c[5];
        var cp = Nls.Resolve(c.Ctx.Options, cpArg);
        if (!Nls.IsSupported(cp) || src == 0 || cch == 0 || cch < -1 || cb < 0 || (dst == 0 && cb != 0)
            || (cp == Nls.CpUtf8 && (defChar != 0 || usedDefault != 0)))
            return c.Ctx.Fail(Win32Error.InvalidParameter);
        const uint known = 0x10 | 0x20 | 0x40 | 0x80 | 0x200 | 0x400;
        if ((flags & ~known) != 0 || (cp != Nls.CpUtf8 && (flags & 0x80) != 0) || (cp == Nls.CpUtf8 && (flags & ~(0x80u | 0x400)) != 0))
            return c.Ctx.Fail(Win32Error.InvalidFlags);
        var text = cch == -1 ? c.Mem.ReadWideString(src) + "\0" : Nls.FromUtf16(c.Mem.ReadBytes(src, cch * 2));
        var def = defChar != 0 ? c.Mem.ReadByte(defChar) : (byte)'?';
        var bytes = Nls.EncodeChars(cp, text, def, (flags & 0x80) != 0, out var used, out var invalid);
        if (invalid) return c.Ctx.Fail(Win32Error.NoUnicodeTranslation);
        if (usedDefault != 0) c.Mem.WriteUInt32(usedDefault, used ? 1u : 0u);
        if (cb == 0) return (uint)bytes.Length;
        if (bytes.Length > cb) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        c.Mem.Write(dst, bytes);
        return (uint)bytes.Length;
    }

    private static string? MapCase(Win32Call c, uint flags, string text)
    {
        switch (flags & ~0x40000000u)
        {
            case 0: return text;
            case 0x100: return string.Create(text.Length, text, (s, t) => { for (var i = 0; i < s.Length; i++) s[i] = char.ToLowerInvariant(t[i]); });
            case 0x200: return string.Create(text.Length, text, (s, t) => { for (var i = 0; i < s.Length; i++) s[i] = char.ToUpperInvariant(t[i]); });
            default: c.Ctx.LastError = Win32Error.InvalidFlags; return null;
        }
    }

    internal static uint LcMapW(Win32Call c)
    {
        uint flags = c[1], src = c[2], dst = c[4];
        var cch = (int)c[3]; var cchDst = (int)c[5];
        if (src == 0 || cch == 0 || cch < -1 || cchDst < 0 || (dst == 0 && cchDst != 0)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var text = cch == -1 ? c.Mem.ReadWideString(src) + "\0" : Nls.FromUtf16(c.Mem.ReadBytes(src, cch * 2));
        if (MapCase(c, flags, text) is not { } mapped) return 0;
        if (cchDst == 0) return (uint)mapped.Length;
        if (mapped.Length > cchDst) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        c.Mem.Write(dst, Nls.ToUtf16(mapped));
        return (uint)mapped.Length;
    }

    private static uint LcMapA(Win32Call c)
    {
        uint flags = c[1], src = c[2], dst = c[4];
        var cch = (int)c[3]; var cchDst = (int)c[5];
        var cp = c.Ctx.Options.AnsiCodePage;
        if (!Nls.IsSupported(cp) || src == 0 || cch == 0 || cch < -1 || cchDst < 0 || (dst == 0 && cchDst != 0)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var bytes = cch == -1 ? c.Mem.ReadBytes(src, c.Mem.StrLen(src) + 1) : c.Mem.ReadBytes(src, cch);
        var text = Nls.DecodeBytes(cp, bytes, false, out _);
        if (MapCase(c, flags, text) is not { } mapped) return 0;
        var outBytes = Nls.EncodeChars(cp, mapped, (byte)'?', false, out _, out _);
        if (cchDst == 0) return (uint)outBytes.Length;
        if (outBytes.Length > cchDst) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        c.Mem.Write(dst, outBytes);
        return (uint)outBytes.Length;
    }

    private static uint StringType(Win32Call c, uint info, uint src, int cch, uint dst, bool wide)
    {
        if (src == 0 || cch == 0 || cch < -1 || dst == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if (info is not (1 or 2 or 4)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if (info != 1) return c.Ctx.Fail(Win32Error.NotSupported);
        string text;
        if (wide) text = cch == -1 ? c.Mem.ReadWideString(src) + "\0" : Nls.FromUtf16(c.Mem.ReadBytes(src, cch * 2));
        else
        {
            var cp = c.Ctx.Options.AnsiCodePage;
            if (!Nls.IsSupported(cp)) return c.Ctx.Fail(Win32Error.InvalidParameter);
            text = Nls.DecodeBytes(cp, cch == -1 ? c.Mem.ReadBytes(src, c.Mem.StrLen(src) + 1) : c.Mem.ReadBytes(src, cch), false, out _);
        }
        var result = new byte[text.Length * 2];
        for (var i = 0; i < text.Length; i++) BitConverter.TryWriteBytes(result.AsSpan(i * 2), Nls.CType1(text[i]));
        c.Mem.Write(dst, result);
        return 1;
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "MultiByteToWideChar", 6, MultiByteToWide);
        Std(K, "WideCharToMultiByte", 8, WideToMultiByte);
        Std(K, "LCMapStringA", 6, LcMapA);
        Std(K, "LCMapStringW", 6, LcMapW);
        Std(K, "GetStringTypeA", 5, c => StringType(c, c[1], c[2], (int)c[3], c[4], false));
        Std(K, "GetStringTypeW", 4, c => StringType(c, c[0], c[1], (int)c[2], c[3], true));
        Std(K, "GetACP", 0, c => (uint)c.Ctx.Options.AnsiCodePage);
        Std(K, "GetOEMCP", 0, c => (uint)c.Ctx.Options.OemCodePage);
        Std(K, "GetCPInfo", 2, c =>
        {
            var cp = Nls.Resolve(c.Ctx.Options, c[0]);
            if (!Nls.IsSupported(cp) || c[1] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            var info = new byte[20];
            BitConverter.TryWriteBytes(info, cp == Nls.CpUtf8 ? 4u : 1u);
            info[4] = (byte)'?';
            c.Mem.Write(c[1], info);
            return 1;
        });

        Std(K, "lstrlenA", 1, c => c[0] == 0 ? 0 : (uint)c.Mem.StrLen(c[0]));
        Std(K, "lstrcpyA", 2, c =>
        {
            if (c[0] == 0 || c[1] == 0) return 0;
            c.Mem.Write(c[0], c.Mem.ReadBytes(c[1], c.Mem.StrLen(c[1]) + 1));
            return c[0];
        });
        Std(K, "lstrcpynA", 3, c =>
        {
            if (c[0] == 0 || c[1] == 0) return 0;
            var max = (int)c[2];
            if (max <= 0) return c[0];
            var n = 0;
            while (n < max - 1 && c.Mem.ReadByte(c[1] + (uint)n) != 0) n++;
            var bytes = c.Mem.ReadBytes(c[1], n);
            c.Mem.Write(c[0], bytes);
            c.Mem.WriteByte(c[0] + (uint)n, 0);
            return c[0];
        });
        Std(K, "lstrcatA", 2, c =>
        {
            if (c[0] == 0 || c[1] == 0) return 0;
            var at = c[0] + (uint)c.Mem.StrLen(c[0]);
            c.Mem.Write(at, c.Mem.ReadBytes(c[1], c.Mem.StrLen(c[1]) + 1));
            return c[0];
        });
        _ = InvalidSize;
    }
}
