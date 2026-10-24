using Iced.Intel;

namespace BlitzCS.BinaryCompiler.Unpack;

internal enum Transfer { None, Jump, Call, Ret }

/// <summary>
/// A deliberately small IA-32 integer interpreter (no FPU/SSE/segment tricks), used only by <see cref="ControlledUnpacker"/>. It decodes with Iced,
/// executes one instruction per step against <see cref="SandboxMemory"/> (managed arrays) and refuses every instruction it does not model with
/// UNPACK_UNSUPPORTED_INSTRUCTION. Deterministic: no clock, no randomness, no host memory is ever executable.
/// </summary>
internal sealed class IA32Emulator
{
    private readonly uint[] r = new uint[8]; // EAX ECX EDX EBX ESP EBP ESI EDI
    private bool cf, pf, af, zf, sf, of, df;
    private readonly SandboxMemory mem;
    private readonly Dictionary<uint, Instruction> cache = [];
    private readonly uint fsBase;

    public IA32Emulator(SandboxMemory memory, uint tebBase)
    {
        mem = memory;
        fsBase = tebBase;
        mem.OnDecodedPageWritten = cache.Clear;
    }

    public uint Eip;
    public uint LastEip;
    public Transfer LastTransfer;
    public long Steps;
    public uint Esp { get => r[4]; set => r[4] = value; }
    public uint Reg(int i) => r[i];
    public uint Eax { get => r[0]; set => r[0] = value; }

    public void Push(uint v) { r[4] -= 4; mem.Write(r[4], 4, v); }
    public uint Pop() { var v = mem.Read(r[4], 4); r[4] += 4; return v; }

    /// <summary>Decodes the instruction at <see cref="Eip"/> (cached until a write hits a decoded page).</summary>
    public Instruction Fetch()
    {
        if (cache.TryGetValue(Eip, out var hit)) return hit;
        var region = mem.Find(Eip) ?? throw new UnpackRefusalException("UNPACK_EXEC_UNMAPPED", $"Control reached unmapped address 0x{Eip:X8}.", Eip);
        var off = (int)(Eip - region.Base);
        var reader = new ByteArrayCodeReader(region.Data, off, Math.Min(15, region.Data.Length - off));
        var decoder = Decoder.Create(32, reader, Eip);
        var i = decoder.Decode();
        if (i.IsInvalid)
            throw new UnpackRefusalException("UNPACK_UNSUPPORTED_INSTRUCTION", $"Undecodable bytes at 0x{Eip:X8}: {Convert.ToHexString(region.Data, off, Math.Min(8, region.Data.Length - off))}.", Eip);
        region.Decoded[off >> 12] = true;
        if (((off + i.Length - 1) >> 12) != (off >> 12)) region.Decoded[(off + i.Length - 1) >> 12] = true;
        cache[Eip] = i;
        return i;
    }

    private UnpackRefusalException Unsupported(in Instruction i, string why = "instruction is not modelled") =>
        new("UNPACK_UNSUPPORTED_INSTRUCTION", $"At 0x{i.IP32:X8}: {i} ({why}).", i.IP32);

    // ---- registers ----
    private uint ReadReg(Register x, out int size)
    {
        if (x >= Register.EAX && x <= Register.EDI) { size = 4; return r[x - Register.EAX]; }
        if (x >= Register.AX && x <= Register.DI) { size = 2; return r[x - Register.AX] & 0xFFFF; }
        if (x >= Register.AL && x <= Register.BL) { size = 1; return r[x - Register.AL] & 0xFF; }
        if (x >= Register.AH && x <= Register.BH) { size = 1; return (r[x - Register.AH] >> 8) & 0xFF; }
        throw new UnpackRefusalException("UNPACK_UNSUPPORTED_INSTRUCTION", $"Register {x} is not modelled.");
    }

