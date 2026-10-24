using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>PeFixtureBuilder -> PeReader -> X64Translator -> GuestModule: translated IA-32 really runs and matches a C# reference.</summary>
[Collection("GuestRuntime")]
public sealed class EndToEndTranslatorTests
{
    private static readonly (uint A, uint B)[] Pairs =
    [
        (0, 0), (1, 2), (0xFFFFFFFF, 1), (0x80000000, 0x7FFFFFFF), (0x12345678, 0x9ABCDEF0), (5, 0xFFFFFFFB), (0xDEADBEEF, 31),
    ];

    private static byte[] Data256()
    {
        var d = new byte[0x100];
        for (var i = 0; i < d.Length; i++) d[i] = (byte)i;
        return d;
    }

    [Fact]
    public void ArithmeticAndLogicWithFlags()
    {
        void Body(Assembler a)
        {
            a.mov(eax, __dword_ptr[esp + 4]); a.mov(ecx, __dword_ptr[esp + 8]);
            a.add(eax, ecx); a.adc(eax, 0x12345); a.sub(eax, ecx); a.xor(eax, 0x55AA55AA); a.neg(eax); a.not(eax);
            a.inc(eax); a.dec(ecx); a.or(eax, ecx); a.and(eax, 0x0FFF0FFF); a.ret();
        }
        foreach (var (x, y) in Pairs)
        {
            var c = (uint)(((ulong)x + y) >> 32);
            var v = unchecked(x + y + 0x12345 + c - y);
            v ^= 0x55AA55AA; v = unchecked((uint)-(int)v); v = ~v; v++; v |= y - 1; v &= 0x0FFF0FFF;
            Assert.Equal(v, Run(Body, [x, y]));
        }
    }

    [Fact]
    public void ConditionCodesAndSetcc()
    {
        void Body(Assembler a)
        {
            a.mov(ecx, __dword_ptr[esp + 4]); a.cmp(ecx, __dword_ptr[esp + 8]);
            a.mov(eax, 0); a.setb(al); a.seta(ah); a.mov(edx, 0); a.setl(dl); a.setg(dh); a.shl(edx, 16); a.or(eax, edx); a.ret();
        }
        foreach (var (x, y) in Pairs)
        {
            uint e = (x < y ? 1u : 0) | (x > y ? 0x100u : 0) | ((int)x < (int)y ? 0x10000u : 0) | ((int)x > (int)y ? 0x1000000u : 0);
            Assert.Equal(e, Run(Body, [x, y]));
        }
    }

    [Fact]
    public void FlagsSurviveCallRetAndIndirectJump()
    {
        Assert.Equal(0xFFFFFFFFu, Run(a =>
        {
            var g = a.CreateLabel();
            a.mov(eax, 0); a.stc(); a.call(g); a.sbb(eax, eax); a.ret();
            a.Label(ref g); a.ret();
        }));
        Assert.Equal(0xFFFFFFFFu, Run(a =>
        {
            var l1 = a.CreateLabel(); var tgt = a.CreateLabel();
            a.call(l1); a.Label(ref l1); a.pop(ecx); a.add(ecx, 9); a.stc(); a.jecxz(tgt); a.jmp(ecx); a.Label(ref tgt); a.sbb(eax, eax); a.ret();
        }));
        Assert.Equal(0u, Run(a =>
        {
            var l1 = a.CreateLabel(); var tgt = a.CreateLabel();
            a.call(l1); a.Label(ref l1); a.pop(ecx); a.add(ecx, 9); a.clc(); a.jecxz(tgt); a.jmp(ecx); a.Label(ref tgt); a.sbb(eax, eax); a.ret();
        }));
    }

