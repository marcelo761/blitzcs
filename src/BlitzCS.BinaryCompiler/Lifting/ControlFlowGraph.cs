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
        foreach (var rel in image.Relocations.Where(r => r.Type == 3).Select(r => r.Rva).Order())
        {
            if ((ulong)rel + 4 > (ulong)mapped.Length) continue;
            var v = BitConverter.ToUInt32(mapped, (int)rel);
            if (v < image.ImageBase) continue;
            var t = v - image.ImageBase;
            if (!IsExec(t) || insts.ContainsKey(t)) { if (insts.ContainsKey(t)) candidates.Add(t); continue; }
            if (InsideDecoded(starts, t)) continue;
            candidates.Add(t);
        }
        foreach (var t in candidates) Enqueue(t, true);
        if (candidates.Count > 0)
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "CFG_RELOC_ROOTS", $"{candidates.Count} relocation target(s) in executable sections were treated as potential function pointers."));
        Drain();

        return new ControlFlowGraph
        {
            Instructions = insts, Leaders = leaders, FunctionEntries = entries, Undecodable = undecodable,
            Blocks = BuildBlocks(), Diagnostics = diags,
        };
    }

    private void EnqueueRoot(uint rva, string what)
    {
        if (IsExec(rva)) { Enqueue(rva, true); return; }
        var section = image.Sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
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
