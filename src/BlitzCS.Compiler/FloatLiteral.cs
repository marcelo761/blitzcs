using System.Globalization;

namespace BlitzCS.Compiler;

internal static class FloatLiteral
{
    internal static string Emit(float value)
    {
        if (float.IsNaN(value)) return "global::System.Single.NaN";
        if (float.IsPositiveInfinity(value)) return "global::System.Single.PositiveInfinity";
        if (float.IsNegativeInfinity(value)) return "global::System.Single.NegativeInfinity";
        if (value == 0) return float.IsNegative(value) ? "-0.0f" : "0.0f";
        return value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }
}
