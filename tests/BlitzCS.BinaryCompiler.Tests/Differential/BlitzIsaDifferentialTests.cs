using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.Testing;
using Iced.Intel;
using Xunit;
using Xunit.Abstractions;
using R = Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// ISA differential tests for segment-register reads (tools/x86_oracle/lib/segregs.c) and VEX/AVX2/BMI1 pass-through (lib/vexops.c).
/// Each library runs natively in the ELF32 oracle and through the translator; the full transcript (return values, per-vector data hashes,
/// final data) must be identical. Failure classes: TRANSLATION, ENVIRONMENT, UNSUPPORTED (see <see cref="OracleDifferential"/>).
/// Every library has a negative control that corrupts one oracle vector and must be detected.
/// </summary>
[Collection("GuestRuntime")]
public sealed class BlitzIsaDifferentialTests
{
    /// <summary>
    /// [Theory] that needs the x86 oracle (same skip message as <c>OracleTheory</c>) and an AVX2/BMI1 host CPU. Without them the case is SKIPPED with
    /// the reason; the oracle driver itself prints "AVX2 unavailable" in that case and never executes VEX code.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RequiresAvx2OracleTheoryAttribute : TheoryAttribute
    {
        public RequiresAvx2OracleTheoryAttribute()
        {
            Skip = OracleRunner.UnavailableReason is { } r ? "x86 oracle unavailable: " + r : RealLibraries.Avx2SkipReason();
        }
    }

    public static TheoryData<OptimizationProfile> Profiles()
    {
        var d = new TheoryData<OptimizationProfile>();
        foreach (var p in TranslatorHarness.Profiles) d.Add(p);
        return d;
    }

    /// <summary>Vectors whose result depends on fs or gs (or on both, as sg_or_all does).</summary>
    private static readonly string[] FsGsVectors = ["sg_gs16", "sg_fs16", "sg_gs32", "sg_fs32", "sg_or_all"];

    /// <summary>
    /// On Windows hosts the segregs comparison leaves out the fs/gs vectors: the PE32 oracle under Wine reports fs 0x63 and gs 0x6B, which are
    /// not validated against real Windows hardware (see the fs/gs tests below). The composed check there is sg_or_four (cs|ss|ds|es).
    /// Linux hosts compare all vectors against the ELF32 oracle.
    /// </summary>
    private static OracleTranscript ForComparison(OracleTranscript t) => !OperatingSystem.IsWindows() ? t : new OracleTranscript
    {
        DataLength = t.DataLength, Functions = t.Functions, InitialDataHash = t.InitialDataHash, FinalData = t.FinalData,
        Vectors = t.Vectors.Where(v => !FsGsVectors.Contains(v.Function)).ToList(),
    };

    [OracleTheory, MemberData(nameof(Profiles))]
    public void Segment_register_reads_match_the_x86_oracle(OptimizationProfile profile)
    {
        var o = OracleDifferential.Run("segregs", profile, oracleOverride: OperatingSystem.IsWindows() ? ForComparison(OracleRunner.Run("segregs")) : null);
        Assert.True(o.Passed, o.ToString());
        // Extra check on the oracle itself: a native 32-bit Linux process runs with cs = __USER32_CS = 0x23 and fs = gs = 0.
        if (OperatingSystem.IsWindows()) return;
        var oracle = OracleRunner.Run("segregs");
        Assert.Equal(0x23u, oracle.Vectors.Single(v => v.Function == "sg_cs16").Result);
        Assert.Equal(0x23u, oracle.Vectors.Single(v => v.Function == "sg_cs32").Result);
        Assert.Equal(0u, oracle.Vectors.Single(v => v.Function == "sg_gs16").Result);
        Assert.Equal(0u, oracle.Vectors.Single(v => v.Function == "sg_fs16").Result);
    }