    private void WriteReg(Register x, uint v)
    {
        if (x >= Register.EAX && x <= Register.EDI) r[x - Register.EAX] = v;
        else if (x >= Register.AX && x <= Register.DI) { ref var d = ref r[x - Register.AX]; d = (d & 0xFFFF0000) | (v & 0xFFFF); }
        else if (x >= Register.AL && x <= Register.BL) { ref var d = ref r[x - Register.AL]; d = (d & 0xFFFFFF00) | (v & 0xFF); }
        else if (x >= Register.AH && x <= Register.BH) { ref var d = ref r[x - Register.AH]; d = (d & 0xFFFF00FF) | ((v & 0xFF) << 8); }
        else throw new UnpackRefusalException("UNPACK_UNSUPPORTED_INSTRUCTION", $"Register {x} is not modelled.");
    }

    private uint Ea(in Instruction i)
    {
        var seg = i.SegmentPrefix;
        if (seg == Register.GS) throw Unsupported(i, "gs: access");
        uint a = i.MemoryDisplacement32;
        if (i.MemoryBase != Register.None) a += ReadReg(i.MemoryBase, out _);
        if (i.MemoryIndex != Register.None) a += ReadReg(i.MemoryIndex, out _) * (uint)i.MemoryIndexScale;
        if (seg == Register.FS) a += fsBase;
        return a;
    }

    private static int MemSize(in Instruction i) => i.MemorySize switch
    {
        MemorySize.UInt8 or MemorySize.Int8 => 1,
        MemorySize.UInt16 or MemorySize.Int16 => 2,
        MemorySize.UInt32 or MemorySize.Int32 or MemorySize.Unknown => 4,
        MemorySize.Packed32_UInt8 => 4,
        _ => 4,
    };

    private int OpSize(in Instruction i, int op)
    {
        switch (i.GetOpKind(op))
        {
            case OpKind.Register: ReadReg(i.GetOpRegister(op), out var s); return s;
            case OpKind.Memory: return MemSize(i);
            default: return 4;
        }
    }

    private static uint Mask(int size) => size == 1 ? 0xFFu : size == 2 ? 0xFFFFu : 0xFFFFFFFFu;
    private static uint Sign(int size) => size == 1 ? 0x80u : size == 2 ? 0x8000u : 0x80000000u;
    private static uint Sx(uint v, int size) => size == 1 ? (uint)(sbyte)v : size == 2 ? (uint)(short)v : v;

    private uint Get(in Instruction i, int op, int size)
    {
        switch (i.GetOpKind(op))
        {
            case OpKind.Register: return ReadReg(i.GetOpRegister(op), out _);
            case OpKind.Memory: return mem.Read(Ea(i), size);
            case OpKind.Immediate8: case OpKind.Immediate8to16: case OpKind.Immediate8to32:
            case OpKind.Immediate16: case OpKind.Immediate32:
                return (uint)i.GetImmediate(op) & Mask(size);
            default: throw Unsupported(i, "operand kind " + i.GetOpKind(op));
        }
    }

    private void Set(in Instruction i, int op, int size, uint v)
    {
        switch (i.GetOpKind(op))
        {
            case OpKind.Register: WriteReg(i.GetOpRegister(op), v & Mask(size)); break;
            case OpKind.Memory: mem.Write(Ea(i), size, v & Mask(size)); break;
            default: throw Unsupported(i, "destination operand kind");
        }
    }

    // ---- flags ----
    private void Szp(uint v, int size)
    {
        zf = v == 0; sf = (v & Sign(size)) != 0;
        pf = System.Numerics.BitOperations.PopCount(v & 0xFF) % 2 == 0;
    }

    private uint Add(uint a, uint b, uint c, int size)
    {
        var full = (ulong)a + b + c; var res = (uint)full & Mask(size);
        cf = full > Mask(size); of = ((a ^ res) & (b ^ res) & Sign(size)) != 0; af = ((a ^ b ^ res) & 0x10) != 0; Szp(res, size);
        return res;
    }

    private uint Sub(uint a, uint b, uint c, int size)
    {
        var res = (a - b - c) & Mask(size);
        cf = (ulong)a < (ulong)b + c; of = ((a ^ b) & (a ^ res) & Sign(size)) != 0; af = ((a ^ b ^ res) & 0x10) != 0; Szp(res, size);
        return res;
    }

    private uint Logic(uint res, int size) { cf = of = af = false; Szp(res, size); return res; }

