using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Regression tests for the Release-only call/return fast paths: they may only change speed, never behavior.</summary>
[Collection("GuestRuntime")]
public sealed class DispatchOptimizationTests
{
    private static int CountMnemonic(TranslationArtifact art, Mnemonic m)
    {
        var d = Decoder.Create(64, new ByteArrayCodeReader(art.HostImage, 0, art.CodeSize), 0);
        var n = 0;
        while (d.IP < (ulong)art.CodeSize) { var i = d.Decode(); if (i.IsInvalid) { if (d.IP >= (ulong)art.CodeSize) break; continue; } if (i.Mnemonic == m) n++; }
        return n;
    }

    [Fact]
    public void ReturnToAnotherSiteThanTheStaticPredictionStillWorks()
    {
        // g rewrites its own return address so it skips "mov eax,1; ret" (6 bytes) at the predicted site and lands on the next leader.
        Assert.Equal(2u, Run(a =>
        {
            var g = a.CreateLabel(); var other = a.CreateLabel();
            a.xor(ebx, ebx); a.jnz(other);                 // never taken; makes the second site a known leader
            a.call(g); a.mov(eax, 1); a.ret();
            a.Label(ref other); a.mov(eax, 2); a.ret();
            a.Label(ref g); a.pop(ecx); a.add(ecx, 6); a.push(ecx); a.ret();
        }));
    }

    [Theory]
    [InlineData(0x12345678u, false)]        // outside the image
    [InlineData(0u, true)]                  // DataVa: inside the image but not translated code
    [InlineData(0x7FFCu, true)]             // DataVa + 0x7FFC: inside the SizeOfImage window, untranslated
    public void ReturnToUntranslatedAddressTrapsInsteadOfExecuting(uint value, bool relativeToData)
    {
        var target = relativeToData ? DataVa + value : value;
        // A trap abandons the call and raises; falling through to the translated "mov eax,7" would yield 7.
        var code = Asm(a => { var g = a.CreateLabel(); a.call(g); a.mov(eax, 7); a.ret(); a.Label(ref g); a.pop(ecx); a.push(target); a.ret(); });
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestTrapException>(() => Run(code));
    }

    [Fact]
    public void IndirectCallAndTableJumpOutOfRangeTrap()
    {
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestTrapException>(() => Run(a => { a.mov(eax, 0x7FFFFFF0); a.call(eax); a.mov(eax, 5); a.ret(); }));
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestTrapException>(() => Run(a => { a.mov(eax, 0xFFFFFFF0); a.jmp(eax); }));
    }

    [Fact]
    public void FlagPreservingDispatchIsKeptWhenAReturnSiteConsumesFlags()
    {
        var consumes = Translate(Asm(a => { var g = a.CreateLabel(); a.stc(); a.call(g); a.sbb(eax, eax); a.ret(); a.Label(ref g); a.ret(); }), OptimizationProfile.Release);
        var ignores = Translate(Asm(a => { var g = a.CreateLabel(); a.call(g); a.add(esp, 0); a.mov(eax, 1); a.ret(); a.Label(ref g); a.ret(); }), OptimizationProfile.Release);
        // shared Dispatch routine + one inline SSE clamp per ret/indirect site when flags must survive; only the shared routine otherwise
        Assert.True(CountMnemonic(consumes.Artifact, Mnemonic.Pminud) >= 3, "flag-consuming return site must keep the exact flag-preserving clamp");
        Assert.Equal(1, CountMnemonic(ignores.Artifact, Mnemonic.Pminud));
    }


    /// <summary>Switch whose jump table lives in .text (read-only) so the translator may embed a host-offset shadow of it.</summary>
    private static (byte[] Code, uint[] CaseVas, uint TableVa) ReadOnlySwitch(bool boundsCheck, bool useCall)
    {
        uint[] cases = [0, 0, 0, 0]; var tab = CodeVa + 0x100;
        byte[] code = [];
        for (var pass = 0; pass < 2; pass++)
        {
            var labels = new Label[4]; Label t = default;
            var curCases = cases; var curTab = tab;
            code = Asm(a =>
            {
                var def = a.CreateLabel(); t = a.CreateLabel();
                for (var k = 0; k < 4; k++) labels[k] = a.CreateLabel();
                a.mov(eax, __dword_ptr[esp + 4]);
                if (boundsCheck) { a.cmp(eax, 3); a.ja(def); }
                if (useCall) { a.call(__dword_ptr[eax * 4 + (int)curTab]); a.add(eax, 5); a.ret(); }
                else a.jmp(__dword_ptr[eax * 4 + (int)curTab]);
                for (var k = 0; k < 4; k++) { a.Label(ref labels[k]); a.mov(eax, 100 + k); a.ret(); }
                a.Label(ref def); a.mov(eax, 0xFFFFFFFF); a.ret();
                a.Label(ref t);
                for (var k = 0; k < 4; k++) a.dd(curCases[k]);
            }, out var res);
            cases = labels.Select(l => (uint)res.GetLabelRIP(l)).ToArray();
            tab = (uint)res.GetLabelRIP(t);
        }
        return (code, cases, tab);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ReadOnlyJumpTableUsesEmbeddedHostOffsetsAndKeepsGenericFallback(bool boundsCheck, bool useCall)
    {
        var (code, cases, tab) = ReadOnlySwitch(boundsCheck, useCall);
        var bias = useCall ? 5u : 0u;
        void Cfg(PeFixtureBuilder f) { for (var k = 0u; k < 4; k++) f.Relocate(tab - PeFixtureBuilder.ImageBase + 4 * k); }
        for (uint k = 0; k < 4; k++) Assert.Equal(100 + bias + k, Run(code, [k], Cfg));
        if (boundsCheck) Assert.Equal(0xFFFFFFFFu, Run(code, [9], Cfg));
        else Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestTrapException>(() => Run(code, [9], Cfg));      // without the guest's own check the zero VA after the table must trap, not run
        Assert.Equal(boundsCheck ? 0xFFFFFFFFu : 101u + bias, Run(code, [0x40000001], Cfg));   // 32-bit address wrap-around reads table[1]: only the generic path may handle it
        var built = Translate(code, OptimizationProfile.Release, Cfg);
        if (!useCall) Assert.Contains(built.Artifact.Diagnostics, d => d.Code == "CFG_JUMP_TABLE");
        var hostOffsets = cases.Select(c => built.Artifact.AddressMap.Single(m => m.GuestRva == c - PeFixtureBuilder.ImageBase).HostOffset).ToArray();
        var expected = hostOffsets.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.True(built.Artifact.HostImage.AsSpan(0, built.Artifact.CodeSize).IndexOf(expected) >= 0, "host-offset table for the read-only jump table was not embedded");
    }

    [Fact]
    public void AlignedLoopHeadsStayAlignedInRelease()
    {
        var code = Asm(a =>
        {
            var loop = a.CreateLabel();
            a.xor(eax, eax); a.mov(ecx, __dword_ptr[esp + 4]);   // 2 + 4 bytes
            for (var k = 0; k < 10; k++) a.nop();                  // loop head at offset 16

            a.Label(ref loop); a.add(eax, ecx); a.dec(ecx); a.jnz(loop); a.ret();
        });
        var b = Translate(code, OptimizationProfile.Release);
        var loopRva = PeFixtureBuilder.TextRva + 16u;
        var entry = b.Artifact.AddressMap.Single(m => m.GuestRva == loopRva);
        Assert.Equal(0u, entry.HostOffset % 16);
        Assert.Equal(15u, Run(code, [5]));
    }
}
