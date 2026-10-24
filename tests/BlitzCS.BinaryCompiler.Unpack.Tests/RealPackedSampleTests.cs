using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;
using Xunit.Abstractions;

namespace BlitzCS.BinaryCompiler.Unpack.Tests;

/// <summary>
/// Runs against a real UPX-packed DLL named by BLITZCS_PACKED_SAMPLE (read only, never copied into the repository). The tests return immediately when the variable is
/// unset or the file is missing. The assertions are structural (reached the OEP, exports populated, derived image translates, a getter returns a plausible value);
/// no library version is hard-coded: the observed value is written to the test output.
/// </summary>
[Collection("GuestRuntime")]
public sealed class RealPackedSampleTests(ITestOutputHelper output)
{
    private static string? Sample => Environment.GetEnvironmentVariable("BLITZCS_PACKED_SAMPLE") is { Length: > 0 } p && File.Exists(p) ? p : null;

    [Fact]
    public void Real_sample_unpacks_translates_and_a_trivial_export_runs()
    {
        if (Sample is null) { output.WriteLine("BLITZCS_PACKED_SAMPLE not set; skipped."); return; }
        var original = File.ReadAllBytes(Sample);
        var r = ControlledUnpacker.TryUnpack(PeReader.Read(original));
        output.WriteLine($"success={r.Success} oep={r.Report.OepRva:X} confidence={r.Report.Confidence} instructions={r.Report.Instructions} elapsed={r.Report.Elapsed} written={r.Report.BytesWrittenToImage}");
        foreach (var e in r.Report.Evidence) output.WriteLine("evidence: " + e);
        foreach (var d in r.Diagnostics) output.WriteLine($"{d.Severity} {d.Code}: {d.Message}");
        output.WriteLine($"resolved by the stub: {r.Report.ResolvedImports.Count} (called: {r.Report.ResolvedImports.Count(i => i.Called)}); rebuilt imports {r.Report.RebuiltImports} in {r.Report.RebuiltImportDescriptors} descriptor(s); code pages {r.Report.CodePages}, data pages {r.Report.DataPages}");
        Assert.True(r.Success);
        Assert.Equal(OepConfidence.High, r.Report.Confidence);
        Assert.Equal(r.Report.ExportsTotal, r.Report.ExportsPopulated);
        Assert.Equal(original, File.ReadAllBytes(Sample));

        var derived = PeReader.Read(r.DerivedPe!);
        Assert.DoesNotContain(derived.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(derived.Sections, s => s.IsExecutable && s.IsWritable);
        Assert.Equal(r.Report.RebuiltImports, derived.Imports.Count);
        var options = new BinaryCompilerOptions();
        var artifact = X64Translator.Translate(derived, options, CacheKey.Compute(derived, options));
        foreach (var g in artifact.Diagnostics.GroupBy(d => (d.Severity, d.Code))) output.WriteLine($"translate {g.Key.Severity} {g.Key.Code} x{g.Count()}");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // The derived image has no relocation table, so function-pointer tables are found by scanning data; the speculative roots may decode into
        // data and are reported as these two warnings only.
        foreach (var w in artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning && d.Code is not ("CFG_DECODE_FAILURE" or "BC_UNTRANSLATED_TARGET")).Take(8)) output.WriteLine($"warn {w.Code} @{w.GuestRva:X}: {w.Message}");
        Assert.All(artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning), d => Assert.Contains(d.Code, new[] { "CFG_DECODE_FAILURE", "BC_UNTRANSLATED_TARGET", "BC_UNSUPPORTED_INSTRUCTION", "BC_SEGMENT_OVERRIDE" }));
        Assert.Contains(artifact.Diagnostics, d => d.Code == "CFG_POINTER_SCAN_ROOTS");

