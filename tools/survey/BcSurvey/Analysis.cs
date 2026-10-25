using System.Buffers.Binary;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BcSurvey;

/// <summary>Header facts the translator's PeImage does not expose.</summary>
public sealed record PeFacts(string Kind, ushort Subsystem, ushort DllCharacteristics, bool HasClr, bool HasBoundImports, bool HasDelayImports, bool HasRelocDir, bool HasTlsDir, bool RelocsStripped, uint SizeOfImage, int ExportCount, string OsVersion, bool HasLoadConfig)
{
    public static string SubsystemName(ushort s) => s switch { 1 => "native", 2 => "gui", 3 => "console", 9 => "wince-gui", 10 => "efi", _ => "sub" + s };

    public static PeFacts Read(byte[] b, PeImage image)
    {
        var pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        var coff = pe + 4; var opt = coff + 20;
        var chars = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(coff + 18));
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt));
        var dllChars = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 70));
        var sub = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 68));
        var nDirs = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 92));
        bool Dir(int i) => i < nDirs && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 96 + 8 * i)) != 0;
        var kind = (chars & 0x2000) != 0 ? "dll" : "exe";
        return new PeFacts(kind, sub, dllChars, Dir(14), Dir(11), Dir(13), Dir(5), Dir(9), (chars & 1) != 0, image.SizeOfImage, image.Exports.Count,
            $"os {BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 40))}.{BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 42))} / subsystem {BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 48))}.{BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 50))}", Dir(10));
    }
}

/// <summary>Static facts derived from the recovered control-flow graph.</summary>
public sealed class CfgView
{
    public PeImage Image { get; }
    public ControlFlowGraph Cfg { get; }
    public uint[] Starts { get; }                 // instruction RVAs, ascending
    public GuestInstruction[] Insts { get; }
    public uint[] Entries { get; }                // function entries, ascending
    private readonly Dictionary<uint, int> index = [];
    public Dictionary<uint, int> ThunkOf { get; } = []; // function entry -> import index (function is exactly `jmp [iat]`)
    public Dictionary<uint, List<uint>> Edges { get; } = []; // function entry -> callee/tail-jump function entries
    private readonly Dictionary<uint, int> iatToImport = [];

    public CfgView(PeImage image, ControlFlowGraph cfg)
    {
        Image = image; Cfg = cfg;
        Starts = [.. cfg.Instructions.Keys]; Insts = [.. cfg.Instructions.Values];
        for (var i = 0; i < Starts.Length; i++) index[Starts[i]] = i;
        Entries = [.. cfg.FunctionEntries];
        for (var k = 0; k < image.Imports.Count; k++) if (!image.Imports[k].IsDelay) iatToImport[image.ImageBase + image.Imports[k].IatRva] = k;
        foreach (var e in Entries)
            if (index.TryGetValue(e, out var ix) && IatSlotOf(Insts[ix].Instr, jmpOrCall: true) is { } slot && iatToImport.TryGetValue(slot, out var imp)
                && Insts[ix].Instr.Code == Code.Jmp_rm32)
                ThunkOf[e] = imp;
        foreach (var g in Insts)
        {
            var f = FunctionOf(g.Rva);
            var i = g.Instr;
            uint? target = null;
            if (i.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch && i.Op0Kind == OpKind.NearBranch32) target = i.NearBranch32 - image.ImageBase;
            if (target is null) continue;
            var tf = FunctionOf(target.Value);
            if (tf == f) continue;
            if (!Edges.TryGetValue(f, out var l)) Edges[f] = l = [];
            l.Add(tf);
        }
    }

    public uint FunctionOf(uint rva)
    {
        var lo = 0; var hi = Entries.Length - 1; var best = 0u;
        while (lo <= hi) { var mid = (lo + hi) / 2; if (Entries[mid] <= rva) { best = Entries[mid]; lo = mid + 1; } else hi = mid - 1; }
        return best;
    }

    public uint FunctionEnd(uint entry)
    {
        var p = Array.BinarySearch(Entries, entry);
        return p >= 0 && p + 1 < Entries.Length ? Entries[p + 1] : (Starts.Length > 0 ? Cfg.Instructions[Starts[^1]].NextRva : entry);
    }

    /// <summary>Absolute memory operand (no base/index/segment) of a call/jmp [m32], or of any instruction when jmpOrCall is false.</summary>
    public uint? IatSlotOf(in Instruction i, bool jmpOrCall)
    {
        if (jmpOrCall && i.Code is not (Code.Call_rm32 or Code.Jmp_rm32)) return null;
        for (var op = 0; op < i.OpCount; op++)
            if (i.GetOpKind(op) == OpKind.Memory && i.MemoryBase == Register.None && i.MemoryIndex == Register.None && i.SegmentPrefix == Register.None && i.MemoryDisplSize == 4)
                return i.MemoryDisplacement32;
        return null;
    }

    private Dictionary<int, List<ImportSite>>? allSites;

    /// <summary>Sites that call or jump through an import slot, directly or through a one-instruction thunk. Also IAT loads (mov reg,[slot]). One pass, cached.</summary>
    public IReadOnlyDictionary<int, List<ImportSite>> AllImportSites()
    {
        if (allSites is not null) return allSites;
        var map = new Dictionary<int, List<ImportSite>>();
        void Add(int k, ImportSite s) { if (!map.TryGetValue(k, out var l)) map[k] = l = []; l.Add(s); }
        foreach (var g in Insts)
        {
            var i = g.Instr;
            if (IatSlotOf(i, false) is { } slot && iatToImport.TryGetValue(slot, out var imp))
            {
                var kind = i.Code is Code.Call_rm32 ? "call" : i.Code is Code.Jmp_rm32 ? "jmp" : "load";
                if (kind == "jmp" && ThunkOf.ContainsKey(g.Rva)) kind = "thunk";
                Add(imp, new ImportSite(g.Rva, kind, g.Speculative, FunctionOf(g.Rva)));
            }
            else if (i.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch && i.Op0Kind == OpKind.NearBranch32
                && ThunkOf.TryGetValue(i.NearBranch32 - Image.ImageBase, out var ti))
                Add(ti, new ImportSite(g.Rva, i.FlowControl == FlowControl.Call ? "call-thunk" : "jmp-thunk", g.Speculative, FunctionOf(g.Rva)));
        }
        return allSites = map;
    }

    public IEnumerable<ImportSite> ImportSites(int importIndex) => AllImportSites().TryGetValue(importIndex, out var l) ? l : [];

    public HashSet<uint> Closure(IEnumerable<uint> rootFunctions)
    {
        var seen = new HashSet<uint>(); var q = new Queue<uint>();
        foreach (var r in rootFunctions) if (seen.Add(r)) q.Enqueue(r);
        while (q.Count > 0)
            if (Edges.TryGetValue(q.Dequeue(), out var l)) foreach (var t in l) if (seen.Add(t)) q.Enqueue(t);
        return seen;
    }

    public int IndexOf(uint rva) => index.TryGetValue(rva, out var i) ? i : -1;
    public bool InExec(uint va)
    {
        if (va < Image.ImageBase) return false;
        var r = va - Image.ImageBase;
        return Image.Sections.Any(s => s.IsExecutable && r >= s.VirtualAddress && r < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
    }
}

public sealed record ImportSite(uint Rva, string Kind, bool Speculative, uint Function);
