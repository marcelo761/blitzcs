using System.Text;

namespace Blitz3DCS.Runtime;

/// <summary>Explicit single-byte compatibility boundary, not the host's default encoding.</summary>
public static class LegacyStrings
{
    private static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(
        1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    /// <summary>Creates one legacy byte; only the low eight bits are significant.</summary>
    public static string Chr(int code) => Ansi.GetString([unchecked((byte)code)]);

    /// <summary>Counts legacy bytes including NUL; rejects text outside the supported byte encoding.</summary>
    public static int Len(string text) => Ansi.GetByteCount(text);
}
