using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Bench;
using BlitzCS.BinaryCompiler.Runtime;
using Iced.Intel;

// M21 benchmark: translated PE32 (x64 AOT) vs the SAME machine code running natively as a static ELF32 on the IA-32 compat mode.
// usage: dotnet run -c Release -- [--samples 15] [--cpu 3] [--target-ms 150] [--only name,name] [--out file.json] [--reuse-n previous.json]
//                                 [--artifacts dir] [--disasm export] [--cold-child cacheDir dll]
if (args.Contains("--ansi-string-scan", StringComparer.Ordinal)) return AnsiStringBench.Run(args);
var opt = Options.Parse(args);
if (opt.ColdChild is not null) return ColdChild(opt);
if (opt.Stress is not null) return Stress(opt.Stress);
if (opt.RegistryDispatch) return RegistryDispatchBench(opt.Cpu, opt.Samples, opt.RegistryIterations, opt.RegistryAlternatingLate);
if (opt.CommandEntryCost) return CommandEntryCostBench(opt.Cpu, opt.Samples, opt.CommandEntryIterations);
var repo = FindRepo();
var artifacts = opt.Artifacts ?? Path.Combine(Path.GetTempPath(), "blitzcs-m21-bench");
Directory.CreateDirectory(artifacts);
if (!File.Exists(Path.Combine(artifacts, "bench.oracle")))
    Run("bash", [Path.Combine(repo, "tools/x86_oracle/bench/build.sh"), artifacts]);
var pinned = Affinity.Pin(opt.Cpu);
var b = BenchImage.Create(artifacts);
if (opt.Disasm is not null) { Disasm.Print(b, opt.Disasm); return 0; }

var report = new SortedDictionary<string, object?>();
report["meta"] = Meta.Collect(repo, opt, pinned, args);

// ---- translation / load costs -------------------------------------------------------------------------------------------
var translation = Translation.Measure(b, artifacts);
report["translation"] = translation.Json;
var art = translation.Artifact;
report["codeSize"] = CodeSize.Measure(b, art);

var rssBefore = Meta.RssKb();
var cbCalls = 0L; var nestedFn = (GuestFunction?)null;
var resolver = new DictionaryGuestImportResolver()
    .Add("host.dll", "cb", GuestImportBuilder.Cdecl(x => { cbCalls++; return x + 1; }))
    .Add("host.dll", "cb_nested", GuestImportBuilder.Cdecl(x => { cbCalls++; return nestedFn!.Call() + x + 1; }));
var loadTimes = new List<double>();
for (var i = 0; i < 15; i++)
{
    var sw = Stopwatch.StartNew();
    using var tmp = GuestModule.Load(art, b.Image, resolver, new GuestModuleOptions { RunEntryPoint = false });
    loadTimes.Add(sw.Elapsed.TotalMilliseconds);
}
using var module = GuestModule.Load(art, b.Image, resolver, new GuestModuleOptions { RunEntryPoint = false });
var rssAfter = Meta.RssKb();
report["load"] = new { loadMs = Dist.Of(loadTimes), rssBeforeKb = rssBefore, rssAfterOneModuleKb = rssAfter, rssDeltaKb = rssAfter - rssBefore };

GuestFunction Bind(string name, int args) => module.Bind(name, GuestCallingConvention.Cdecl, args);
var init = Bind("w_init", 1);
nestedFn = Bind("nop_fn", 0);