    [Fact]
    public void Shifts()
    {
        void Body(Assembler a)
        {
            a.mov(eax, __dword_ptr[esp + 4]); a.mov(ecx, __dword_ptr[esp + 8]); a.mov(edx, eax);
            a.shl(eax, cl); a.mov(ebx, edx); a.shr(ebx, cl); a.xor(eax, ebx); a.mov(ebx, edx); a.sar(ebx, cl); a.add(eax, ebx);
            a.mov(ebx, edx); a.rol(ebx, cl); a.xor(eax, ebx); a.mov(ebx, edx); a.shld(ebx, ecx, cl); a.add(eax, ebx); a.ret();
        }
        foreach (var (x, y) in Pairs)
        {
            var c = (int)(y & 31);
            uint v = x << c; v ^= x >> c; v = unchecked(v + (uint)((int)x >> c)); v ^= (x << c) | (c == 0 ? 0 : x >> (32 - c));
            v = unchecked(v + (c == 0 ? x : (x << c) | (y >> (32 - c))));
            Assert.Equal(v, Run(Body, [x, y]));
        }
    }

    [Fact]
    public void MultiplyAndDivide()
    {
        void Mul(Assembler a) { a.mov(eax, __dword_ptr[esp + 4]); a.mul(__dword_ptr[esp + 8]); a.xor(eax, edx); a.ret(); }
        foreach (var (x, y) in Pairs) { var p = (ulong)x * y; Assert.Equal((uint)p ^ (uint)(p >> 32), Run(Mul, [x, y])); }
        void IMul(Assembler a) { a.imul(eax, __dword_ptr[esp + 4], 1000); a.imul(eax, __dword_ptr[esp + 8]); a.ret(); }
        foreach (var (x, y) in Pairs) Assert.Equal(unchecked((uint)((int)x * 1000 * (int)y)), Run(IMul, [x, y]));
        void IDiv(Assembler a)
        {
            a.mov(eax, __dword_ptr[esp + 4]); a.cdq(); a.mov(ecx, __dword_ptr[esp + 8]); a.idiv(ecx); a.lea(eax, __[eax + edx * 8]); a.ret();
        }
        foreach (var (x, y) in new (int, int)[] { (100, 7), (-100, 7), (100, -7), (-1000000, -999), (12345, 1) })
            Assert.Equal(unchecked((uint)(x / y + (x % y) * 8)), Run(IDiv, [(uint)x, (uint)y]));
        void Div(Assembler a) { a.mov(eax, __dword_ptr[esp + 4]); a.xor(edx, edx); a.div(__dword_ptr[esp + 8]); a.add(eax, edx); a.ret(); }
        Assert.Equal(0xFFFFFFFFu / 10 + 0xFFFFFFFFu % 10, Run(Div, [0xFFFFFFFF, 10]));
    }

    [Fact]
    public void AddressingFormsIncludingNegativeDisplacementAndWrap()
    {
        var data = Data256();
        uint Dw(int off) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
        Assert.Equal(Dw(0x10), Run(a => { a.mov(eax, __dword_ptr[DataVa + 0x10]); a.ret(); }, data: data));
        Assert.Equal(Dw(0x14), Run(a => { a.mov(ebx, 0x10); a.mov(ecx, 4); a.mov(eax, __dword_ptr[ebx + ecx * 2 + (int)DataVa - 4]); a.ret(); }, data: data));
        // [ebx*4 + disp32]
        Assert.Equal(Dw(0x20), Run(a => { a.mov(ebx, 8); a.mov(eax, __dword_ptr[ebx * 4 + (int)DataVa]); a.ret(); }, data: data));
        // negative displacement relative to ebp
        Assert.Equal(0xCAFEu, Run(a => { a.mov(ebp, esp); a.push(0xCAFE); a.mov(eax, __dword_ptr[ebp - 4]); a.add(esp, 4); a.ret(); }));
        // IA-32 address wrap: ecx + disp wraps modulo 2^32 and hits the data section
        Assert.Equal(Dw(0), Run(a => { a.mov(ecx, 0xFFFFFFFF); a.mov(eax, __dword_ptr[ecx + (int)(DataVa + 1)]); a.ret(); }, data: data));
        Assert.Equal(Dw(4), Run(a => { a.mov(ecx, 0xFFFFFFF0); a.mov(edx, 0x20); a.mov(eax, __dword_ptr[ecx + edx * 1 + (int)(DataVa - 0xC)]); a.ret(); }, data: data));
        // lea wraps too
        Assert.Equal(0x10u, Run(a => { a.mov(ecx, 0xFFFFFFF0); a.lea(eax, __[ecx + 0x20]); a.ret(); }));
        // stores through absolute and indexed operands
        Run(a => { a.mov(eax, 0x01020304); a.mov(__dword_ptr[DataVa + 0x40], eax); a.mov(ebx, 2); a.mov(__byte_ptr[ebx * 4 + (int)DataVa + 0x50], al); a.ret(); },
            data: data, inspect: m =>
            {
                Assert.Equal(0x01020304u, m.Memory.ReadUInt32(DataVa + 0x40));
                Assert.Equal(0x04u, m.Memory.ReadUInt32(DataVa + 0x58) & 0xFF);
            });
    }

