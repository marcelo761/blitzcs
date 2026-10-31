using BlitzCS.BinaryCompiler.Lifting;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>With a relocation table, a fixup can only lie in a displacement or immediate field, so the code/data decision does not depend on the load base.</summary>
public sealed class CfgRelocationRuleTests
{
    private const uint Low = PeFixtureBuilder.LowBase;
    private static readonly uint[] Bases = [0x10000000, 0x30000000, 0x50000000];
    private const string RelocReason = "relocated pointer over opcode bytes (data)";

    private static ControlFlowGraph Cfg(PeImage image, uint b)
    {
        var moved = image.Rebase(b);
        return ControlFlowGraph.Build(moved, moved.MapImage());
    }

    private static uint[] Decisions(PeImage image, uint b) => [.. Cfg(image, b).Instructions.Keys];

    private static void AssertBaseIndependent(PeImage image)
    {
        var first = Decisions(image, Bases[0]);
        foreach (var b in Bases.Skip(1)) Assert.Equal(first, Decisions(image, b));
    }

    /// <summary>f: call helper ; [relocated table of 4 code addresses] ; helper: ud2 ; t1..t4: mov eax,n ; ret</summary>
    private static (PeImage Image, uint Table, uint[] Targets) TableAfterNonReturningCall(Func<uint, bool>? relocateEntry = null)
    {
        var a = new Asm32(Low);
        a.Raw(0xE8).U32(16);                                  // call helper (at offset 21)
        var table = a.Here;
        var tableAt = a.Here;
        for (var i = 0; i < 4; i++) a.U32(0);                 // patched below
        var helper = a.Here; a.Raw(0x0F, 0x0B);               // ud2: never returns
        var targets = new List<uint>();
        for (var i = 0; i < 4; i++) { targets.Add(a.Here); a.MovEaxImm((uint)(i + 1)).Ret(); }
        var code = a.Bytes;
        for (var i = 0; i < 4; i++) BitConverter.GetBytes(Low + PeFixtureBuilder.TextRva + targets[i]).CopyTo(code, tableAt + i * 4);
        var fx = new PeFixtureBuilder().WithImageBase(Low).WithCode(code).Export("f", 0);
        for (var i = 0u; i < 4; i++) if (relocateEntry?.Invoke(i) ?? true) fx.Relocate(PeFixtureBuilder.TextRva + table + i * 4);
        Assert.Equal(21u, helper);
        return (PeReader.Read(fx.Build()), PeFixtureBuilder.TextRva + table, [.. targets.Select(t => PeFixtureBuilder.TextRva + t)]);
    }