// ---- workloads ----------------------------------------------------------------------------------------------------------
var oracle = new Oracle(Path.Combine(artifacts, "bench.oracle"), opt.Cpu);
var workloads = Workload.All.Where(w => opt.Only is null || opt.Only.Contains(w.Name)).ToList();
var results = new List<object>();
using var perf = new PerfCounters();
var perfOk = Enumerable.Range(0, 4).Select(perf.Available).ToArray();
foreach (var w in workloads)
{
    var fn = Bind(w.Export, 1);
    var n = opt.ReuseN is not null && opt.ReuseN.TryGetValue(w.Name, out var fixedN) ? fixedN : oracle.Calibrate(w, opt.TargetMs);
    // equality check before any timing
    init.Call(12345u);
    var guestResult = fn.Call(n);
    var native = oracle.Run(w.Name, n, 1)[0];
    var equal = guestResult == native.Result;
    if (!equal) { Console.Error.WriteLine($"MISMATCH {w.Name}: native {native.Result:X8} translated {guestResult:X8}"); }
    // warmup
    init.Call(12345u); fn.Call(n / 8 + 1);
    var oSamples = new List<OracleSample>(); var tNs = new List<double>(); var tCnt = new List<long[]>(); var oWall = new List<double>();
    for (var i = 0; i < opt.Samples; i++)
    {
        void Native() { var o = oracle.RunTimed(w.Name, n); oSamples.Add(o.Sample); oWall.Add(o.WallMs * 1e6); }
        void Translated()
        {
            init.Call(12345u);
            perf.Start(); var sw = Stopwatch.GetTimestamp();
            var r = fn.Call(n);
            var el = Stopwatch.GetElapsedTime(sw); var c = perf.Stop();
            if (r != native.Result) equal = false;
            tNs.Add(el.TotalMilliseconds * 1e6); tCnt.Add(c);
        }
        if ((i & 1) == 0) { Native(); Translated(); } else { Translated(); Native(); }
    }
    if (oSamples.Any(s => s.Result != native.Result)) equal = false;
    var oNs = oSamples.Select(s => (double)s.Ns).ToList();
    var ratios = oNs.Zip(tNs, (o, t) => t / o).ToList();
    object CounterDists(IEnumerable<long[]> rows, bool[] ok)
    {
        var d = new Dictionary<string, object?>();
        var list = rows.ToList();
        for (var k = 0; k < 4; k++)
            d[PerfCounters.Names[k]] = list.All(r => r[k] >= 0) ? Dist.Of(list.Select(r => (double)r[k]).ToList()) : null;
        return d;
    }
    var oMed = Dist.Of(oNs).Median; var tMed = Dist.Of(tNs).Median;
    double Med(IEnumerable<long[]> rows, int k) { var l = rows.Select(r => (double)r[k]).ToList(); return l.All(x => x >= 0) ? Dist.Of(l).Median : double.NaN; }
    var oC = oSamples.Select(s => s.Counters).ToList();
    var instrRatio = Med(tCnt, 1) / Med(oC, 1);
    var cycRatio = Med(tCnt, 0) / Med(oC, 0);
    results.Add(new
    {
        name = w.Name, description = w.Description, export = w.Export, n, resultChecksum = $"0x{native.Result:X8}", resultsEqualBeforeAndDuringTiming = equal,
        native = new { ns = Dist.Of(oNs), tscTicks = Dist.Of(oSamples.Select(s => (double)s.Tsc).ToList()), externalWallNs = Dist.Of(oWall), counters = CounterDists(oC, perfOk) },
        translated = new { ns = Dist.Of(tNs), counters = CounterDists(tCnt, perfOk) },
        overheadPercent = new
        {
            medianOfMedians = (tMed / oMed - 1) * 100,
            pairedRatioMedianPercent = (Dist.Of(ratios).Median - 1) * 100,
            pairedRatioP10Percent = (Dist.Of(ratios).P10 - 1) * 100,
            pairedRatioP90Percent = (Dist.Of(ratios).P90 - 1) * 100,
            cyclesRatioMinus1Percent = double.IsNaN(cycRatio) ? (double?)null : (cycRatio - 1) * 100,
            dynamicInstructionExpansion = double.IsNaN(instrRatio) ? (double?)null : instrRatio,
        },
    });
    Console.Error.WriteLine($"{w.Name,-8} n={n,-10} native {oMed / 1e6,8:F1} ms  translated {tMed / 1e6,8:F1} ms  overhead {(tMed / oMed - 1) * 100,7:F1}%  instr x{instrRatio:F2}  eq={equal}");
}
report["workloads"] = results;
report["perfCountersAvailable"] = PerfCounters.Names.Select((nme, i) => new { counter = nme, available = perfOk[i] }).ToArray();

// ---- ABI transition latency ---------------------------------------------------------------------------------------------
report["abi"] = Abi.Measure(b, module, oracle, opt, resolver.Resolve("host.dll", "cb", null)!, art, ref cbCalls);

// ---- process cold start -------------------------------------------------------------------------------------------------
if (opt.Only is null) report["coldStart"] = ColdStart.Measure(b, art, artifacts, opt);

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
if (opt.Out is not null) File.WriteAllText(opt.Out, json + "\n"); else Console.WriteLine(json);
return report["workloads"] is List<object> && results.Count > 0 && !json.Contains("\"resultsEqualBeforeAndDuringTiming\": false") ? 0 : 1;

static string FindRepo()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "BlitzCS.slnx"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("repository root (BlitzCS.slnx) not found");
}

static string Run(string exe, IEnumerable<string> a)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var x in a) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed ({p.ExitCode}): {e}");
    return o;
}

// Robustness check, not a benchmark: translate arbitrary PE32 DLLs (nothing is executed) and report errors, size and time.
static int Stress(string[] dlls)
{
    var failures = 0;
    foreach (var dll in dlls)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var image = PeReader.Read(File.ReadAllBytes(dll));
            var art = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(), "stress");
            var errs = art.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            Console.WriteLine($"{Path.GetFileName(dll),-40} ok  {sw.Elapsed.TotalMilliseconds,8:F1} ms  host code {art.CodeSize,8}  image {image.SizeOfImage,8}  error-diagnostics {errs}  total {art.Diagnostics.Count}");
        }
        catch (Exception e) { failures++; Console.WriteLine($"{Path.GetFileName(dll),-40} EXCEPTION {e.GetType().Name}: {e.Message}"); }
    }
    return failures == 0 ? 0 : 1;
}

