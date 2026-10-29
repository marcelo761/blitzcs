using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Bench;

/// <summary>
/// Reproducible microbenchmark for GuestMemory.ReadAnsiString. To compare Runtime builds,
/// copy each build's Runtime.dll into this benchmark's bin/Release/net10.0 folder and run
/// with --no-build. Disable ReadyToRun when testing a Windows-published DLL on Linux; the
/// report includes the loaded assembly hash to identify each result.
/// </summary>
internal static class AnsiStringBench
{
    public static int Run(string[] args)
    {
        var cpu = 3;
        var samples = 5;
        var iterations = 10_000;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ansi-string-scan": break;
                case "--cpu": cpu = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--samples": samples = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("unknown ANSI benchmark argument " + args[i]);
            }
        }
        if (samples <= 0 || iterations <= 0) throw new ArgumentOutOfRangeException(nameof(args), "samples and iterations must be positive");

        var pinned = Affinity.Pin(cpu);
        byte[] code = [0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3]; // mov eax, 1; ret
        var bytes = new BenchPeBuilder().WithCode(code).Export("ansi_bench_entry", 0).Build();
        var image = PeReader.Read(bytes);
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "ansi-string-scan");
        var errors = artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidOperationException("translation failed: " + string.Join("; ", errors.Select(e => e.Message)));

        using var module = GuestModule.Load(artifact, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        using var buffer = module.Memory.Allocate(512);
        var rows = new List<object>();
        foreach (var length in new[] { 8, 24, 128 })
        {
            var input = Enumerable.Repeat((byte)'x', length + 1).ToArray();
            input[length] = 0;
            module.Memory.Write(buffer.Address, input);
            for (var i = 0; i < 20_000; i++) _ = module.Memory.ReadAnsiString(buffer.Address);

            var elapsedNs = new List<double>(samples);
            var allocatedBytes = new List<double>(samples);
            var expected = (long)iterations * (length * 31L + 'x' + 'x');
            for (var sample = 0; sample < samples; sample++)
            {
                GC.Collect();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                long checksum = 0;
                for (var i = 0; i < iterations; i++)
                {
                    var value = module.Memory.ReadAnsiString(buffer.Address);
                    checksum += value.Length * 31L + value[0] + value[^1];
                }
                var elapsed = Stopwatch.GetElapsedTime(start);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                if (checksum != expected) throw new InvalidOperationException($"length {length} checksum mismatch: {checksum} != {expected}");
                elapsedNs.Add(elapsed.TotalNanoseconds / iterations);
                allocatedBytes.Add(allocated / (double)iterations);
            }

            rows.Add(new
            {
                stringLength = length,
                iterationsPerSample = iterations,
                samplesNsPerCall = elapsedNs,
                medianNsPerCall = Dist.Of(elapsedNs).Median,
                samplesAllocatedBytesPerCall = allocatedBytes,
                medianAllocatedBytesPerCall = Dist.Of(allocatedBytes).Median,
                expectedChecksum = expected,
            });
        }

        var runtimePath = typeof(GuestMemory).Assembly.Location;
        var runtimeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimePath))).ToLowerInvariant();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            benchmark = "guest-memory-ansi-string-scan",
            runtimeAssembly = Path.GetFileName(runtimePath),
            runtimeSha256 = runtimeHash,
            dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            cpu = cpu,
            pinnedCpu = pinned ? cpu : (int?)null,
            samples,
            warmupCallsPerLength = 20_000,
            strings = rows,
            compareCommand = "DOTNET_ReadyToRun=0 COMPlus_ReadyToRun=0 DOTNET_TieredPGO=0 dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench --no-build -- --ansi-string-scan --cpu 3 --samples 5 --iterations 10000",
            dllSwapNote = "For a paired Runtime comparison, preserve the benchmark output directory and replace only BlitzCS.BinaryCompiler.Runtime.dll between process runs. Do not rebuild between runs; verify runtimeSha256 differs as expected.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