    [Fact]
    public void PushPopCallRet()
    {
        Assert.Equal(0x12345678u + 7 * 9, Run(a =>
        {
            a.mov(eax, 9); a.push(eax); a.push(7); a.push(0x12345678); a.pop(ecx); a.pop(edx); a.pop(eax);
            a.imul(edx, eax); a.lea(eax, __[ecx + edx]); a.ret();
        }));
        Assert.Equal(1u, Run(a => { a.push(esp); a.pop(eax); a.cmp(eax, esp); a.mov(eax, 0); a.sete(al); a.ret(); }));
        Assert.Equal(unchecked((uint)-8), Run(a =>
        {
            a.mov(ebp, esp); a.lea(ecx, __[esp - 8]); a.push(ecx); a.pop(esp); a.mov(eax, esp); a.mov(esp, ebp); a.sub(eax, esp); a.ret();
        }));
        Assert.Equal(6u, Run(a => { a.push(5); a.push(6); a.pop(__dword_ptr[esp]); a.pop(eax); a.ret(); }));
        Run(a => { a.push(__dword_ptr[esp + 4]); a.pop(__dword_ptr[DataVa + 0x20]); a.mov(eax, 0); a.ret(); }, [0xABCD1234],
            inspect: m => Assert.Equal(0xABCD1234u, m.Memory.ReadUInt32(DataVa + 0x20)));
        // nested/recursive calls (factorial) and ret imm16
        Assert.Equal(720u, Run(a =>
        {
            var fact = a.CreateLabel(); var @base = a.CreateLabel();
            a.push(__dword_ptr[esp + 4]); a.call(fact); a.ret();
            a.Label(ref fact);
            a.mov(eax, __dword_ptr[esp + 4]); a.cmp(eax, 1); a.jbe(@base);
            a.dec(eax); a.push(eax); a.call(fact); a.imul(eax, __dword_ptr[esp + 4]); a.ret(4);
            a.Label(ref @base); a.mov(eax, 1); a.ret(4);
        }, [6]));
    }

    [Fact]
    public void PushadPopadPushfdPopfd()
    {
        Assert.Equal(0u, Run(a =>
        {
            a.mov(ebx, esp); a.pushad(); a.popad(); a.mov(eax, __dword_ptr[esp - 20]); a.sub(eax, ebx); a.ret();
        }));
        Assert.Equal(1u + 2 + 3 + 4 + 5 + 6 + 7, Run(a =>
        {
            a.mov(eax, 1); a.mov(ecx, 2); a.mov(edx, 3); a.mov(ebx, 4); a.mov(ebp, 5); a.mov(esi, 6); a.mov(edi, 7);
            a.pushad(); a.xor(eax, eax); a.xor(ecx, ecx); a.xor(edx, edx); a.xor(ebx, ebx); a.xor(ebp, ebp); a.xor(esi, esi); a.xor(edi, edi);
            a.popad(); a.add(eax, ecx); a.add(eax, edx); a.add(eax, ebx); a.add(eax, ebp); a.add(eax, esi); a.add(eax, edi); a.ret();
        }));
        Assert.Equal(0xFFFFFFFFu, Run(a => { a.stc(); a.pushfd(); a.clc(); a.popfd(); a.sbb(eax, eax); a.ret(); }));
        Assert.Equal(3u, Run(a => { a.stc(); a.pushfd(); a.pop(eax); a.and(eax, 3); a.ret(); }));
    }

