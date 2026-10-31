using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>
/// Segment registers, <c>int 29h</c> and VEX. The selectors a read returns depend on the target (see <see cref="Selector"/>):
/// windows-x64 uses the documented WoW64 selectors (cs 0x23, ss/ds/es/gs 0x2B, fs 0x53). The PE32 oracle under Wine reads fs 0x63 and gs 0x6B (Wine's own GDT), so fs and gs are NOT validated against real Windows hardware;
/// linux-sysv matches the static ELF32 oracle (cs 0x23, ss/ds/es 0x2B, fs and gs 0). A real 32-bit Linux process with glibc TLS can have a
/// non-zero gs; the linux-sysv target keeps the values the oracle reports. Reads (<c>mov r/m16, sreg</c>, <c>push sreg</c>) store those constants; loads
/// (<c>pop ds/es</c>, <c>mov ds/es, r/m16</c>) accept a value equal to the current selector and otherwise raise a named deferred fault (trap kind 5);
/// every other load (<c>pop ss/fs/gs</c>, <c>mov ss/fs/gs</c>, lds/les/lss/lfs/lgs) is a deferred fault, as are far calls (build-time error, unchanged).
/// <c>int 29h</c> (__fastfail) stores ECX in GuestContext[8] and raises trap kind 4. Trap kinds 4 and 5 are emitted only when used, so images
/// without these instructions keep byte-identical code.
/// </summary>
internal sealed partial class X64CodeGen
{
    public const uint SelCs = 0x23, SelData = 0x2B;
    /// <summary>
    /// fs/gs selectors. windows-x64 uses the documented WoW64 values (fs 0x53, gs 0x2B); linux-sysv reads 0 for both (matches the ELF32 oracle).
    /// NOT validated against real Windows hardware: the PE32 oracle under Wine reads fs 0x63 and gs 0x6B instead (see BlitzIsaDifferentialTests).
    /// </summary>
    public const uint SelFsWin64 = 0x53, SelFsSysV = 0, SelGsSysV = 0;
    public const int CtxFastFailCode = 8;

    private ulong trap4Label, trap5Label;

    /// <summary>The selector a read of <paramref name="r"/> returns on this target. Loads compare against the same value (<see cref="CheckSelectorOrFault"/>).</summary>
    private uint Selector(Register r) => r switch
    {
        Register.CS => SelCs,
        Register.FS => abi == HostAbi.Win64 ? SelFsWin64 : SelFsSysV,
        Register.GS => abi == HostAbi.Win64 ? SelData : SelGsSysV,
        _ => SelData,
    };

