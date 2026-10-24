using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>
/// Register conventions inside translated code: guest EAX..EDI map to RAX..RDI (upper halves always zero), guest ESP is RSP
/// (always zero-extended), R12 = host image base, R13 = dispatch table address, R15 = guest TEB base (loaded from the call context by EnterGuest), R8-R11 and XMM15 are translator scratch.
/// Translated code is bound to the image's preferred base (guest addresses are embedded as constants).
/// </summary>
internal sealed partial class X64CodeGen
{
    /// <summary>Guest ECX / EDX at entry (runtime -> EnterGuest; thiscall and fastcall register arguments, zero otherwise).</summary>
    public const int CtxEcxIn = 8, CtxEdxIn = 12;
    public const int CtxNativeRsp = 0, CtxTrapKind = 16, CtxTrapAddress = 24, CtxFault = 32, CtxTeb = 40,
        CtxEdx = 48, CtxMode = 56, CtxSt0Raw = 64, CtxSt0Double = 80, CtxSt0Single = 88, CtxXmm0 = 96, CtxStatus = 112, CtxSavedFcw = 116, CtxCurrentFcw = 118,
        CtxSavedMxcsr = 120, CtxCurrentMxcsr = 124, CtxSize = 128,
        CtxImportFlags = 60; // u32: extended-return flags staged by a managed import (1 = EDX valid in CtxEdx, 2 = ST0 80-bit image valid in CtxSt0Raw)
    /// <summary>Capture request bits in CtxMode (runtime -> EnterGuest).</summary>
    public const uint ModeSt0 = 2, ModeXmm0 = 4;
    /// <summary>Status bits in CtxStatus (ExitCommon -> runtime): 1 = x87 stack not empty on entry (guest not run), 2 = x87 stack unbalanced when the guest returned (reset), 4 = ST0 requested but the x87 stack was empty.</summary>
    public const uint StatusEntryX87 = 1, StatusExitX87 = 2, StatusSt0Empty = 4;

    private readonly PeImage image;
    private readonly ControlFlowGraph cfg;
    private readonly bool release;
    private readonly ulong dataBase;
    private readonly uint tableOffset, boundOffset, thunkOffset, tableEntries, exitRva, stubBaseRva;
    public readonly CodeEmitter E = new();
    public readonly List<BinaryDiagnostic> Diagnostics = [];
    private readonly bool flagsDeadAtDispatchTargets;
    private readonly HashSet<uint> tableJumps = [];
    private readonly Dictionary<uint, List<uint>> returnCandidates = [];
    private readonly Dictionary<uint, GuestInstruction> instrMap;
    private readonly HashSet<uint> entrySet;
    private readonly List<(ulong Table, List<ulong> Targets)> shadowTables = [];
    private byte[]? mappedImage;
    private HashSet<uint>? relocRvas;

    /// <summary>Label offsets of the previous layout pass; the host-offset jump tables embedded in the code are filled from it and then verified against the final layout.</summary>
    public Func<ulong, uint>? PreviousOffsets { get; set; }