        using var resolver = new Win32ImportResolver();
        var missing = resolver.Analyze(derived).Count(v => v.Status == Win32ImportStatus.Missing);
        output.WriteLine($"imports {derived.Imports.Count}: Win32 component implements {derived.Imports.Count - missing}, missing {missing}");
        var getter = derived.Exports.FirstOrDefault(e => e.Name is { } n && n.Contains("GetVersion", StringComparison.OrdinalIgnoreCase));
        if (getter is null) return;
        float value;
        using (var module = GuestModule.Load(artifact, derived, resolver, new GuestModuleOptions { RunEntryPoint = false, AllowUnboundImports = true }))
            value = module.Bind(getter.Name!, GuestCallingConvention.Stdcall, 0).CallSingle();
        output.WriteLine($"{getter.Name} -> {value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        Assert.True(float.IsFinite(value) && value > 0 && value < 100, "a version getter should return a small positive float");

        // With the Win32 component's msvcrt/kernel32/winmm services DllMain (the OEP) now runs to completion and returns TRUE.
        using var attachResolver = new Win32ImportResolver(); // one resolver per guest module
        using var attached = GuestModule.Load(artifact, derived, attachResolver, new GuestModuleOptions { RunEntryPoint = true, AllowUnboundImports = true });
        output.WriteLine("DllMain: returned TRUE (module loaded)");
        Assert.Equal(3.75f, attached.Bind(getter.Name!, GuestCallingConvention.Stdcall, 0).CallSingle());
        Assert.Empty(Kernel32ExtraService.ThreadFaults(attachResolver.Context));

        uint Call(string name, params uint[] args)
        {
            try { var r = attached.Bind("_FSOUND_" + name + "@" + 4 * args.Length, GuestCallingConvention.Stdcall, args.Length).Call(args); output.WriteLine($"{name}({string.Join(",", args)}) -> {(sbyte)r}"); return r; }
            catch (GuestException e) { output.WriteLine($"{name}: {e.GetType().Name}: {e.Message}"); throw; }
        }

        // State-only API. Values other than the ones asserted are observations (written to the output), not validation against FMOD documentation.
        Assert.Equal(0, (sbyte)Call("GetError"));
        Assert.Equal(0, (sbyte)Call("GetNumDrivers"));       // headless host: winmm reports no devices
        foreach (var output3 in new uint[] { 0, 1 })          // FSOUND_OUTPUT_NOSOUND = 0 and WINMM = 1 (docs/native-original-m13.md observes NOSOUND = 0)
        {
            Call("SetOutput", output3);
            Assert.Equal((sbyte)output3, (sbyte)Call("GetOutput"));
        }
        Call("SetOutput", 0);
        output.WriteLine("GetMixer observed " + (sbyte)Call("GetMixer") + "; GetOutputRate observed " + Call("GetOutputRate"));
        // FSOUND_Init / SetDriver / driver enumeration dispatch through a function-pointer table. The unpacker drops relocations, so the translator
        // finds those targets by scanning data for code pointers.
        try { Call("Init", 44100, 64, 0); output.WriteLine("Init completed"); }
        catch (GuestException e) { output.WriteLine("FSOUND_Init: " + e.GetType().Name + ": " + e.Message); throw; }
        Assert.Equal(0u, Call("GetError") & 0xFF);                   // no error recorded after a successful NOSOUND initialisation
        Assert.Equal(0, (sbyte)Call("GetOutput"));                   // NOSOUND stays selected
        // State-only getters after initialisation: observations, not validation against the FMOD documentation.
        foreach (var getter2 in new[] { "GetMaxChannels", "GetChannelsPlaying", "GetNumHWChannels", "GetCPUUsage", "GetSFXMasterVolume" })
            try { Call(getter2); } catch (GuestException e) { output.WriteLine($"{getter2}: {e.GetType().Name}"); }
        // A guest worker thread that dies leaves its waiters spinning on Windows too; fail fast with a clear message rather than hang the run.
        var finished = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (!finished.Wait(TimeSpan.FromSeconds(30)))
                Environment.FailFast("FSOUND_Close did not return; thread faults: " + string.Join(" | ", Kernel32ExtraService.ThreadFaults(attachResolver.Context).Select(f => f.Message)));
        }) { IsBackground = true };
        watchdog.Start();
        Call("Close");
        finished.Set();
        Assert.Empty(Kernel32ExtraService.ThreadFaults(attachResolver.Context));
        Assert.Equal(0u, Call("GetError") & 0xFF);
    }
}
