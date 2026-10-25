using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>
/// Lowering of <c>fs:</c> memory operands onto the guest TEB (see docs/binary-teb-contract.md). The TEB base lives in R15 for the
/// whole guest call. Static offsets are validated against the modelled field table at translation time; run-time offsets can only address the
/// TLS slot array and are bounds-checked at run time, so an out-of-range offset traps (kind 3) instead of reading host memory.
/// </summary>
internal sealed partial class X64CodeGen
{
    public const uint TebSize = 0x1000, TlsSlotsOffset = 0xE10, TlsSlotsBytes = 0x100;

    /// <summary>Modelled TEB fields: start, byte length, writable by the guest.</summary>
    private static readonly (uint Start, uint Length, bool Writable, string Name)[] TebFields =
    [
        (0x00, 4, true, "NT_TIB.ExceptionList"), (0x04, 4, false, "StackBase"), (0x08, 4, false, "StackLimit"), (0x18, 4, false, "NT_TIB.Self"),
        (0x20, 4, false, "ClientId.UniqueProcess"), (0x24, 4, false, "ClientId.UniqueThread"), (0x2C, 4, true, "ThreadLocalStoragePointer"),
        (0x30, 4, false, "ProcessEnvironmentBlock"), (0x34, 4, true, "LastErrorValue"), (TlsSlotsOffset, TlsSlotsBytes, true, "TlsSlots[64]"),
    ];

    private readonly InstructionInfoFactory infoFactory = new();
    private readonly Dictionary<uint, ulong> fsTrapStubs = [];

    private void FsError(GuestInstruction g, string code, string reason)
        => Unsupported(g, code, reason + $" [{g.Instr}]");

    /// <summary>
    /// Validates the <c>fs:</c> operand of <paramref name="g"/> and emits the run-time offset check, if any. On success returns the host
    /// operand addressing the TEB; on failure an Error diagnostic and a trap have been emitted and false is returned.
    /// </summary>
    private bool TryLowerFsOperand(GuestInstruction g, out MemoryOperand operand)
    {
        operand = default;
        var i = g.Instr;
        UsedMemory? used = null;
        foreach (var m in infoFactory.GetInfo(i, InstructionInfoOptions.NoRegisterUsage).GetUsedMemory())
            if (m.Access != OpAccess.NoMemAccess) { used = m; break; }
        if (used is null) { FsError(g, "BC_FS_UNSUPPORTED_OFFSET", "the fs: operand has no analysable memory access"); return false; }
        var access = used.Value.Access;
        var size = (uint)used.Value.MemorySize.GetSize();
        if (size == 0) size = 4;
        var writes = access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;
        var disp = unchecked((uint)i.MemoryDisplacement64);

        if (i.MemoryBase == Register.None && i.MemoryIndex == Register.None)
        {
            foreach (var f in TebFields)
            {
                if (disp < f.Start || (ulong)disp + size > (ulong)f.Start + f.Length) continue;
                if (writes && !f.Writable)
                { FsError(g, "BC_FS_UNSUPPORTED_OFFSET", $"write to read-only TEB field fs:[0x{disp:X}] ({f.Name})"); return false; }
                operand = new MemoryOperand(Register.R15, disp, disp == 0 ? 0 : disp <= 127 ? 1 : 8); // 64-bit addressing: a 32-bit displacement is displSize 8 in Iced
                return true;
            }
            FsError(g, "BC_FS_UNSUPPORTED_OFFSET", $"fs:[0x{disp:X}] ({size}-byte {(writes ? "write" : "read")}) is outside the modelled TEB fields");
            return false;
        }

        if (i.MemoryBase is Register.EIP or Register.RIP || i.MemoryBase.IsGPR16() || i.MemoryIndex.IsGPR16())
        { FsError(g, "BC_FS_UNSUPPORTED_OFFSET", "unsupported addressing form for an fs: operand"); return false; }
        // r9d = (base + index*scale + disp - TlsSlotsOffset) in 32 bits; valid iff r9d <= TlsSlotsBytes - size (unsigned). The slot array is
        // addressed as [r15 + r9 + TlsSlotsOffset]. Flags are saved around the check only when the instruction consumes them.
        var d = unchecked((int)(disp - TlsSlotsOffset));
        var baseReg = i.MemoryBase;
        A(Instruction.Create(Code.Lea_r32_m, Register.R9D,
            new MemoryOperand(baseReg, i.MemoryIndex, i.MemoryIndexScale, d, baseReg == Register.None ? 4 : DispSize(baseReg, d))));
        var preserve = i.RflagsRead != RflagsBits.None;
        if (preserve) A(Instruction.Create(Code.Pushfq));
        A(Instruction.Create(Code.Cmp_rm32_imm32, Register.R9D, (int)(TlsSlotsBytes - size)));
        Jcc(ConditionCode.a, FsTrapStub(g));
        if (preserve) A(Instruction.Create(Code.Popfq));
        operand = new MemoryOperand(Register.R15, Register.R9, 1, TlsSlotsOffset, 8);
        return true;
    }

    private ulong FsTrapStub(GuestInstruction g)
    {
        var va = Va(g.Rva);
        if (!fsTrapStubs.TryGetValue(va, out var label)) fsTrapStubs[va] = label = E.NewLabel();
        return label;
    }

    private void EmitFsTrapStubs()
    {
        foreach (var (va, label) in fsTrapStubs.OrderBy(s => s.Key))
        {
            E.Place(label);
            A(Instruction.Create(Code.Mov_r32_imm32, Register.R9D, va));
            Jmp(Trap3Label);
        }
    }

    /// <summary>The memory operand of a push/pop/call/jmp/load-style lowering: the guest operand, or the TEB operand for an fs: access.</summary>
    private bool TryMemoryOperand(GuestInstruction g, out MemoryOperand operand)
    {
        if (g.Instr.SegmentPrefix == Register.FS) return TryLowerFsOperand(g, out operand);
        operand = MemOf(g.Instr);
        return true;
    }

    /// <summary>Rewrites the memory operand of <paramref name="pass"/> to the host TEB operand.</summary>
    private static void ApplyOperand(ref Instruction pass, MemoryOperand m)
    {
        pass.SegmentPrefix = Register.None;
        pass.MemoryBase = m.Base;
        pass.MemoryIndex = m.Index;
        pass.MemoryIndexScale = m.Scale;
        pass.MemoryDisplacement64 = (ulong)m.Displacement;
        pass.MemoryDisplSize = m.DisplSize;
    }
}
