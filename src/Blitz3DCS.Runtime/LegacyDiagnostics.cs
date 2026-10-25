using System.Diagnostics;

namespace Blitz3DCS.Runtime;

/// <summary>Legacy debugger messages routed to the host's .NET trace listeners.</summary>
public static class LegacyDiagnostics
{
    /// <summary>Writes one diagnostic line; unlike Print, this does not write to the BASIC console.</summary>
    public static void DebugLog(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // The original debugger receives a NUL-terminated string, not its trailing bytes.
        var end = text.IndexOf('\0');
        Trace.WriteLine(end < 0 ? text : text[..end]);
    }
}
