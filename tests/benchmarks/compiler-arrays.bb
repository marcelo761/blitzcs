; Standalone compiler benchmark: BASIC array operations, no engine provider.
<?cs module
internal static class Measurements
{
    private static readonly System.Collections.Generic.List<object> Samples = new();
    private static long allocatedBefore, started;
    internal static void Begin()
    {
        System.GC.Collect();
        allocatedBefore = System.GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    internal static void Finish()
    {
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Samples.Add(new { milliseconds = elapsed, allocatedBytes = allocated });
    }
    internal static void Print()
    {
        if (BB.checksum != 999999) throw new System.InvalidOperationException("Array benchmark checksum failed.");
        System.Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Samples));
    }
}
?>
Global checksum=0
Dim cells(63,63)
Work()
For pass=1 To 5
    <?cs Measurements.Begin(); ?>
    Work()
    <?cs Measurements.Finish(); ?>
Next
<?cs Measurements.Print(); ?>
End

Function Work()
    For i=0 To 999999
        cells(i And 63,(i Shr 6) And 63)=i
        checksum=cells(i And 63,(i Shr 6) And 63)
    Next
End Function
