using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Hand-assembled thiscall/fastcall callees: MSVC-style hidden-pointer placement, Bind rules, float returns and the lifetime of runtime buffers.</summary>
[Collection("GuestRuntime")]
public sealed class AbiRegisterConventionTests
{
    private static GuestModule Load(byte[] code, OptimizationProfile profile, params (string Name, uint Offset)[] exports)
    {
        var fx = new PeFixtureBuilder().WithCode(code).WithData(new byte[0x100]);
        foreach (var (n, o) in exports) fx.Export(n, o);
        var image = PeReader.Read(fx.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "abi");
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
    }

    private static void Both(Action<Assembler> body, string export, Action<GuestModule> test)
    {
        var code = Asm(body);
        foreach (var p in Profiles) { using var m = Load(code, p, (export, 0)); test(m); }
    }

    [Fact]
    public void MsvcThiscallStructReturnPutsThisInEcxAndThePointerOnTheStack()
    {
        // MSVC: this in ECX, hidden pointer = first stack word, callee pops it (ret 8) and returns it in EAX.
        Both(a => { a.mov(eax, __dword_ptr[esp + 4]); a.mov(__dword_ptr[eax], ecx); a.mov(edx, __dword_ptr[esp + 8]); a.mov(__dword_ptr[eax + 4], edx); a.ret(8); }, "ts", m =>
        {
            var f = m.Bind("ts", GuestCallingConvention.Thiscall, 3);   // this, a, hidden pointer
            Assert.Equal(1, f.RegisterArgCount);
            var bytes = f.CallStructViaHiddenPointer(8, 0x1234u, 0x99u);
            Assert.Equal([0x34, 0x12, 0, 0, 0x99, 0, 0, 0], bytes);
        });
    }

    [Fact]
    public void MsvcFastcallStructReturnPutsThePointerInEcxAndTheFirstArgumentInEdx()
    {
        // ECX = hidden pointer, EDX = first declared argument, second on the stack; ret 4.
        Both(a => { a.mov(__dword_ptr[ecx], edx); a.mov(eax, __dword_ptr[esp + 4]); a.mov(__dword_ptr[ecx + 4], eax); a.mov(eax, ecx); a.ret(4); }, "fs", m =>
        {
            var f = m.Bind("fs", GuestCallingConvention.Fastcall, 3);
            Assert.Equal([0x11, 0, 0, 0, 0x22, 0, 0, 0], f.CallStructViaHiddenPointer(8, 0x11u, 0x22u));
        });
    }

