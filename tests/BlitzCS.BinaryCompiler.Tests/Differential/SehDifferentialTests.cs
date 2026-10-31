using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// Guest SEH (docs/binary-seh.md) against the x86 oracle. lib/seh.c raises with KERNEL32!RaiseException and handles through a hand-built FS:[0] chain the way the MSVC
/// runtime does (search, RtlUnwind with EH_UNWINDING calls, _UnwindNestedFrames, _JumpToContinuation, nested raise inside a handler, cdecl and stdcall handlers,
/// ContinueExecution, RtlUnwind without a record). The ELF oracle runs it natively with a reference model of the dispatcher; the PE32 oracle (Windows or Wine) runs it
/// with the real kernel32 (both transcripts were byte-identical under Wine 11 when this test was written). The translated guest uses the runtime's dispatcher.
/// The cases outside the supported scope fault by name.
/// </summary>
[Collection("GuestRuntime")]
public sealed class SehDifferentialTests
{
    public static TheoryData<OptimizationProfile> Profiles()
    {
        var d = new TheoryData<OptimizationProfile>();
        foreach (var p in TranslatorHarness.Profiles) d.Add(p);
        return d;
    }

    [OracleTheory, MemberData(nameof(Profiles))]
    public void Translated_guest_dispatches_and_unwinds_like_the_x86_oracle(OptimizationProfile profile)
    {
        var outcome = OracleDifferential.Run("seh", profile);
        Assert.True(outcome.Passed, outcome.ToString());
        Assert.True(outcome.Vectors > 2000);
    }

    private static (GuestModule Module, Func<string, GuestFunction> Fn) Load(IGuestImportResolver? resolver = null)
    {
        var art = OracleRunner.Build("seh");
        var image = OracleDifferential.LoadImage(art);
        var tr = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "seh-faults");
        Assert.DoesNotContain(tr.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        resolver ??= new DictionaryGuestImportResolver()
            .Add("KERNEL32.dll", "RaiseException", GuestStructuredExceptions.RaiseExceptionImport())
            .Add("KERNEL32.dll", "RtlUnwind", GuestStructuredExceptions.RtlUnwindImport());
        var module = GuestModule.Load(tr, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        return (module, name => module.Bind(name, GuestCallingConvention.Cdecl, name.StartsWith("s_un") || name.StartsWith("s_non") ? 1 : 2));
    }

    [OracleFact]
    public void An_exception_no_handler_takes_is_a_named_unhandled_fault_and_the_module_stays_usable()
    {
        var (module, fn) = Load();
        using (module)
        {
            var e = Assert.Throws<GuestImportException>(() => fn("s_unhandled").Call(0xE0001234));
            var inner = Assert.IsType<GuestUnhandledException>(e.InnerException);
            Assert.Equal(0xE0001234u, inner.Code);
            Assert.Equal([0xE0001234u], inner.Parameters);
            Assert.Contains("Unhandled guest exception 0xE0001234", inner.Message);
            Assert.Equal(OracleRunner.Run("seh").Vectors.First(v => v.Function == "s_catch").Result, fn("s_catch").Call(0, 0));
        }
    }

    [OracleFact]
    public void Continue_execution_for_a_non_continuable_exception_is_a_named_fault()
    {
        var (module, fn) = Load();
        using (module)
        {
            var e = Assert.Throws<GuestImportException>(() => fn("s_noncontinuable").Call(0xE0005678));
            var inner = Assert.IsType<GuestSehException>(e.InnerException);
            Assert.Contains("non-continuable exception 0xE0005678", inner.Message);
        }
    }

    [OracleFact]
    public void An_exception_that_would_cross_a_host_frame_between_two_guest_calls_is_a_named_fault()
    {
        // The first RaiseException (inside s_catch, which has handlers installed) calls back into the guest (a nested guest call through managed code);
        // the exception raised there would have to reach s_catch's records above the host frame.
        GuestModule? m = null;
        var first = true;
        var raise = GuestImportBuilder.StdcallCall(4, c =>
        {
            if (!first) return GuestStructuredExceptions.RaiseException(c);
            first = false;
            var unhandled = m!.ImageBase + PeFixtureBuilder.TextRva + OracleRunner.Build("seh").Exports.Single(x => x.Name == "s_unhandled").Offset;
            return c.CallGuest(unhandled, 7);
        });
        var resolver = new DictionaryGuestImportResolver().Add("KERNEL32.dll", "RaiseException", raise).Add("KERNEL32.dll", "RtlUnwind", GuestStructuredExceptions.RtlUnwindImport());
        var (module, fn) = Load(resolver);
        m = module;
        using (module)
        {
            var e = Assert.Throws<GuestImportException>(() => fn("s_catch").Call(0, 0));
            var chain = e.ToString();
            Assert.Contains("cross a host (managed) frame", chain);
        }
    }
}
