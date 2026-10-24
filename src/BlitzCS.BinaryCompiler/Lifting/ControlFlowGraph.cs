using Iced.Intel;

namespace BlitzCS.BinaryCompiler.Lifting;

/// <summary>One decoded guest instruction. Speculative instructions were only reached from relocation-derived pointer candidates.</summary>
public sealed record GuestInstruction(uint Rva, int Length, Instruction Instr, bool Speculative)
{
    public uint NextRva => Rva + (uint)Length;
}

/// <summary>A maximal run of contiguous guest instructions entered only at its first instruction.</summary>
public sealed class BasicBlock(uint startRva)
{
    public uint StartRva { get; } = startRva;
    public List<GuestInstruction> Instructions { get; } = [];
    public uint EndRva => Instructions.Count == 0 ? StartRva : Instructions[^1].NextRva;
    public List<uint> Successors { get; } = [];
}

/// <summary>Recovered control flow of the executable sections of a PE32 image.</summary>
public sealed class ControlFlowGraph
{
    /// <summary>Decoded instructions keyed by RVA (ascending).</summary>
    public required SortedDictionary<uint, GuestInstruction> Instructions { get; init; }
    /// <summary>RVAs that are entered other than by straight-line fallthrough (branch targets, entries, return addresses, table targets).</summary>
    public required SortedSet<uint> Leaders { get; init; }
    /// <summary>Exports, entry point, direct call targets, jump-table targets and pointer candidates.</summary>
    public required SortedSet<uint> FunctionEntries { get; init; }
    /// <summary>RVAs where decoding failed, with the reason.</summary>
    public required SortedDictionary<uint, string> Undecodable { get; init; }
    public required IReadOnlyList<BasicBlock> Blocks { get; init; }
    public required IReadOnlyList<BinaryDiagnostic> Diagnostics { get; init; }

    public static ControlFlowGraph Build(PeImage image, byte[] mapped) => new CfgBuilder(image, mapped).Build();
}

internal sealed class CfgBuilder(PeImage image, byte[] mapped)
{
    private const int MaxJumpTableEntries = 4096;
    private readonly SortedDictionary<uint, GuestInstruction> insts = [];
    private readonly SortedSet<uint> leaders = [];
    private readonly SortedSet<uint> entries = [];
    private readonly SortedDictionary<uint, string> undecodable = [];
    private readonly List<BinaryDiagnostic> diags = [];
    private readonly Queue<(uint Rva, bool Entry)> queue = new();
    private readonly HashSet<uint> relocSet = [];
    private readonly List<(uint Start, uint End)> exec = [];
    private bool speculative;

    /// <summary>RVAs of the TLS callbacks listed in the (zero-terminated) callback array, as far as the array lies inside the image.</summary>
    public static IEnumerable<uint> TlsCallbackRvas(PeImage image, byte[] mapped)
    {
        if (image.Tls is not { CallbacksVa: not 0 } tls || tls.CallbacksVa < image.ImageBase) yield break;
        var at = tls.CallbacksVa - image.ImageBase;
        for (var n = 0; n < 256 && (ulong)at + 4 <= (ulong)mapped.Length; n++, at += 4)
        {
            var va = BitConverter.ToUInt32(mapped, (int)at);
            if (va == 0) yield break;
            if (va >= image.ImageBase && va - image.ImageBase < image.SizeOfImage) yield return va - image.ImageBase;
        }
    }

