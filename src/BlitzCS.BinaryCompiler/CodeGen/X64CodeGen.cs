using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>
/// Register conventions inside translated code: guest EAX..EDI map to RAX..RDI (upper halves always zero), guest ESP is RSP
/// (always zero-extended), R12 = host image base, R13 = dispatch table address, R8-R11 and XMM15 are translator scratch.
/// Translated code is bound to the image's preferred base (guest addresses are embedded as constants).
/// </summary>
internal sealed class X64CodeGen
{
    public const int CtxNativeRsp = 0, CtxTrapKind = 16, CtxTrapAddress = 24;

    private readonly PeImage image;
    private readonly ControlFlowGraph cfg;
    private readonly bool release;
    private readonly ulong dataBase;
    private readonly uint tableOffset, boundOffset, thunkOffset, tableEntries, exitRva, stubBaseRva;
    public readonly CodeEmitter E = new();
    public readonly List<BinaryDiagnostic> Diagnostics = [];

    public ulong EnterLabel, ExitCommonLabel, DispatchLabel, Trap1Label, Trap2Label;
    public readonly Dictionary<uint, ulong> GuestLabels = [];
    public readonly List<uint> LabeledRvas = [];
    public readonly List<ulong> ImportBodyLabels = [];
    private readonly Dictionary<(uint Va, int Kind), ulong> stubs = [];
    private readonly HashSet<uint> labeled = [];
    private readonly SortedSet<uint> reportedTargets = [];

    public X64CodeGen(PeImage image, ControlFlowGraph cfg, bool release, ulong dataBase, uint thunkOffset, uint boundOffset, uint tableOffset, uint tableEntries, uint exitRva, uint stubBaseRva)
    {
        this.image = image; this.cfg = cfg; this.release = release; this.dataBase = dataBase;
        this.thunkOffset = thunkOffset; this.boundOffset = boundOffset; this.tableOffset = tableOffset; this.tableEntries = tableEntries;
        this.exitRva = exitRva; this.stubBaseRva = stubBaseRva;
    }

    // ---- operand helpers -------------------------------------------------------------------------------------------
    private static int DispSize(Register b, long d) => d == 0 ? (b is Register.EBP or Register.R13D or Register.R13 or Register.RBP ? 1 : 0) : d is >= -128 and <= 127 ? 1 : 4;
    private static MemoryOperand M(Register b, long d = 0) => new(b, d, DispSize(b, d));
    private static MemoryOperand M(Register b, Register i, int scale, long d = 0) => new(b, i, scale, d, DispSize(b, d));
    private MemoryOperand Ctx(int off) => new(Register.RIP, (long)dataBase + off, 8);
    private static MemoryOperand MemOf(Instruction i)
    {
        var d = (int)(uint)i.MemoryDisplacement64;
        var size = i.MemoryBase == Register.None ? 4 : DispSize(i.MemoryBase, d);
        return new MemoryOperand(i.MemoryBase, i.MemoryIndex, i.MemoryIndexScale, d, size);
    }

    private void A(Instruction i) => E.Add(i);
    private void Jmp(ulong label) => A(Instruction.CreateBranch(Code.Jmp_rel32_64, label));
    private void Jcc(ConditionCode cc, ulong label) => A(Instruction.CreateBranch(JccCodes[(int)cc], label));
    private void SpAdd(int delta) => A(Instruction.Create(Code.Lea_r32_m, Register.ESP, M(Register.ESP, delta)));
    private void StoreAtSp(int off, Register r) => A(Instruction.Create(Code.Mov_rm32_r32, M(Register.ESP, off), r));
    private void StoreImmAtSp(int off, uint v) => A(Instruction.Create(Code.Mov_rm32_imm32, M(Register.ESP, off), v));
    private void LoadFromSp(Register r, int off) => A(Instruction.Create(Code.Mov_r32_rm32, r, M(Register.ESP, off)));

    private static readonly Code[] JccCodes = BuildJcc();
    private static Code[] BuildJcc()
    {
        string[] n = ["", "Jo", "Jno", "Jb", "Jae", "Je", "Jne", "Jbe", "Ja", "Js", "Jns", "Jp", "Jnp", "Jl", "Jge", "Jle", "Jg"];
        var r = new Code[n.Length];
        for (var k = 1; k < n.Length; k++) r[k] = Enum.Parse<Code>(n[k] + "_rel32_64");
        return r;
    }