    public ulong FaultExitLabel, EnterLabel, ExitCommonLabel, DispatchLabel, Trap1Label, Trap2Label, Trap3Label;
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
        instrMap = new Dictionary<uint, GuestInstruction>(cfg.Instructions);
        entrySet = [.. cfg.FunctionEntries];
        foreach (var d in cfg.Diagnostics) if (d.Code == "CFG_JUMP_TABLE" && d.GuestRva is { } at) tableJumps.Add(at);
        flagsDeadAtDispatchTargets = release && DispatchTargetsIgnoreFlags();
    }

    private const RflagsBits ArithmeticFlags = RflagsBits.OF | RflagsBits.SF | RflagsBits.ZF | RflagsBits.AF | RflagsBits.CF | RflagsBits.PF;

    /// <summary>
    /// True when no statically known dynamic-dispatch target consumes the arithmetic flags it is entered with, so the cheaper
    /// flag-clobbering `cmp`+`jae` bounds check may replace the flag-preserving SSE clamp for ret, indirect call and
    /// table-driven jmp. The targets are every call return site and every non-speculative function entry (exports,
    /// direct call targets, jump-table targets). Relocation-derived pointer candidates are excluded: they cannot be told apart from
    /// data tables stored in .text, and a function called through a pointer is assumed to follow the ABI (flags are not an input). A target whose first flag-touching instruction reads a flag
    /// (e.g. `stc; call f` ... `sbb eax,eax`) disables the optimization for the whole image, which then keeps the exact
    /// flag-preserving dispatch. Calls and returns are assumed not to consume flags (the i386 ABIs leave EFLAGS undefined
    /// across calls; only DF is specified, and `cmp` never changes DF).
    /// </summary>
    private bool DispatchTargetsIgnoreFlags()
    {
        var targets = new HashSet<uint>();
        foreach (var g in instrMap.Values)
            if (g.Instr.FlowControl is FlowControl.Call or FlowControl.IndirectCall) targets.Add(g.NextRva);
        foreach (var e in entrySet) targets.Add(e);
        foreach (var rva in targets)
            if (instrMap.TryGetValue(rva, out var gi) && !gi.Speculative && !FlagsDeadIn(rva)) return false;
        return true;
    }


    private const int MaxReturnCandidates = 4, MaxEntryScan = 4096;

    /// <summary>
    /// Statically predicted return sites of the `ret` at <paramref name="retRva"/>: the sites of direct calls to every function entry
    /// that can reach the ret without crossing a call (tail `jmp`s included), plus, for entries nobody calls directly (address-taken
    /// functions), the sites of all indirect calls. The prediction only selects a faster path: a return to any other address still
    /// goes through the generic bounds-checked dispatch, so an unpredicted or tampered return address behaves exactly as before
    /// (which is also why an incomplete analysis, e.g. across an unrecovered indirect jump, can only cost speed).
    /// </summary>
    private List<uint> ReturnCandidates(uint retRva)
    {
        if (returnCandidates.TryGetValue(retRva, out var cached)) return cached;
        BuildReturnIndexIfNeeded();
        var result = new List<uint>();
        // Prediction only pays when the return can come from a handful of sites; with more callers than MaxReturnCandidates it is not made at all.
        var tooMany = false;
        if (retEntries!.TryGetValue(retRva, out var entries))
            foreach (var e in entries)
            {
                var sites = directCallers!.TryGetValue(e, out var direct) ? direct : exportRvas!.Contains(e) ? [] : indirectSites!;
                if (sites.Count > MaxReturnCandidates) { tooMany = true; break; }
                foreach (var s in sites)
                    if (labeled.Contains(s) && !result.Contains(s)) result.Add(s);
                if (result.Count > MaxReturnCandidates) { tooMany = true; break; }
            }
        if (tooMany) result.Clear();
        returnCandidates[retRva] = result;
        return result;
    }

    private Dictionary<uint, List<uint>>? directCallers, retEntries;
    private List<uint>? indirectSites;
    private HashSet<uint>? exportRvas;

    /// <summary>Indexes direct/indirect call sites and, for every `ret`, the function entries that reach it without crossing a call.</summary>
    private void BuildReturnIndexIfNeeded()
    {
        if (retEntries is not null) return;
        directCallers = []; retEntries = []; indirectSites = [];
        exportRvas = image.Exports.Select(e => e.Rva).ToHashSet();
        foreach (var g in instrMap.Values)
        {
            if (g.Instr.FlowControl == FlowControl.Call && g.Instr.Op0Kind == OpKind.NearBranch32)
            {
                var t = unchecked((uint)g.Instr.NearBranchTarget - image.ImageBase);
                if (!directCallers.TryGetValue(t, out var l)) directCallers[t] = l = [];
                l.Add(g.NextRva);
            }
            else if (g.Instr.FlowControl == FlowControl.IndirectCall) indirectSites.Add(g.NextRva);
        }
        var seen = new HashSet<uint>(); var work = new Stack<uint>();
        foreach (var entry in entrySet)
        {
            seen.Clear(); work.Push(entry); seen.Add(entry);
            while (work.Count > 0)
            {
                var at = work.Pop();
                if (!instrMap.TryGetValue(at, out var g)) continue;
                var ins = g.Instr;
                void Go(uint to) { if (seen.Count < MaxEntryScan && seen.Add(to)) work.Push(to); }
                switch (ins.FlowControl)
                {
                    case FlowControl.Return:
                        if (!retEntries.TryGetValue(at, out var list)) retEntries[at] = list = [];
                        list.Add(entry); break;
                    case FlowControl.Next or FlowControl.Call or FlowControl.IndirectCall: Go(g.NextRva); break;
                    case FlowControl.ConditionalBranch:
                        Go(g.NextRva);
                        if (ins.Op0Kind == OpKind.NearBranch32) Go(unchecked((uint)ins.NearBranchTarget - image.ImageBase));
                        break;
                    case FlowControl.UnconditionalBranch:
                        if (ins.Op0Kind == OpKind.NearBranch32) Go(unchecked((uint)ins.NearBranchTarget - image.ImageBase));
                        break;
                }
            }
        }
    }

    private bool FlagsDeadIn(uint rva)
    {
        var live = ArithmeticFlags;
        for (var step = 0; step < 32; step++)
        {
            if (!instrMap.TryGetValue(rva, out var g)) return false;
            var ins = g.Instr;
            if ((ins.RflagsRead & live) != 0) return false;
            live &= ~ins.RflagsModified;
            if (live == 0) return true;
            switch (ins.FlowControl)
            {
                case FlowControl.Next: rva = g.NextRva; break;
                case FlowControl.UnconditionalBranch when ins.Op0Kind == OpKind.NearBranch32:
                    rva = unchecked((uint)ins.NearBranchTarget - image.ImageBase); break;
                case FlowControl.Call or FlowControl.IndirectCall or FlowControl.Return or FlowControl.IndirectBranch:
                    return true; // ABI: callees and callers do not take flags as input
                default: return false;
            }
        }
        return false;
    }

    // ---- operand helpers -------------------------------------------------------------------------------------------
    private static int DispSize(Register b, long d) => d == 0 ? (b is Register.EBP or Register.R13D or Register.R13 or Register.RBP ? 1 : 0) : d is >= -128 and <= 127 ? 1 : 4;
    private static MemoryOperand M(Register b, long d = 0) => new(b, d, DispSize(b, d));
    private static MemoryOperand M(Register b, Register i, int scale, long d = 0) => new(b, i, scale, d, DispSize(b, d));
    // The per-call context block lives in caller memory and is addressed through R14 (callee-saved, never touched by guest code).
    private static MemoryOperand Ctx(int off) => new(Register.R14, (long)off, 1);
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
        FaultExitLabel = E.NewLabel(); Trap1Label = E.NewLabel(); Trap2Label = E.NewLabel(); Trap3Label = E.NewLabel(); ExitCommonLabel = E.NewLabel(); DispatchLabel = E.NewLabel(); EnterLabel = E.NewLabel();
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
        E.Place(Trap3Label);
        A(Instruction.Create(Code.Mov_rm32_imm32, Ctx(CtxTrapKind), 3u));
        Jmp(trapStore);

        // A managed import raised a fault: abandon the guest call at once instead of letting guest code run on with a bogus result.
        E.Place(FaultExitLabel);
        A(Instruction.Create(Code.Xor_r32_rm32, Register.EAX, Register.EAX));
        Jmp(ExitCommonLabel);

        // ulong EnterGuest(ulong hostTarget /*rdi*/, uint guestEsp /*esi*/, ulong callContext /*rdx*/)
        E.Place(EnterLabel);
        foreach (var r in new[] { Register.RBX, Register.RBP, Register.R12, Register.R13, Register.R14, Register.R15 })
            A(Instruction.Create(Code.Push_r64, r));
        A(Instruction.Create(Code.Mov_r64_rm64, Register.R14, Register.RDX));
        A(Instruction.Create(Code.Mov_r64_rm64, Register.R15, Ctx(CtxTeb))); // the thread's guest TEB; the caller's R15 was pushed above, so nested entries restore it
        A(Instruction.Create(Code.Push_r64, Register.RDX)); // padding: keeps the saved native RSP 16-byte aligned for the import stubs' managed calls
        A(Instruction.Create(Code.Mov_rm64_r64, Ctx(CtxNativeRsp), Register.RSP));
        A(Instruction.Create(Code.Mov_rm32_imm32, Ctx(CtxTrapKind), 0u));
        // Floating-point state contract: the x87 stack must be empty (TOP == 0) on entry; the host's x87 control word and MXCSR are
        // restored by ExitCommon if the guest changed them.
        var x87Bad = E.NewLabel();
        A(Instruction.Create(Code.Fnstsw_AX, Register.AX));
        A(Instruction.Create(Code.Test_AX_imm16, Register.AX, 0x3800));
        Jcc(ConditionCode.ne, x87Bad);
        A(Instruction.Create(Code.Fnstcw_m2byte, Ctx(CtxSavedFcw)));
        A(Instruction.Create(Code.Stmxcsr_m32, Ctx(CtxSavedMxcsr)));
        var enterGo = E.NewLabel();
        Jmp(enterGo);
        E.Place(x87Bad);
        A(Instruction.Create(Code.Or_rm32_imm8, Ctx(CtxStatus), 1));
        A(Instruction.Create(Code.Xor_r32_rm32, Register.EAX, Register.EAX));
        Jmp(ExitCommonLabel);
        E.Place(enterGo);
        A(Instruction.Create(Code.Lea_r64_m, Register.R12, new MemoryOperand(Register.RIP, 0L, 8)));
        A(Instruction.Create(Code.Lea_r64_m, Register.R13, new MemoryOperand(Register.RIP, (long)dataBase + tableOffset, 8)));
        A(Instruction.Create(Code.Mov_r64_rm64, Register.R8, Register.RDI));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.ESP, Register.ESI));
        foreach (var r in new[] { Register.EAX, Register.EBX, Register.EBP, Register.ESI, Register.EDI })
            A(Instruction.Create(Code.Xor_r32_rm32, r, r));
        // Register arguments of thiscall/fastcall come from the (zeroed by default) call context; cdecl/stdcall calls leave both zero.
        A(Instruction.Create(Code.Mov_r32_rm32, Register.ECX, Ctx(CtxEcxIn)));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EDX, Ctx(CtxEdxIn)));
        A(Instruction.Create(Code.Cld));
        A(Instruction.Create(Code.Jmp_rm64, Register.R8));

        // Shared exit: guest EAX is the result. R14 (the caller's context) is restored by the pops, so nested entries work.
        E.Place(ExitCommonLabel);
        A(Instruction.Create(Code.Mov_r64_rm64, Register.RSP, Ctx(CtxNativeRsp)));
        EmitExitFloatingPoint();
        A(Instruction.Create(Code.Pop_r64, Register.R9)); // padding
        foreach (var r in new[] { Register.R15, Register.R14, Register.R13, Register.R12, Register.RBP, Register.RBX })
            A(Instruction.Create(Code.Pop_r64, r));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Register.EAX));
        A(Instruction.Create(Code.Retnq));

        // Dispatch: r8d = guest VA. Flags are preserved (only lea/mov/SSE are used). Out-of-range indexes clamp to the
        // reserved last entry (0), which targets the trap routine; r9d keeps the original VA for the trap report.
        E.Place(DispatchLabel);
        EmitDispatchPreservingFlags();

        var importExt = E.NewLabel();
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
            A(Instruction.Create(Code.Cmp_rm32_imm8, Ctx(CtxFault), 0));
            Jcc(ConditionCode.ne, FaultExitLabel);
            A(Instruction.Create(Code.Pop_r64, Register.R9));
            A(Instruction.Create(Code.Pop_r64, Register.R8));
            A(Instruction.Create(Code.Pop_r64, Register.RDI));
            A(Instruction.Create(Code.Pop_r64, Register.RSI));
            A(Instruction.Create(Code.Mov_r64_rm64, Register.R9, Register.RAX));
            A(Instruction.Create(Code.Shr_rm64_imm8, Register.R9, (byte)32));
            A(Instruction.Create(Code.Test_rm32_r32, Register.R9D, Register.R9D)); // bit 63 of the import result = extended return (EDX / ST0), see ImportExtLabel
            Jcc(ConditionCode.s, importExt);
            A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Register.EAX));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.R10D, M(Register.R8D)));
            A(Instruction.Create(Code.Lea_r32_m, Register.R8D, M(Register.R8D, Register.R9D, 1, 4)));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.ESP, Register.R8D));
            A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, Register.R10D));
            Jmp(DispatchLabel);
        }
        EmitImportExtendedReturn(importExt);
    }

    /// <summary>
    /// Shared out-of-line tail of an import whose managed side staged an extended return (bit 63 of its result; see GuestCall.SetReturnEdx / SetReturnSt0).
    /// Entered from an import body with R9D = pop bytes | 0x80000000, EAX = result, R8D = guest ESP at the call. The runtime stored the flags at CtxImportFlags
    /// (1 = reload EDX from CtxEdx, 2 = push the 80-bit ST0 image at CtxSt0Raw onto the x87 stack), then this performs the normal import return.
    /// </summary>
    private void EmitImportExtendedReturn(ulong label)
    {
        E.Place(label);
        var noEdx = E.NewLabel(); var noSt0 = E.NewLabel();
        A(Instruction.Create(Code.And_rm32_imm32, Register.R9D, 0x7FFFFFFF));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R10D, Ctx(CtxImportFlags)));
        A(Instruction.Create(Code.Test_rm32_imm32, Register.R10D, 2));
        Jcc(ConditionCode.e, noSt0);
        A(Instruction.Create(Code.Fld_m80fp, Ctx(CtxSt0Raw)));
        E.Place(noSt0);
        A(Instruction.Create(Code.Test_rm32_imm32, Register.R10D, 1));
        Jcc(ConditionCode.e, noEdx);
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EDX, Ctx(CtxEdx)));
        E.Place(noEdx);
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Register.EAX));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R10D, M(Register.R8D)));
        A(Instruction.Create(Code.Lea_r32_m, Register.R8D, M(Register.R8D, Register.R9D, 1, 4)));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.ESP, Register.R8D));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, Register.R10D));
        Jmp(DispatchLabel);
    }

    /// <summary>
    /// Part of ExitCommon (R14 = call context, guest EAX = result): stores EDX, captures ST0 / XMM0 when the call asked for them, verifies that the
    /// x87 stack is balanced (resetting it otherwise) and restores the host's x87 control word and MXCSR if the guest changed them. EAX is preserved.
    /// </summary>
    private void EmitExitFloatingPoint()
    {
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R9D, Register.EAX));
        A(Instruction.Create(Code.Mov_rm32_r32, Ctx(CtxEdx), Register.EDX));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R10D, Ctx(CtxMode)));
        var noSt0 = E.NewLabel(); var haveSt0 = E.NewLabel(); var afterSt0 = E.NewLabel(); var noXmm = E.NewLabel(); var balanced = E.NewLabel();
        var fcwOk = E.NewLabel(); var mxcsrOk = E.NewLabel();
        A(Instruction.Create(Code.Test_rm32_imm32, Register.R10D, (int)ModeSt0));
        Jcc(ConditionCode.e, noSt0);
        A(Instruction.Create(Code.Fxam));
        A(Instruction.Create(Code.Fnstsw_AX, Register.AX));
        A(Instruction.Create(Code.And_rm8_imm8, Register.AH, 0x45));
        A(Instruction.Create(Code.Cmp_rm8_imm8, Register.AH, 0x41));      // C3=1, C2=0, C0=1: ST0 empty
        Jcc(ConditionCode.ne, haveSt0);
        A(Instruction.Create(Code.Or_rm32_imm8, Ctx(CtxStatus), 4));
        Jmp(afterSt0);
        E.Place(haveSt0);
        A(Instruction.Create(Code.Fld_sti, Register.ST0));
        A(Instruction.Create(Code.Fstp_m64fp, Ctx(CtxSt0Double)));
        A(Instruction.Create(Code.Fld_sti, Register.ST0));
        A(Instruction.Create(Code.Fstp_m32fp, Ctx(CtxSt0Single)));
        A(Instruction.Create(Code.Fstp_m80fp, Ctx(CtxSt0Raw)));
        E.Place(afterSt0);
        E.Place(noSt0);
        A(Instruction.Create(Code.Test_rm32_imm32, Register.R10D, (int)ModeXmm0));
        Jcc(ConditionCode.e, noXmm);
        A(Instruction.Create(Code.Movups_xmmm128_xmm, Ctx(CtxXmm0), Register.XMM0));
        E.Place(noXmm);
        A(Instruction.Create(Code.Fnstsw_AX, Register.AX));
        A(Instruction.Create(Code.Test_AX_imm16, Register.AX, 0x3800));
        Jcc(ConditionCode.e, balanced);
        A(Instruction.Create(Code.Or_rm32_imm8, Ctx(CtxStatus), 2));
        A(Instruction.Create(Code.Fninit));
        E.Place(balanced);
        A(Instruction.Create(Code.Fnstcw_m2byte, Ctx(CtxCurrentFcw)));
        A(Instruction.Create(Code.Movzx_r32_rm16, Register.EAX, Ctx(CtxCurrentFcw)));
        A(Instruction.Create(Code.Cmp_r16_rm16, Register.AX, Ctx(CtxSavedFcw)));
        Jcc(ConditionCode.e, fcwOk);
        A(Instruction.Create(Code.Fldcw_m2byte, Ctx(CtxSavedFcw)));
        E.Place(fcwOk);
        A(Instruction.Create(Code.Stmxcsr_m32, Ctx(CtxCurrentMxcsr)));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Ctx(CtxCurrentMxcsr)));
        A(Instruction.Create(Code.Cmp_r32_rm32, Register.EAX, Ctx(CtxSavedMxcsr)));
        Jcc(ConditionCode.e, mxcsrOk);
        A(Instruction.Create(Code.Ldmxcsr_m32, Ctx(CtxSavedMxcsr)));
        E.Place(mxcsrOk);
        A(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, Register.R9D));
    }

    /// <summary>r8d = guest VA -> jump to its translation (see the comment at the Dispatch routine). Clobbers r8/r9/xmm15, preserves flags.</summary>
    private void EmitDispatchPreservingFlags()
    {
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R9D, Register.R8D));
        A(Instruction.Create(Code.Lea_r32_m, Register.R8D, M(Register.R8D, unchecked((int)(0u - image.ImageBase)))));
        A(Instruction.Create(Code.Movd_xmm_rm32, Register.XMM15, Register.R8D));
        A(Instruction.Create(Code.Pminud_xmm_xmmm128, Register.XMM15, new MemoryOperand(Register.RIP, (long)dataBase + boundOffset, 8)));
        A(Instruction.Create(Code.Movd_rm32_xmm, Register.R8D, Register.XMM15));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, M(Register.R13, Register.R8, 4)));
        A(Instruction.Create(Code.Lea_r64_m, Register.R8, M(Register.R12, Register.R8, 1)));
        A(Instruction.Create(Code.Jmp_rm64, Register.R8));
    }

    /// <summary>Same contract as <see cref="EmitDispatchPreservingFlags"/> except that the arithmetic flags are clobbered (see <see cref="DispatchTargetsIgnoreFlags"/>).</summary>
    private void EmitDispatchClobberingFlags()
    {
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R9D, Register.R8D));
        A(Instruction.Create(Code.Lea_r32_m, Register.R8D, M(Register.R8D, unchecked((int)(0u - image.ImageBase)))));
        A(Instruction.Create(Code.Cmp_rm32_imm32, Register.R8D, (int)(tableEntries - 1)));
        Jcc(ConditionCode.ae, Trap1Label); // out of range: r9d still holds the guest VA for the trap report
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, M(Register.R13, Register.R8, 4)));
        A(Instruction.Create(Code.Lea_r64_m, Register.R8, M(Register.R12, Register.R8, 1)));
        A(Instruction.Create(Code.Jmp_rm64, Register.R8));
    }

    /// <summary>
    /// Release inlines the dispatch sequence at every ret/indirect site: each site then owns its indirect-branch predictor
    /// entry and saves a jump. <paramref name="mayClobberFlags"/> additionally allows the cmp/jae bounds check.
    /// </summary>
    private void JmpDispatch(bool mayClobberFlags)
    {
        if (!release) Jmp(DispatchLabel);
        else if (mayClobberFlags && flagsDeadAtDispatchTargets) EmitDispatchClobberingFlags();
        else EmitDispatchPreservingFlags();
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
            // Keep the compiler's intent: a branch target the original code deliberately aligned to 16 bytes (it is preceded by NOP
            // padding: loop heads, function entries) is aligned in the translation too. The guest padding is dropped above and
            // replaced by a multi-byte NOP sized for the translated layout; targets that are only aligned by coincidence are left alone.
            if (release && !labeled.Contains(g.Rva) && IsPaddingBeforeAlignedLabel(list, k)) continue;   // our own NOP padding replaces it
            if (release && labeled.Contains(g.Rva) && (IsIntentionallyAligned(list, k) || IsDeadPaddedFunctionEntry(list, k))) E.AlignNext();
            if (labeled.Contains(g.Rva)) { E.Place(GuestLabel(g.Rva)); LabeledRvas.Add(g.Rva); }
            var endsInCall = false;
            var consumed = release ? TryMergePushRun(list, k, out endsInCall) : 0;
            bool falls;
            if (consumed > 0) { k += consumed - 1; g = list[k]; falls = !endsInCall; }
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
        EmitFsTrapStubs();
        EmitShadowTables();
    }


    /// <summary>A guest instruction that only exists to pad code to an alignment boundary (nop, xchg ax,ax, lea/mov of a register onto itself).</summary>
    private static bool IsNopIdiom(Instruction i)
    {
        switch (i.Mnemonic)
        {
            case Mnemonic.Nop: return true;
            case Mnemonic.Xchg: return i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && i.Op0Register == i.Op1Register;
            case Mnemonic.Mov: return i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && i.Op0Register == i.Op1Register && i.Op0Register.IsGPR32();
            case Mnemonic.Lea:
                return i.Op0Register.IsGPR32() && i.MemoryBase == i.Op0Register && i.MemoryIndex == Register.None && i.MemoryDisplacement64 == 0
                    && i.MemorySegment != Register.FS && i.MemorySegment != Register.GS;
            default: return false;
        }
    }

    /// <summary>True when guest NOP-idioms from <paramref name="k"/> on run straight into an instruction that is a branch target at a 16-byte aligned address.</summary>
    private bool IsPaddingBeforeAlignedLabel(List<GuestInstruction> list, int k)
    {
        var j = k;
        while (j < list.Count && IsNopIdiom(list[j].Instr) && !labeled.Contains(list[j].Rva) && j + 1 < list.Count && list[j + 1].Rva == list[j].NextRva) j++;
        return j > k && j < list.Count && labeled.Contains(list[j].Rva) && (Va(list[j].Rva) & 15) == 0;
    }


    private const int MaxShadowEntries = 4096;

    /// <summary>
    /// Release: `jmp`/`call [table + index*4]` (table in a read-only section whose leading entries are relocated pointers to translated
    /// blocks: switch jump tables, function-pointer arrays) goes through a table of host offsets embedded in the code instead of
    /// loading the guest VA and translating it at run time. Because the guest table cannot change, entry i of the shadow is exactly
    /// what the generic dispatch would compute for entry i of the guest table. The index is
    /// range-checked against the recovered entry count; anything else (including a tampered index) takes the generic
    /// bounds-checked dispatch emitted right after, so behavior is unchanged. Returns false when the shortcut does not apply.
    /// </summary>
    private bool TryEmitShadowTable(GuestInstruction g, ulong generic, bool isCall)
    {
        var i = g.Instr;
        if (!flagsDeadAtDispatchTargets || i.Op0Kind != OpKind.Memory || i.MemoryBase != Register.None
            || i.MemoryIndex == Register.None || i.MemoryIndexScale != 4 || !i.MemoryIndex.IsGPR32() || i.MemoryIndex == Register.ESP) return false;
        var tableVa = (uint)i.MemoryDisplacement64;
        if (tableVa < image.ImageBase) return false;
        var table = tableVa - image.ImageBase;
        var section = image.Sections.FirstOrDefault(s => table >= s.VirtualAddress && table < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
        if (section is null || section.IsWritable) return false;                 // a writable table could change after translation
        mappedImage ??= image.MapImage();
        relocRvas ??= image.Relocations.Where(r => r.Type == 3).Select(r => r.Rva).ToHashSet();
        var relocs = relocRvas;
        var targets = new List<uint>();
        for (var n = 0; n < MaxShadowEntries; n++)
        {
            var at = (ulong)table + (ulong)n * 4;
            if (at + 4 > (ulong)mappedImage.Length || !relocs.Contains((uint)at)) break;
            var v = BitConverter.ToUInt32(mappedImage, (int)at);
            var rva = unchecked(v - image.ImageBase);
            if (v < image.ImageBase || !cfg.Instructions.ContainsKey(rva) || !labeled.Contains(rva)) break; // the shortcut covers the leading run of translated blocks only
            targets.Add(rva);
        }
        if (targets.Count == 0) return false;
        var tableLabel = E.NewLabel();
        shadowTables.Add((tableLabel, targets.Select(GuestLabel).ToList()));
        A(Instruction.Create(Code.Cmp_rm32_imm32, i.MemoryIndex, targets.Count));
        Jcc(ConditionCode.ae, generic);
        A(Instruction.Create(Code.Lea_r64_m, Register.R9, new MemoryOperand(Register.RIP, (long)tableLabel, 8)));
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, M(Register.R9, i.MemoryIndex.GetFullRegister(), 4)));
        if (isCall) PushReturn(g.NextRva);
        A(Instruction.Create(Code.Lea_r64_m, Register.R8, M(Register.R12, Register.R8, 1)));
        A(Instruction.Create(Code.Jmp_rm64, Register.R8));
        return true;
    }

    private void EmitShadowTables()
    {
        foreach (var (table, targets) in shadowTables)
        {
            E.Place(table);
            foreach (var label in targets) A(Instruction.CreateDeclareDword(PreviousOffsets?.Invoke(label) ?? 0u));
        }
    }

    /// <summary>True when every embedded host-offset table matches the final code layout, i.e. the previous pass laid the code out exactly like this one.</summary>
    public bool ShadowTablesMatch(Func<ulong, uint> offsetOf, byte[] code)
    {
        foreach (var (table, targets) in shadowTables)
        {
            var at = (int)offsetOf(table);
            for (var n = 0; n < targets.Count; n++)
                if (BitConverter.ToUInt32(code, at + 4 * n) != offsetOf(targets[n])) return false;
        }
        return true;
    }

    private bool IsIntentionallyAligned(List<GuestInstruction> list, int k)
        => (Va(list[k].Rva) & 15) == 0 && k > 0 && list[k - 1].NextRva == list[k].Rva && IsNopIdiom(list[k - 1].Instr);

    /// <summary>
    /// A function entry that nothing falls into (the previous instruction ends in ret/jmp or there is a gap): its padding is never executed, so
    /// aligning it is free, and it re-synchronizes the layout so that branch-size changes in earlier code cannot move later alignments.
    /// </summary>
    private bool IsDeadPaddedFunctionEntry(List<GuestInstruction> list, int k)
        => entrySet.Contains(list[k].Rva) && (k == 0 || list[k - 1].NextRva != list[k].Rva || !CfgFallsThrough(list[k - 1].Instr));

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

    /// <summary>
    /// Release only: a run of register/immediate pushes in one block is lowered with a single ESP adjustment. A direct `call rel32`
    /// that directly follows the run joins it, so the argument pushes and the return-address push share that adjustment.
    /// Returns the number of guest instructions consumed (0 = nothing merged).
    /// </summary>
    private int TryMergePushRun(List<GuestInstruction> list, int k, out bool endsInCall)
    {
        endsInCall = false;
        if (!IsSimplePush(list[k].Instr, out _, out _)) return 0;
        var n = 1;
        while (k + n < list.Count && n < 8 && list[k + n].Rva == list[k + n - 1].NextRva && !labeled.Contains(list[k + n].Rva)
               && IsSimplePush(list[k + n].Instr, out _, out _)) n++;
        var callAt = k + n;
        var withCall = callAt < list.Count && list[callAt].Rva == list[callAt - 1].NextRva && !labeled.Contains(list[callAt].Rva)
                       && list[callAt].Instr.Code == Code.Call_rel32_32 && list[callAt].Instr.FlowControl == FlowControl.Call;
        if (n < 2 && !withCall) return 0;
        for (var j = 0; j < n; j++)
        {
            IsSimplePush(list[k + j].Instr, out var imm, out var reg);
            if (imm is { } v) StoreImmAtSp(-4 * (j + 1), v); else StoreAtSp(-4 * (j + 1), reg);
        }
        if (!withCall) { SpAdd(-4 * n); return n; }
        var call = list[callAt];
        StoreImmAtSp(-4 * (n + 1), Va(call.NextRva));
        SpAdd(-4 * (n + 1));
        Jmp(TargetLabel(ResolveThread((uint)call.Instr.NearBranchTarget), call.Rva));
        endsInCall = true;
        return n + 1;
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
        if (i.SegmentPrefix == Register.GS)
        {
            Unsupported(g, "BC_SEGMENT_OVERRIDE", "gs segment override (GS is the x64 TEB register and is not modelled)");
            return false;
        }
        MemoryOperand? fsOperand = null;
        if (i.SegmentPrefix == Register.FS)
        {
            var hasMemory = false;
            for (var op = 0; op < i.OpCount; op++) if (i.GetOpKind(op) == OpKind.Memory) hasMemory = true;
            if (i.IsStringInstruction || !hasMemory)
            {
                Unsupported(g, "BC_SEGMENT_OVERRIDE", "fs segment override on an instruction without a plain memory operand (string instruction or stray prefix)");
                return false;
            }
            // lea (and other instructions that do not access memory) ignore the segment: only the address computation remains.
            var accesses = false;
            foreach (var m in infoFactory.GetInfo(i, InstructionInfoOptions.NoRegisterUsage).GetUsedMemory()) if (m.Access != OpAccess.NoMemAccess) accesses = true;
            if (accesses && i.Code is not (Code.Push_rm32 or Code.Pop_rm32 or Code.Call_rm32 or Code.Jmp_rm32))
            {
                if (!TryLowerFsOperand(g, out var fm)) return false;
                fsOperand = fm;
            }
        }
        for (var op = 0; op < i.OpCount; op++)
        {
            var k = i.GetOpKind(op);
            if (k == OpKind.Register && i.GetOpRegister(op).IsSegmentRegister()) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "segment register operand (fs/gs/ds/... cannot be loaded, stored or pushed; only fs: memory operands are modelled)"); return false; }
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
                if (!TryMemoryOperand(g, out var pushSrc)) return false;
                A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, pushSrc));
                StoreAtSp(-4, Register.R8D); SpAdd(-4); return true;
            case Code.Pop_r32 or Code.Pop_rm32 when i.Op0Kind == OpKind.Register:
                if (i.Op0Register == Register.ESP) LoadFromSp(Register.ESP, 0);
                else { LoadFromSp(i.Op0Register, 0); SpAdd(4); }
                return true;
            case Code.Pop_rm32:
            {
                if (!TryMemoryOperand(g, out var popDst)) return false;
                LoadFromSp(Register.R8D, 0); SpAdd(4);
                A(Instruction.Create(Code.Mov_rm32_r32, popDst, Register.R8D));
                return true;
            }
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
            {
                var generic = E.NewLabel();
                if (i.SegmentPrefix != Register.FS && TryEmitShadowTable(g, generic, true)) E.Place(generic);
                if (!LoadBranchSource(g)) return false;
                PushReturn(g.NextRva);
                JmpDispatch(true);
                return false;
            }
            case Code.Retnd:
            case Code.Retnd_imm16:
            {
                int pop = i.Code == Code.Retnd_imm16 ? i.Immediate16 : 0;
                LoadFromSp(Register.R8D, 0);
                SpAdd(4 + pop);
                if (flagsDeadAtDispatchTargets)
                    foreach (var site in ReturnCandidates(g.Rva))
                    {
                        A(Instruction.Create(Code.Cmp_rm32_imm32, Register.R8D, (int)Va(site)));
                        Jcc(ConditionCode.e, GuestLabel(site));
                    }
                JmpDispatch(true);
                return false;
            }
            case Code.Jmp_rel8_32 or Code.Jmp_rel32_32:
                Jmp(TargetLabel(ResolveThread((uint)i.NearBranchTarget), g.Rva));
                return false;
            case Code.Jmp_rm32:
            {
                var generic = E.NewLabel();
                var shadow = i.SegmentPrefix != Register.FS && TryEmitShadowTable(g, generic, false);
                if (shadow) E.Place(generic);
                if (!LoadBranchSource(g)) return false;
                JmpDispatch(tableJumps.Contains(g.Rva));
                return false;
            }
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
        if (fsOperand is { } teb)
        {
            // The accumulator-only moffs encodings (opcodes A0-A3) address an absolute offset; the TEB operand needs the ModRM form.
            var modrm = pass.Code switch
            {
                Code.Mov_AL_moffs8 => Instruction.Create(Code.Mov_r8_rm8, Register.AL, teb),
                Code.Mov_AX_moffs16 => Instruction.Create(Code.Mov_r16_rm16, Register.AX, teb),
                Code.Mov_EAX_moffs32 => Instruction.Create(Code.Mov_r32_rm32, Register.EAX, teb),
                Code.Mov_moffs8_AL => Instruction.Create(Code.Mov_rm8_r8, teb, Register.AL),
                Code.Mov_moffs16_AX => Instruction.Create(Code.Mov_rm16_r16, teb, Register.AX),
                Code.Mov_moffs32_EAX => Instruction.Create(Code.Mov_rm32_r32, teb, Register.EAX),
                _ => (Instruction?)null,
            };
            if (modrm is { } converted) pass = converted; else ApplyOperand(ref pass, teb);
        }
        if (!CodeEmitter.TryEncodeProbe(pass, out var err)) { Unsupported(g, "BC_UNSUPPORTED_INSTRUCTION", "cannot be encoded in 64-bit mode (" + err + ")"); return false; }
        A(pass);
        return i.FlowControl != FlowControl.Exception && i.FlowControl != FlowControl.Interrupt;
    }

    /// <summary>Loads the guest target of an indirect call/jmp (register or memory operand) into r8d.</summary>
    private bool LoadBranchSource(GuestInstruction g)
    {
        var i = g.Instr;
        if (i.Op0Kind == OpKind.Register) { A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, i.Op0Register)); return true; }
        if (!TryMemoryOperand(g, out var src)) return false;
        A(Instruction.Create(Code.Mov_r32_rm32, Register.R8D, src));
        return true;
    }

    public IEnumerable<(uint Rva, ulong Label)> GuestEntries() => LabeledRvas.Select(r => (r, GuestLabels[r]));
}