    private bool Cond(ConditionCode cc) => cc switch
    {
        ConditionCode.o => of, ConditionCode.no => !of, ConditionCode.b => cf, ConditionCode.ae => !cf,
        ConditionCode.e => zf, ConditionCode.ne => !zf, ConditionCode.be => cf || zf, ConditionCode.a => !cf && !zf,
        ConditionCode.s => sf, ConditionCode.ns => !sf, ConditionCode.p => pf, ConditionCode.np => !pf,
        ConditionCode.l => sf != of, ConditionCode.ge => sf == of, ConditionCode.le => zf || sf != of, ConditionCode.g => !zf && sf == of,
        _ => throw new UnpackRefusalException("UNPACK_UNSUPPORTED_INSTRUCTION", "Unknown condition code."),
    };

    private uint PackFlags() =>
        (cf ? 1u : 0) | 2u | (pf ? 4u : 0) | (af ? 0x10u : 0) | (zf ? 0x40u : 0) | (sf ? 0x80u : 0) | 0x200u | (df ? 0x400u : 0) | (of ? 0x800u : 0);

    private void UnpackFlags(uint v)
    {
        cf = (v & 1) != 0; pf = (v & 4) != 0; af = (v & 0x10) != 0; zf = (v & 0x40) != 0; sf = (v & 0x80) != 0; df = (v & 0x400) != 0; of = (v & 0x800) != 0;
    }

    private uint Shift(Mnemonic m, uint a, int count, int size)
    {
        var bits = size * 8;
        count &= 31;
        if (count == 0) return a;
        uint res;
        switch (m)
        {
            case Mnemonic.Shl: case Mnemonic.Sal:
                res = (uint)((ulong)a << count) & Mask(size);
                cf = ((((ulong)a << count) >> bits) & 1) != 0; of = ((res & Sign(size)) != 0) ^ cf; Szp(res, size); return res;
            case Mnemonic.Shr:
                res = count >= 32 ? 0 : a >> count;
                cf = ((a >> (count - 1)) & 1) != 0; of = (a & Sign(size)) != 0; Szp(res, size); return res;
            case Mnemonic.Sar:
            {
                var sa = (int)Sx(a, size);
                res = (uint)(sa >> count) & Mask(size);
                cf = ((sa >> (count - 1)) & 1) != 0; of = false; Szp(res, size); return res;
            }
            case Mnemonic.Rol:
            {
                var n = count % bits; res = n == 0 ? a : ((a << n) | (a >> (bits - n))) & Mask(size);
                cf = (res & 1) != 0; of = ((res & Sign(size)) != 0) ^ cf; return res;
            }
            case Mnemonic.Ror:
            {
                var n = count % bits; res = n == 0 ? a : ((a >> n) | (a << (bits - n))) & Mask(size);
                cf = (res & Sign(size)) != 0; of = ((res ^ (res << 1)) & Sign(size)) != 0; return res;
            }
            case Mnemonic.Rcl:
                res = a;
                for (var k = 0; k < count % (bits + 1); k++) { var c = (res & Sign(size)) != 0; res = ((res << 1) | (cf ? 1u : 0)) & Mask(size); cf = c; }
                of = ((res & Sign(size)) != 0) ^ cf; return res;
            case Mnemonic.Rcr:
                res = a;
                for (var k = 0; k < count % (bits + 1); k++) { var c = (res & 1) != 0; res = (res >> 1) | (cf ? Sign(size) : 0); cf = c; }
                of = ((res ^ (res << 1)) & Sign(size)) != 0; return res;
            default: throw new UnpackRefusalException("UNPACK_UNSUPPORTED_INSTRUCTION", $"Shift {m} is not modelled.");
        }
    }

