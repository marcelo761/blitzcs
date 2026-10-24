using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// The same machine code translated at its preferred base and at other bases must behave identically on every vector, and both must equal the
/// native ELF32 oracle (which runs at 0x10000000). Absolute pointers in the data section (function-pointer tables, jump tables) are normalised
/// back by the base delta at each relocation site before comparison, so a wrongly rebased table is a divergence.
/// </summary>
[Collection("GuestRuntime")]
public sealed class RebaseDifferentialTests
{
    public static TheoryData<string, OptimizationProfile, uint> Cases()
    {
        var d = new TheoryData<string, OptimizationProfile, uint>();
        foreach (var lib in new[] { "state", "arith", "conv", "cb", "memops", "fp" })
            foreach (var p in TranslatorHarness.Profiles)
                foreach (var b in new[] { 0x20000000u, 0x7A5B0000u })
                    d.Add(lib, p, b);
        return d;
    }

    [OracleTheory, MemberData(nameof(Cases))]
    public void Rebased_guest_matches_the_x86_oracle_and_the_preferred_base(string lib, OptimizationProfile profile, uint loadBase)
    {
        // relocation-free libraries cannot move: that is covered by a separate explicit-error test
        var art = OracleRunner.Build(lib);
        if (art.Relocs.Count == 0)
        {
            var image = PeReader.Read(OracleDifferential.BuildFixture(art));
            Assert.Equal("PE_REBASE_NO_RELOCS", Assert.Throws<PeRebaseException>(() => image.Rebase(loadBase)).Code);
            return;
        }
        var atPreferred = OracleDifferential.Run(lib, profile);
        var atBase = OracleDifferential.Run(lib, profile, loadBase: loadBase);
        Assert.True(atPreferred.Passed, atPreferred.ToString());
        Assert.True(atBase.Passed, $"rebased to 0x{loadBase:X8}: {atBase}");
        Assert.Equal(atPreferred.Vectors, atBase.Vectors);
    }

    [OracleFact]
    public void A_wrongly_rebased_table_is_detected()
    {
        // Mutation check: stop adding the delta to the data-section relocations of 'state' (its function-pointer/jump tables) -> must diverge.
        var art = OracleRunner.Build("state");
        Assert.Contains(art.Relocs, r => r >= PeFixtureBuilder.DataRva && r < PeFixtureBuilder.DataRva + 0x1000);
        var broken = art with { Relocs = [.. art.Relocs.Where(r => r < PeFixtureBuilder.DataRva)] };   // table entries no longer relocated
        var fixture = new PeFixtureBuilder().WithCode(broken.Code).WithData(broken.Data);
        foreach (var (n, o) in broken.Exports) fixture.Export(n, o);
        foreach (var r in broken.Relocs) fixture.Relocate(r);
        var image = PeReader.Read(fixture.Build()).Rebase(0x20000000);
        var tr = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(), "mut");
        using var m = BlitzCS.BinaryCompiler.Runtime.GuestModule.Load(tr, image, new BlitzCS.BinaryCompiler.Runtime.DictionaryGuestImportResolver(), new BlitzCS.BinaryCompiler.Runtime.GuestModuleOptions { RunEntryPoint = false });
        var oracle = OracleRunner.Run("state");
        var diverged = false;
        foreach (var v in oracle.Vectors)
        {
            var f = oracle.Functions.First(x => x.Name == v.Function);
            try { if (m.Bind(f.Name, f.Convention, f.ArgCount).Call(v.Args) != v.Result) { diverged = true; break; } }
            catch (BlitzCS.BinaryCompiler.Runtime.GuestException) { diverged = true; break; }
        }
        Assert.True(diverged, "a table that was not relocated must change behaviour");
    }
}
