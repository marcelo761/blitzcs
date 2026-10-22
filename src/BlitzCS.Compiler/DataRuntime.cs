using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlitzCS.Compiler;

internal static class DataRuntime
{
    internal static string Emit(IReadOnlyList<IrDataValue> data)
    {
        var output = new StringBuilder("""
            internal static class __BlitzData
            {
                private readonly record struct Entry(int Kind, int Integer, float Float, string Text);
                private static readonly Entry[] values = new Entry[]
                {
            """);
        foreach (var value in data)
        {
            var item = value.Type == ValueType.Integer
                ? $"new(0, {value.Integer}, 0f, \"\")"
                : value.Type == ValueType.Float
                    ? $"new(1, 0, {Float(value.Float)}, \"\")"
                    : $"new(2, 0, 0f, {JsonSerializer.Serialize(value.Text ?? "")})";
            output.Append("        ").Append(item).AppendLine(",");
        }
        output.AppendLine("""
                };
                private static int cursor;
                private static Entry Next(string location)
                {
                    if ((uint)cursor >= (uint)values.Length)
                        throw new System.InvalidOperationException(location + ": Out of data.");
                    return values[cursor++];
                }
                internal static void Restore(int index) => cursor = index;
                internal static int ReadInteger(string location)
                {
                    var value = Next(location);
                    return value.Kind switch
                    {
                        0 => value.Integer,
                        1 => unchecked((int)value.Float),
                        _ => global::BB.__BlitzInteger(value.Text)
                    };
                }
                internal static float ReadFloat(string location)
                {
                    var value = Next(location);
                    return value.Kind switch
                    {
                        0 => value.Integer,
                        1 => value.Float,
                        _ => global::BB.__BlitzFloat(value.Text)
                    };
                }
                internal static string ReadString(string location)
                {
                    var value = Next(location);
                    return value.Kind switch
                    {
                        0 => global::BB.__BlitzString(value.Integer),
                        1 => global::BB.__BlitzString(value.Float),
                        _ => value.Text
                    };
                }
            }
            """);
        return output.ToString();
    }

    private static string Float(float value)
    {
        if (float.IsNaN(value)) return "global::System.Single.NaN";
        if (float.IsPositiveInfinity(value)) return "global::System.Single.PositiveInfinity";
        if (float.IsNegativeInfinity(value)) return "global::System.Single.NegativeInfinity";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text == "-0") return "-0.0f";
        if (text == "0") return "0.0f";
        return text + "f";
    }
}
