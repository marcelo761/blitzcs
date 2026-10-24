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
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);

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

        // DllMain needs CRT/Win32 services the component lacks: it must fail loudly, naming the import.
        var failure = Assert.Throws<GuestInitializationException>(() =>
            GuestModule.Load(artifact, derived, resolver, new GuestModuleOptions { RunEntryPoint = true, AllowUnboundImports = true }));
        output.WriteLine("DllMain: " + failure.Message);
        Assert.Contains("unbound import", failure.Message);
    }
}
