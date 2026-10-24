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
}
