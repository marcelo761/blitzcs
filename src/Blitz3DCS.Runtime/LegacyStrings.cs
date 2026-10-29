using System.Globalization;
using System.Text;

namespace Blitz3DCS.Runtime;

/// <summary>Single-byte string compatibility, using explicit CP1252 rather than host Unicode rules.</summary>
public static class LegacyStrings
{
    private static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(
        1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
    private static readonly string[] Chars = CreateChars();

    private static string[] CreateChars()
    {
        var chars = new string[256];
        for (var value = 0; value < chars.Length; value++)
            chars[value] = Ansi.GetString([(byte)value]);
        return chars;
    }

    public static string String(string text, int repeat)
    {
        Validate(text);
        if (repeat <= 0 || text.Length == 0) return "";
        return string.Create(checked(text.Length * repeat), text, static (output, value) =>
        {
            for (var offset = 0; offset < output.Length; offset += value.Length)
                value.AsSpan().CopyTo(output[offset..]);
        });
    }

    public static string Left(string text, int count)
    {
        Validate(text);
        Positive(count);
        return text[..Math.Min(count, text.Length)];
    }

    public static string Right(string text, int count)
    {
        Validate(text);
        Positive(count);
        return text[Math.Max(0, text.Length - count)..];
    }

    public static string Mid(string text, int start, int count = -1)
    {
        Validate(text);
        Offset(start);
        var offset = Math.Min(start - 1, text.Length);
        return count < 0 ? text[offset..] : text.Substring(offset, Math.Min(count, text.Length - offset));
    }

    public static int Instr(string text, string find, int from = 1)
    {
        Validate(text);
        Validate(find);
        Offset(from);
        var offset = from - 1;
        return offset > text.Length ? 0 : text.IndexOf(find, offset, StringComparison.Ordinal) + 1;
    }

    public static string Replace(string text, string from, string to)
    {
        Validate(text);
        Validate(from);
        Validate(to);
        if (text.Length == 0) return "";
        // The original loops forever for a nonempty input and an empty search string.
        if (from.Length == 0) throw new ArgumentException("Replace search string must not be empty.", nameof(from));
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    public static string Upper(string text) => ChangeCase(text, upper: true);
    public static string Lower(string text) => ChangeCase(text, upper: false);

    public static string Trim(string text)
    {
        Validate(text);
        var start = 0;
        var end = text.Length;
        // The original CRT C-locale isgraph predicate keeps only ASCII graphics.
        while (start < end && !IsGraph(text[start])) start++;
        while (end > start && !IsGraph(text[end - 1])) end--;
        return text[start..end];
    }

    public static string LSet(string text, int size)
    {
        Validate(text);
        Positive(size);
        return text.Length >= size ? text[..size] : text.PadRight(size, ' ');
    }

    public static string RSet(string text, int size)
    {
        Validate(text);
        Positive(size);
        return text.Length >= size ? text[(text.Length - size)..] : text.PadLeft(size, ' ');
    }

    /// <summary>Creates one legacy byte; only the low eight bits are significant.</summary>
    public static string Chr(int code) => Chars[unchecked((byte)code)];

    public static int Asc(string text)
    {
        Validate(text);
        if (text.Length == 0) return -1;
        Span<byte> first = stackalloc byte[1];
        Ansi.GetBytes(text.AsSpan(0, 1), first);
        return first[0];
    }

    /// <summary>Counts legacy bytes including NUL; rejects text outside the supported byte encoding.</summary>
    public static int Len(string text)
    {
        Validate(text);
        return text.Length;
    }

    public static string Hex(int value) => unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture);
    public static string Bin(int value) => Convert.ToString(value, 2).PadLeft(32, '0');
    public static string CurrentDate() => DateTime.Now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public static string CurrentTime() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // CP1252 maps each supported UTF-16 character to exactly one byte. Validate
        // without allocating an encoded copy, then use ordinal slices/searches.
        if (!Ascii.IsValid(text.AsSpan())) Ansi.GetByteCount(text);
    }

    private static bool IsGraph(char value) => value is >= '!' and <= '~';

    private static string ChangeCase(string text, bool upper)
    {
        Validate(text);
        var first = upper ? 'a' : 'A';
        var last = upper ? 'z' : 'Z';
        var index = 0;
        while (index < text.Length && (text[index] < first || text[index] > last)) index++;
        if (index == text.Length) return text;
        return string.Create(text.Length, (text, first, last, delta: upper ? -32 : 32), static (output, state) =>
        {
            for (var i = 0; i < output.Length; i++)
            {
                var value = state.text[i];
                output[i] = value >= state.first && value <= state.last ? (char)(value + state.delta) : value;
            }
        });
    }

    private static void Positive(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "parameter must be positive");
    }

    private static void Offset(int value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "parameter must be greater than 0");
    }
}
