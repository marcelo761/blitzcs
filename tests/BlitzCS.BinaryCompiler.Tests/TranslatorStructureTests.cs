using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class TranslatorStructureTests
{
    private static List<(Instruction Ins, int Offset, int Length)> DecodeHost(TranslationArtifact art)
    {
        var decoder = Decoder.Create(64, art.HostImage.AsMemory(0, art.CodeSize).ToArray());
        var list = new List<(Instruction, int, int)>();
        while (decoder.IP < (ulong)art.CodeSize)
        {
            var off = (int)decoder.IP;
            decoder.Decode(out var ins);
            if (ins.IsInvalid) break;
            list.Add((ins, off, ins.Length));
        }
        return list;
    }

    private static uint TableEntry(TranslationArtifact art, uint rva) =>
        BinaryPrimitives.ReadUInt32LittleEndian(art.HostImage.AsSpan((int)art.DispatchTableOffset + 4 * (int)rva));

    private static readonly byte[] SimpleCode = Asm(a =>
    {
        var g = a.CreateLabel();
        a.mov(eax, __dword_ptr[DataVa]); a.call(g); a.ret();
        a.Label(ref g); a.inc(eax); a.ret();
    });

    [Theory]
    [InlineData(OptimizationProfile.Debug)]
    [InlineData(OptimizationProfile.Release)]
    public void ArtifactSatisfiesTheContract(OptimizationProfile profile)
    {
        var b = Translate(SimpleCode, profile, f => f.Import("k.dll", "a").ToString());
        var art = b.Artifact; var img = b.Image;
        Assert.Equal(0, art.CodeSize % 4096);
        Assert.True(art.HostImage.Length > art.CodeSize);
        Assert.Equal(img.Sha256, art.SourceSha256); Assert.Equal("test-key", art.CacheKey);
        Assert.Equal(img.ImageBase, art.GuestImageBase); Assert.Equal(img.SizeOfImage, art.GuestImageSize);
        Assert.True(art.ExitGuestRva >= img.SizeOfImage); Assert.True(art.ImportStubBaseRva >= img.SizeOfImage);
        Assert.True(art.ImportStubBaseRva > art.ExitGuestRva);
        Assert.True(art.ContextOffset >= art.CodeSize && art.ContextOffset + 64 <= art.HostImage.Length);
        Assert.True(art.ImportThunkTableOffset >= art.ContextOffset + 64);
        Assert.True(art.DispatchTableOffset >= art.ImportThunkTableOffset + 8 * (uint)art.Imports.Count);
        Assert.True(art.EnterGuestOffset < art.CodeSize && art.EnterGuestOffset != 0);
        Assert.Single(art.Imports);
        Assert.NotEqual(0u, TableEntry(art, art.ExitGuestRva));
        Assert.NotEqual(0u, TableEntry(art, art.ImportStubBaseRva));
        Assert.Equal(0u, TableEntry(art, art.ImportStubBaseRva + 16));                  // beyond the imports: trap
        Assert.Equal(0u, TableEntry(art, PeFixtureBuilder.TextRva + 0x7FF));            // not code: trap
        var export = Assert.Single(art.Exports);
        Assert.Equal("_f", export.Name); Assert.Equal(PeFixtureBuilder.TextRva, export.GuestRva);
        Assert.Equal(GuestCallingConvention.Cdecl, export.Convention);
        Assert.Equal(export.HostOffset, TableEntry(art, export.GuestRva));
        Assert.Contains(art.AddressMap, e => e.GuestRva == export.GuestRva && e.HostOffset == export.HostOffset);
        Assert.All(art.AddressMap, e => Assert.Equal(e.HostOffset, TableEntry(art, e.GuestRva)));
        Assert.Equal(art.AddressMap.OrderBy(e => e.GuestRva).Select(e => e.GuestRva), art.AddressMap.Select(e => e.GuestRva));
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // data area starts zeroed except for the clamp vector and table
        Assert.All(art.HostImage.AsSpan((int)art.ContextOffset, 64).ToArray(), x => Assert.Equal(0, x));
        Assert.True(art.AddressMap.All(e => e.HostOffset != 0 && e.HostOffset < art.CodeSize));
    }

    [Fact]
    public void HostCodeDecodesCleanlyAndStartsWithTheTrapRoutine()
    {
        foreach (var profile in Profiles)
        {
            var art = Translate(SimpleCode, profile).Artifact;
            var insts = DecodeHost(art);
            Assert.Equal(Code.Mov_rm32_imm32, insts[0].Ins.Code);                       // trap routine at host offset 0
            Assert.Equal(0, insts[0].Offset);
            Assert.True(insts[^1].Offset + insts[^1].Length <= art.CodeSize);
            Assert.Contains(insts, i => i.Offset == art.EnterGuestOffset);
            Assert.All(art.AddressMap, e => Assert.Contains(insts, i => i.Offset == e.HostOffset));
            Assert.All(art.Exports, e => Assert.Contains(insts, i => i.Offset == e.HostOffset));
        }
    }

    [Fact]
    public void AbsoluteDisplacementEncodesAs32BitAbsoluteWithAddressSizePrefix()
    {
        var art = Translate(SimpleCode, OptimizationProfile.Debug).Artifact;
        var export = art.Exports[0];
        var first = DecodeHost(art).First(i => i.Offset == export.HostOffset);
        Assert.True(first.Ins.Code is Code.Mov_r32_rm32 or Code.Mov_EAX_moffs32);
        Assert.Equal(Register.None, first.Ins.MemoryBase); Assert.Equal(Register.None, first.Ins.MemoryIndex);
        Assert.Equal(DataVa, (uint)first.Ins.MemoryDisplacement64);
        Assert.Equal(0x67, art.HostImage[first.Offset]);                                  // addr32: zero-extended, not sign-extended
        Assert.Equal(first.Ins.Code == Code.Mov_EAX_moffs32 ? new byte[] { 0x67, 0xA1 } : new byte[] { 0x67, 0x8B, 0x04, 0x25 }, art.HostImage.AsSpan(first.Offset, first.Ins.Code == Code.Mov_EAX_moffs32 ? 2 : 4).ToArray());
    }

    [Fact]
    public void PushPopCallRetAreLoweredToExplicit32BitStackSequences()
    {
        var art = Translate(Asm(a => { var g = a.CreateLabel(); a.push(ebx); a.pop(ecx); a.call(g); a.ret(); a.Label(ref g); a.ret(); }), OptimizationProfile.Debug).Artifact;
        var insts = DecodeHost(art);
        var start = art.Exports[0].HostOffset;
        var mine = insts.SkipWhile(i => i.Offset != start).Take(12).Select(i => i.Ins).ToList();
        // push ebx -> mov [esp-4],ebx ; lea esp,[esp-4]
        Assert.Equal(Code.Mov_rm32_r32, mine[0].Code); Assert.Equal(Register.ESP, mine[0].MemoryBase); Assert.Equal(-4, (int)(uint)mine[0].MemoryDisplacement64);
        Assert.Equal(Code.Lea_r32_m, mine[1].Code); Assert.Equal(Register.ESP, mine[1].Op0Register);
        // pop ecx -> mov ecx,[esp] ; lea esp,[esp+4]
        Assert.Equal(Code.Mov_r32_rm32, mine[2].Code); Assert.Equal(Register.ECX, mine[2].Op0Register);
        Assert.Equal(Code.Lea_r32_m, mine[3].Code);
        // call g -> mov dword [esp-4], retVA ; lea esp ; jmp
        Assert.Equal(Code.Mov_rm32_imm32, mine[4].Code);
        Assert.Equal(CodeVa + 7, mine[4].Immediate32);                                      // push(1) pop(1) call(5): return address is start+7
        Assert.True(mine[6].Code is Code.Jmp_rel32_64 or Code.Jmp_rel8_64);
    }

    [Fact]
    public void ReleaseMergesPushRunsAndThreadsJumps()
    {
        var pushes = Asm(a => { a.push(eax); a.push(ecx); a.push(0x1234); a.add(esp, 12); a.ret(); });
        var debug = DecodeHost(Translate(pushes, OptimizationProfile.Debug).Artifact).Count(i => i.Ins.Code == Code.Lea_r32_m && i.Ins.Op0Register == Register.ESP);
        var rel = DecodeHost(Translate(pushes, OptimizationProfile.Release).Artifact).Count(i => i.Ins.Code == Code.Lea_r32_m && i.Ins.Op0Register == Register.ESP);
        Assert.True(rel < debug, $"release {rel} vs debug {debug}");
        // jmp -> jmp -> ret: in Release the first jump goes straight to the final target
        var chain = Asm(a =>
        {
            var l1 = a.CreateLabel(); var l2 = a.CreateLabel();
            a.jmp(l1); a.Label(ref l1); a.jmp(l2); a.Label(ref l2); a.ret();
        });
        var art = Translate(chain, OptimizationProfile.Release).Artifact;
        var insts = DecodeHost(art);
        var first = insts.First(i => i.Offset == art.Exports[0].HostOffset);
        var target = art.AddressMap.Single(e => e.GuestRva == PeFixtureBuilder.TextRva + 4);
        Assert.True(first.Ins.Code is Code.Jmp_rel32_64 or Code.Jmp_rel8_64);
        Assert.Equal(target.HostOffset, (uint)first.Ins.NearBranch64);
        // Debug leaves one instruction entry per guest instruction
        Assert.True(Translate(chain, OptimizationProfile.Debug).Artifact.AddressMap.Count >= 3);
    }

    [Fact]
    public void TranslationIsDeterministic()
    {
        foreach (var profile in Profiles)
        {
            var a = Translate(SimpleCode, profile).Artifact; var b = Translate(SimpleCode, profile).Artifact;
            Assert.Equal(a.HostImage, b.HostImage);
            Assert.Equal(a.AddressMap, b.AddressMap);
            Assert.Equal(a.Exports, b.Exports);
            Assert.Equal(a.Diagnostics, b.Diagnostics);
        }
    }

    [Fact]
    public void ExportAbiHintsAreAttached()
    {
        var art = Translate(Asm(a => { a.ret(8); }), OptimizationProfile.Release, f => f.Export("_g@8", 0), exportF: false).Artifact;
        var e = Assert.Single(art.Exports);
        Assert.Equal(GuestCallingConvention.Stdcall, e.Convention); Assert.Equal(8, e.ArgBytes);
    }

    public static IEnumerable<object[]> UnsupportedCases()
    {
        yield return ["gs", "BC_SEGMENT_OVERRIDE", (Action<Assembler>)(a => a.db(0x65, 0x8B, 0x05, 0, 0, 0, 0))];
        yield return ["fs unmodelled offset", "BC_FS_UNSUPPORTED_OFFSET", (Action<Assembler>)(a => a.db(0x64, 0x8B, 0x05, 0x1C, 0, 0, 0))];
        yield return ["aaa", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.aaa())];
        yield return ["daa", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.daa())];
        yield return ["int", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.@int(0x2E))];
        yield return ["in", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.@in(al, dx))];
        yield return ["hlt", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.hlt())];
        yield return ["pushfw", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.db(0x66, 0x9C))];
        yield return ["jmp far", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.db(0xEA, 0x34, 0x12, 0, 0, 0x10, 0))];
        yield return ["16-bit addressing", "BC_UNSUPPORTED_INSTRUCTION", (Action<Assembler>)(a => a.mov(ax, __word_ptr[bx + si]))];
    }

    [Theory]
    [MemberData(nameof(UnsupportedCases))]
    public void UnsupportedInstructionsProduceErrorDiagnosticsWithRva(string name, string code, Action<Assembler> emit)
    {
        var bytes = Asm(a => { a.nop(); emit(a); a.ret(); });
        foreach (var profile in Profiles)
        {
            var art = Translate(bytes, profile).Artifact;
            var d = Assert.Single(art.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
            Assert.Equal(code, d.Code);
            Assert.Equal(PeFixtureBuilder.TextRva + 1, d.GuestRva);
            Assert.False(string.IsNullOrWhiteSpace(d.Message), name);
            // the unsupported site is replaced by a trap, never by a guessed encoding
            Assert.DoesNotContain(DecodeHost(art), i => i.Ins.Code == Code.Hlt || i.Ins.Code is Code.Aaa or Code.Daa);
        }
    }

    [Fact]
    public void NoErrorsForTheSupportedSubset()
    {
        var bytes = Asm(a =>
        {
            a.inc(eax); a.dec(ecx); a.pushad(); a.popad(); a.pushfd(); a.popfd(); a.enter(8, 1); a.leave(); a.cdq(); a.lahf(); a.sahf();
            a.cld(); a.rep.movsd(); a.rep.stosb(); a.xlatb(); a.setc(al); a.cmovne(eax, ecx); a.bt(eax, 3); a.@lock.xadd(__dword_ptr[esp], eax);
            a.movq(mm0, __qword_ptr[esp]); a.emms(); a.pxor(xmm0, xmm0); a.fld1(); a.fstp(st0); a.int3(); a.ret();
        });
        foreach (var profile in Profiles)
            Assert.DoesNotContain(Translate(bytes, profile).Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
