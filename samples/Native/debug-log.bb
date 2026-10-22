; A host may attach a diagnostic sink without redirecting BASIC Print output.
Global messages%=0
<?cs module
public static class DiagnosticBridge
{
    public static string Format(string text) => "[" + text + "]";
}
?>
<?cs
System.Diagnostics.Trace.Listeners.Add(
    new System.Diagnostics.TextWriterTraceListener(System.Console.Error));
BB.logline("from C#");
?>
LogLine CS::DiagnosticBridge.Format("from BASIC")
<?cs System.Diagnostics.Trace.Flush(); ?>
Print messages
End

Function LogLine(message$)
    DebugLog message
    messages=messages+1
End Function
