using System.Diagnostics;
using System.Text.Json;
using BlitzCS.OriginalRuntime;

// Executed by the original-runtime x86 host, against the real DLL. No substitute engine.
var results = new List<object>();
Commands.Graphics3D(640, 480, 0, 2);
var camera = Commands.CreateCamera(0);
Commands.PositionEntity(camera, 0, 0, -5, 0);
var cube = Commands.CreateCube(0);
if (Commands.EntityX(cube, 0) != 0) throw new InvalidOperationException("Initial native entity position is incorrect.");
// Exercise conversion boundaries and cleanup when a later argument cannot be encoded.
foreach (var name in new[] { "", "café", "\0", "a\0b\0", new string('x', 4096) + "é\0tail" })
{
    Commands.NameEntity(cube, name);
    if (Commands.EntityName(cube) != name) throw new InvalidOperationException("String boundary round trip failed.");
}
for (int i = 0; i < 100; i++)
{
    try
    {
        Commands.Replace("allocated first", "\ud800", "replacement");
        throw new InvalidOperationException("Unencodable string was silently accepted.");
    }
    catch (System.Text.EncoderFallbackException) { }
}
if (Commands.Replace("café café", "café", "tea") != "tea tea")
    throw new InvalidOperationException("Multiple string arguments failed.");
// A warm media cache must still reject a provider root that becomes invalid.
_ = NativeMedia.FSOUND_GetVersion();
var nativeRoot = Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_NATIVE_ROOT");
try
{
    Environment.SetEnvironmentVariable("BLITZCS_ORIGINAL_NATIVE_ROOT", null);
    try
    {
        _ = NativeMedia.FSOUND_GetVersion();
        throw new Exception("Media cache ignored a missing provider root.");
    }
    catch (InvalidOperationException) { }
    Environment.SetEnvironmentVariable("BLITZCS_ORIGINAL_NATIVE_ROOT", Path.Combine(Path.GetTempPath(), "blitzcs-missing-media-" + Guid.NewGuid()));
    try
    {
        _ = NativeMedia.FSOUND_GetVersion();
        throw new Exception("Media cache ignored a changed provider root.");
    }
    catch (DllNotFoundException) { }
}
finally { Environment.SetEnvironmentVariable("BLITZCS_ORIGINAL_NATIVE_ROOT", nativeRoot); }
const int iterations = 100_000;
void Numeric()
{
    for (int i = 0; i < iterations; i++)
    {
        Commands.PositionEntity(cube, i & 7, 2.5f, 0, 0);
        if (Commands.EntityX(cube, 0) != (i & 7) || Commands.EntityY(cube, 0) != 2.5f)
            throw new InvalidOperationException("Native numeric round trip failed.");
    }
}
void Strings()
{
    const string name = "café\0tail\0";
    for (int i = 0; i < 10_000; i++)
    {
        Commands.NameEntity(cube, name);
        if (Commands.EntityName(cube) != name)
            throw new InvalidOperationException("Native string round trip failed.");
    }
}
void Media()
{
    for (int i = 0; i < iterations; i++)
    {
        float version = NativeMedia.FSOUND_GetVersion();
        if (version < 3.7f || version > 3.8f) throw new InvalidOperationException("Original FMOD version query failed.");
    }
}
void Measure(string name, Action action, int calls)
{
    action(); // Warm JIT, lazy binding and native resources before measuring.
    for (int round = 0; round < 5; round++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        action();
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        results.Add(new { name, round, calls, milliseconds = ms, allocatedBytes = bytes });
        if (name == "numeric" && Environment.GetEnvironmentVariable("BLITZCS_BINDING_REQUIRE_ZERO_ALLOC") == "1" && bytes != 0)
            throw new InvalidOperationException($"Warmed numeric bindings allocated {bytes} bytes.");
    }
}
if (Environment.GetEnvironmentVariable("BLITZCS_BINDING_CORRECTNESS_ONLY") != "1")
{
    Measure("numeric", Numeric, iterations * 3);
    Measure("strings", Strings, 20_000);
    Measure("media", Media, iterations);
}
Commands.PositionEntity(cube, 0, 0, 0, 0);
for (int frame = 0; frame < 120; frame++)
{
    Commands.TurnEntity(cube, 0.1f, 0.2f, 0, 0);
    Commands.UpdateWorld(1);
    Commands.RenderWorld(1);
    Commands.Flip(0);
}
var output = Environment.GetEnvironmentVariable("BLITZCS_BINDING_BENCHMARK_OUTPUT")
    ?? throw new InvalidOperationException("Set BLITZCS_BINDING_BENCHMARK_OUTPUT to a new JSON file.");
File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS native bindings: numeric, embedded-NUL strings, GC and 120 rendered frames.");
