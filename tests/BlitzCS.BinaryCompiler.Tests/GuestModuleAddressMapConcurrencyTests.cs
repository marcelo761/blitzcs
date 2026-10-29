using System.Collections.Concurrent;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class GuestModuleAddressMapConcurrencyTests
{
    [Fact]
    public void ConcurrentFirstHasAndInvokeRvaCallsPublishOneCompleteAddressMap()
    {
        var code = TranslatorHarness.Asm(a =>
        {
            a.mov(Iced.Intel.AssemblerRegisters.eax, Iced.Intel.AssemblerRegisters.__dword_ptr[Iced.Intel.AssemblerRegisters.esp + 4]);
            a.inc(Iced.Intel.AssemblerRegisters.eax);
            a.ret();
        });
        var built = TranslatorHarness.Translate(code, OptimizationProfile.Release);
        using var module = GuestModule.Load(built.Artifact, built.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        const uint rva = PeFixtureBuilder.TextRva;

        // No Bind, HasTranslatedRva, or InvokeRva runs before the workers start, so
        // these first calls race to publish the module's address map.
        const int threadCount = 8;
        const int callsPerThread = 200;
        using var ready = new CountdownEvent(threadCount);
        using var start = new ManualResetEventSlim(false);
        var failures = new ConcurrentBag<string>();
        var threads = Enumerable.Range(0, threadCount).Select(worker => new Thread(() =>
        {
            try
            {
                ready.Signal();
                if (!start.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("start gate timed out");

                for (uint call = 0; call < callsPerThread; call++)
                {
                    var expected = (uint)(worker * callsPerThread) + call + 1;
                    if (((worker + call) & 1) == 0)
                    {
                        var actual = module.InvokeRva(rva, "_f", GuestReturn.Eax, [expected - 1]).Eax;
                        if (actual != expected) failures.Add($"worker {worker}, call {call}: InvokeRva returned {actual}, expected {expected}");
                        if (!module.HasTranslatedRva(rva)) failures.Add($"worker {worker}, call {call}: HasTranslatedRva missed RVA 0x{rva:X}");
                    }
                    else
                    {
                        if (!module.HasTranslatedRva(rva)) failures.Add($"worker {worker}, call {call}: HasTranslatedRva missed RVA 0x{rva:X}");
                        var actual = module.InvokeRva(rva, "_f", GuestReturn.Eax, [expected - 1]).Eax;
                        if (actual != expected) failures.Add($"worker {worker}, call {call}: InvokeRva returned {actual}, expected {expected}");
                    }
                }
            }
            catch (Exception error) { failures.Add($"worker {worker}: {error}"); }
        })).ToArray();

        foreach (var thread in threads) thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "workers did not reach the start gate");
        start.Set();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "concurrent address-map lookup did not finish");

        Assert.Empty(failures);
        Assert.False(module.HasTranslatedRva(0xDEADBEEFu));
    }
}
