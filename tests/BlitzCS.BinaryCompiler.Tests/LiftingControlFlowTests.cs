using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Lifting;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class LiftingControlFlowTests
{
    private static ControlFlowGraph Cfg(byte[] code, Action<PeFixtureBuilder>? configure = null, byte[]? data = null, bool exportF = true)
    {
        var fx = new PeFixtureBuilder().WithCode(code);
        if (exportF) fx.Export("_f", 0);
        if (data is not null) fx.WithData(data);
        configure?.Invoke(fx);
        var image = PeReader.Read(fx.Build());
        return ControlFlowGraph.Build(image, image.MapImage());
    }

    private const uint T = PeFixtureBuilder.TextRva;

    [Fact]
    public void RecoversExportsDirectCallTargetsAndFallthroughLeaders()
    {
        var code = Asm(a =>
        {
            var g = a.CreateLabel(); var skip = a.CreateLabel();
            a.xor(eax, eax); a.call(g); a.test(eax, eax); a.jz(skip); a.inc(eax); a.Label(ref skip); a.ret();
            a.Label(ref g); a.mov(eax, 1); a.ret();
        });
        var cfg = Cfg(code);
        Assert.Contains(T, cfg.FunctionEntries);
        Assert.Contains(cfg.Instructions.Values, i => i.Instr.Mnemonic == Iced.Intel.Mnemonic.Inc);
        var callee = cfg.Instructions.Values.Single(i => i.Instr.Mnemonic == Iced.Intel.Mnemonic.Call).Instr.NearBranchTarget - PeFixtureBuilder.ImageBase;
        Assert.Contains((uint)callee, cfg.FunctionEntries);
        var afterCall = cfg.Instructions.Values.Single(i => i.Instr.Mnemonic == Iced.Intel.Mnemonic.Call).NextRva;
        Assert.Contains(afterCall, cfg.Leaders);
        Assert.True(cfg.Blocks.Count >= 4);
        Assert.All(cfg.Blocks, b => Assert.NotEmpty(b.Instructions));
        // blocks are contiguous and start at leaders or after terminators
        foreach (var b in cfg.Blocks)
            for (var k = 1; k < b.Instructions.Count; k++) { Assert.Equal(b.Instructions[k - 1].NextRva, b.Instructions[k].Rva); Assert.DoesNotContain(b.Instructions[k].Rva, cfg.Leaders); }
    }

    [Fact]
    public void DoesNotDecodeDataEmbeddedInExecutableSections()
    {
        var code = Asm(a => { a.mov(eax, 1); a.ret(); a.db(0xDE, 0xAD, 0xBE, 0xEF, 0x90, 0x90, 0x90, 0x90); });
        var cfg = Cfg(code);
        Assert.Equal(2, cfg.Instructions.Count);
        Assert.DoesNotContain(cfg.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
    }

    [Fact]
    public void DecodeFailureInReachableBytesIsAWarning()
    {
        var code = Asm(a => { a.nop(); a.db(0xFF, 0xFF); });
        var cfg = Cfg(code);
        var d = Assert.Single(cfg.Diagnostics, x => x.Code == "CFG_DECODE_FAILURE");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity); Assert.Equal(T + 1, d.GuestRva);
        Assert.Contains(T + 1, cfg.Undecodable.Keys);
        var art = Translate(code, OptimizationProfile.Release).Artifact;
        Assert.DoesNotContain(art.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void BranchesOutOfExecutableCodeAreWarnedNotInvented()
    {
        var code = Asm(a => { a.jmp(DataVa); });
        var art = Translate(code, OptimizationProfile.Release).Artifact;
        Assert.Contains(art.Diagnostics, d => d.Code == "BC_UNTRANSLATED_TARGET" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void RelocationTargetsInCodeBecomeFunctionPointerRoots()
    {
        // a pointer in .data (relocated) to a function that nothing calls directly
        var code = Asm(a => { a.ret(); a.nop(); a.nop(); a.mov(eax, 7); a.ret(); });
        var data = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), CodeVa + 3);
        var cfg = Cfg(code, f => f.Relocate(PeFixtureBuilder.DataRva + 0x20), data);
        Assert.Contains(T + 3, cfg.Instructions.Keys);
        Assert.Contains(T + 3, cfg.FunctionEntries);
        Assert.Contains(cfg.Diagnostics, d => d.Code == "CFG_RELOC_ROOTS" && d.Severity == DiagnosticSeverity.Info);
        // without the relocation the pointer is just data
        Assert.DoesNotContain(T + 3, Cfg(code, null, data).Instructions.Keys);
    }

    [Fact]
    public void JumpTableTargetsAreRootsAndTheScanIsBounded()
    {
        var code = Asm(a =>
        {
            a.mov(eax, __dword_ptr[esp + 4]); a.jmp(__dword_ptr[eax * 4 + (int)DataVa]); a.int3();
            a.mov(eax, 1); a.ret(); a.mov(eax, 2); a.ret();
        });
        var data = new byte[0x100];
        var movs = new List<uint>();
        for (var i = 0; i + 4 < code.Length; i++) if (code[i] == 0xB8) movs.Add((uint)i);
        Assert.Equal(2, movs.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), CodeVa + movs[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), CodeVa + movs[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0xDEADBEEF);           // not code: scan stops here
        var cfg = Cfg(code, f => f.Relocate(PeFixtureBuilder.DataRva).Relocate(PeFixtureBuilder.DataRva + 4).Relocate(PeFixtureBuilder.DataRva + 8), data);
        Assert.Contains(T + movs[0], cfg.Instructions.Keys); Assert.Contains(T + movs[1], cfg.Instructions.Keys);
        var jt = Assert.Single(cfg.Diagnostics, d => d.Code == "CFG_JUMP_TABLE");
        Assert.Contains("2 target", jt.Message);
        // an image without a relocation table cannot cross-check entries, so the bounded scan still applies
        Assert.Contains(Cfg(code, null, data).Diagnostics, d => d.Code == "CFG_JUMP_TABLE");
    }

    [Fact]
    public void ImageWithoutRootsHasNoInstructions()
    {
        var cfg = Cfg(Asm(a => a.ret()), exportF: false);
        Assert.Empty(cfg.Instructions);
    }
}