// End-to-end foreign-dispatch benchmark. The consumer is first and the target is
// last; the initial call takes the full scan, then steady-state calls exercise the MRU cache.
static int RegistryDispatchBench(int cpu, int samples, int iterations, bool alternatingLate)
{
    var pinned = Affinity.Pin(cpu);
    int[] providers = alternatingLate ? [2, 16, 64, 256, 1024] : [1, 16, 64, 256, 1024];
    var rows = new List<object>();
    foreach (var providerCount in providers)
    {
        var consumerBytes = new BenchPeBuilder()
            .WithCode([0xFF, 0x74, 0x24, 0x08, 0xFF, 0x54, 0x24, 0x08, 0x83, 0xC4, 0x04, 0xC3])
            .Export("callptr", 0).Relocate(BenchPeBuilder.DataRva).Build();
        var providerBytes = new BenchPeBuilder()
            .WithCode([0x8B, 0x44, 0x24, 0x04, 0x40, 0xC3])
            .Export("target", 0).Relocate(BenchPeBuilder.DataRva).Build();
        var consumer = PeReader.Read(consumerBytes);
        var provider = PeReader.Read(providerBytes);
        var modules = new List<(string Name, PeImage Image)> { ("consumer", consumer) };
        var fillerCount = alternatingLate ? providerCount - 2 : providerCount;
        for (var i = 0; i < fillerCount; i++) modules.Add(($"provider{i:D4}", provider));
        if (alternatingLate)
        {
            modules.Add(("provider-late-a", provider));
            modules.Add(("provider-late-b", provider));
        }
        var bases = ModuleBaseAllocator.Allocate(modules);
        var artifacts = new List<(string Name, TranslationArtifact Artifact, PeImage Image)>();
        for (var i = 0; i < modules.Count; i++)
        {
            var image = modules[i].Image.Rebase(bases[i].Base);
            var artifact = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(
                modules[i].Image,
                new BinaryCompilerOptions(OptimizationProfile.Release, LoadBase: bases[i].Base),
                $"registry-dispatch-{providerCount}-{i}");
            if (artifact.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException($"translation failed for {modules[i].Name}: {string.Join("; ", artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message))}");
            artifacts.Add((modules[i].Name, artifact, image));
        }
        using var set = new GuestModuleSet();
        foreach (var m in artifacts) set.Add(m.Name + ".dll", m.Artifact, m.Image, ExportConventions.AllCdecl);
        set.LoadAll();
        var call = set["consumer"].Bind("callptr", GuestCallingConvention.Cdecl, 2);
        var last = artifacts[^1].Artifact;
        var targetAddress = last.GuestImageBase + last.Exports.Single(e => e.Name == "target").GuestRva;
        var alternateTargetAddress = alternatingLate
            ? artifacts[^2].Artifact.GuestImageBase + artifacts[^2].Artifact.Exports.Single(e => e.Name == "target").GuestRva
            : targetAddress;
        uint RunBatch()
        {
            uint sum = 0;
            for (uint i = 0; i < iterations; i++)
            {
                var address = alternatingLate && (i & 1) == 0 ? alternateTargetAddress : targetAddress;
                sum = unchecked(sum + call.Call(address, i));
            }
            return sum;
        }

        var expected = unchecked((uint)((long)iterations * (iterations + 1) / 2));
        if (RunBatch() != expected) throw new InvalidOperationException($"registry dispatch checksum mismatch at {providerCount + 1} records");
        _ = RunBatch(); // warm-up
        var nsPerCall = new List<double>();
        for (var sample = 0; sample < samples; sample++)
        {
            var sw = Stopwatch.StartNew();
            var checksum = RunBatch();
            sw.Stop();
            if (checksum != expected) throw new InvalidOperationException("registry dispatch checksum changed during timing");
            nsPerCall.Add(sw.Elapsed.TotalNanoseconds / iterations);
        }
        rows.Add(new { registryRecords = providerCount + 1, targetRecord = alternatingLate ? $"last two ({providerCount}, {providerCount + 1})" : (object)(providerCount + 1), iterationsPerSample = iterations, samples, expectedChecksum = expected, medianNsPerCall = Dist.Of(nsPerCall).Median, samplesNsPerCall = nsPerCall });
        Console.Error.WriteLine($"registry records={providerCount + 1,4}  median={Dist.Of(nsPerCall).Median,9:F1} ns/call");
    }
    var benchmark = alternatingLate ? "foreign-dispatch-alternating-last-two-records-mru-misses" : "foreign-dispatch-last-record-steady-state-cache";
    Console.WriteLine(JsonSerializer.Serialize(new { benchmark, pinnedCpu = pinned ? cpu : (int?)null, iterationsPerSample = iterations, samples, rows }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// Isolates the public guest-call entry paths with the same tiny translated target:
// a pre-bound GuestFunction, RVA lookup on every call, and a guest-side loop that
// amortizes the managed->guest entry over many translated calls.
static int CommandEntryCostBench(int cpu, int samples, int iterations)
{
    var pinned = Affinity.Pin(cpu);
    // nop_fn returns 1; guest_loop calls it count times and returns count.
    byte[] code =
    [
        0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,
        0x8B, 0x4C, 0x24, 0x04, 0x31, 0xD2, 0x85, 0xC9, 0x74, 0x0A,
        0xE8, 0xEB, 0xFF, 0xFF, 0xFF, 0x01, 0xC2, 0x49, 0x75, 0xF6, 0x89, 0xD0, 0xC3,
    ];
    var bytes = new BenchPeBuilder().WithCode(code).Export("nop_fn", 0).Export("guest_loop", 6).Build();
    var image = PeReader.Read(bytes);
    var artifact = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(
        image, new BinaryCompilerOptions(OptimizationProfile.Release), "command-entry-cost");
    var errors = artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0) throw new InvalidOperationException("translation failed: " + string.Join("; ", errors.Select(e => e.Message)));

    using var module = GuestModule.Load(artifact, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
    var cached = module.Bind("nop_fn", GuestCallingConvention.Cdecl, 0);
    var loop = module.Bind("guest_loop", GuestCallingConvention.Cdecl, 1);
    var rva = BenchPeBuilder.TextRva;

    uint CachedBatch(int count)
    {
        uint sum = 0;
        for (var i = 0; i < count; i++) sum = unchecked(sum + cached.Call());
        return sum;
    }
    uint RvaBatch(int count)
    {
        uint sum = 0;
        for (var i = 0; i < count; i++) sum = unchecked(sum + module.InvokeRva(rva, "nop_fn", GuestReturn.Eax, ReadOnlySpan<uint>.Empty).Eax);
        return sum;
    }

    var expected = unchecked((uint)iterations);
    if (CachedBatch(100) != 100 || RvaBatch(100) != 100 || loop.Call(100) != 100)
        throw new InvalidOperationException("entry-path checksum mismatch before timing");
    for (var i = 0; i < 20_000; i++) { _ = cached.Call(); _ = module.InvokeRva(rva, "nop_fn", GuestReturn.Eax, ReadOnlySpan<uint>.Empty); }
    _ = loop.Call(100_000);

    object Measure(string name, Func<int, uint> body)
    {
        var elapsedNs = new List<double>(samples);
        var allocatedBytes = new List<double>(samples);
        for (var sample = 0; sample < samples; sample++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var checksum = body(iterations);
            var elapsed = Stopwatch.GetElapsedTime(start);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (checksum != expected) throw new InvalidOperationException($"{name} checksum mismatch: {checksum} != {expected}");
            elapsedNs.Add(elapsed.TotalNanoseconds / iterations);
            allocatedBytes.Add(allocated / (double)iterations);
        }
        return new { name, medianNsPerOperation = Dist.Of(elapsedNs).Median, samplesNsPerOperation = elapsedNs,
            medianAllocatedBytesPerOperation = Dist.Of(allocatedBytes).Median, samplesAllocatedBytesPerOperation = allocatedBytes };
    }

    var rows = new object[]
    {
        Measure("cached-GuestFunction.Call", CachedBatch),
        Measure("GuestModule.InvokeRva", RvaBatch),
        Measure("guest-side-loop-calling-nop_fn", count => loop.Call((uint)count)),
    };
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        benchmark = "guest-command-entry-cost",
        pinnedCpu = pinned ? cpu : (int?)null,
        iterations,
        samples,
        target = "same translated nop_fn (returns 1); guest-loop calls it N times and returns N",
        rows,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int ColdChild(Options o)
{
    // Minimal real use: cached artifact -> load -> one export call. Used to time whole-process cold start.
    if (o.ColdChild == "noop") return 0;
    var dll = o.ColdChildDll!;
    var res = BinaryBuild.Translate(dll, new BinaryCompilerOptions(), o.ColdChild!);
    var image = PeReader.Read(File.ReadAllBytes(dll));
    var imports = new DictionaryGuestImportResolver()
        .Add("host.dll", "cb", GuestImportBuilder.Cdecl(x => x + 1)).Add("host.dll", "cb_nested", GuestImportBuilder.Cdecl(x => x + 1));
    using var m = GuestModule.Load(res.Artifact, image, imports, new GuestModuleOptions { RunEntryPoint = false });
    var r = m.Bind("add3", GuestCallingConvention.Cdecl, 3).Call(1, 2, 3);
    return r == 6 ? 0 : 3;
}

internal sealed record Workload(string Name, string Export, string Description, uint Seed)
{
    public static readonly Workload[] All =
    [
        new("int", "w_int", "integer xorshift/multiply/rotate loop", 20_000_000),
        new("fnv", "w_fnv", "FNV-1a hash over a 64 KB buffer", 40),
        new("crc", "w_crc", "table CRC32 over a 64 KB buffer", 40),
        new("mem", "w_mem", "rep movsd copy + repne scasb scan, 64 KB", 600),
        new("fib", "w_fib", "recursive fibonacci (call/ret heavy)", 30),
        new("calls", "w_calls", "non-inlined leaf and 3-deep call chain", 10_000_000),
        new("switch", "w_switch", "bytecode-style 16-way switch (jump table)", 20_000_000),
        new("ind", "w_ind", "indirect calls via function-pointer table", 20_000_000),
        new("lz", "w_lz", "LZ-style hash match finder + mixing", 20),
        new("matmul", "w_matmul", "40x40 integer matrix multiply", 100),
        new("x87", "w_x87", "x87 float multiply-accumulate", 20_000),
        new("sse", "w_sse", "SSE2 packed float multiply-accumulate", 40_000),
    ];
}

internal sealed record OracleSample(uint Result, ulong Tsc, ulong Ns, long[] Counters);

internal sealed class Oracle(string path, int cpu)
{
    public IReadOnlyList<OracleSample> Run(string name, uint n, int reps) => RunTimed(name, n, reps).Samples;
    public (OracleSample Sample, double WallMs) RunTimed(string name, uint n) { var r = RunTimed(name, n, 1); return (r.Samples[0], r.WallMs); }

    private (List<OracleSample> Samples, double WallMs) RunTimed(string name, uint n, int reps)
    {
        var psi = new ProcessStartInfo("taskset") { RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", cpu.ToString(CultureInfo.InvariantCulture), path, name, n.ToString(CultureInfo.InvariantCulture), reps.ToString(CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var wall = sw.Elapsed.TotalMilliseconds;
        if (p.ExitCode != 0) throw new InvalidOperationException($"oracle exited {p.ExitCode}");
        var list = new List<OracleSample>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse).ToArray();
            list.Add(new OracleSample((uint)f[0], f[1], f[2], f.Skip(3).Select(v => v == ulong.MaxValue ? -1L : (long)v).ToArray()));
        }
        return (list, wall);
    }

    /// <summary>Picks n so the native run takes about targetMs (always >= 100 ms with the default 150).</summary>
    public uint Calibrate(Workload w, double targetMs)
    {
        if (w.Name == "fib")
        {
            for (uint n = 24; n < 46; n++) if (Run(w.Name, n, 1)[0].Ns / 1e6 >= targetMs * 0.8) return n;
            throw new InvalidOperationException("fib calibration failed");
        }
        var cur = w.Seed;
        for (var round = 0; round < 3; round++)
        {
            var ms = Math.Min(Run(w.Name, cur, 1)[0].Ns, Run(w.Name, cur, 1)[0].Ns) / 1e6;
            cur = (uint)Math.Max(1, Math.Round(cur * targetMs / Math.Max(ms, 0.01)));
        }
        return cur;
    }
}

internal sealed class BenchImage
{
    public required string Dll, CodeBin, ExportsJson;
    public required byte[] Code;
    public required PeImage Image;
    public required Dictionary<string, uint> Exports;
    public required uint CbIatRva, CbNestedIatRva;

    public static BenchImage Create(string dir)
    {
        var code = File.ReadAllBytes(Path.Combine(dir, "bench.code.bin"));
        var data = File.ReadAllBytes(Path.Combine(dir, "bench.data.bin"));
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bench.exports.json")));
        var fx = new BenchPeBuilder().WithCode(code).WithData(data);
        var exports = new Dictionary<string, uint>();
        foreach (var e in meta.RootElement.GetProperty("exports").EnumerateArray())
        { var name = e.GetProperty("name").GetString()!; var off = e.GetProperty("offset").GetUInt32(); exports[name] = off; fx.Export(name, off); }
        foreach (var r in meta.RootElement.GetProperty("relocRvas").EnumerateArray()) fx.Relocate(r.GetUInt32());
        var cb = fx.Import("host.dll", "cb"); var cbn = fx.Import("host.dll", "cb_nested");
        var path = Path.Combine(dir, "bench.dll");
        File.WriteAllBytes(path, fx.Build());
        return new BenchImage { Dll = path, CodeBin = Path.Combine(dir, "bench.code.bin"), ExportsJson = Path.Combine(dir, "bench.exports.json"), Code = code, Image = PeReader.Read(File.ReadAllBytes(path)), Exports = exports, CbIatRva = cb, CbNestedIatRva = cbn };
    }
}

internal static class Translation
{
    public sealed record Result(TranslationArtifact Artifact, object Json);

    public static Result Measure(BenchImage b, string dir)
    {
        var o = new BinaryCompilerOptions();
        var cold = new List<double>(); var parse = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            var cache = Path.Combine(dir, "cache-cold-" + i); if (Directory.Exists(cache)) Directory.Delete(cache, true);
            var sw = Stopwatch.StartNew();
            var r = BinaryBuild.Translate(b.Dll, o, cache);
            cold.Add(sw.Elapsed.TotalMilliseconds);
            if (r.CacheHit) throw new InvalidOperationException("expected a cold translation");
            sw.Restart(); _ = PeReader.Read(File.ReadAllBytes(b.Dll)); parse.Add(sw.Elapsed.TotalMilliseconds);
        }
        var warmCache = Path.Combine(dir, "cache-warm"); if (Directory.Exists(warmCache)) Directory.Delete(warmCache, true);
        var art = BinaryBuild.Translate(b.Dll, o, warmCache).Artifact;
        var hit = new List<double>();
        for (var i = 0; i < 25; i++)
        {
            var sw = Stopwatch.StartNew(); var r = BinaryBuild.Translate(b.Dll, o, warmCache); hit.Add(sw.Elapsed.TotalMilliseconds);
            if (!r.CacheHit) throw new InvalidOperationException("expected a cache hit");
        }
        return new(art, new { coldTranslateMs = Dist.Of(cold), peParseOnlyMs = Dist.Of(parse), cacheHitMs = Dist.Of(hit), note = "BinaryBuild.Translate end-to-end (read file + parse + hash + translate + cache write) in-process after JIT warm-up of the first run; the first cold sample includes JIT." });
    }
}

internal static class CodeSize
{
    public static object Measure(BenchImage b, TranslationArtifact art)
    {
        var map = art.AddressMap.OrderBy(m => m.HostOffset).ToArray();
        var perFn = new List<object>();
        long totalGuestBytes = 0, totalHostBytes = 0, totalGuestIns = 0, totalHostIns = 0;
        var fns = b.Exports.OrderBy(e => e.Value).ToList();
        for (var i = 0; i < fns.Count; i++)
        {
            var start = fns[i].Value; var end = i + 1 < fns.Count ? fns[i + 1].Value : (uint)b.Code.Length;
            var gIns = Disasm.DecodeGuest(b.Code, start, end).Count;
            long hostBytes = 0;
            var rvaStart = BenchPeBuilder.TextRva + start; var rvaEnd = BenchPeBuilder.TextRva + end;
            long hIns = 0;
            for (var k = 0; k < map.Length; k++)
            {
                if (map[k].GuestRva < rvaStart || map[k].GuestRva >= rvaEnd) continue;
                var next = k + 1 < map.Length ? map[k + 1].HostOffset : (uint)art.CodeSize;
                hostBytes += next - map[k].HostOffset;
                var last = k + 1 >= map.Length;
                hIns += Disasm.DecodeHost(art.HostImage, map[k].HostOffset, next, last).Count;
                if (last) hostBytes -= next - map[k].HostOffset - (long)Disasm.DecodeHost(art.HostImage, map[k].HostOffset, next, true).Sum(x => x.Length);
            }
            totalGuestBytes += end - start; totalHostBytes += hostBytes; totalGuestIns += gIns; totalHostIns += hIns;
            perFn.Add(new { name = fns[i].Key, guestBytes = end - start, hostBytes, guestInstructions = gIns, hostInstructions = hIns });
        }
        return new
        {
            originalCodeBytes = b.Code.Length, hostImageBytes = art.HostImage.Length, hostCodeBytes = art.CodeSize,
            hostRwDataBytes = art.HostImage.Length - art.CodeSize, addressMapEntries = art.AddressMap.Count,
            functionsTotal = new { guestBytes = totalGuestBytes, hostBytes = totalHostBytes, guestInstructions = totalGuestIns, hostInstructions = totalHostIns, byteExpansion = (double)totalHostBytes / totalGuestBytes, staticInstructionExpansion = (double)totalHostIns / totalGuestIns },
            perFunction = perFn,
            note = "Host bytes of a guest instruction include any translator stub emitted between it and the next mapped instruction. Guest bytes include embedded rodata (jump/function tables) that follows each function in .text.",
        };
    }
}

internal static class Disasm
{
    public static List<Instruction> DecodeGuest(byte[] code, uint start, uint end)
    {
        var d = Decoder.Create(32, new ByteArrayCodeReader(code, (int)start, (int)(end - start)), BenchPeBuilder.ImageBase + BenchPeBuilder.TextRva + start);
        var l = new List<Instruction>();
        while (d.IP < BenchPeBuilder.ImageBase + BenchPeBuilder.TextRva + end) { var i = d.Decode(); if (i.IsInvalid) break; l.Add(i); }
        return l;
    }

    /// <summary>Decodes [start,end). <paramref name="stopAtTerminator"/> ends at the first unconditional jmp/ret/int3 (the last mapped block is followed by cold stubs, padding and data tables).</summary>
    public static List<Instruction> DecodeHost(byte[] image, uint start, uint end, bool stopAtTerminator = false)
    {
        var d = Decoder.Create(64, new ByteArrayCodeReader(image, (int)start, (int)(end - start)), start);
        var l = new List<Instruction>();
        while (d.IP < end)
        {
            var i = d.Decode(); if (i.IsInvalid) break; l.Add(i);
            if (stopAtTerminator && i.FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch or FlowControl.Return or FlowControl.Exception) break;
        }
        return l;
    }

    public static void Print(BenchImage b, string export)
    {
        var cache = Path.Combine(Path.GetDirectoryName(b.Dll)!, "cache-disasm");
        if (Directory.Exists(cache)) Directory.Delete(cache, true); // the cache key does not include the translator build: never show a stale translation
        var art = BinaryBuild.Translate(b.Dll, new BinaryCompilerOptions(), cache).Artifact;
        var fns = b.Exports.OrderBy(e => e.Value).ToList();
        uint start, end;
        if (export.Contains('-')) { var parts = export.Split('-'); start = Convert.ToUInt32(parts[0], 16); end = Convert.ToUInt32(parts[1], 16); } // text offsets, hex: 100-180
        else
        {
            var idx = fns.FindIndex(f => f.Key == export);
            start = fns[idx].Value; end = idx + 1 < fns.Count ? fns[idx + 1].Value : (uint)b.Code.Length;
        }
        var f = new NasmFormatter();
        var o = new StringOutput();
        Console.WriteLine($"; guest {export} @ RVA 0x{BenchPeBuilder.TextRva + start:X}");
        foreach (var i in DecodeGuest(b.Code, start, end)) { o.Reset(); f.Format(i, o); Console.WriteLine($"{i.IP:X8}  {o}"); }
        Console.WriteLine("; translated");
        var map = art.AddressMap.OrderBy(m => m.HostOffset).ToArray();
        var rt = map.Where(m => m.GuestRva >= BenchPeBuilder.TextRva).Min(m => m.HostOffset);
        Console.WriteLine($"; translator runtime (trap, EnterGuest, exit, dispatch, import bodies): host 0x0..0x{rt:X}");
        foreach (var i in DecodeHost(art.HostImage, 0, rt)) { o.Reset(); f.Format(i, o); Console.WriteLine($"  {i.IP:X6}  {o}"); }
        for (var k = 0; k < map.Length; k++)
        {
            if (map[k].GuestRva < BenchPeBuilder.TextRva + start || map[k].GuestRva >= BenchPeBuilder.TextRva + end) continue;
            var next = k + 1 < map.Length ? map[k + 1].HostOffset : (uint)art.CodeSize;
            Console.WriteLine($"; guest 0x{map[k].GuestRva:X} -> host 0x{map[k].HostOffset:X}");
            foreach (var i in DecodeHost(art.HostImage, map[k].HostOffset, next)) { o.Reset(); f.Format(i, o); Console.WriteLine($"  {i.IP:X6}  {o}"); }
        }
    }
}

internal static class Abi
{
    public static object Measure(BenchImage b, GuestModule m, Oracle oracle, Options opt, GuestImport cb, TranslationArtifact art, ref long cbCalls)
    {
        const int Calls = 200_000;
        var nop = m.Bind("nop_fn", GuestCallingConvention.Cdecl, 0);
        var add3 = m.Bind("add3", GuestCallingConvention.Cdecl, 3);
        var cbloop = m.Bind("w_cbloop", GuestCallingConvention.Cdecl, 2);
        // guest-visible stub addresses = what the loader wrote into the IAT
        var stub = m.Memory.ReadUInt32(m.ImageBase + b.CbIatRva); var stubNested = m.Memory.ReadUInt32(m.ImageBase + b.CbNestedIatRva);
        for (var i = 0; i < 20_000; i++) { nop.Call(); add3.Call(1, 2, 3); }
        for (var i = 0; i < 4; i++) { cbloop.Call(1_000_000, stub); cbloop.Call(1_000_000, stubNested); } // let tiered JIT settle on the import path
        Dist PerCall(Func<int, uint> body, int perIteration = 1)
        {
            var xs = new List<double>();
            for (var s = 0; s < 15; s++) { var sw = Stopwatch.GetTimestamp(); body(Calls); xs.Add(Stopwatch.GetElapsedTime(sw).TotalNanoseconds / (Calls * (double)perIteration)); }
            return Dist.Of(xs);
        }
        var empty = PerCall(n => { uint a = 0; for (var i = 0; i < n; i++) a += nop.Call(); return a; });
        var three = PerCall(n => { uint a = 0; for (var i = 0; i < n; i++) a += add3.Call(1, 2, 3); return a; });
        // managed->guest->managed import (guest loops N times inside one export call, so the entry cost amortises away)
        var callbackLoop = PerCall(n => cbloop.Call((uint)n, stub));
        var nested = PerCall(n => cbloop.Call((uint)n, stubNested));
        var nativeCbLoop = oracle.Run("cbloop", 200_000_000, 1)[0];
        var nativeNopLoop = oracle.Run("nop", 200_000_000, 1)[0];
        var baselinePInvoke = PInvokeBaseline(Calls);
        var lockReference = LockReference(Calls);
        var guestNoCallbackGuestSide = PerCall(n => m.Bind("w_int", GuestCallingConvention.Cdecl, 1).Call((uint)n)); // reference: pure guest loop per iteration
        return new
        {
            emptyExportCallNsPerCall = empty,
            addThreeArgsExportCallNsPerCall = three,
            importCallbackNsPerGuestCallbackIncludingLoop = callbackLoop,
            nestedImportThenGuestExportNsPerIteration = nested,
            nativeEquivalentLoopNsPerIteration = new { callThroughFunctionPointer = nativeCbLoop.Ns / 200_000_000.0, directNopCall = nativeNopLoop.Ns / 200_000_000.0 },
            managedToNativeUnmanagedFunctionPointerNsPerCall = baselinePInvoke,
            referenceGuestLoopNsPerIteration = guestNoCallbackGuestSide,
            lockReferenceNs = lockReference,
            importCallbacksObserved = cbCalls,
            interpretation = "emptyExport = managed->guest->managed round trip (lock, stack setup, EnterGuest/Exit). importCallback = guest->managed->guest-return per call (spill, native stack switch, UnmanagedCallersOnly thunk, GuestCall handler). nested = callback + managed->guest re-entry (nop_fn) inside the handler; subtract callback for the nested-call cost.",
        };
    }

    /// <summary>Reference costs of the synchronization primitives GuestModule.Invoke uses on every call (attribution of the empty-call latency).</summary>
    private static object LockReference(int calls)
    {
        var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        var gate = new object();
        Dist Time(Action body)
        {
            var xs = new List<double>();
            for (var s = 0; s < 15; s++) { var sw = Stopwatch.GetTimestamp(); body(); xs.Add(Stopwatch.GetElapsedTime(sw).TotalNanoseconds / calls); }
            return Dist.Of(xs);
        }
        for (var i = 0; i < 100_000; i++) { rw.EnterReadLock(); rw.ExitReadLock(); lock (gate) { } }
        return new
        {
            readerWriterLockSlimRecursiveEnterExitReadNs = Time(() => { for (var i = 0; i < calls; i++) { rw.EnterReadLock(); rw.ExitReadLock(); } }),
            monitorEnterExitUncontendedNs = Time(() => { for (var i = 0; i < calls; i++) { lock (gate) { } } }),
        };
    }

    private static unsafe Dist PInvokeBaseline(int calls)
    {
        var lib = NativeLibrary.Load("libc.so.6");
        var f = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(lib, "abs");
        for (var i = 0; i < 20_000; i++) f(i);
        var xs = new List<double>();
        for (var s = 0; s < 15; s++) { var sw = Stopwatch.GetTimestamp(); var a = 0; for (var i = 0; i < calls; i++) a += f(i); xs.Add(Stopwatch.GetElapsedTime(sw).TotalNanoseconds / calls); GC.KeepAlive(a.ToString()); }
        return Dist.Of(xs);
    }
}

internal static class ColdStart
{
    public static object Measure(BenchImage b, TranslationArtifact art, string dir, Options opt)
    {
        var cache = Path.Combine(dir, "cache-coldstart"); Directory.CreateDirectory(cache);
        BinaryBuild.Translate(b.Dll, new BinaryCompilerOptions(), cache); // prime: the measured processes always hit the cache
        var dll = typeof(Options).Assembly.Location;
        double Time(params string[] a)
        {
            var psi = new ProcessStartInfo("taskset") { RedirectStandardOutput = true, RedirectStandardError = true };
            // Launched through `dotnet <dll>` the process is the dotnet host and needs the dll; launched through the apphost it is the program itself.
            var host = Environment.ProcessPath!;
            var launch = string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase) ? new[] { host, dll } : new[] { host };
            foreach (var x in new[] { "-c", opt.Cpu.ToString(CultureInfo.InvariantCulture) }.Concat(launch).Concat(a)) psi.ArgumentList.Add(x);
            var sw = Stopwatch.StartNew(); using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd(); var err = proc.StandardError.ReadToEnd(); proc.WaitForExit();
            if (proc.ExitCode != 0) throw new InvalidOperationException("cold child failed: " + err);
            return sw.Elapsed.TotalMilliseconds;
        }
        var noop = new List<double>(); var load = new List<double>(); var elf = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            noop.Add(Time("--cold-child", "noop", "-"));
            load.Add(Time("--cold-child", cache, b.Dll));
        }
        var oracle = new Oracle(Path.Combine(dir, "bench.oracle"), opt.Cpu);
        for (var i = 0; i < 15; i++) elf.Add(oracle.RunTimed("nop", 1).WallMs);
        _ = art;
        return new
        {
            dotnetRuntimeStartupFloorMs = Dist.Of(noop),
            dotnetProcessWithCachedTranslationLoadAndOneCallMs = Dist.Of(load),
            incrementalCostOverRuntimeFloorMs = Dist.Of(load).Median - Dist.Of(noop).Median,
            nativeElf32ProcessLaunchMs = Dist.Of(elf),
            note = "Whole-process wall time including 'dotnet' host start; the child reads the cached artifact, loads the module and performs one export call.",
        };
    }
}

internal static class Meta
{
    public static long RssKb()
    {
        foreach (var l in File.ReadLines("/proc/self/status")) if (l.StartsWith("VmRSS:", StringComparison.Ordinal)) return long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        return -1;
    }

    private static string Sh(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true }; psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(cmd);
            using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return o;
        }
        catch (Exception e) { return "unavailable: " + e.Message; }
    }
    private static string F(string path) { try { return File.ReadAllText(path).Trim(); } catch { return "unavailable"; } }

    public static object Collect(string repo, Options o, bool pinned, string[] args) => new
    {
        generatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        commit = Sh($"git -C '{repo}' rev-parse HEAD"),
        workingTreeDirty = Sh($"git -C '{repo}' status --porcelain | wc -l") != "0",
        cpu = Sh("grep -m1 'model name' /proc/cpuinfo | cut -d: -f2").Trim(),
        logicalCpus = Environment.ProcessorCount,
        kernel = F("/proc/sys/kernel/osrelease"),
        os = Sh("grep PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '\"'"),
        memTotalKb = Sh("grep MemTotal /proc/meminfo | tr -s ' ' | cut -d' ' -f2"),
        virtualization = Sh("systemd-detect-virt || true"),
        cpuGovernor = F("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"),
        intelNoTurbo = F("/sys/devices/system/cpu/intel_pstate/no_turbo"),
        perfEventParanoid = F("/proc/sys/kernel/perf_event_paranoid"),
        dotnet = RuntimeInformation.FrameworkDescription,
        gcc = Sh("gcc --version | head -1"),
        oracleCompileFlags = "-m32 -O2 -ffreestanding -fno-pic -fno-pie -fno-stack-protector -fno-asynchronous-unwind-tables -fcf-protection=none -fno-builtin -nostdlib",
        pinnedCpu = pinned ? o.Cpu : (int?)null,
        samplesPerWorkload = o.Samples, targetNativeMs = o.TargetMs,
        commandLine = "dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench -- " + string.Join(' ', args),
        method = "Per sample, interleaved (order alternates): (a) native ELF32 oracle process pinned with taskset, self-timed (clock_gettime + rdtsc) around the workload with in-process user-space perf_event_open counters; (b) the same machine code translated and run through GuestModule on the pinned .NET thread, timed with Stopwatch and perf_event_open on that thread. Both run w_init(12345) first and must return identical checksums.",
    };
}