    [Fact]
    public void EnterAndLeave()
    {
        Assert.Equal(16u, Run(a => { a.enter(16, 0); a.mov(eax, ebp); a.sub(eax, esp); a.leave(); a.ret(); }));
        Assert.Equal(12u, Run(a => { a.mov(ebp, esp); a.enter(8, 1); a.mov(eax, ebp); a.sub(eax, esp); a.leave(); a.ret(); }));
        Assert.Equal(12u, Run(a => { a.mov(ebp, esp); a.enter(4, 2); a.mov(eax, ebp); a.sub(eax, esp); a.leave(); a.ret(); }));
        // enter 0,0 / leave round trip preserves ebp and the stack
        Assert.Equal(0x1234u, Run(a => { a.mov(ebp, 0x1234); a.enter(32, 0); a.leave(); a.mov(eax, ebp); a.ret(); }));
    }

    [Fact]
    public void LoopsAndJecxz()
    {
        foreach (var n in new uint[] { 1, 2, 10, 100 })
            Assert.Equal(n * (n + 1) / 2, Run(a =>
            {
                var l = a.CreateLabel();
                a.mov(ecx, __dword_ptr[esp + 4]); a.xor(eax, eax); a.Label(ref l); a.add(eax, ecx); a.loop(l); a.ret();
            }, [n]));
        void J(Assembler a)
        {
            var z = a.CreateLabel();
            a.mov(ecx, __dword_ptr[esp + 4]); a.jecxz(z); a.mov(eax, 1); a.ret(); a.Label(ref z); a.mov(eax, 2); a.ret();
        }
        Assert.Equal(2u, Run(J, [0])); Assert.Equal(1u, Run(J, [7]));
        Assert.Equal(32u, Run(a =>
        {
            var l = a.CreateLabel();
            a.mov(ecx, 5); a.xor(eax, eax); a.Label(ref l); a.inc(eax); a.cmp(eax, 3); a.loopne(l); a.imul(eax, eax, 10); a.add(eax, ecx); a.ret();
        }));
        Assert.Equal(5u, Run(a => { var l = a.CreateLabel(); a.mov(ecx, 5); a.xor(eax, eax); a.Label(ref l); a.inc(eax); a.cmp(eax, eax); a.loope(l); a.ret(); }));
    }

