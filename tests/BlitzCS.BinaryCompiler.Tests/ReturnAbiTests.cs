using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Return registers (EDX:EAX, ST0, XMM0, hidden struct pointer) and the floating-point state contract of a guest call.</summary>
[Collection("GuestRuntime")]
public sealed class ReturnAbiTests
{
    private static GuestModule Load(byte[] code, OptimizationProfile profile, params (string Name, uint Offset)[] exports)
    {
        var fx = new PeFixtureBuilder().WithCode(code);
        foreach (var (n, o) in exports) fx.Export(n, o);
        var image = PeReader.Read(fx.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "ret");
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
    }

    private static void Both(Action<Assembler> body, Action<GuestModule> test)
    {
        var code = Asm(body);
        foreach (var p in Profiles) { using var m = Load(code, p, ("_f", 0)); test(m); }
    }

    private static GuestFunction F(GuestModule m, int args = 0) => m.Bind("_f", GuestCallingConvention.Cdecl, args);

    [Fact]
    public void St0ReturnIsCapturedAndPoppedForFloatAndDouble()
    {
        // fld qword [esp+4]; ret   (double in ST0)
        Both(a => { a.fld(__qword_ptr[esp + 4]); a.ret(); }, m =>
        {
            var f = F(m, 2);
            Assert.Equal(1.5, f.CallDouble(Lo(1.5), Hi(1.5)));
            for (var i = 0; i < 20; i++) Assert.Equal(-2.25 * i, f.CallDouble(Lo(-2.25 * i), Hi(-2.25 * i)));   // the stack never fills up
            Assert.Equal(0.1f, f.CallSingle(Lo(0.1), Hi(0.1)));                                              // narrowing: double -> float rounding
            var r = f.Invoke(GuestReturn.St0, Lo(1.0), Hi(1.0));
            Assert.Equal(0x8000000000000000UL, r.St0Mantissa); Assert.Equal((ushort)0x3FFF, r.St0SignExponent);
        });
    }

    private static uint Lo(double d) => (uint)BitConverter.DoubleToUInt64Bits(d);
    private static uint Hi(double d) => (uint)(BitConverter.DoubleToUInt64Bits(d) >> 32);

    [Fact]
    public void St0EdgeValuesKeepTheirBits()
    {
        Both(a => { a.fld(__qword_ptr[esp + 4]); a.ret(); }, m =>
        {
            var f = F(m, 2);
            foreach (var v in new[] { double.NaN, -0.0, 0.0, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon, double.MaxValue, 2.2250738585072009e-308 })
            {
                var got = f.CallDouble(Lo(v), Hi(v));
                Assert.Equal(BitConverter.DoubleToUInt64Bits(v) & (double.IsNaN(v) ? 0xFFF8000000000000UL : ulong.MaxValue), BitConverter.DoubleToUInt64Bits(got) & (double.IsNaN(v) ? 0xFFF8000000000000UL : ulong.MaxValue));
            }
        });
    }