    public ControlFlowGraph Build()
    {
        foreach (var s in image.Sections.Where(s => s.IsExecutable))
        {
            // Only bytes that exist in the file are code: the zero-filled tail of a section is not an instruction stream.
            var size = s.VirtualSize != 0 ? Math.Min(s.VirtualSize, s.RawSize) : s.RawSize;
            var end = (uint)Math.Min((ulong)s.VirtualAddress + size, (ulong)mapped.Length);
            if (end > s.VirtualAddress) exec.Add((s.VirtualAddress, end));
        }
        foreach (var r in image.Relocations.Where(r => r.Type == 3)) relocSet.Add(r.Rva);

        foreach (var e in image.Exports.Where(e => e.ForwarderTarget is null).OrderBy(e => e.Rva)) EnqueueRoot(e.Rva, $"export '{e.Name ?? "#" + e.Ordinal}'");
        if (image.EntryPointRva != 0) EnqueueRoot(image.EntryPointRva, "entry point");
        foreach (var rva in TlsCallbackRvas(image, mapped)) EnqueueRoot(rva, "TLS callback");
        Drain();

        speculative = true;
        var starts = insts.Values.ToArray();
        var candidates = new SortedSet<uint>();
        foreach (var rel in PointerSlots())
        {
            if ((ulong)rel + 4 > (ulong)mapped.Length) continue;
            var v = BitConverter.ToUInt32(mapped, (int)rel);
            if (v < image.ImageBase) continue;
            var t = v - image.ImageBase;
            if (!IsExec(t) || insts.ContainsKey(t)) { if (insts.ContainsKey(t)) candidates.Add(t); continue; }
            if (InsideDecoded(starts, t)) continue;
            if (image.Relocations.Count == 0 && (InsideDecoded(starts, rel) || !LooksLikeFunctionStart(t))) continue;
            candidates.Add(t);
        }
        foreach (var t in candidates) Enqueue(t, true);
        if (image.Relocations.Count == 0 && candidates.Count > 0)
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "CFG_POINTER_SCAN_ROOTS", $"The image has no relocation table; {candidates.Count} data dword(s) among {pointerScanSlots} scanned point into code and were treated as potential function pointers."));
        else if (candidates.Count > 0)
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "CFG_RELOC_ROOTS", $"{candidates.Count} relocation target(s) in executable sections were treated as potential function pointers."));
        Drain();
        if (image.Relocations.Count == 0) DiscoverImmediateRoots();

        return new ControlFlowGraph
        {
            Instructions = insts, Leaders = leaders, FunctionEntries = entries, Undecodable = undecodable,
            Blocks = BuildBlocks(), Diagnostics = diags,
        };
    }

    /// <summary>
    /// Locations that may hold code pointers. With a relocation table these are exactly the HIGHLOW fixups. Without one (stripped or
    /// derived images, e.g. unpacked ones) every aligned dword of a file-backed section outside decoded code is a candidate; a candidate
    /// only becomes a root when its value lands in file-backed executable memory, and such roots are speculative (warnings, not errors).
    /// </summary>
    private IEnumerable<uint> PointerSlots()
    {
        if (image.Relocations.Any(r => r.Type == 3))
        {
            foreach (var rva in image.Relocations.Where(r => r.Type == 3).Select(r => r.Rva).Order()) yield return rva;
            yield break;
        }
        var count = 0;
        // Every file-backed section is scanned: images produced by packers keep code and data in the same section. The caller drops the dwords
        // that lie inside decoded instructions, so only data words remain.
        foreach (var section in image.Sections.Where(x => x.RawSize != 0))
        {
            var size = Math.Min(section.VirtualSize != 0 ? section.VirtualSize : section.RawSize, section.RawSize);
            var start = (section.VirtualAddress + 3u) & ~3u;
            var end = (uint)Math.Min((ulong)section.VirtualAddress + size, (ulong)mapped.Length);
            for (var at = start; at + 4 <= end; at += 4) { count++; yield return at; }
        }
        pointerScanSlots = count;
    }

    private int pointerScanSlots;

    /// <summary>
    /// Without relocations the addresses of functions that are only stored (callback registration: <c>push offset f</c>,
    /// <c>mov [mem], offset f</c>) cannot be told from other constants by a fixup. Any 32-bit immediate of a decoded instruction that
    /// equals the address of file-backed executable memory, not inside an already decoded instruction, is a speculative root. Repeated
    /// to a fixed point because each new function can reveal more.
    /// </summary>
    private void DiscoverImmediateRoots()
    {
        var seen = new HashSet<uint>();
        for (var round = 0; round < 32; round++)
        {
            var starts = insts.Values.ToArray();
            var fresh = new SortedSet<uint>();
            foreach (var g in starts)
                for (var i = 0; i < g.Instr.OpCount; i++)
                {
                    if (g.Instr.GetOpKind(i) != OpKind.Immediate32) continue;
                    var v = g.Instr.Immediate32;
                    if (v < image.ImageBase) continue;
                    var t = v - image.ImageBase;
                    if (!IsExec(t) || insts.ContainsKey(t) || InsideDecoded(starts, t) || !seen.Add(t)) continue;
                    if (!LooksLikeFunctionStart(t)) { rejectedAsData++; continue; }
                    fresh.Add(t);
                }
            if (fresh.Count == 0) break;
            foreach (var t in fresh) Enqueue(t, true);
            Drain();
        }
        if (seen.Count > 0)
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "CFG_IMMEDIATE_ROOTS", $"The image has no relocation table; {seen.Count - rejectedAsData} code address(es) held as instruction immediates were treated as potential function pointers ({rejectedAsData} more did not look like function starts and were ignored)."));
    }

    private int rejectedAsData;

    /// <summary>Instructions that compiled user-mode code does not contain; meeting one in a trial decode means the bytes are data.</summary>
    private static bool IsUserModeNonsense(Mnemonic m) => m is Mnemonic.In or Mnemonic.Out or Mnemonic.Insb or Mnemonic.Insw or Mnemonic.Insd
        or Mnemonic.Outsb or Mnemonic.Outsw or Mnemonic.Outsd or Mnemonic.Cli or Mnemonic.Sti or Mnemonic.Hlt or Mnemonic.Bound or Mnemonic.Into
        or Mnemonic.Lgdt or Mnemonic.Lidt or Mnemonic.Lldt or Mnemonic.Ltr or Mnemonic.Wrmsr or Mnemonic.Rdmsr or Mnemonic.Invd or Mnemonic.Wbinvd
        or Mnemonic.Arpl or Mnemonic.Aaa or Mnemonic.Aas or Mnemonic.Daa or Mnemonic.Das or Mnemonic.Salc or Mnemonic.Retf or Mnemonic.Iretd
       ;

    /// <summary>
    /// Evidence that a guessed address starts a function rather than pointing into inline data: the preceding byte ends a function or is padding
    /// (ret, ret imm16, int3, nop) and a short trial decode is clean. Used only for pointer guesses without relocations.
    /// </summary>
    private bool LooksLikeFunctionStart(uint t)
    {
        if (t == 0 || t >= mapped.Length) return false;
        var before = mapped[t - 1];
        var afterRet = before is 0xC3 or 0xCC or 0x90 || (t >= 3 && mapped[t - 3] == 0xC2);
        if (!afterRet) return false;
        var end = ExecEnd(t);
        if (end == 0) return false;
        var decoder = Iced.Intel.Decoder.Create(32, new ByteArrayCodeReader(mapped, (int)t, (int)Math.Min(end - t, 512u)), image.ImageBase + t);
        var zeroAdds = 0;
        for (var n = 0; n < 48; n++)
        {
            var ins = decoder.Decode();
            if (ins.IsInvalid || IsUserModeNonsense(ins.Mnemonic)) return false;
            zeroAdds = ins.Code == Code.Add_rm8_r8 && ins.Length == 2 && mapped[ins.IP - image.ImageBase] == 0 ? zeroAdds + 1 : 0;
            if (zeroAdds >= 3) return false;
            if (ins.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch) return true;
        }
        return true;
    }

    private void EnqueueRoot(uint rva, string what)
    {
        if (IsExec(rva)) { Enqueue(rva, true); return; }
        var section = image.Sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
        // An export or entry that lies in a non-executable section is data (tables, variables), not a code root.
        if (section is { IsExecutable: false } && section.RawSize != 0) return;
        var detail = section is { RawSize: 0 }
            ? $"lies in section '{section.Name}', which has no data in the file; code there is produced at run time (packed or self-modifying image) and cannot be translated ahead of time"
            : "does not lie in file-backed executable memory";
        diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Error, "BC_ROOT_NOT_FILE_BACKED", $"The {what} at RVA 0x{rva:X} {detail}.", rva));
    }

    private static bool InsideDecoded(GuestInstruction[] starts, uint t)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var s = starts[mid];
            if (t < s.Rva) hi = mid - 1; else if (t >= s.NextRva) lo = mid + 1; else return true;
        }
        return false;
    }

    private bool IsExec(uint rva) { foreach (var (s, e) in exec) if (rva >= s && rva < e) return true; return false; }
    private uint ExecEnd(uint rva) { foreach (var (s, e) in exec) if (rva >= s && rva < e) return e; return 0; }

    private void Enqueue(uint rva, bool entry)
    {
        leaders.Add(rva);
        if (entry) entries.Add(rva);
        queue.Enqueue((rva, entry));
    }

    private void Drain()
    {
        while (queue.Count > 0) Explore(queue.Dequeue().Rva);
    }

    private void Explore(uint rva)
    {
        while (true)
        {
            if (insts.ContainsKey(rva) || undecodable.ContainsKey(rva)) return;
            if (!IsExec(rva))
            {
                if (!speculative) diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "CFG_TARGET_NOT_EXECUTABLE", $"Control flow reaches RVA 0x{rva:X}, which is not in an executable section.", rva));
                undecodable[rva] = "not executable";
                return;
            }
            var decoder = Decoder.Create(32, new ByteArrayCodeReader(mapped, (int)rva, mapped.Length - (int)rva));
            decoder.IP = image.ImageBase + rva;
            decoder.Decode(out var ins);
            var end = (ulong)rva + (ulong)ins.Length;
            if (ins.IsInvalid || end > ExecEnd(rva))
            {
                undecodable[rva] = "invalid or truncated instruction";
                diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "CFG_DECODE_FAILURE", $"Cannot decode an instruction at RVA 0x{rva:X}; the bytes are treated as data.", rva));
                return;
            }
            var gi = new GuestInstruction(rva, ins.Length, ins, speculative);
            insts[rva] = gi;
            var next = gi.NextRva;
            switch (ins.FlowControl)
            {
                case FlowControl.Next:
                    rva = next; continue;
                case FlowControl.UnconditionalBranch:
                    AddTarget(ins, false);
                    return;
                case FlowControl.IndirectBranch:
                    ScanJumpTable(gi);
                    return;
                case FlowControl.ConditionalBranch:
                    AddTarget(ins, false);
                    leaders.Add(next); rva = next; continue;
                case FlowControl.Call:
                    AddTarget(ins, true);
                    leaders.Add(next); rva = next; continue;
                case FlowControl.IndirectCall:
                    leaders.Add(next); rva = next; continue;
                case FlowControl.Return:
                case FlowControl.Interrupt:
                case FlowControl.Exception:
                    return;
                default:
                    rva = next; continue;
            }
        }
    }

    private void AddTarget(Instruction ins, bool isCall)
    {
        if (ins.Op0Kind != OpKind.NearBranch32) return;
        var va = ins.NearBranchTarget;
        if (va < image.ImageBase) return;
        var t = (uint)(va - image.ImageBase);
        Enqueue(t, isCall);
    }

    private void ScanJumpTable(GuestInstruction gi)
    {
        var i = gi.Instr;
        if (i.Code != Code.Jmp_rm32 || i.Op0Kind != OpKind.Memory || i.MemoryBase != Register.None
            || i.MemoryIndex == Register.None || i.MemoryIndexScale != 4) return;
        var tableVa = (uint)i.MemoryDisplacement64;
        if (tableVa < image.ImageBase) return;
        var table = tableVa - image.ImageBase;
        var count = 0;
        for (; count < MaxJumpTableEntries; count++)
        {
            var at = (ulong)table + (ulong)count * 4;
            if (at + 4 > (ulong)mapped.Length) break;
            if (relocSet.Count > 0 && !relocSet.Contains((uint)at)) break;
            var v = BitConverter.ToUInt32(mapped, (int)at);
            if (v < image.ImageBase || !IsExec(v - image.ImageBase)) break;
            Enqueue(v - image.ImageBase, true);
        }
        if (count > 0)
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "CFG_JUMP_TABLE", $"Jump table at RVA 0x{table:X} with {count} target(s) recovered for the indirect jump at RVA 0x{gi.Rva:X}.", gi.Rva));
        else
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "CFG_INDIRECT_JUMP", $"Indirect jump at RVA 0x{gi.Rva:X} has no recognizable jump table; it is dispatched at run time.", gi.Rva));
    }

    private List<BasicBlock> BuildBlocks()
    {
        var blocks = new List<BasicBlock>();
        BasicBlock? cur = null; GuestInstruction? prev = null;
        foreach (var gi in insts.Values)
        {
            var contiguous = prev is not null && prev.NextRva == gi.Rva && FallsThrough(prev.Instr);
            if (cur is null || !contiguous || leaders.Contains(gi.Rva))
            {
                cur = new BasicBlock(gi.Rva); blocks.Add(cur);
            }
            cur.Instructions.Add(gi);
            prev = gi;
        }
        foreach (var b in blocks)
        {
            var last = b.Instructions[^1];
            if (FallsThrough(last.Instr)) b.Successors.Add(last.NextRva);
            if (last.Instr.Op0Kind == OpKind.NearBranch32 && last.Instr.NearBranchTarget >= image.ImageBase)
                b.Successors.Add((uint)(last.Instr.NearBranchTarget - image.ImageBase));
        }
        return blocks;
    }

    internal static bool FallsThrough(Instruction i) => i.FlowControl switch
    {
        FlowControl.UnconditionalBranch or FlowControl.IndirectBranch or FlowControl.Return
            or FlowControl.Interrupt or FlowControl.Exception => false,
        _ => true,
    };
}