    /// <summary>Executes the instruction already fetched at <see cref="Eip"/>. Control-flow targets are applied to <see cref="Eip"/>.</summary>
    public void Execute(in Instruction i)
    {
        Steps++;
        LastEip = Eip;
        LastTransfer = Transfer.None;
        var next = i.NextIP32;
        Eip = next;
        var m = i.Mnemonic;
        var cc = i.ConditionCode;
        if (cc != ConditionCode.None)
        {
            if (i.FlowControl == FlowControl.ConditionalBranch) { if (Cond(cc)) { Eip = i.NearBranch32; LastTransfer = Transfer.Jump; } return; }
            if (i.OpCount == 1) { Set(i, 0, 1, Cond(cc) ? 1u : 0u); return; }
            if (Cond(cc)) Set(i, 0, OpSize(i, 0), Get(i, 1, OpSize(i, 0)));
            return;
        }
        int sz;
        switch (m)
        {
            case Mnemonic.Nop: case Mnemonic.Pause: return;
            case Mnemonic.Mov:
                if (i.Op0Kind == OpKind.Register && i.Op0Register >= Register.ES && i.Op0Register <= Register.GS) throw Unsupported(i, "segment register write");
                sz = OpSize(i, 0); Set(i, 0, sz, Get(i, 1, sz)); return;
            case Mnemonic.Movzx: { var s1 = OpSize(i, 1); Set(i, 0, OpSize(i, 0), Get(i, 1, s1) & Mask(s1)); return; }
            case Mnemonic.Movsx: { var s1 = OpSize(i, 1); Set(i, 0, OpSize(i, 0), Sx(Get(i, 1, s1), s1)); return; }
            case Mnemonic.Lea: WriteReg(i.Op0Register, i.MemoryBase == Register.None && i.MemoryIndex == Register.None ? i.MemoryDisplacement32 : EaNoSeg(i)); return;
            case Mnemonic.Push:
            {
                uint v;
                if (i.Op0Kind == OpKind.Register) { v = ReadReg(i.Op0Register, out var psz); if (psz != 4) throw Unsupported(i, "16-bit push"); }
                else if (i.Op0Kind == OpKind.Memory) v = mem.Read(Ea(i), 4);
                else v = (uint)i.GetImmediate(0);
                Push(v);
                return;
            }
            case Mnemonic.Pop:
            {
                var v = Pop();
                if (i.Op0Kind == OpKind.Register) WriteReg(i.Op0Register, v); else mem.Write(Ea(i), 4, v);
                return;
            }
            case Mnemonic.Pushad:
            {
                var t = r[4];
                for (var k = 0; k < 8; k++) Push(k == 4 ? t : r[k]);
                return;
            }
            case Mnemonic.Popad:
                for (var k = 7; k >= 0; k--) { var v = Pop(); if (k != 4) r[k] = v; }
                return;
            case Mnemonic.Pushfd: Push(PackFlags()); return;
            case Mnemonic.Popfd: UnpackFlags(Pop()); return;
            case Mnemonic.Leave: r[4] = r[5]; r[5] = Pop(); return;
            case Mnemonic.Add: sz = OpSize(i, 0); Set(i, 0, sz, Add(Get(i, 0, sz), Get(i, 1, sz), 0, sz)); return;
            case Mnemonic.Adc: sz = OpSize(i, 0); Set(i, 0, sz, Add(Get(i, 0, sz), Get(i, 1, sz), cf ? 1u : 0, sz)); return;
            case Mnemonic.Sub: sz = OpSize(i, 0); Set(i, 0, sz, Sub(Get(i, 0, sz), Get(i, 1, sz), 0, sz)); return;
            case Mnemonic.Sbb: sz = OpSize(i, 0); Set(i, 0, sz, Sub(Get(i, 0, sz), Get(i, 1, sz), cf ? 1u : 0, sz)); return;
            case Mnemonic.Cmp: sz = OpSize(i, 0); Sub(Get(i, 0, sz), Get(i, 1, sz), 0, sz); return;
            case Mnemonic.And: sz = OpSize(i, 0); Set(i, 0, sz, Logic(Get(i, 0, sz) & Get(i, 1, sz), sz)); return;
            case Mnemonic.Or: sz = OpSize(i, 0); Set(i, 0, sz, Logic(Get(i, 0, sz) | Get(i, 1, sz), sz)); return;
            case Mnemonic.Xor: sz = OpSize(i, 0); Set(i, 0, sz, Logic(Get(i, 0, sz) ^ Get(i, 1, sz), sz)); return;
            case Mnemonic.Test: sz = OpSize(i, 0); Logic(Get(i, 0, sz) & Get(i, 1, sz), sz); return;
            case Mnemonic.Inc: { sz = OpSize(i, 0); var c = cf; Set(i, 0, sz, Add(Get(i, 0, sz), 1, 0, sz)); cf = c; return; }
            case Mnemonic.Dec: { sz = OpSize(i, 0); var c = cf; Set(i, 0, sz, Sub(Get(i, 0, sz), 1, 0, sz)); cf = c; return; }
            case Mnemonic.Neg: { sz = OpSize(i, 0); var a = Get(i, 0, sz); var res = Sub(0, a, 0, sz); cf = a != 0; Set(i, 0, sz, res); return; }
            case Mnemonic.Not: sz = OpSize(i, 0); Set(i, 0, sz, ~Get(i, 0, sz)); return;
            case Mnemonic.Shl: case Mnemonic.Sal: case Mnemonic.Shr: case Mnemonic.Sar: case Mnemonic.Rol: case Mnemonic.Ror: case Mnemonic.Rcl: case Mnemonic.Rcr:
            {
                sz = OpSize(i, 0);
                var count = i.Op1Kind == OpKind.Register ? (int)(r[1] & 0xFF) : i.OpCount == 1 ? 1 : (int)i.GetImmediate(1);
                Set(i, 0, sz, Shift(m, Get(i, 0, sz), count, sz));
                return;
            }
            case Mnemonic.Shld: case Mnemonic.Shrd:
            {
                sz = OpSize(i, 0); if (sz != 4) throw Unsupported(i, "16-bit double shift");
                var count = (i.Op2Kind == OpKind.Register ? (int)(r[1] & 0xFF) : (int)i.GetImmediate(2)) & 31;
                if (count == 0) return;
                ulong a = Get(i, 0, 4), b = Get(i, 1, 4);
                uint res;
                if (m == Mnemonic.Shld) { res = (uint)(((a << 32 | b) << count) >> 32); cf = ((a >> (32 - count)) & 1) != 0; }
                else { res = (uint)((b << 32 | a) >> count); cf = ((a >> (count - 1)) & 1) != 0; }
                of = ((res ^ (uint)a) & 0x80000000) != 0; Szp(res, 4); Set(i, 0, 4, res);
                return;
            }
            case Mnemonic.Imul:
            {
                sz = OpSize(i, 0);
                if (i.OpCount == 1)
                {
                    if (sz != 4) throw Unsupported(i, "8/16-bit imul");
                    var p = (long)(int)r[0] * (int)Get(i, 0, 4); r[0] = (uint)p; r[2] = (uint)(p >> 32); cf = of = p != (int)p; return;
                }
                var a = i.OpCount == 3 ? Get(i, 1, sz) : Get(i, 0, sz);
                var b = i.OpCount == 3 ? Get(i, 2, sz) : Get(i, 1, sz);
                var full = (long)(int)Sx(a, sz) * (int)Sx(b, sz);
                var res = (uint)full & Mask(sz);
                cf = of = full != (long)(int)Sx(res, sz); Szp(res, sz);
                Set(i, 0, sz, res);
                return;
            }
            case Mnemonic.Mul:
            {
                if (OpSize(i, 0) != 4) throw Unsupported(i, "8/16-bit mul");
                var p = (ulong)r[0] * Get(i, 0, 4); r[0] = (uint)p; r[2] = (uint)(p >> 32); cf = of = r[2] != 0; return;
            }
            case Mnemonic.Div:
            {
                if (OpSize(i, 0) != 4) throw Unsupported(i, "8/16-bit div");
                var d = Get(i, 0, 4);
                var n = ((ulong)r[2] << 32) | r[0];
                if (d == 0 || n / d > uint.MaxValue) throw new UnpackRefusalException("UNPACK_CPU_EXCEPTION", $"Divide error at 0x{i.IP32:X8}.", i.IP32);
                r[0] = (uint)(n / d); r[2] = (uint)(n % d); return;
            }
            case Mnemonic.Idiv:
            {
                if (OpSize(i, 0) != 4) throw Unsupported(i, "8/16-bit idiv");
                var d = (int)Get(i, 0, 4);
                var n = ((long)(int)r[2] << 32) | r[0];
                if (d == 0 || n / d != (int)(n / d)) throw new UnpackRefusalException("UNPACK_CPU_EXCEPTION", $"Divide error at 0x{i.IP32:X8}.", i.IP32);
                r[0] = (uint)(int)(n / d); r[2] = (uint)(int)(n % d); return;
            }
            case Mnemonic.Xchg:
            {
                sz = OpSize(i, 0); var a = Get(i, 0, sz); var b = Get(i, 1, sz); Set(i, 0, sz, b); Set(i, 1, sz, a); return;
            }
            case Mnemonic.Cdq: r[2] = (r[0] & 0x80000000) != 0 ? 0xFFFFFFFF : 0; return;
            case Mnemonic.Cwde: r[0] = (uint)(short)r[0]; return;
            case Mnemonic.Bswap: { var x = i.Op0Register - Register.EAX; r[x] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(r[x]); return; }
            case Mnemonic.Cld: df = false; return;
            case Mnemonic.Std: df = true; return;
            case Mnemonic.Clc: cf = false; return;
            case Mnemonic.Stc: cf = true; return;
            case Mnemonic.Cmc: cf = !cf; return;
            case Mnemonic.Sahf: { var ah = (r[0] >> 8) & 0xFF; cf = (ah & 1) != 0; pf = (ah & 4) != 0; af = (ah & 0x10) != 0; zf = (ah & 0x40) != 0; sf = (ah & 0x80) != 0; return; }
            case Mnemonic.Lahf: r[0] = (r[0] & 0xFFFF00FF) | ((PackFlags() & 0xFF) << 8); return;
            case Mnemonic.Bt: case Mnemonic.Bts: case Mnemonic.Btr: case Mnemonic.Btc:
            {
                if (i.Op0Kind != OpKind.Register) throw Unsupported(i, "bit test on memory");
                sz = OpSize(i, 0);
                var bit = Get(i, 1, sz) % (uint)(sz * 8); var v = Get(i, 0, sz);
                cf = ((v >> (int)bit) & 1) != 0;
                if (m == Mnemonic.Bts) v |= 1u << (int)bit; else if (m == Mnemonic.Btr) v &= ~(1u << (int)bit); else if (m == Mnemonic.Btc) v ^= 1u << (int)bit;
                if (m != Mnemonic.Bt) Set(i, 0, sz, v);
                return;
            }
            case Mnemonic.Bsf: case Mnemonic.Bsr:
            {
                sz = OpSize(i, 0); var v = Get(i, 1, sz);
                zf = v == 0;
                if (v != 0) Set(i, 0, sz, m == Mnemonic.Bsf ? (uint)System.Numerics.BitOperations.TrailingZeroCount(v) : (uint)System.Numerics.BitOperations.Log2(v));
                return;
            }
            case Mnemonic.Jmp: Eip = Target(i, next); LastTransfer = Transfer.Jump; return;
            case Mnemonic.Call: { var t = Target(i, next); Push(next); Eip = t; LastTransfer = Transfer.Call; return; }
            case Mnemonic.Ret:
                Eip = Pop();
                if (i.OpCount == 1) r[4] += i.Immediate16;
                LastTransfer = Transfer.Ret; return;
            case Mnemonic.Jecxz: if (r[1] == 0) { Eip = i.NearBranch32; LastTransfer = Transfer.Jump; } return;
            case Mnemonic.Loop: r[1]--; if (r[1] != 0) { Eip = i.NearBranch32; LastTransfer = Transfer.Jump; } return;
            case Mnemonic.Loope: r[1]--; if (r[1] != 0 && zf) { Eip = i.NearBranch32; LastTransfer = Transfer.Jump; } return;
            case Mnemonic.Loopne: r[1]--; if (r[1] != 0 && !zf) { Eip = i.NearBranch32; LastTransfer = Transfer.Jump; } return;
            case Mnemonic.Movsb: case Mnemonic.Movsw: case Mnemonic.Stosb: case Mnemonic.Stosw: case Mnemonic.Stosd: case Mnemonic.Lodsb: case Mnemonic.Lodsw: case Mnemonic.Lodsd:
            case Mnemonic.Scasb: case Mnemonic.Scasw: case Mnemonic.Scasd: case Mnemonic.Cmpsb: case Mnemonic.Cmpsw:
                StringOp(i, m); return;
            case Mnemonic.Movsd when i.Code == Code.Movsd_m32_m32: StringOp(i, m); return;
            case Mnemonic.Cmpsd when i.Code == Code.Cmpsd_m32_m32: StringOp(i, m); return;
            case Mnemonic.Int3: case Mnemonic.Int: case Mnemonic.Int1: case Mnemonic.Into:
                throw new UnpackRefusalException("UNPACK_ANTI_DEBUG", $"Software interrupt at 0x{i.IP32:X8} ({i}); exception-driven control flow and debugger traps are refused.", i.IP32);
            case Mnemonic.Rdtsc: case Mnemonic.Rdtscp: case Mnemonic.Cpuid:
                throw new UnpackRefusalException("UNPACK_ANTI_DEBUG", $"Timing/identification instruction at 0x{i.IP32:X8} ({i}); the sandbox is deterministic and does not answer it.", i.IP32);
            default: throw Unsupported(i);
        }
    }

