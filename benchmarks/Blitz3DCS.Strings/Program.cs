using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Blitz3DCS.Runtime;

// A managed boundary microbenchmark, not a comparison with original Blitz3D speed.
var iterations = args.Length == 0 ? 500_000 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
if (iterations is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
var shortText = new string('a', 63) + "z";
var longText = new string('a', 4095) + "z";
var upperText = new string('A', 64);
var results = new List<object>();
Measure("ordinal-instr-64", () => shortText.IndexOf("z", StringComparison.Ordinal) + 1);
Measure("scalar-validated-instr-64", () => ScalarValidationBaseline.Instr(shortText, "z"));
Measure("legacy-instr-64", () => LegacyStrings.Instr(shortText, "z"));
Measure("ordinal-instr-4096", () => longText.IndexOf("z", StringComparison.Ordinal) + 1);
Measure("scalar-validated-instr-4096", () => ScalarValidationBaseline.Instr(longText, "z"));
Measure("legacy-instr-4096", () => LegacyStrings.Instr(longText, "z"));
Measure("ordinal-left-64", () => Consume(shortText[..8]));
Measure("scalar-validated-left-64", () => Consume(ScalarValidationBaseline.Left(shortText, 8)));
Measure("legacy-left-64", () => Consume(LegacyStrings.Left(shortText, 8)));
Measure("legacy-upper-unchanged-64", () => Consume(LegacyStrings.Upper(upperText)));
Measure("legacy-replace-no-match-64", () => Consume(LegacyStrings.Replace(shortText, "missing", "x")));
Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    iterations,
    rounds = 5,
    note = "Same delegate/loop baseline; median wall time, allocation counter for calling thread; no timing assertions or native-original speed claim.",
    results
}, new JsonSerializerOptions { WriteIndented = true }));

void Measure(string name, Func<int> operation)
{
    long checksum = 0;
    for (var i = 0; i < 20_000; i++) checksum += operation();
    var timings = new double[5];
    var allocations = new double[5];
    for (var round = 0; round < timings.Length; round++)
    {
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var before = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) checksum += operation();
        var elapsed = Stopwatch.GetTimestamp() - before;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        timings[round] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
        allocations[round] = (double)bytes / iterations;
    }
    Array.Sort(timings);
    Array.Sort(allocations);
    results.Add(new { name, median_nanoseconds_per_call = timings[2], median_allocated_bytes_per_call = allocations[2], checksum });
}

static int Consume(string text)
{
    Volatile.Write(ref StringSink.Value, text);
    return text.Length;
}

internal static class StringSink
{
    internal static string? Value;
}

// Reproduces the pre-optimization CP1252 boundary in the same executable.
// This is an owned managed control, not the historical native runtime.
internal static class ScalarValidationBaseline
{
    private static readonly System.Text.Encoding Ansi = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(
        1252, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback)!;

    public static int Instr(string text, string find, int from = 1)
    {
        Validate(text);
        Validate(find);
        if (from <= 0) throw new ArgumentOutOfRangeException(nameof(from));
        var offset = from - 1;
        return offset > text.Length ? 0 : text.IndexOf(find, offset, StringComparison.Ordinal) + 1;
    }

    public static string Left(string text, int count)
    {
        Validate(text);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return text[..Math.Min(count, text.Length)];
    }

    private static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Ansi.GetByteCount(text);
    }
}