    private uint Va(uint rva) => unchecked(image.ImageBase + rva);

    // ---- runtime routines ------------------------------------------------------------------------------------------
    public void EmitRuntime()
    {
        Trap1Label = E.NewLabel(); Trap2Label = E.NewLabel(); ExitCommonLabel = E.NewLabel(); DispatchLabel = E.NewLabel(); EnterLabel = E.NewLabel();
        var trapStore = E.NewLabel();
        // Host offset 0 is the trap routine: dispatch table entries that are 0 land here (untranslated indirect target).
        E.Place(Trap1Label);
        A(Instruction.Create(Code.Mov_rm32_imm32, Ctx(CtxTrapKind), 1u));
        E.Place(trapStore);
        A(Instruction.Create(Code.Mov_rm64_r64, Ctx(CtxTrapAddress), Register.R9));
        A(Instruction.Create(Code.Xor_r32_rm32, Register.EAX, Register.EAX));
        Jmp(ExitCommonLabel);
        E.Place(Trap2Label);
        A(Instruction.Create(Code.Mov_rm32_imm32, Ctx(CtxTrapKind), 2u));
        Jmp(trapStore);

        // ulong EnterGuest(ulong hostTarget /*rdi*/, uint guestEsp /*esi*/)
        E.Place(EnterLabel);
        foreach (var r in new[] { Register.RBX, Register.RBP, Register.R12, Register.R13, Register.R14, Register.R15 })
            A(Instruction.Create(Code.Push_r64, r));
        A(Instruction.Create(Code.Push_rm64, Ctx(CtxNativeRsp)));
        A(Instruction.Create(Code.Mov_rm64_r64, Ctx(CtxNativeRsp), Register.RSP));
        A(Instruction.Create(Code.Mov_rm32_imm32, Ctx(CtxTrapKind), 0u));
        A(Instruction.Create(Code.Lea_r64_m, Register.R12, new MemoryOperand(Register.RIP, 0L, 8)));
        A(Instruction.Create(Code.Lea_r64_m, Register.R13, new MemoryOperand(Register.RIP, (long)dataBase + tableOffset, 8)));
        A(Instruction.Create(Code.Mov_r64_rm64, Register.R8, Register.RDI));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.ESP, Register.ESI));
        foreach (var r in new[] { Register.EAX, Register.EBX, Register.ECX, Register.EDX, Register.EBP, Register.ESI, Register.EDI })
            A(Instruction.Create(Code.Xor_r32_rm32, r, r));
        A(Instruction.Create(Code.Cld));
        A(Instruction.Create(Code.Jmp_rm64, Register.R8));

        // Shared exit: guest EAX is the result. Restores the previous context slot so nested entries work.
        E.Place(ExitCommonLabel);
        A(Instruction.Create(Code.Mov_r64_rm64, Register.RSP, Ctx(CtxNativeRsp)));
        A(Instruction.Create(Code.Pop_rm64, Ctx(CtxNativeRsp)));
        foreach (var r in new[] { Register.R15, Register.R14, Register.R13, Register.R12, Register.RBP, Register.RBX })
            A(Instruction.Create(Code.Pop_r64, r));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Register.EAX));
        A(Instruction.Create(Code.Retnq));

        // Dispatch: r8d = guest VA. Flags are preserved (only lea/mov/SSE are used). Out-of-range indexes clamp to the
        // reserved last entry (0), which targets the trap routine; r9d keeps the original VA for the trap report.
        E.Place(DispatchLabel);
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R9D, Register.R8D));
        A(Instruction.Create(Code.Lea_r32_m, Register.R8D, M(Register.R8D, unchecked((int)(0u - image.ImageBase)))));
        A(Instruction.Create(Code.Movd_xmm_rm32, Register.XMM15, Register.R8D));
        A(Instruction.Create(Code.Pminud_xmm_xmmm128, Register.XMM15, new MemoryOperand(Register.RIP, (long)dataBase + boundOffset, 8)));
        A(Instruction.Create(Code.Movd_rm32_xmm, Register.R8D, Register.XMM15));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, M(Register.R13, Register.R8, 4)));
        A(Instruction.Create(Code.Lea_r64_m, Register.R8, M(Register.R12, Register.R8, 1)));
        A(Instruction.Create(Code.Jmp_rm64, Register.R8));

        for (var k = 0; k < image.Imports.Count; k++)
        {
            var body = E.NewLabel(); ImportBodyLabels.Add(body);
            E.Place(body);
            A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, Register.ESP));
            A(Instruction.Create(Code.Mov_r64_rm64, Register.RSP, Ctx(CtxNativeRsp)));
            A(Instruction.Create(Code.Push_r64, Register.RSI));
            A(Instruction.Create(Code.Push_r64, Register.RDI));
            A(Instruction.Create(Code.Push_r64, Register.R8));
            A(Instruction.Create(Code.Push_r64, Register.R8));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.EDI, Register.R8D));
            A(Instruction.Create(Code.Cld));
            A(Instruction.Create(Code.Call_rm64, new MemoryOperand(Register.RIP, (long)dataBase + thunkOffset + 8L * k, 8)));
            A(Instruction.Create(Code.Pop_r64, Register.R9));
            A(Instruction.Create(Code.Pop_r64, Register.R8));
            A(Instruction.Create(Code.Pop_r64, Register.RDI));
            A(Instruction.Create(Code.Pop_r64, Register.RSI));
            A(Instruction.Create(Code.Mov_r64_rm64, Register.R9, Register.RAX));
            A(Instruction.Create(Code.Shr_rm64_imm8, Register.R9, (byte)32));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Register.EAX));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.R10D, M(Register.R8D)));
            A(Instruction.Create(Code.Lea_r32_m, Register.R8D, M(Register.R8D, Register.R9D, 1, 4)));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.ESP, Register.R8D));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, Register.R10D));
            Jmp(DispatchLabel);
        }
    }

    // ---- guest code ------------------------------------------------------------------------------------------------
    public ulong GuestLabel(uint rva)
    {
        if (!GuestLabels.TryGetValue(rva, out var l)) GuestLabels[rva] = l = E.NewLabel();
        return l;
    }

    private ulong TargetLabel(uint va, uint? fromRva)
    {
        var rva = unchecked(va - image.ImageBase);
        if (cfg.Instructions.ContainsKey(rva)) return GuestLabel(rva);
        var kind = cfg.Undecodable.ContainsKey(rva) ? 2 : 1;
        if (reportedTargets.Add(va))
            Diagnostics.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "BC_UNTRANSLATED_TARGET", $"Direct branch target 0x{va:X8} is not translated code; reaching it traps at run time.", fromRva));
        if (!stubs.TryGetValue((va, kind), out var s)) stubs[(va, kind)] = s = E.NewLabel();
        return s;
    }

    private uint ResolveThread(uint va)
    {
        if (!release) return va;
        for (var depth = 0; depth < 8; depth++)
        {
            var rva = unchecked(va - image.ImageBase);
            if (!cfg.Instructions.TryGetValue(rva, out var g) || g.Instr.Code is not (Code.Jmp_rel8_32 or Code.Jmp_rel32_32)) break;
            var nv = (uint)g.Instr.NearBranchTarget;
            if (nv == va) break;
            va = nv;
        }
        return va;
    }

    public void EmitGuestCode()
    {
        var list = cfg.Instructions.Values.ToList();
        if (release)
        {
            foreach (var l in cfg.Leaders) labeled.Add(l);
            for (var k = 0; k < list.Count; k++)
                if (CfgFalls(list[k]) && (k + 1 == list.Count || list[k + 1].Rva != list[k].NextRva)) labeled.Add(list[k].NextRva);
        }
        else foreach (var g in list) labeled.Add(g.Rva);

        for (var k = 0; k < list.Count; k++)
        {
            var g = list[k];
            if (labeled.Contains(g.Rva)) { E.Place(GuestLabel(g.Rva)); LabeledRvas.Add(g.Rva); }
            var consumed = release ? TryMergePushRun(list, k) : 0;
            bool falls;
            if (consumed > 1) { k += consumed - 1; g = list[k]; falls = true; }
            else falls = Lower(g);
            if (falls && (k + 1 == list.Count || list[k + 1].Rva != g.NextRva))
                Jmp(TargetLabel(Va(g.NextRva), g.Rva));
        }
        foreach (var ((va, kind), label) in stubs.OrderBy(s => s.Key.Va).ThenBy(s => s.Key.Kind))
        {
            E.Place(label);
            A(Instruction.Create(Code.Mov_r32_imm32, Register.R9D, va));
            Jmp(kind == 2 ? Trap2Label : Trap1Label);
        }
    }

    private static bool CfgFalls(GuestInstruction g) => CfgFallsThrough(g.Instr) && g.Instr.FlowControl != FlowControl.Call;
    private static bool CfgFallsThrough(Instruction i) => i.FlowControl is not (FlowControl.UnconditionalBranch or FlowControl.IndirectBranch or FlowControl.Return or FlowControl.Interrupt or FlowControl.Exception);

    private static bool IsSimplePush(Instruction i, out uint? imm, out Register reg)
    {
        imm = null; reg = Register.None;
        switch (i.Code)
        {
            case Code.Push_r32 when i.Op0Register != Register.ESP: reg = i.Op0Register; return true;
            case Code.Pushd_imm32 or Code.Pushd_imm8: imm = (uint)i.GetImmediate(0); return true;
            default: return false;
        }
    }

    /// <summary>Release only: a run of register/immediate pushes in one block is lowered with a single ESP adjustment.</summary>
    private int TryMergePushRun(List<GuestInstruction> list, int k)
    {
        if (!IsSimplePush(list[k].Instr, out _, out _)) return 0;
        var n = 1;
        while (k + n < list.Count && n < 8 && list[k + n].Rva == list[k + n - 1].NextRva && !labeled.Contains(list[k + n].Rva)
               && IsSimplePush(list[k + n].Instr, out _, out _)) n++;
        if (n < 2) return 0;
        for (var j = 0; j < n; j++)
        {
            IsSimplePush(list[k + j].Instr, out var imm, out var reg);
            if (imm is { } v) StoreImmAtSp(-4 * (j + 1), v); else StoreAtSp(-4 * (j + 1), reg);
        }
        SpAdd(-4 * n);
        return n;
    }

    private void Unsupported(GuestInstruction g, string code, string reason)
    {
        Diagnostics.Add(new BinaryDiagnostic(g.Speculative ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, code,
            $"Unsupported instruction '{g.Instr.Mnemonic.ToString().ToLowerInvariant()}': {reason}.", g.Rva));
        A(Instruction.Create(Code.Mov_r32_imm32, Register.R9D, Va(g.Rva)));
        Jmp(Trap2Label);
    }

    private void PushReturn(uint nextRva)
    {
        StoreImmAtSp(-4, Va(nextRva));
        SpAdd(-4);
    }

    /// <summary>Lowers one guest instruction. Returns true when execution can continue into the next emitted instruction.</summary>
    private bool Lower(GuestInstruction g)
    {
        var i = g.Instr;
        if (i.SegmentPrefix is Register.FS or Register.GS)
        {
            Unsupported(g, "BC_SEGMENT_OVERRIDE", "fs/gs segment override (TLS and SEH are not supported)");
            return false;
        }
        for (var op = 0; op < i.OpCount; op++)
        {
            var k = i.GetOpKind(op);
            if (k == OpKind.Register && i.GetOpRegister(op).IsSegmentRegister()) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "segment register operand"); return false; }
            if (k is OpKind.MemorySegSI or OpKind.MemoryESDI or OpKind.MemorySegDI) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "16-bit addressing"); return false; }
            if (k is OpKind.FarBranch16 or OpKind.FarBranch32 or OpKind.NearBranch16) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "far or 16-bit branch"); return false; }
        }
        if (i.MemoryBase.IsGPR16() || i.MemoryIndex.IsGPR16()) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "16-bit addressing"); return false; }

        switch (i.Code)
        {
            case Code.Push_r32 or Code.Push_rm32 when i.Op0Kind == OpKind.Register:
                StoreAtSp(-4, i.Op0Register); SpAdd(-4); // push esp stores the pre-decrement value
                return true;
            case Code.Pushd_imm32 or Code.Pushd_imm8:
                StoreImmAtSp(-4, (uint)i.GetImmediate(0)); SpAdd(-4); return true;
            case Code.Push_rm32:
                A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, MemOf(i)));
                StoreAtSp(-4, Register.R8D); SpAdd(-4); return true;
            case Code.Pop_r32 or Code.Pop_rm32 when i.Op0Kind == OpKind.Register:
                if (i.Op0Register == Register.ESP) LoadFromSp(Register.ESP, 0);
                else { LoadFromSp(i.Op0Register, 0); SpAdd(4); }
                return true;
            case Code.Pop_rm32:
                LoadFromSp(Register.R8D, 0); SpAdd(4);
                A(Instruction.Create(Code.Mov_rm32_r32, MemOf(i), Register.R8D));
                return true;
            case Code.Pushad:
                A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, Register.ESP));
                SpAdd(-32);
                StoreAtSp(28, Register.EAX); StoreAtSp(24, Register.ECX); StoreAtSp(20, Register.EDX); StoreAtSp(16, Register.EBX);
                StoreAtSp(12, Register.R8D); StoreAtSp(8, Register.EBP); StoreAtSp(4, Register.ESI); StoreAtSp(0, Register.EDI);
                return true;
            case Code.Popad:
                LoadFromSp(Register.EDI, 0); LoadFromSp(Register.ESI, 4); LoadFromSp(Register.EBP, 8);
                LoadFromSp(Register.EBX, 16); LoadFromSp(Register.EDX, 20); LoadFromSp(Register.ECX, 24); LoadFromSp(Register.EAX, 28);
                SpAdd(32); return true;
            case Code.Pushfd:
                A(Instruction.Create(Code.Pushfq));
                A(Instruction.Create(Code.Pop_r64, Register.R8));
                StoreAtSp(-4, Register.R8D); SpAdd(-4); return true;
            case Code.Popfd:
                LoadFromSp(Register.R8D, 0); SpAdd(4);
                A(Instruction.Create(Code.Push_r64, Register.R8));
                A(Instruction.Create(Code.Popfq));
                return true;
            case Code.Leaved:
                A(Instruction.Create(Code.Mov_r32_rm32, Register.ESP, Register.EBP));
                LoadFromSp(Register.EBP, 0); SpAdd(4); return true;
            case Code.Enterd_imm16_imm8:
            {
                var size = i.Immediate16; var level = i.Immediate8_2nd & 31;
                SpAdd(-4); StoreAtSp(0, Register.EBP);
                A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, Register.ESP));
                for (var l = 1; l < level; l++)
                {
                    A(Instruction.Create(Code.Lea_r32_m, Register.EBP, M(Register.EBP, -4)));
                    A(Instruction.Create(Code.Mov_r32_rm32, Register.R9D, M(Register.EBP)));
                    SpAdd(-4); StoreAtSp(0, Register.R9D);
                }
                if (level > 0) { SpAdd(-4); StoreAtSp(0, Register.R8D); }
                A(Instruction.Create(Code.Mov_r32_rm32, Register.EBP, Register.R8D));
                if (size != 0) SpAdd(-size);
                return true;
            }
            case Code.Call_rel32_32:
                PushReturn(g.NextRva);
                Jmp(TargetLabel(ResolveThread((uint)i.NearBranchTarget), g.Rva));
                return false;
            case Code.Call_rm32:
                LoadBranchSource(i);
                PushReturn(g.NextRva);
                Jmp(DispatchLabel);
                return false;
            case Code.Retnd:
            case Code.Retnd_imm16:
            {
                int pop = i.Code == Code.Retnd_imm16 ? i.Immediate16 : 0;
                LoadFromSp(Register.R8D, 0);
                SpAdd(4 + pop);
                Jmp(DispatchLabel);
                return false;
            }
            case Code.Jmp_rel8_32 or Code.Jmp_rel32_32:
                Jmp(TargetLabel(ResolveThread((uint)i.NearBranchTarget), g.Rva));
                return false;
            case Code.Jmp_rm32:
                LoadBranchSource(i);
                Jmp(DispatchLabel);
                return false;
            case Code.Loop_rel8_32_ECX or Code.Loope_rel8_32_ECX or Code.Loopne_rel8_32_ECX:
            {
                var fall = E.NewLabel();
                A(Instruction.Create(Code.Lea_r32_m, Register.ECX, M(Register.ECX, -1)));
                A(Instruction.CreateBranch(Code.Jecxz_rel8_64, fall));
                if (i.Code == Code.Loope_rel8_32_ECX) Jcc(ConditionCode.ne, fall);
                else if (i.Code == Code.Loopne_rel8_32_ECX) Jcc(ConditionCode.e, fall);
                Jmp(TargetLabel(ResolveThread((uint)i.NearBranchTarget), g.Rva));
                E.Place(fall);
                return true;
            }
            case Code.Jecxz_rel8_32:
            {
                var taken = E.NewLabel(); var over = E.NewLabel();
                A(Instruction.CreateBranch(Code.Jecxz_rel8_64, taken));
                Jmp(over);
                E.Place(taken);
                Jmp(TargetLabel(ResolveThread((uint)i.NearBranchTarget), g.Rva));
                E.Place(over);
                return true;
            }
        }

        if (i.FlowControl == FlowControl.ConditionalBranch && i.ConditionCode != ConditionCode.None && i.Op0Kind == OpKind.NearBranch32
            && i.Mnemonic is not (Mnemonic.Loop or Mnemonic.Loope or Mnemonic.Loopne or Mnemonic.Jecxz or Mnemonic.Jcxz))
        {
            Jcc(i.ConditionCode, TargetLabel(ResolveThread((uint)i.NearBranchTarget), g.Rva));
            return true;
        }

        if (i.Code is Code.Inc_r32 or Code.Dec_r32 or Code.Inc_r16 or Code.Dec_r16)
        {
            var code = i.Code switch { Code.Inc_r32 => Code.Inc_rm32, Code.Dec_r32 => Code.Dec_rm32, Code.Inc_r16 => Code.Inc_rm16, _ => Code.Dec_rm16 };
            A(Instruction.Create(code, i.Op0Register));
            return true;
        }

        var oc = i.Code.ToOpCode();
        if (oc.Encoding == EncodingKind.D3NOW) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "3DNow! is not supported"); return false; }
        if (oc.Encoding != EncodingKind.Legacy) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "VEX/EVEX/XOP encodings are not supported"); return false; }
        if (!oc.Mode64) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "not valid in 64-bit mode and has no lowering"); return false; }
        if (oc.IsPrivileged || oc.IsStackInstruction || i.Mnemonic is Mnemonic.In or Mnemonic.Out or Mnemonic.Insb or Mnemonic.Insw or Mnemonic.Insd
            or Mnemonic.Outsb or Mnemonic.Outsw or Mnemonic.Outsd or Mnemonic.Int or Mnemonic.Int1 or Mnemonic.Into or Mnemonic.Syscall
            or Mnemonic.Sysenter or Mnemonic.Sysexit or Mnemonic.Sysret or Mnemonic.Cli or Mnemonic.Sti or Mnemonic.Hlt or Mnemonic.Lds or Mnemonic.Les
            or Mnemonic.Lfs or Mnemonic.Lgs or Mnemonic.Lss)
        { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "privileged, system or unlowered stack instruction"); return false; }
        if (i.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.IndirectBranch or FlowControl.UnconditionalBranch or FlowControl.Return
            or FlowControl.ConditionalBranch or FlowControl.XbeginXabortXend || (i.FlowControl == FlowControl.Interrupt && i.Code != Code.Int3))
        { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "unsupported control transfer"); return false; }

        var pass = i;
        pass.SegmentPrefix = Register.None;
        if (!CodeEmitter.TryEncodeProbe(pass, out var err)) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "cannot be encoded in 64-bit mode (" + err + ")"); return false; }
        A(pass);
        return i.FlowControl != FlowControl.Exception && i.FlowControl != FlowControl.Interrupt;
    }

    /// <summary>Loads the guest target of an indirect call/jmp (register or memory operand) into r8d.</summary>
    private void LoadBranchSource(Instruction i)
    {
        if (i.Op0Kind == OpKind.Register) A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, i.Op0Register));
        else A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, MemOf(i)));
    }

    public IEnumerable<(uint Rva, ulong Label)> GuestEntries() => LabeledRvas.Select(r => (r, GuestLabels[r]));
}