    /// <summary>Windows hosts only: the guest of a windows-x64 artifact executes here (the loader refuses foreign artifacts elsewhere).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class WindowsHostFactAttribute : FactAttribute
    {
        public WindowsHostFactAttribute()
        {
            Skip = OperatingSystem.IsWindows() ? null : "windows-x64 constants are compared on Windows hosts (the loader refuses windows-x64 artifacts elsewhere)";
        }
    }

    private readonly ITestOutputHelper output;
    public BlitzIsaDifferentialTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// The translated windows-x64 guest's fs selector, checked against the documented WoW64 contract (0x53). The Wine oracle's value (fs 0x63 under
    /// Wine's own GDT) is written to the test output as information only: it is not validated against real Windows hardware and never fails this test.
    /// </summary>
    [WindowsHostFact]
    public void Windows_guest_fs_selector_equals_the_documented_wow64_contract()
    {
        var guest = TranslatorHarness.Run(a => { a.mov(R.eax, R.fs); a.ret(); });
        ReportOracleInformation("sg_fs16");
        Assert.True(guest == 0x53u, $"translated windows-x64 guest reads fs 0x{guest:x}; the documented WoW64 contract is 0x53");
    }

    /// <summary>The translated windows-x64 guest's gs selector against the documented contract (0x2B); the oracle value is information only (see the fs test).</summary>
    [WindowsHostFact]
    public void Windows_guest_gs_selector_equals_the_documented_wow64_contract()
    {
        var guest = TranslatorHarness.Run(a => { a.mov(R.eax, R.gs); a.ret(); });
        ReportOracleInformation("sg_gs16");
        Assert.True(guest == 0x2Bu, $"translated windows-x64 guest reads gs 0x{guest:x}; the documented WoW64 contract is 0x2B");
    }

    private void ReportOracleInformation(string function)
    {
        if (OracleRunner.UnavailableReason is not null) return;
        var v = OracleRunner.Run("segregs").Vectors.Single(x => x.Function == function).Result;
        output.WriteLine($"information only: the x86 oracle on this host reports {function} = 0x{v:x} (not validated against real Windows hardware)");
    }

    [OracleTheory, MemberData(nameof(Profiles))]
    public void Segment_register_negative_control_is_detected(OptimizationProfile profile)
    {
        AssertNegativeControlDetected("segregs", profile, OperatingSystem.IsWindows() ? ForComparison : null);
    }

    [RequiresAvx2OracleTheory, MemberData(nameof(Profiles))]
    public void Vex_and_bmi_operations_match_the_x86_oracle(OptimizationProfile profile)
    {
        var o = OracleDifferential.Run("vexops", profile);
        Assert.True(o.Passed, o.ToString());
    }

    [RequiresAvx2OracleTheory, MemberData(nameof(Profiles))]
    public void Vex_and_bmi_negative_control_is_detected(OptimizationProfile profile)
    {
        AssertNegativeControlDetected("vexops", profile);
    }

    /// <summary>
    /// Flips one result bit in the middle of the oracle transcript. The comparison MUST report a TRANSLATION divergence: at the corrupted vector when
    /// the guest is correct, or earlier when the guest already diverges (then the earlier divergence is reported, which is also a detection).
    /// </summary>
    private static void AssertNegativeControlDetected(string lib, OptimizationProfile profile, Func<OracleTranscript, OracleTranscript>? select = null)
    {
        var t = select is null ? OracleRunner.Run(lib) : select(OracleRunner.Run(lib));
        var victim = t.Vectors.Count / 2;
        var vs = t.Vectors.ToList();
        vs[victim] = vs[victim] with { Result = vs[victim].Result ^ 1 };
        var corrupted = new OracleTranscript
        {
            DataLength = t.DataLength, Functions = t.Functions, InitialDataHash = t.InitialDataHash, Vectors = vs, FinalData = t.FinalData,
        };
        var o = OracleDifferential.Run(lib, profile, oracleOverride: corrupted);
        Assert.True(o.Divergence is not null, $"negative control not detected for {lib}/{profile} (corrupted vector #{victim}): {o}");
        Assert.StartsWith(FailureClass.Translation, o.Divergence);
    }
}
