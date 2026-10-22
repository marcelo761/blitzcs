namespace BlitzCS.Compiler;

// This runtime is emitted with the program, keeping BasicCompiler.Compile self-contained.
internal static class CompatibilityRuntime
{
    internal const string Source = """
    internal static int __BlitzAbs(int value) => value >= 0 ? value : unchecked(-value);
    internal static float __BlitzAbs(float value) => System.MathF.Abs(value);
    internal static int __BlitzSgn(int value) => value > 0 ? 1 : value < 0 ? -1 : 0;
    internal static float __BlitzSgn(float value) => value > 0 ? 1f : value < 0 ? -1f : 0f;
    internal static int __BlitzInteger(int value) => value;
    internal static int __BlitzInteger(float value)
    {
        var rounded = System.MathF.Round(value, System.MidpointRounding.ToEven);
        // x87's indefinite integer for NaN, infinity and out-of-range conversion.
        return !float.IsFinite(rounded) || rounded >= 2147483648f || rounded < -2147483648f
            ? int.MinValue : (int)rounded;
    }
    internal static int __BlitzInteger(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^[\t\n\v\f\r ]*[+-]?[0-9]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) return 0;
        var text = match.Value.TrimStart();
        var negative = text[0] == '-';
        var index = text[0] is '+' or '-' ? 1 : 0;
        var result = 0;
        for (; index < text.Length; index++) result = unchecked(result * 10 + text[index] - '0');
        return negative ? unchecked(-result) : result;
    }
    internal static float __BlitzFloat(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^[\t\n\v\f\r ]*[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? (float)number : 0f;
    }
    internal static string __BlitzString(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static string __BlitzString(float value)
    {
        if (float.IsNaN(value)) return "NaN";
        if (float.IsPositiveInfinity(value)) return "Infinity";
        if (float.IsNegativeInfinity(value)) return "-Infinity";
        var negative = float.IsNegative(value);
        var scientific = System.MathF.Abs(value).ToString("E5", System.Globalization.CultureInfo.InvariantCulture);
        var e = scientific.IndexOf('E');
        var digits = scientific[..e].Replace(".", "");
        var exponent = int.Parse(scientific[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var dec = exponent + 1;
        string text;
        if (dec <= -3 || dec > 8)
        {
            var tail = digits[1..].TrimEnd('0');
            // The original MSVC _gcvt retains the decimal point even without a fractional digit.
            text = digits[..1] + "." + tail + "e" + (exponent < 0 ? "-" : "+") + System.Math.Abs(exponent).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            text = dec <= 0 ? "0." + new string('0', -dec) + digits
                : dec < 6 ? digits[..dec] + "." + digits[dec..]
                : digits + new string('0', dec - 6) + ".0";
            while (text.EndsWith("0", System.StringComparison.Ordinal) && text[^2] != '.') text = text[..^1];
        }
        return negative ? "-" + text : text;
    }
""";
}