    [Fact]
    public void Edx_Eax_Int64ReturnAndStructInRegisters()
    {
        Both(a => { a.mov(eax, __dword_ptr[esp + 4]); a.mov(edx, __dword_ptr[esp + 8]); a.ret(); }, m =>
        {
            var f = F(m, 2);
            Assert.Equal(0x8000000000000000UL, f.CallUInt64(0, 0x80000000u));                  // INT64_MIN
            Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, f.CallUInt64(0xFFFFFFFFu, 0xFFFFFFFFu));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, f.CallStructInRegisters(8, 0x04030201u, 0x08070605u));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, f.CallStructInRegisters(4, 0x04030201u, 0x08070605u));
            Assert.Equal(new byte[] { 1, 2 }, f.CallStructInRegisters(2, 0x04030201u, 0u));
            Assert.Equal(new byte[] { 1 }, f.CallStructInRegisters(1, 0x04030201u, 0u));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.CallStructInRegisters(3, 0u, 0u));
            Assert.Equal(0x04030201u, f.Call(0x04030201u, 0u));                                // plain Call is unchanged
        });
    }

    [Fact]
    public void HiddenPointerStructReturn()
    {
        // _f(sret, a, b): sret->{a, b, a+b}; return sret   (stdcall would pop 12, cdecl 0: the frame is discarded either way)
        Both(a =>
        {
            a.mov(ecx, __dword_ptr[esp + 4]); a.mov(eax, __dword_ptr[esp + 8]); a.mov(edx, __dword_ptr[esp + 12]);
            a.mov(__dword_ptr[ecx], eax); a.mov(__dword_ptr[ecx + 4], edx); a.add(eax, edx); a.mov(__dword_ptr[ecx + 8], eax); a.mov(eax, ecx); a.ret();
        }, m =>
        {
            var f = F(m, 3);
            var bytes = f.CallStructViaHiddenPointer(12, 7u, 35u);
            Assert.Equal([7, 0, 0, 0, 35, 0, 0, 0, 42, 0, 0, 0], bytes);
            Assert.Throws<ArgumentException>(() => f.CallStructViaHiddenPointer(12, 7u));      // wrong explicit argument count
        });
    }

    [Fact]
    public void HiddenPointerCalleeThatDoesNotReturnThePointerIsAnAbiError()
    {
        Both(a => { a.xor(eax, eax); a.ret(); }, m =>
        {
            var ex = Assert.Throws<GuestAbiException>(() => F(m, 1).CallStructViaHiddenPointer(12));
            Assert.Contains("hidden struct pointer", ex.Message);
        });
    }

    [Fact]
    public void Xmm0ReturnIsAnExplicitOptIn()
    {
        // movss xmm0, [esp+4]; ret   /   movsd xmm0, [esp+4]
        Both(a => { a.movss(xmm0, __dword_ptr[esp + 4]); a.ret(); }, m =>
        {
            Assert.Equal(3.5f, F(m, 1).CallXmm0Single(BitConverter.SingleToUInt32Bits(3.5f)));
            var r = F(m, 1).Invoke(GuestReturn.Xmm0, BitConverter.SingleToUInt32Bits(-0.0f));
            Assert.Equal(0x80000000u, (uint)r.Xmm0Low);
        });
        Both(a => { a.movsd(xmm0, __qword_ptr[esp + 4]); a.ret(); }, m =>
            Assert.Equal(-7.25, F(m, 2).CallXmm0Double(Lo(-7.25), Hi(-7.25))));
    }

    [Fact]
    public void ReadingAnUncapturedRegisterThrowsInsteadOfReturningZero()
    {
        Both(a => { a.mov(eax, 5); a.ret(); }, m =>
        {
            var r = F(m).Invoke(GuestReturn.Eax);
            Assert.Equal(5u, r.Eax);
            Assert.Throws<InvalidOperationException>(() => r.St0Double);
            Assert.Throws<InvalidOperationException>(() => r.Xmm0Low);
        });
    }

    [Fact]
    public void ValueLeftOnTheX87StackByAnEaxCallIsAnAbiErrorAndTheStackIsReset()
    {
        var code = Asm(a => { a.fld1(); a.ret(); });
        foreach (var p in Profiles)
        {
            using var m = Load(code, p, ("_f", 0));
            var f = F(m);
            for (var i = 0; i < 12; i++)                                  // would overflow the 8-entry x87 stack if it were not reset
            {
                var ex = Assert.Throws<GuestAbiException>(() => f.Call());
                Assert.Contains("x87", ex.Message);
            }
            Assert.Equal(1.0, f.CallDouble());                           // the typed call works on the same module afterwards
        }
    }

    [Fact]
    public void RequestingSt0WhenTheGuestReturnsNothingThereIsAnAbiError()
    {
        Both(a => { a.mov(eax, 1); a.ret(); }, m =>
        {
            var ex = Assert.Throws<GuestAbiException>(() => F(m).CallDouble());
            Assert.Contains("empty x87 stack", ex.Message);
            Assert.Equal(1u, F(m).Call());
        });
    }

    [Fact]
    public void ExtraValuesBesidesTheRequestedSt0AreReported()
    {
        Both(a => { a.fld1(); a.fldz(); a.ret(); }, m =>
        {
            for (var i = 0; i < 12; i++) Assert.Throws<GuestAbiException>(() => F(m).CallDouble());   // each call resets the stack, so it never overflows
        });
    }

    [Fact]
    public void FpuControlWordAndMxcsrAreRestoredAfterTheGuestChangesThem()
    {
        var otherCw = HostPlatform.IsWindows ? 0x37F : 0x27F;                    // the OTHER precision than the host's natural one (so a missing restore is visible)
        var change = Asm(a =>
        {
            a.sub(esp, 4);
            a.mov(__word_ptr[esp], otherCw); a.fldcw(__word_ptr[esp]);
            a.mov(__dword_ptr[esp], 0x9FC0); a.ldmxcsr(__dword_ptr[esp]);        // FTZ|DAZ, round to nearest
            a.add(esp, 4); a.mov(eax, 1); a.ret();
        });
        var read = Asm(a => { a.sub(esp, 4); a.fnstcw(__word_ptr[esp]); a.movzx(eax, __word_ptr[esp]); a.stmxcsr(__dword_ptr[esp]); a.mov(edx, __dword_ptr[esp]); a.add(esp, 4); a.ret(); });
        foreach (var p in Profiles)
        {
            using var m = Load([.. change, .. read], p, ("_change", 0), ("_read", (uint)change.Length));
            var rd = m.Bind("_read", GuestCallingConvention.Cdecl, 0);
            var before = rd.Invoke(GuestReturn.Eax);
            Assert.Equal(1u, m.Bind("_change", GuestCallingConvention.Cdecl, 0).Call());
            var after = rd.Invoke(GuestReturn.Eax);
            Assert.Equal(before.Eax, after.Eax); Assert.Equal(before.Edx, after.Edx);                // the next guest call starts from the host's x87 CW and MXCSR
            Assert.Equal(HostPlatform.NaturalX87ControlWord, after.Eax);
        }
    }

    [Fact]
    public void GuestSeesTheHostControlStateOnEveryCall()
    {
        var code = Asm(a => { a.sub(esp, 4); a.fnstcw(__word_ptr[esp]); a.movzx(eax, __word_ptr[esp]); a.add(esp, 4); a.ret(); });
        foreach (var p in Profiles) { using var m = Load(code, p, ("_f", 0)); Assert.Equal(HostPlatform.NaturalX87ControlWord, F(m).Call()); Assert.Equal(HostPlatform.NaturalX87ControlWord, F(m).Call()); }
    }

    [Fact]
    public void ParallelThreadsReturnTheirOwnFloatingPointValues()
    {
        var code = Asm(a => { a.fld(__qword_ptr[esp + 4]); a.fadd(__qword_ptr[esp + 4]); a.mov(edx, __dword_ptr[esp + 4]); a.ret(); });
        foreach (var p in Profiles)
        {
            using var m = Load(code, p, ("_f", 0));
            var f = F(m, 2);
            var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                for (var k = 0; k < 3000; k++)
                {
                    var v = t * 1000.0 + k * 0.125;
                    var r = f.Invoke(GuestReturn.St0, Lo(v), Hi(v));
                    if (r.St0Double != 2 * v || r.Edx != Lo(v)) { bad.Add($"thread {t} k={k}: {r.St0Double} edx={r.Edx:X}"); return; }
                }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            Assert.Empty(bad);
        }
    }

    [Fact]
    public void FloatingPointReturnsWorkInsideReentrantCallbacks()
    {
        GuestFunction? fn = null;
        var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.CdeclCall(call =>
        {
            var d = fn!.CallDouble(call.Arg(0), call.Arg(1));            // nested guest call while the outer one is inside an import
            Assert.Equal(2.5, d);
            return 7;
        }));
        var code = Asm(a =>
        {
            // push the two argument words again, call the hook, then return the original double in ST0
            a.push(__dword_ptr[esp + 8]); a.push(__dword_ptr[esp + 8]); a.call(__dword_ptr[PeFixtureBuilder.ImageBase + PeFixtureBuilder.IdataRva + 0x400]); a.add(esp, 8);
            a.fld(__qword_ptr[esp + 4]); a.fadd(__qword_ptr[esp + 4]); a.ret();
        });
        // The nested target is a second export that simply loads the double.
        var plain = Asm(a => { a.fld(__qword_ptr[esp + 4]); a.ret(); });
        foreach (var p in Profiles)
        {
            var fx = new PeFixtureBuilder().WithCode([.. code, .. plain]).Export("_outer", 0).Export("_inner", (uint)code.Length);
            fx.Import("HOST.DLL", "hook");
            var image = PeReader.Read(fx.Build());
            var art = X64Translator.Translate(image, new BinaryCompilerOptions(p), "ret-reent");
            using var m = GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
            fn = m.Bind("_inner", GuestCallingConvention.Cdecl, 2);
            Assert.Equal(5.0, m.Bind("_outer", GuestCallingConvention.Cdecl, 2).CallDouble(Lo(2.5), Hi(2.5)));
        }
    }
}
