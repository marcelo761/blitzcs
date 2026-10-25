using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class ImportFaultTests
{
    private static byte[] CallThenSpin() => Asm(a =>
    {
        var spin = a.CreateLabel();
        a.call(__dword_ptr[PeFixtureBuilder.ImageBase + PeFixtureBuilder.IdataRva + 0x400]);
        a.Label(ref spin); a.jmp(spin); // reached only if a faulted import fails to abandon the call
    });

    /// <summary>Runs on a worker thread with a deadline, so a guest that fails to abandon its call fails the test instead of hanging it.</summary>
    private static T Bounded<T>(Func<T> body)
    {
        T? result = default; Exception? failure = null;
        var thread = new Thread(() => { try { result = body(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "guest kept running after its import faulted");
        if (failure is not null) throw failure;
        return result!;
    }

    [Fact]
    public void FaultingImportAbandonsTheGuestCallInsteadOfLettingItRunOn()
    {
        var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.CdeclCall(_ => throw new InvalidOperationException("host service failed")));
        foreach (var profile in Profiles)
        {
            var b = Translate(CallThenSpin(), profile, f => f.Import("HOST.DLL", "hook"));
            using var m = GuestModule.Load(b.Artifact, b.Image, resolver, new GuestModuleOptions { RunEntryPoint = false });
            var fn = m.Bind("_f", GuestCallingConvention.Cdecl, 0);
            var error = Bounded(() => Assert.Throws<GuestImportException>(() => fn.Call()));
            Assert.Contains("host service failed", error.Message);
            Assert.Throws<GuestImportException>(() => fn.Call()); // module still usable and still faulting deterministically
        }
    }

    [Fact]
    public void UnboundImportFailsLoadByDefaultAndNamesTheSymbolWhenAllowed()
    {
        var b = Translate(CallThenSpin(), OptimizationProfile.Release, f => f.Import("HOST.DLL", "hook"));
        var load = Assert.Throws<GuestImportException>(() => GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains("HOST.DLL!hook", load.Message);

        using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false, AllowUnboundImports = true });
        var fn = m.Bind("_f", GuestCallingConvention.Cdecl, 0);
        var error = Bounded(() => Assert.Throws<GuestImportException>(() => fn.Call()));
        Assert.Contains("HOST.DLL!hook", error.Message);
    }
}
