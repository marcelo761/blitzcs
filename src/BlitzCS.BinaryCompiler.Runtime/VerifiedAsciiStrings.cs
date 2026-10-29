using System.Text;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Allocation-light managed equivalents for a guarded ASCII subset of Blitz string commands.</summary>
internal static class VerifiedAsciiStrings
{
    internal static bool TryCallString(string name, ReadOnlySpan<BinaryArgument> arguments, out string value)
    {
        value = "";
        switch (name)
        {
            case "Left":
            case "Right":
                if (arguments.Length != 2 || !TryAsciiText(arguments[0], allowNul: false, out var rangeText) ||
                    !TryInteger(arguments[1], out var rangeCount) || rangeCount < 0)
                    return false;
                value = name == "Left"
                    ? rangeText[..Math.Min(rangeCount, rangeText.Length)]
                    : rangeText[Math.Max(0, rangeText.Length - rangeCount)..];
                return true;

            case "Mid":
                if (arguments.Length != 3 || !TryAsciiText(arguments[0], allowNul: true, out var midText) ||
                    !TryInteger(arguments[1], out var start) || start <= 0 || !TryInteger(arguments[2], out var count))
                    return false;
                var offset = Math.Min(start - 1, midText.Length);
                value = count < 0 ? midText[offset..] : midText.Substring(offset, Math.Min(count, midText.Length - offset));
                return true;

            case "Upper":
            case "Lower":
                if (arguments.Length != 1 || !TryAsciiText(arguments[0], allowNul: false, out var caseText))
                    return false;
                value = ChangeAsciiCase(caseText, upper: name == "Upper");
                return true;

            case "Trim":
                if (arguments.Length != 1 || !TryAsciiText(arguments[0], allowNul: false, out var trimText))
                    return false;
                var first = 0;
                var last = trimText.Length;
                while (first < last && !IsGraph(trimText[first])) first++;
                while (last > first && !IsGraph(trimText[last - 1])) last--;
                value = first == 0 && last == trimText.Length ? trimText : trimText[first..last];
                return true;

            default:
                return false;
        }
    }

    internal static bool TryCallInt(string name, ReadOnlySpan<BinaryArgument> arguments, out int value)
    {
        value = 0;
        switch (name)
        {
            case "Len":
                if (arguments.Length != 1 || !TryAsciiText(arguments[0], allowNul: true, out var lengthText))
                    return false;
                value = lengthText.Length;
                return true;

            case "Asc":
                if (arguments.Length != 1 || !TryAsciiText(arguments[0], allowNul: true, out var ascText))
                    return false;
                value = ascText.Length == 0 ? -1 : ascText[0];
                return true;

            case "Instr":
                if (arguments.Length != 3 || !TryAsciiText(arguments[0], allowNul: true, out var text) ||
                    !TryAsciiText(arguments[1], allowNul: true, out var find) ||
                    !TryInteger(arguments[2], out var from) || from <= 0 || (text.Length == 0 && find.Length == 0))
                    return false;
                var offset = from - 1;
                value = offset > text.Length ? 0 : text.IndexOf(find, offset, StringComparison.Ordinal) + 1;
                return true;

            default:
                return false;
        }
    }

    private static bool TryAsciiText(BinaryArgument argument, bool allowNul, out string text)
    {
        text = "";
        if (!argument.IsText || argument.Text is not { } candidate || !Ascii.IsValid(candidate.AsSpan()) ||
            (!allowNul && candidate.AsSpan().Contains('\0')))
            return false;
        text = candidate;
        return true;
    }

    private static bool TryInteger(BinaryArgument argument, out int value)
    {
        value = 0;
        if (argument.IsText || argument.IsFloat || argument.IsPointer) return false;
        value = unchecked((int)argument.Bits);
        return true;
    }

    private static string ChangeAsciiCase(string text, bool upper)
    {
        var first = upper ? 'a' : 'A';
        var last = upper ? 'z' : 'Z';
        var delta = upper ? -32 : 32;
        var index = 0;
        while (index < text.Length && (text[index] < first || text[index] > last)) index++;
        if (index == text.Length) return text;
        return string.Create(text.Length, (text, first, last, delta), static (output, state) =>
        {
            for (var i = 0; i < output.Length; i++)
            {
                var value = state.text[i];
                output[i] = value >= state.first && value <= state.last ? (char)(value + state.delta) : value;
            }
        });
    }

    private static bool IsGraph(char value) => value is >= '!' and <= '~';
}
