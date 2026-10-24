using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Per-call contexts: guest calls run in parallel, and a callback may block on another thread that also calls the guest.</summary>
[Collection("GuestRuntime")]
public sealed class ConcurrencyTests
{
    private static uint Reference(uint n) { uint s = 0; for (uint i = 0; i < n; i++) s += (i * i) ^ i; return s; }

    [Fact]
    public void ParallelCallsOnSeparateGuestStacksAreCorrect()
    {
        var code = Asm(a =>
        {
            var loop = a.CreateLabel(); var done = a.CreateLabel();
            a.mov(ecx, __dword_ptr[esp + 4]); a.xor(eax, eax); a.xor(edx, edx);
            a.Label(ref loop); a.cmp(edx, ecx); a.jae(done);
            a.mov(ebx, edx); a.imul(ebx, edx); a.xor(ebx, edx); a.add(eax, ebx); a.inc(edx); a.jmp(loop);
            a.Label(ref done); a.ret();
        });
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile);
            using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            var fn = m.Bind("_f", GuestCallingConvention.Cdecl, 1);
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                for (uint k = 0; k < 400; k++)
                {
                    var n = (uint)(t * 997 + k * 13) % 3000;
                    var got = fn.Call(n);
                    if (got != Reference(n)) failures.Add($"thread {t} n={n}: {got:X} != {Reference(n):X}");
                }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(60)), "parallel guest calls did not finish (deadlock?)");
            Assert.Empty(failures);
        }
    }

    [Fact]
    public void CallbackBlockingOnAnotherThreadThatCallsTheGuestDoesNotDeadlock()
    {
        GuestFunction? fn = null;
        var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.CdeclCall(call =>
        {
            var x = call.Arg(0);
            if (x == 0) return 0;
            uint inner = 0;
            var worker = new Thread(() => inner = fn!.Call(x - 1));
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "nested guest call on another thread did not finish (deadlock?)");
            return inner;
        }));
        var code = Asm(a =>
        {
            a.mov(eax, __dword_ptr[esp + 4]); a.push(eax);
            a.call(__dword_ptr[PeFixtureBuilder.ImageBase + PeFixtureBuilder.IdataRva + 0x400]);
            a.add(esp, 4); a.inc(eax); a.ret();
        });
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile, f => f.Import("HOST.DLL", "hook"));
            using var m = GuestModule.Load(b.Artifact, b.Image, resolver, new GuestModuleOptions { RunEntryPoint = false });
            fn = m.Bind("_f", GuestCallingConvention.Cdecl, 1);
            Assert.Equal(6u, fn.Call(5));
        }
    }
}
