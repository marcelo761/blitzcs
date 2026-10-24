using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>Proves the differential harness is not vacuous: corrupted fixtures / corrupted oracle transcripts MUST be detected.</summary>
[Collection("GuestRuntime")]
public sealed class HarnessSanityTests(ITestOutputHelper output)
{
    private static readonly OptimizationProfile[] Profiles = TranslatorHarness.Profiles;

    [OracleTheory]
    [InlineData(OptimizationProfile.Debug)]
    [InlineData(OptimizationProfile.Release)]
    public void Mutated_opcode_in_fixture_code_is_detected(OptimizationProfile profile)
    {
        // add32 = mov eax,[esp+8]; add eax,[esp+4]; ret : turn the add (03 /r) into a sub (2B /r).
        var art = OracleRunner.Build("arith");
        Assert.Equal(0x03, art.Code[4]);
        Assert.True(OracleDifferential.Run("arith", profile).Passed, "baseline must pass before mutating");
        var o = OracleDifferential.Run("arith", profile, new FixtureMutation(CodeOffset: 4, CodeXor: 0x28));
        output.WriteLine(o.ToString());
        Assert.NotNull(o.Divergence);
        Assert.Contains("add32", o.Divergence);
        Assert.StartsWith(FailureClass.Translation, o.Divergence);
    }

    [OracleTheory]
    [InlineData(OptimizationProfile.Debug)]
    [InlineData(OptimizationProfile.Release)]
    public void Mutated_initial_data_byte_is_detected(OptimizationProfile profile)
    {
        var o = OracleDifferential.Run("arith", profile, new FixtureMutation(DataOffset: 0, DataXor: 0x40));
        output.WriteLine(o.ToString());
        Assert.NotNull(o.Divergence);
        Assert.Contains("initial data", o.Divergence);
    }

    [OracleFact]
    public void Corrupted_oracle_return_value_is_detected_at_the_right_vector()
    {
        var t = OracleRunner.Run("intops1");
        const int victim = 1234;
        var vs = t.Vectors.ToList(); vs[victim] = vs[victim] with { Result = vs[victim].Result ^ 1 };
        var o = OracleDifferential.Run("intops1", OptimizationProfile.Release, oracleOverride: Clone(t, vectors: vs));
        Assert.NotNull(o.Divergence);
        Assert.Contains($"vector #{victim} ", o.Divergence);
    }

    [OracleFact]
    public void Corrupted_oracle_data_hash_final_data_and_callback_trace_are_detected()
    {
        var t = OracleRunner.Run("state");
        var vs = t.Vectors.ToList(); vs[77] = vs[77] with { DataHash = ~vs[77].DataHash };
        var o = OracleDifferential.Run("state", OptimizationProfile.Debug, oracleOverride: Clone(t, vectors: vs));
        Assert.Contains("data-section hash", o.Divergence);
        Assert.Contains("vector #77 ", o.Divergence);

        var fin = (byte[])t.FinalData.Clone(); fin[5] ^= 0xff;
        o = OracleDifferential.Run("state", OptimizationProfile.Debug, oracleOverride: Clone(t, finalData: fin));
        Assert.Contains("final data differs at offset 0x5", o.Divergence);

        var c = OracleRunner.Run("cb");
        var withTrace = c.Vectors.Select((v, i) => (v, i)).First(x => x.v.Trace.Length > 0);
        var cv = c.Vectors.ToList(); var tr = (string[])withTrace.v.Trace.Clone(); tr[0] += "0"; cv[withTrace.i] = withTrace.v with { Trace = tr };
        o = OracleDifferential.Run("cb", OptimizationProfile.Release, oracleOverride: Clone(c, vectors: cv));
        Assert.Contains("callback trace[0]", o.Divergence);
        Assert.Contains($"vector #{withTrace.v.Index} ", o.Divergence);
    }

