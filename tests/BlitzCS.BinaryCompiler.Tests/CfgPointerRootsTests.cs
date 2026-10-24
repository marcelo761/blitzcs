using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Without a relocation table (stripped or derived images) address-taken functions are found from data words and instruction immediates, with validation.</summary>
[Collection("GuestRuntime")]
public sealed class CfgPointerRootsTests
{
    private static byte[] Le(uint v) => BitConverter.GetBytes(v);

    private static uint Run(byte[] code, byte[]? data, OptimizationProfile profile)
    {
        var b = Translate(code, profile, null, true, data);
        using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        return m.Bind("_f", GuestCallingConvention.Cdecl, 0).Call();
    }

    // _f: call dword ptr [DataVa] ; ret      -> function F starts at offset 7, right after the ret
    private static byte[] CallThroughTable() => Asm(a =>
    {
        a.call(__dword_ptr[DataVa]); a.ret();
        a.mov(eax, 42); a.ret();
    });

    [Fact]
    public void FunctionPointerStoredInADataTableIsTranslatedWithoutRelocations()
    {
        foreach (var profile in Profiles)
            Assert.Equal(42u, Run(CallThroughTable(), Le(CodeVa + 7), profile));
    }

    [Fact]
    public void CodeAddressHeldAsAnImmediateIsTranslatedWithoutRelocations()
    {
        // mov eax, F ; call eax ; ret   -> F at offset 8
        var code = Asm(a => { a.mov(eax, CodeVa + 8); a.call(eax); a.ret(); a.mov(eax, 42); a.ret(); });
        foreach (var profile in Profiles) Assert.Equal(42u, Run(code, null, profile));
    }

    [Fact]
    public void ScanDiagnosticsAreInformationalAndNamed()
    {
        var b = Translate(CallThroughTable(), OptimizationProfile.Release, null, true, Le(CodeVa + 7));
        Assert.Contains(b.Artifact.Diagnostics, d => d.Code == "CFG_POINTER_SCAN_ROOTS" && d.Severity == DiagnosticSeverity.Info);
        Assert.DoesNotContain(b.Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AddressInsideDataThatDoesNotLookLikeAFunctionIsNotARoot()
    {
        // The table points at an address in the middle of the code (offset 3), not at a function start preceded by a ret; it must trap, not be guessed into code.
        var code = Asm(a => { a.call(__dword_ptr[DataVa]); a.ret(); a.mov(eax, 42); a.ret(); });
        var b = Translate(code, OptimizationProfile.Release, null, true, Le(CodeVa + 3));
        using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        Assert.Throws<GuestTrapException>(() => m.Bind("_f", GuestCallingConvention.Cdecl, 0).Call());
    }

    [Fact]
    public void WithRelocationsTheScanIsNotUsed()
    {
        var b = Translate(CallThroughTable(), OptimizationProfile.Release, f => f.Relocate(PeFixtureBuilder.DataRva), true, Le(CodeVa + 7));
        Assert.DoesNotContain(b.Artifact.Diagnostics, d => d.Code is "CFG_POINTER_SCAN_ROOTS" or "CFG_IMMEDIATE_ROOTS");
    }

    [Theory]
    [InlineData("jmp8")]
    [InlineData("jmp32")]
    [InlineData("ret")]
    [InlineData("retimm")]
    public void FunctionPlacedAfterAnyFlowEndingInstructionIsRecognised(string preceding)
    {
        // _f: call dword ptr [DataVa] ; ret ; <preceding instruction> ; F: mov eax, 42 ; ret
        var code = Asm(a =>
        {
            var skip = a.CreateLabel();
            a.call(__dword_ptr[DataVa]); a.ret();
            switch (preceding)
            {
                case "jmp8": a.jmp(skip); break;
                case "jmp32": a.db(0xE9, 0, 0, 0, 0); break;
                case "ret": a.ret(); break;
                case "retimm": a.ret(4); break;
            }
            a.Label(ref skip); a.mov(eax, 42); a.ret();
        });
        var offset = Array.IndexOf(code, (byte)0xB8);   // F starts at the mov eax, imm32 opcode
        foreach (var profile in Profiles)
            Assert.Equal(42u, Run(code, Le(CodeVa + (uint)offset), profile));
    }

    [Fact]
    public void AddressAfterAnOrdinaryInstructionIsStillNotAFunctionStart()
    {
        // The word points right after "mov eax, 1" (not a flow-ending instruction): data-like, must not become a root.
        var code = Asm(a => { a.call(__dword_ptr[DataVa]); a.ret(); a.mov(ebx, 1); a.mov(eax, 42); a.ret(); });
        var offset = Array.LastIndexOf(code, (byte)0xB8);
        var b = Translate(code, OptimizationProfile.Release, null, true, Le(CodeVa + (uint)offset));
        using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        Assert.Throws<GuestTrapException>(() => m.Bind("_f", GuestCallingConvention.Cdecl, 0).Call());
    }
}
