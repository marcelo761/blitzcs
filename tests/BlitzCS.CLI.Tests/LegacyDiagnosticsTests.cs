using System.Diagnostics;
using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyDiagnosticsTests
{
    [Fact]
    public void DebugLogDeliversEachMessageToAnAttachedDiagnosticListener()
    {
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        Trace.Listeners.Add(listener);
        try
        {
            LegacyDiagnostics.DebugLog("Blitz3DCS diagnostic: Olá");
            LegacyDiagnostics.DebugLog("before\0after");
            LegacyDiagnostics.DebugLog("");
            Trace.Flush();
            Assert.Contains("Blitz3DCS diagnostic: Olá" + Environment.NewLine, output.ToString());
            Assert.Contains("before" + Environment.NewLine, output.ToString());
            Assert.DoesNotContain("after", output.ToString());
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public void NullIsRejectedAtTheManagedApiBoundary() =>
        Assert.Throws<ArgumentNullException>(() => LegacyDiagnostics.DebugLog(null!));
}