    [Fact]
    public void RelocatedAddressTableAfterANonReturningCallStaysDataAtEveryBase()
    {
        var (image, table, targets) = TableAfterNonReturningCall();
        foreach (var b in Bases)
        {
            var cfg = Cfg(image, b);
            for (var o = 0u; o < 16; o++) Assert.DoesNotContain(cfg.Instructions.Keys, k => k <= table + o && table + o < k + (uint)cfg.Instructions[k].Length);
            foreach (var t in targets) Assert.True(cfg.Instructions.ContainsKey(t), $"target 0x{t:X} translated at base 0x{b:X}");
            Assert.DoesNotContain(cfg.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }
        AssertBaseIndependent(image);
    }

    [Fact]
    public void OnlyTheRelocatedEntryIsKeptOutOfCode_MutationCheck()
    {
        // A single fixup on the first entry is enough to keep that dword from being decoded at every base (the rule, not the older heuristics, decides).
        var (image, table, _) = TableAfterNonReturningCall(i => i == 0);
        foreach (var b in Bases)
        {
            var cfg = Cfg(image, b);
            Assert.DoesNotContain(cfg.Instructions.Keys, k => k <= table && table < k + (uint)cfg.Instructions[k].Length);
        }
        AssertBaseIndependent(image);
    }

    [Fact]
    public void RelocatedImmediatesAndAbsoluteOperandsInRealCodeAreFullyTranslated()
    {
        var a = new Asm32(Low);
        a.Raw(0xB8).Abs(PeFixtureBuilder.TextRva + 0x30);      // mov eax, offset g
        a.Raw(0xFF, 0xD0);                                       // call eax
        a.Raw(0x68).Abs(PeFixtureBuilder.DataRva);               // push offset data
        a.Raw(0x58);                                             // pop eax
        a.CallIat(PeFixtureBuilder.DataRva + 4);                 // call dword ptr [abs]
        a.Raw(0x81, 0xF8).Abs(PeFixtureBuilder.DataRva);         // cmp eax, offset data
        a.Raw(0xC7, 0x05).Abs(PeFixtureBuilder.DataRva + 8).Abs(PeFixtureBuilder.TextRva + 0x30);   // mov [abs], imm32-abs (two fixups)
        a.Ret();
        while (a.Here < 0x30) a.Raw(0xCC);
        var g = a.Here; a.MovEaxImm(7).Ret();
        var fx = new PeFixtureBuilder().WithImageBase(Low).WithCode(a.Bytes).WithData(new byte[0x40]).Export("f", 0);
        foreach (var r in a.Relocs) fx.Relocate(r);
        var image = PeReader.Read(fx.Build());
        foreach (var b in Bases)
        {
            var cfg = Cfg(image, b);
            Assert.True(cfg.Instructions.ContainsKey(PeFixtureBuilder.TextRva + g), $"g translated at 0x{b:X}");
            Assert.DoesNotContain(cfg.Undecodable.Values, v => v == RelocReason);
            Assert.Equal(8, cfg.Instructions.Count(i => i.Key < PeFixtureBuilder.TextRva + 0x30));
        }
        AssertBaseIndependent(image);
    }

    [Fact]
    public void RelocatedJumpTableDisplacementInsideAFunctionStillResolvesAtEveryBase()
    {
        var a = new Asm32(Low);
        a.Raw(0x8B, 0x44, 0x24, 0x04, 0x83, 0xE0, 0x01);        // mov eax,[esp+4] ; and eax,1
        a.Raw(0xFF, 0x24, 0x85).Abs(PeFixtureBuilder.DataRva);   // jmp [table+eax*4]
        var j0 = a.Here; a.MovEaxImm(1).Ret();
        var j1 = a.Here; a.MovEaxImm(2).Ret();
        var data = new byte[0x20];
        BitConverter.GetBytes(Low + PeFixtureBuilder.TextRva + j0).CopyTo(data, 0);
        BitConverter.GetBytes(Low + PeFixtureBuilder.TextRva + j1).CopyTo(data, 4);
        var fx = new PeFixtureBuilder().WithImageBase(Low).WithCode(a.Bytes).WithData(data).Export("f", 0);
        foreach (var r in a.Relocs) fx.Relocate(r);
        fx.Relocate(PeFixtureBuilder.DataRva).Relocate(PeFixtureBuilder.DataRva + 4);
        var image = PeReader.Read(fx.Build());
        foreach (var b in Bases)
        {
            var cfg = Cfg(image, b);
            Assert.Contains(PeFixtureBuilder.TextRva + j0, cfg.Instructions.Keys);
            Assert.Contains(PeFixtureBuilder.TextRva + j1, cfg.Instructions.Keys);
            Assert.Empty(cfg.Undecodable);
        }
        AssertBaseIndependent(image);
    }

    private static CfgOutcome Probe(uint relocOffset)
    {
        // f: mov eax, imm32 (B8 + 4 bytes at offset 1) ; ret.   A fixup at relocOffset is only legal at offset 1.
        var code = new byte[] { 0xB8, 0x00, 0x00, 0x00, 0x10, 0xC3 };
        var fx = new PeFixtureBuilder().WithImageBase(Low).WithCode(code).Export("f", 0).Relocate(PeFixtureBuilder.TextRva + relocOffset);
        var image = PeReader.Read(fx.Build());
        var cfg = ControlFlowGraph.Build(image, image.MapImage());
        return new CfgOutcome(cfg.Instructions.ContainsKey(PeFixtureBuilder.TextRva), cfg.Undecodable.ContainsValue(RelocReason));
    }

    private sealed record CfgOutcome(bool Translated, bool Rejected);

    [Theory]
    [InlineData(1u, true)]    // exactly the immediate field
    [InlineData(0u, false)]   // over the opcode byte
    [InlineData(2u, false)]   // straddles the end of the immediate
    [InlineData(4u, false)]
    public void TheFixupMustLieWhollyInsideAnOperandField(uint offset, bool legal)
    {
        var o = Probe(offset);
        Assert.Equal(legal, o.Translated);
        Assert.Equal(!legal, o.Rejected);
    }
}
