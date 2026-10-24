using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// Differential tests: every vector is executed natively on x86 by an ELF32 oracle (same machine code, same absolute
/// addresses) and on the translated guest; return values, data-section bytes and callback traces must be identical.
/// Failure classes: TRANSLATION (diverges), ENVIRONMENT (oracle could not run), UNSUPPORTED (translator Error diagnostic).
/// </summary>
[Collection("GuestRuntime")]
public sealed class OracleDifferentialTests
{
    public static TheoryData<string, OptimizationProfile> Cases()
    {
        var d = new TheoryData<string, OptimizationProfile>();
        foreach (var lib in new[] { "arith", "intops1", "intops2", "memops", "state", "conv", "cb", "fp", "sse", "exotic" })
            foreach (var p in TranslatorHarness.Profiles) d.Add(lib, p);
        return d;
    }

    [OracleTheory, MemberData(nameof(Cases))]
    public void Translated_guest_matches_x86_oracle(string lib, OptimizationProfile profile)
    {
        var outcome = OracleDifferential.Run(lib, profile);
        Assert.True(outcome.Passed, outcome.ToString());
    }
}