    private void DeferredFault(GuestInstruction g, string reason)
    {
        Diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "BC_DEFERRED_SEGMENT_FAULT",
            $"Instruction '{g.Instr.Mnemonic.ToString().ToLowerInvariant()}' is not modelled: {reason}. It raises a named run-time fault if it is ever executed.", g.Rva));
        A(Instruction.Create(Code.Mov_r32_imm32, Register.R9D, Va(g.Rva)));
        Jmp(trap5Label != 0 ? trap5Label : trap5Label = E.NewLabel());
    }

    /// <summary>Handles segment-register instructions and int 29h. Returns true when the instruction was handled (<paramref name="falls"/> says whether execution continues).</summary>
    private bool TryLowerSegmentOrFastFail(GuestInstruction g, MemoryOperand? fsOperand, out bool falls)
    {
        var i = g.Instr;
        falls = false;
        if (i.Mnemonic == Mnemonic.Int && i.OpCount == 1 && i.Immediate8 == 0x29)
        {
            A(Instruction.Create(Code.Mov_rm32_r32, Ctx(CtxFastFailCode), Register.ECX));
            A(Instruction.Create(Code.Mov_r32_imm32, Register.R9D, Va(g.Rva)));
            Jmp(trap4Label != 0 ? trap4Label : trap4Label = E.NewLabel());
            return true;
        }
        if (i.Mnemonic is Mnemonic.Lds or Mnemonic.Les or Mnemonic.Lss or Mnemonic.Lfs or Mnemonic.Lgs)
        {
            DeferredFault(g, "loading a far pointer would load a segment register");
            return true;
        }
        if (i.Mnemonic == Mnemonic.Push && i.Op0Kind == OpKind.Register && i.Op0Register.IsSegmentRegister())
        {
            var sel = Selector(i.Op0Register);
            if (i.Code.ToString().StartsWith("Pushw", StringComparison.Ordinal)) { SpAdd(-2); A(Instruction.Create(Code.Mov_rm16_imm16, M(Register.ESP), (int)sel)); }
            else { StoreImmAtSp(-4, sel); SpAdd(-4); }
            falls = true; return true;
        }
        if (i.Mnemonic == Mnemonic.Pop && i.Op0Kind == OpKind.Register && i.Op0Register.IsSegmentRegister())
        {
            var reg = i.Op0Register;
            var word = i.Code.ToString().StartsWith("Popw", StringComparison.Ordinal);
            if (reg is not (Register.DS or Register.ES)) { DeferredFault(g, $"pop {reg.ToString().ToLowerInvariant()} would load a segment register"); return true; }
            A(Instruction.Create(word ? Code.Movzx_r32_rm16 : Code.Mov_r32_rm32, Register.R8D, M(Register.ESP)));
            CheckSelectorOrFault(g, Selector(reg));
            SpAdd(word ? 2 : 4);
            falls = true; return true;
        }
        if (i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register.IsSegmentRegister())
        {
            var reg = i.Op0Register;
            if (reg is not (Register.DS or Register.ES)) { DeferredFault(g, $"mov {reg.ToString().ToLowerInvariant()}, ... would load a segment register"); return true; }
            if (i.Op1Kind == OpKind.Register)
            {
                var src = i.Op1Register;
                if (src.IsGPR32()) { A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, src)); A(Instruction.Create(Code.Movzx_r32_rm16, Register.R8D, Register.R8W)); }
                else A(Instruction.Create(Code.Movzx_r32_rm16, Register.R8D, src));
            }
            else
            {
                A(Instruction.Create(Code.Movzx_r32_rm16, Register.R8D, fsOperand ?? MemOf(i)));
            }
            CheckSelectorOrFault(g, Selector(reg));
            falls = true; return true;
        }
        if (i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op1Kind == OpKind.Register && i.Op1Register.IsSegmentRegister())
        {
            var sel = Selector(i.Op1Register);
            if (i.Op0Kind == OpKind.Register)
            {
                if (i.Op0Register.IsGPR32()) A(Instruction.Create(Code.Mov_r32_imm32, i.Op0Register, sel));
                else A(Instruction.Create(Code.Mov_r16_imm16, i.Op0Register, (ushort)sel));
            }
            else
            {
                var m = fsOperand ?? MemOf(i);
                A(Instruction.Create(Code.Mov_rm16_imm16, m, (int)sel));
            }
            falls = true; return true;
        }
        return false;
    }

    /// <summary>R8D holds a selector value: accept it when it equals the current one, else raise the deferred fault. EFLAGS are preserved.</summary>
    private void CheckSelectorOrFault(GuestInstruction g, uint current)
    {
        var ok = E.NewLabel();
        A(Instruction.Create(Code.Pushfq));
        A(Instruction.Create(Code.Cmp_rm32_imm32, Register.R8D, (int)current));
        Jcc(ConditionCode.e, ok);
        Diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "BC_DEFERRED_SEGMENT_FAULT",
            $"Instruction '{g.Instr.Mnemonic.ToString().ToLowerInvariant()}' loads a segment register; only the current flat selector 0x{current:X2} is accepted, any other value raises a named run-time fault.", g.Rva));
        A(Instruction.Create(Code.Mov_r32_imm32, Register.R9D, Va(g.Rva)));
        Jmp(trap5Label != 0 ? trap5Label : trap5Label = E.NewLabel());
        E.Place(ok);
        A(Instruction.Create(Code.Popfq));
    }

    /// <summary>Emits the trap routines for kinds 4 and 5 when some instruction used them.</summary>
    private void EmitLateTraps()
    {
        foreach (var (label, kind) in new[] { (trap4Label, 4u), (trap5Label, 5u) })
        {
            if (label == 0) continue;
            E.Place(label);
            A(Instruction.Create(Code.Mov_rm32_imm32, Ctx(CtxTrapKind), kind));
            A(Instruction.Create(Code.Mov_rm64_r64, Ctx(CtxTrapAddress), Register.R9));
            A(Instruction.Create(Code.Xor_r32_rm32, Register.EAX, Register.EAX));
            Jmp(ExitCommonLabel);
        }
    }

    private static readonly Register[] Ymm0To7 = [Register.YMM0, Register.YMM1, Register.YMM2, Register.YMM3, Register.YMM4, Register.YMM5, Register.YMM6, Register.YMM7];

    /// <summary>VEX pass-through checks. Returns an error text when the instruction cannot be passed through.</summary>
    private static string? VexRejection(Instruction i)
    {
        for (var op = 0; op < i.OpCount; op++)
            if (i.GetOpKind(op) == OpKind.Register)
            {
                var r = i.GetOpRegister(op);
                if (r.IsYMM() && r.GetNumber() > 7 || r.IsXMM() && r.GetNumber() > 7) return "VEX register above ymm7";
            }
        return null;
    }
}