    [OracleFact]
    public void Oracle_transcripts_are_substantial_and_libraries_carry_relocations_and_callbacks()
    {
        var expected = new Dictionary<string, int> { ["arith"] = 100, ["intops1"] = 5000, ["intops2"] = 20000, ["memops"] = 5000, ["state"] = 5000, ["conv"] = 4000, ["cb"] = 2000, ["fp"] = 3000, ["sse"] = 5000 };
        var total = 0;
        foreach (var (lib, min) in expected)
        {
            var t = OracleRunner.Run(lib);
            output.WriteLine($"{lib}: {t.Vectors.Count} vectors, {t.Functions.Count} functions, data {t.DataLength} bytes, relocs {OracleRunner.Build(lib).Relocs.Count}");
            Assert.True(t.Vectors.Count >= min, $"{lib}: only {t.Vectors.Count} vectors");
            Assert.All(t.Functions, f => Assert.Contains(t.Vectors, v => v.Function == f.Name)); // every function was exercised
            total += t.Vectors.Count;
        }
        output.WriteLine($"total vectors per profile: {total}");
        Assert.True(OracleRunner.Build("state").Relocs.Count >= 16, "function-pointer/jump tables must produce HIGHLOW relocations");
        Assert.Contains(OracleRunner.Run("cb").Vectors, v => v.Trace.Length >= 3);
        Assert.NotEmpty(OracleRunner.Build("fp").Relocs);
    }

    private static OracleTranscript Clone(OracleTranscript t, List<OracleVector>? vectors = null, byte[]? finalData = null) => new()
    {
        DataLength = t.DataLength, Functions = t.Functions, InitialDataHash = t.InitialDataHash, Vectors = vectors ?? t.Vectors.ToList(), FinalData = finalData ?? t.FinalData,
    };
}

/// <summary>Conventions the runtime cannot call must be diagnosed or refused, never silently mistranslated.</summary>
[Collection("GuestRuntime")]
public sealed class UnsupportedConventionTests(ITestOutputHelper output)
{
    [OracleTheory]
    [InlineData(OptimizationProfile.Debug)]
    [InlineData(OptimizationProfile.Release)]
    public void Fastcall_and_thiscall_exports_are_refused_not_mistranslated(OptimizationProfile profile)
    {
        var art = OracleRunner.Build("unsup");
        var image = PeReader.Read(OracleDifferential.BuildFixture(art));
        var tr = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(profile), "unsup");
        foreach (var d in tr.Diagnostics) output.WriteLine($"{d.Severity} {d.Code}: {d.Message}");
        foreach (var e in tr.Exports) output.WriteLine($"export {e.Name}: {e.Convention} argbytes={e.ArgBytes}");
        var hasError = tr.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        if (hasError)
        {
            Assert.Throws<GuestLoadException>(() => GuestModule.Load(tr, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
            return;
        }
        using var m = GuestModule.Load(tr, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        foreach (var name in new[] { "u_fast2", "u_fast3", "u_this1", "u_this0" })
        {
            var e = tr.Exports.Single(x => x.Name == name);
            // Whatever the translator concluded, the runtime must never let it be *called* as a plain stack-argument function by guessing.
            Assert.True(e.Convention is GuestCallingConvention.Fastcall or GuestCallingConvention.Thiscall or GuestCallingConvention.Unknown,
                $"{FailureClass.Translation}: {name} classified as {e.Convention}; register-argument conventions must not be classified as stack conventions");
            Assert.ThrowsAny<Exception>(() => m.Bind(name, GuestCallingConvention.Unknown, 2));
        }
    }

    [OracleTheory]
    [InlineData(OptimizationProfile.Debug)]
    [InlineData(OptimizationProfile.Release)]
    public void Bcd_adjust_instructions_are_diagnosed_as_unsupported(OptimizationProfile profile)
    {
        // GAP (documented): daa/das/aam have no 64-bit lowering. They must surface as a build Error, never as a wrong result.
        var art = OracleRunner.Build("bcd", null);
        var image = PeReader.Read(OracleDifferential.BuildFixture(art));
        var tr = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(profile), "bcd");
        var errors = tr.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        output.WriteLine($"{FailureClass.Unsupported}: " + string.Join("; ", errors.Select(e => e.Message)));
        Assert.Contains(errors, e => e.Code == "BC_UNSUPPORTED_INSTRUCTION" && e.Message.Contains("daa"));
        Assert.Contains(errors, e => e.Message.Contains("das"));
        Assert.Contains(errors, e => e.Message.Contains("aam"));
        Assert.Throws<GuestLoadException>(() => GuestModule.Load(tr, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
    }

    [Fact(Skip = "GAP: x87/SSE return values (st0/xmm0) and 64-bit EDX:EAX returns are not observable: GuestFunction.Call/EnterGuest return EAX only. Float math is covered through integer-returning functions plus data-section comparison (libs fp, sse).")]
    public void Floating_point_return_registers_are_not_observable() { }
}
