using System.Runtime.Intrinsics.X86;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class TranslatorSegmentVexTests
{
    /// <summary>The selector each segment register reads as, per target. windows-x64 is a WoW64 process; linux-sysv matches the ELF32 oracle (fs and gs are 0).</summary>
    public static TheoryData<string, string, uint> SelectorTable()
    {
        var d = new TheoryData<string, string, uint>();
        foreach (var (seg, win, lin) in new[] { ("cs", 0x23u, 0x23u), ("ss", 0x2Bu, 0x2Bu), ("ds", 0x2Bu, 0x2Bu), ("es", 0x2Bu, 0x2Bu), ("gs", 0x2Bu, 0u), ("fs", 0x53u, 0u) })
        {
            d.Add(BinaryTargets.WindowsX64, seg, win);
            d.Add(BinaryTargets.LinuxSysV, seg, lin);
        }
        return d;
    }

    /// <summary>The rows of <see cref="SelectorTable"/> for the target this process runs on (the only target whose artifacts can be loaded and executed here).</summary>
    public static TheoryData<string, uint> HostSelectorTable()
    {
        var d = new TheoryData<string, uint>();
        foreach (var (target, seg, expected) in SelectorTable().Select(r => ((string)r[0], (string)r[1], (uint)r[2])))
            if (target == BinaryTargets.Host) d.Add(seg, expected);
        return d;
    }

    /// <summary>What a translated <c>mov eax, sreg</c> stores on <paramref name="target"/>, read from the host code (works for either target on any host).</summary>
    private static uint ReadSelectorAs(string target, string seg, OptimizationProfile profile)
    {
        var sreg = seg switch { "cs" => cs, "ss" => ss, "ds" => ds, "es" => es, "gs" => gs, _ => fs };
        // windows-x64 refuses a fixed base below 0x20000000; the linux-sysv fixture keeps its usual low base.
        var fx = new PeFixtureBuilder().WithImageBase(target == BinaryTargets.WindowsX64 ? 0x60000000u : PeFixtureBuilder.LowBase).WithCode(Asm(a => { a.mov(eax, 0xDEADBEEFu); a.mov(eax, sreg); a.ret(); })).Export("_f", 0);
        var art = X64Translator.Translate(PeReader.Read(fx.Build()), new BinaryCompilerOptions(profile, TargetOs: target), "k");
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // The guest instruction before the read is a canary: the host instruction that loads the selector is the next `mov eax, imm32`.
        var imms = DecodeHost(art).Where(i => i.Code == Code.Mov_r32_imm32 && i.Op0Register == Register.EAX).Select(i => i.Immediate32).ToList();
        var k = imms.IndexOf(0xDEADBEEFu);
        Assert.True(k >= 0 && k + 1 < imms.Count, $"no selector load after the canary for {target}/{seg}");
        return imms[k + 1];
    }

    [Theory]
    [MemberData(nameof(SelectorTable))]
    public void SegmentRegisterReadsGiveTheTargetSelectors(string target, string seg, uint expected)
    {
        foreach (var profile in Profiles)
            Assert.Equal(expected, ReadSelectorAs(target, seg, profile));
    }

    [Theory]
    [MemberData(nameof(HostSelectorTable))]
    public void SegmentRegisterReadsGiveFlatSelectors(string name, uint expected)
    {
        var sreg = name switch { "cs" => cs, "ss" => ss, "ds" => ds, "es" => es, "gs" => gs, _ => fs };
        // 32-bit destination zero-extends, 16-bit keeps the upper half, memory destination writes 16 bits, push stores a dword.
        var r32 = Run(a => { a.mov(eax, 0xFFFFFFFFu); a.mov(eax, sreg); a.ret(); });
        Assert.Equal(expected, r32);
        var r16 = Run(a => { a.mov(eax, 0x12340000u); a.mov(ax, sreg); a.ret(); });
        Assert.Equal(0x12340000u | expected, r16);
        var mem = Run(a => { a.sub(esp, 8); a.mov(__dword_ptr[esp], 0xAABBCCDDu); a.mov(__word_ptr[esp], sreg); a.mov(eax, __dword_ptr[esp]); a.add(esp, 8); a.ret(); });
        Assert.Equal(0xAABB0000u | expected, mem);
        var push = Run(a => { a.push(sreg); a.pop(eax); a.ret(); });
        Assert.Equal(expected, push);
    }

    [Fact]
    public void SegmentReadsKeepFlags()
    {
        var r = Run(a => { a.xor(eax, eax); a.stc(); a.mov(ecx, ds); a.setc(al); a.ret(); });
        Assert.Equal(1u, r);
    }

    [Fact]
    public void PopAndMovOfTheCurrentFlatSelectorAreAcceptedAndKeepFlags()
    {
        var r = Run(a => { a.xor(eax, eax); a.stc(); a.push(ds); a.pop(es); a.mov(cx, 0x2B); a.mov(ds, cx); a.mov(es, ecx); a.setc(al); a.ret(); });
        Assert.Equal(1u, r);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void LoadingAnotherSelectorRaisesANamedFault(int form)
    {
        Action<Assembler> body = form switch
        {
            0 => a => { a.mov(ax, 0x1B); a.mov(ds, ax); a.ret(); },
            1 => a => { a.push(0x1B); a.pop(es); a.ret(); },
            2 => a => { a.mov(ax, 0x2B); a.mov(ss, ax); a.ret(); },
            3 => a => { a.mov(ax, 0x53); a.mov(fs, ax); a.ret(); },
            _ => a => { a.les(eax, __dword_ptr[ecx]); a.ret(); },
        };
        var code = Asm(body);
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile);
            Assert.DoesNotContain(b.Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Contains(b.Artifact.Diagnostics, d => d.Code == "BC_DEFERRED_SEGMENT_FAULT");
            using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            var ex = Assert.Throws<GuestTrapException>(() => m.Bind("_f", GuestCallingConvention.Cdecl, 0).Call());
            Assert.Contains(form switch { 0 => "mov ds", 1 => "pop es", 2 => "mov ss", 3 => "mov fs", _ => "les" }, ex.Message);
        }
    }

    [Fact]
    public void FastFailRaisesADedicatedExceptionWithTheCode()
    {
        var code = Asm(a => { a.mov(ecx, 2); a.@int(0x29); a.ret(); });
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile);
            Assert.DoesNotContain(b.Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            var ex = Assert.Throws<GuestFastFailException>(() => m.Bind("_f", GuestCallingConvention.Cdecl, 0).Call());
            Assert.Equal(2u, ex.Code);
            Assert.Contains("FAST_FAIL_STACK_COOKIE_CHECK_FAILURE", ex.Message);
            Assert.Equal(CodeVa + 5, ex.Address);
        }
        Assert.Equal("FAST_FAIL_99", GuestFastFailException.CodeName(99));
    }

    [Fact]
    public void OtherInterruptsStayUnsupported()
    {
        var art = Translate(Asm(a => { a.@int(0x2E); a.ret(); }), OptimizationProfile.Release).Artifact;
        Assert.Contains(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "BC_UNSUPPORTED_INSTRUCTION");
    }

    [Fact]
    public void ImagesWithoutTheseInstructionsEmitNoLateTraps()
    {
        // Late trap routines exist only on demand: a plain function must not contain a write of trap kind 4 or 5.
        var art = Translate(Asm(a => { a.mov(eax, 1); a.ret(); }), OptimizationProfile.Release).Artifact;
        Assert.DoesNotContain(DecodeHost(art), i => i.Code == Code.Mov_rm32_imm32 && i.Immediate32 is 4 or 5 && i.MemoryBase == Register.R14 && i.MemoryDisplacement64 == 16);
    }

    private static List<Instruction> DecodeHost(TranslationArtifact art)
    {
        var decoder = Decoder.Create(64, art.HostImage.AsMemory(0, art.CodeSize).ToArray());
        var list = new List<Instruction>();
        while (decoder.IP < (ulong)art.CodeSize) { decoder.Decode(out var ins); if (ins.IsInvalid) break; list.Add(ins); }
        return list;
    }

    [Fact]
    public void VexIsPassedThroughAndEvexIsRejected()
    {
        var code = Asm(a => { a.vaddps(ymm1, ymm2, ymm3); a.vzeroupper(); a.vzeroall(); a.ret(); });
        foreach (var profile in Profiles)
            Assert.DoesNotContain(Translate(code, profile).Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var art = Translate(Asm(a => { a.vaddps(zmm1, zmm2, zmm3); a.ret(); }), OptimizationProfile.Release).Artifact;
        Assert.Contains(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "BC_UNSUPPORTED_INSTRUCTION");
    }

    [Fact]
    public void VexExecutesWhenTheHostHasAvx2()
    {
        if (!Avx2.IsSupported) return; // skipped: this host CPU has no AVX2, so VEX execution cannot be checked here
        var data = new byte[64];
        for (var k = 0; k < 8; k++) BitConverter.GetBytes(k + 1).CopyTo(data, k * 4);
        var r = Run(a =>
        {
            a.vmovdqu(ymm0, __ymmword_ptr[DataVa]);
            a.vpaddd(ymm1, ymm0, ymm0);
            a.vextracti128(xmm2, ymm1, 1);
            a.vpextrd(eax, xmm2, 3);   // element 7 doubled = 16
            a.vzeroupper();
            a.ret();
        }, data: data);
        Assert.Equal(16u, r);
        Assert.Equal(0xFFu, Run(a => { a.mov(ecx, 0xF0F0u); a.mov(edx, 0xFFu); a.andn(eax, ecx, edx); a.ret(); }) & 0x0Fu | 0xF0u);
    }

    [Fact]
    public void AddressTableAfterANoReturnCallIsDataButRealCodeAfterACallIsNot()
    {
        // call target; <table of 0x00401xxx dwords that decodes as arpl/bound/...>   versus   call target; real code
        var tableCode = Asm(a => { a.call(CodeVa + 0x100); a.db(0x5C, 0x10, 0x40, 0x00, 0x62, 0x10, 0x40, 0x00, 0x63, 0x10, 0x40, 0x00, 0x7E, 0x10, 0x40, 0x00); });
        var art = Translate(tableCode, OptimizationProfile.Release).Artifact;
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(art.Diagnostics, d => d.Code == "CFG_DATA_AFTER_CALL");

        var realCode = Asm(a => { a.call(CodeVa + 0x100); a.mov(eax, 7); a.add(eax, ecx); a.push(eax); a.pop(edx); a.ret(); });
        var real = Translate(realCode, OptimizationProfile.Release).Artifact;
        Assert.DoesNotContain(real.Diagnostics, d => d.Code == "CFG_DATA_AFTER_CALL");
    }
}