    [Fact]
    public void RegisterArgumentsReachEcxAndEdxAndTheRestIsOnTheStack()
    {
        // fastcall f(a, b, c): returns a*100 + b*10 + c, written with ecx/edx/[esp+4]
        Both(a => { a.imul(ecx, ecx, 100); a.imul(edx, edx, 10); a.add(ecx, edx); a.add(ecx, __dword_ptr[esp + 4]); a.mov(eax, ecx); a.ret(4); }, "@f@12", m =>
        {
            var f = m.Bind("@f@12", GuestCallingConvention.Unknown, 3);
            Assert.Equal(2, f.RegisterArgCount);
            Assert.Equal(123u, f.Call(1, 2, 3));
            Assert.Equal(123u, f.InvokeRegisters(GuestReturn.Eax, 2, 1, 2, [3]).Eax);
            Assert.Throws<ArgumentException>(() => f.Call(1, 2));
            Assert.Throws<ArgumentException>(() => f.InvokeRegisters(GuestReturn.Eax, 1, 1, 2, [3]));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.InvokeRegisters(GuestReturn.Eax, 3, 1, 2, []));
        });
    }

    [Fact]
    public void CdeclCallsStillSeeZeroedEcxAndEdx()
    {
        Both(a => { a.mov(eax, ecx); a.or(eax, edx); a.ret(); }, "_z", m =>
        {
            var z = m.Bind("_z", GuestCallingConvention.Cdecl, 0);
            Assert.Equal(0u, z.Call());
            // a previous register-convention call on this thread must not leak into the next one
            Assert.Equal(0u, z.Call());
        });
    }

    [Fact]
    public void ThiscallFloatReturnIsReadFromSt0()
    {
        // thiscall float f(this, x): fld dword [esp+4]; ret 4
        Both(a => { a.fld(__dword_ptr[esp + 4]); a.ret(4); }, "tf", m =>
        {
            var f = m.Bind("tf", GuestCallingConvention.Thiscall, 2);
            Assert.Equal(2.5f, f.CallSingle(0xDEADu, BitConverter.SingleToUInt32Bits(2.5f)));
        });
    }

    [Fact]
    public void BindRulesForRegisterConventions()
    {
        Both(a => a.ret(), "plain", m =>
        {
            Assert.Throws<ArgumentException>(() => m.Bind("plain", GuestCallingConvention.Unknown, 1));           // Unknown is never guessed
            Assert.Throws<ArgumentOutOfRangeException>(() => m.Bind("plain", GuestCallingConvention.Thiscall, 0)); // 'this' is mandatory
            Assert.Equal(GuestCallingConvention.Thiscall, m.Bind("plain", GuestCallingConvention.Thiscall, 1).Convention);
            Assert.Equal(GuestCallingConvention.Fastcall, m.Bind("plain", GuestCallingConvention.Fastcall, 0).Convention);
        });
        Both(a => a.ret(8), "@g@8", m =>
        {
            Assert.Equal(GuestCallingConvention.Fastcall, m.Bind("@g@8", GuestCallingConvention.Unknown, 2).Convention);
            Assert.Throws<ArgumentException>(() => m.Bind("@g@8", GuestCallingConvention.Fastcall, 3));   // decoration says 8 bytes of parameters
            Assert.Throws<ArgumentException>(() => m.Bind("@g@8", GuestCallingConvention.Stdcall, 2));    // contradicts the decoration
        });
    }

    [Fact]
    public void OutputBuffersAreCheckedCopiedBackAndFreedOnSuccessAndOnFailure()
    {
        // cdecl: stores the first argument (the buffer address) in the data section, writes 0xAABBCCDD to it and returns the address
        Both(a =>
        {
            a.mov(eax, __dword_ptr[esp + 4]);
            a.mov(__dword_ptr[(int)(PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva)], eax);
            a.mov(__dword_ptr[eax], 0xAABBCCDDu);
            a.ret();
        }, "_w", m =>
        {
            var f = m.Bind("_w", GuestCallingConvention.Cdecl, 1);
            var o = f.InvokeWithBuffers(GuestReturn.Eax, GuestArg.Out<uint>());
            Assert.Equal(0xAABBCCDDu, o.GetUInt32(0));
            Assert.Equal(0xAABBCCDDu, (uint)BitConverter.ToInt32(o.Bytes(0)));
            Assert.False(m.Memory.IsMapped(o.Eax, 1));                       // freed
            Assert.Throws<ArgumentException>(() => o.Get<ulong>(0));        // size mismatch is an error, not a silent truncation
            Assert.Throws<ArgumentException>(() => o.Bytes(1));
            Assert.Throws<ArgumentException>(() => f.InvokeWithBuffers(GuestReturn.Eax));   // argument count is checked before anything is allocated
            Assert.Throws<ArgumentException>(() => f.InvokeWithBuffers(GuestReturn.Eax, GuestArg.In(new byte[] { 1 })).Bytes(0)); // input-only buffers have no output
            Assert.Equal(new byte[] { 0xDD, 0xCC, 0xBB, 0xAA }, f.InvokeWithBuffers(GuestReturn.Eax, GuestArg.InOut(new byte[] { 1, 2, 3, 4 })).Bytes(0));
            Assert.Equal(0, f.InvokeWithBuffers(GuestReturn.Eax, GuestArg.Out(0)).Bytes(0).Length == 0 ? 0 : 1);
        });
        // the callee records the address and then jumps into untranslated space: the buffer must still be freed
        Both(a =>
        {
            a.mov(eax, __dword_ptr[esp + 4]);
            a.mov(__dword_ptr[(int)(PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva)], eax);
            a.mov(eax, 0x12345678);
            a.jmp(eax);
        }, "_t", m =>
        {
            var f = m.Bind("_t", GuestCallingConvention.Cdecl, 1);
            Assert.ThrowsAny<GuestException>(() => f.InvokeWithBuffers(GuestReturn.Eax, GuestArg.Out(64)));
            var address = m.Memory.ReadUInt32(PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva);
            Assert.NotEqual(0u, address);
            Assert.False(m.Memory.IsMapped(address, 1));
        });
    }
}