internal sealed record Options(int Samples, int Cpu, double TargetMs, HashSet<string>? Only, string? Out, string? Artifacts, string? Disasm, string? ColdChild, string? ColdChildDll, Dictionary<string, uint>? ReuseN, string[]? Stress, bool RegistryDispatch, bool RegistryAlternatingLate, int RegistryIterations, bool CommandEntryCost, int CommandEntryIterations)
{
    public static Options Parse(string[] a)
    {
        int samples = 15, cpu = 3, registryIterations = 100_000, commandEntryIterations = 100_000; double target = 150; HashSet<string>? only = null; string? @out = null, art = null, dis = null, cc = null, ccd = null; Dictionary<string, uint>? reuse = null; string[]? stress = null; bool registryDispatch = false, registryAlternatingLate = false, commandEntryCost = false;
        for (var i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--samples": samples = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--cpu": cpu = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--target-ms": target = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--only": only = [.. a[++i].Split(',')]; break;
                case "--out": @out = a[++i]; break;
                case "--artifacts": art = a[++i]; break;
                case "--disasm": dis = a[++i]; break;
                case "--cold-child": cc = a[++i]; ccd = a[++i]; break;
                case "--translate-stress": stress = a[(i + 1)..]; i = a.Length; break;
                case "--registry-dispatch": registryDispatch = true; break;
                case "--registry-dispatch-alternating-late": registryDispatch = true; registryAlternatingLate = true; break;
                case "--registry-iterations": registryIterations = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--command-entry-cost": commandEntryCost = true; break;
                case "--command-entry-iterations": commandEntryIterations = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--reuse-n":
                    using (var doc = JsonDocument.Parse(File.ReadAllText(a[++i])))
                        reuse = doc.RootElement.GetProperty("workloads").EnumerateArray().ToDictionary(w => w.GetProperty("name").GetString()!, w => w.GetProperty("n").GetUInt32());
                    break;
                default: throw new ArgumentException("unknown argument " + a[i]);
            }
        return new(samples, cpu, target, only, @out, art, dis, cc, ccd, reuse, stress, registryDispatch, registryAlternatingLate, registryIterations, commandEntryCost, commandEntryIterations);
    }
}