    [Fact]
    public void JumpTableSwitch()
    {
        // case bodies are only reachable through the table, so this proves jump table recovery
        var code = Asm(a =>
    {
            var def = a.CreateLabel(); var c0 = a.CreateLabel(); var c1 = a.CreateLabel(); var c2 = a.CreateLabel(); var c3 = a.CreateLabel();
            a.mov(eax, __dword_ptr[esp + 4]); a.cmp(eax, 3); a.ja(def);
            a.jmp(__dword_ptr[eax * 4 + (int)DataVa]);
            a.Label(ref c0); a.mov(eax, 100); a.ret();
            a.Label(ref c1); a.mov(eax, 101); a.ret();
            a.Label(ref c2); a.mov(eax, 102); a.ret();
            a.Label(ref c3); a.mov(eax, 103); a.ret();
            a.Label(ref def); a.mov(eax, 0xFFFFFFFF); a.ret();
        }, out var res);
        var data = new byte[0x100];
        var labels = new[] { 100, 101, 102, 103 };
        // recover the case label addresses by scanning for the mov eax, imm sequences
        var addrs = new List<uint>();
        for (var i = 0; i + 5 < code.Length; i++)
            if (code[i] == 0xB8 && labels.Contains(code[i + 1]) && code[i + 2] == 0 && code[i + 3] == 0 && code[i + 4] == 0) addrs.Add(CodeVa + (uint)i);
        Assert.Equal(4, addrs.Count);
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), addrs[i]);
        void Cfg(PeFixtureBuilder f) { for (var i = 0; i < 4; i++) f.Relocate(PeFixtureBuilder.DataRva + (uint)i * 4); }
        for (uint i = 0; i < 4; i++) Assert.Equal(100 + i, Run(code, [i], Cfg, data));
        Assert.Equal(0xFFFFFFFFu, Run(code, [9], Cfg, data));
        var built = Translate(code, OptimizationProfile.Release, Cfg, true, data);
        Assert.Contains(built.Artifact.Diagnostics, d => d.Code == "CFG_JUMP_TABLE");
        foreach (var addr in addrs) Assert.Contains(built.Artifact.AddressMap, e => e.GuestRva == addr - PeFixtureBuilder.ImageBase);
    }

    [Fact]
    public void IndirectCallsThroughRegisterAndPointer()
    {
        var helper = Asm(a => { a.mov(eax, __dword_ptr[esp + 4]); a.add(eax, 1000); a.ret(); });
        var wrapper = Asm(a =>
        {
            a.push(__dword_ptr[esp + 4]); a.call(__dword_ptr[DataVa + 0x10]); a.add(esp, 4);   // call through a relocated pointer
            a.push(eax); a.mov(ecx, __dword_ptr[DataVa + 0x10]); a.call(ecx); a.add(esp, 4); a.ret(); // call through a register
        });
        var full = wrapper.Concat(new byte[16 - wrapper.Length % 16]).ToArray();
        var helperRva = (uint)full.Length;
        full = full.Concat(helper).ToArray();
        var data = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10), CodeVa + helperRva);
        Assert.Equal(7u + 2000, Run(full, [7], f => f.Relocate(PeFixtureBuilder.DataRva + 0x10), data));
    }

    [Fact]
    public void StringOperations()
    {
        var data = Data256();
        Run(a =>
        {
            a.cld(); a.mov(esi, DataVa); a.mov(edi, DataVa + 0x40); a.mov(ecx, 4); a.rep.movsd();
            a.mov(edi, DataVa + 0x80); a.mov(ecx, 8); a.mov(eax, 0xAB); a.rep.stosb();
            a.mov(eax, edi); a.ret();
        }, data: data, inspect: m =>
        {
            for (var i = 0; i < 16; i++) Assert.Equal((uint)i, m.Memory.ReadUInt32(DataVa + 0x40 + (uint)i) & 0xFF);
            for (var i = 0; i < 8; i++) Assert.Equal(0xABu, m.Memory.ReadUInt32(DataVa + 0x80 + (uint)i) & 0xFF);
        });
        Assert.Equal(DataVa + 0x88, Run(a => { a.mov(edi, DataVa + 0x80); a.mov(ecx, 8); a.mov(eax, 0xAB); a.rep.stosb(); a.mov(eax, edi); a.ret(); }, data: data));
        // zero count is a no-op; std copies backwards; repne scasb finds a byte
        Assert.Equal(DataVa, Run(a => { a.mov(edi, DataVa); a.xor(ecx, ecx); a.rep.stosb(); a.mov(eax, edi); a.ret(); }, data: data));
        Run(a =>
        {
            a.std(); a.mov(esi, DataVa + 0x0C); a.mov(edi, DataVa + 0x4C); a.mov(ecx, 4); a.rep.movsd(); a.cld(); a.mov(eax, edi); a.ret();
        }, data: data, inspect: m => Assert.Equal(0x0F0E0D0Cu, m.Memory.ReadUInt32(DataVa + 0x4C)));
        Assert.Equal(DataVa + 0x21, Run(a =>
        {
            a.cld(); a.mov(edi, DataVa); a.mov(ecx, 0x100); a.mov(eax, 0x20); a.repne.scasb(); a.mov(eax, edi); a.ret();
        }, data: data));
    }

    [Fact]
    public void SetccCmovBitTestXchgCmpxchgLock()
    {
        foreach (var (x, y) in Pairs)
        {
            Assert.Equal(x < y ? x : y, Run(a => { a.mov(eax, __dword_ptr[esp + 4]); a.mov(ecx, __dword_ptr[esp + 8]); a.cmp(eax, ecx); a.cmova(eax, ecx); a.ret(); }, [x, y]));
            Assert.Equal((x >> (int)(y & 31)) & 1, Run(a => { a.mov(ecx, __dword_ptr[esp + 4]); a.mov(edx, __dword_ptr[esp + 8]); a.bt(ecx, edx); a.mov(eax, 0); a.setc(al); a.ret(); }, [x, y]));
            Assert.Equal(y, Run(a => { a.mov(eax, __dword_ptr[esp + 4]); a.mov(ecx, __dword_ptr[esp + 8]); a.xchg(eax, ecx); a.xchg(eax, ecx); a.xchg(eax, ecx); a.ret(); }, [x, y]));
        }
        Run(a =>
        {
            a.mov(__dword_ptr[DataVa], 5); a.mov(eax, 5); a.mov(ecx, 9); a.@lock.cmpxchg(__dword_ptr[DataVa], ecx);
            a.mov(eax, 3); a.@lock.xadd(__dword_ptr[DataVa], eax); a.xchg(__dword_ptr[DataVa + 4], eax); a.bswap(eax); a.ret();
        }, inspect: m => Assert.Equal(12u, m.Memory.ReadUInt32(DataVa)));
        Assert.Equal(0x78563412u, Run(a => { a.mov(eax, 0x12345678); a.bswap(eax); a.ret(); }));
        Assert.Equal(0xFFFFFF80u, Run(a => { a.mov(eax, 0x80); a.movsx(eax, al); a.ret(); }));
        Assert.Equal(0xFFFFFFFFu, Run(a => { a.mov(eax, 0xFFFF8000); a.cwde(); a.cdq(); a.mov(eax, edx); a.ret(); }));
        Assert.Equal(7u, Run(a => { a.mov(ecx, 0x80); a.bsf(eax, ecx); a.ret(); }));
        Assert.Equal(0xA5u, Run(a => { a.mov(eax, 0xA5A5A5A5); a.movzx(eax, al); a.ret(); }));
    }

    [Fact]
    public void SseAndX87PassThrough()
    {
        Assert.Equal(BitConverter.SingleToUInt32Bits(7.5f), Run(a =>
        {
            a.movss(xmm0, __dword_ptr[esp + 4]); a.addss(xmm0, __dword_ptr[esp + 8]); a.movd(eax, xmm0); a.ret();
        }, [BitConverter.SingleToUInt32Bits(3.25f), BitConverter.SingleToUInt32Bits(4.25f)]));
        Assert.Equal(36u, Run(a => { a.fild(__dword_ptr[esp + 4]); a.fmul(st0, st0); a.fistp(__dword_ptr[esp - 4]); a.mov(eax, __dword_ptr[esp - 4]); a.ret(); }, [6]));
    }

    [Fact]
    public void ImportCallsPreserveCalleeSavedRegistersAndStack()
    {
        var iat = 0u;
        void Cfg(PeFixtureBuilder f) => iat = f.Import("HOST.DLL", "hook");
        var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.Stdcall(x => x + 1));
        var code = Asm(a =>
    {
            var bad = a.CreateLabel();
            a.mov(esi, 0x11111111); a.mov(edi, 0x22222222); a.mov(ebx, 0x33333333); a.mov(ebp, esp);
            a.push(__dword_ptr[esp + 4]); a.call(__dword_ptr[PeFixtureBuilder.ImageBase + PeFixtureBuilder.IdataRva + 0x400]);
            a.cmp(ebp, esp); a.jne(bad);
            a.cmp(esi, 0x11111111); a.jne(bad); a.cmp(edi, 0x22222222); a.jne(bad); a.cmp(ebx, 0x33333333); a.jne(bad);
            a.ret();
            a.Label(ref bad); a.mov(eax, 0xDEAD); a.ret();
        });
        Assert.Equal(42u, Run(code, [41], Cfg, imports: resolver));
        Assert.Equal(PeFixtureBuilder.IdataRva + 0x400, iat);
    }

    [Fact]
    public void UntranslatedIndirectTargetTrapsInsteadOfCrashing()
    {
        var b = Translate(Asm(a => { a.mov(eax, 0x12345678); a.jmp(eax); }), OptimizationProfile.Release);
        Assert.DoesNotContain(b.Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        var fn = m.Bind("_f", GuestCallingConvention.Cdecl, 0);
        try { Assert.Equal(0u, fn.Call()); } catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { /* the loader may surface the trap as an exception */ }
    }
}