    private uint EaNoSeg(in Instruction i)
    {
        uint a = i.MemoryDisplacement32;
        if (i.MemoryBase != Register.None) a += ReadReg(i.MemoryBase, out _);
        if (i.MemoryIndex != Register.None) a += ReadReg(i.MemoryIndex, out _) * (uint)i.MemoryIndexScale;
        return a;
    }

    private uint Target(in Instruction i, uint next) => i.Op0Kind switch
    {
        OpKind.NearBranch32 => i.NearBranch32,
        OpKind.Register => ReadReg(i.Op0Register, out _),
        OpKind.Memory => mem.Read(Ea(i), 4),
        _ => throw Unsupported(i, "branch operand"),
    };

    private void StringOp(in Instruction i, Mnemonic m)
    {
        var name = m.ToString();
        var sz = name.EndsWith('b') ? 1 : name.EndsWith('w') ? 2 : 4;
        var repe = i.HasRepePrefix || i.HasRepPrefix; var repne = i.HasRepnePrefix;
        var rep = repe || repne;
        var step = df ? (uint)-sz : (uint)sz;
        var cmpLike = m is Mnemonic.Scasb or Mnemonic.Scasw or Mnemonic.Scasd or Mnemonic.Cmpsb or Mnemonic.Cmpsw or Mnemonic.Cmpsd;
        do
        {
            if (rep && r[1] == 0) break;
            switch (m)
            {
                case Mnemonic.Movsb: case Mnemonic.Movsw: case Mnemonic.Movsd: mem.Write(r[7], sz, mem.Read(r[6], sz)); r[6] += step; r[7] += step; break;
                case Mnemonic.Stosb: case Mnemonic.Stosw: case Mnemonic.Stosd: mem.Write(r[7], sz, sz == 1 ? r[0] & 0xFF : sz == 2 ? r[0] & 0xFFFF : r[0]); r[7] += step; break;
                case Mnemonic.Lodsb: case Mnemonic.Lodsw: case Mnemonic.Lodsd: { var v = mem.Read(r[6], sz); if (sz == 1) r[0] = (r[0] & 0xFFFFFF00) | v; else if (sz == 2) r[0] = (r[0] & 0xFFFF0000) | v; else r[0] = v; r[6] += step; break; }
                case Mnemonic.Scasb: case Mnemonic.Scasw: case Mnemonic.Scasd: Sub(r[0] & Mask(sz), mem.Read(r[7], sz), 0, sz); r[7] += step; break;
                default: Sub(mem.Read(r[6], sz), mem.Read(r[7], sz), 0, sz); r[6] += step; r[7] += step; break;
            }
            if (rep)
            {
                r[1]--; Steps++;
                if (cmpLike && ((repe && !zf) || (repne && zf))) break;
            }
        } while (rep);
    }
}
