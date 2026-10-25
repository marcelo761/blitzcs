using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>What the instructions reachable from a function's entry say about how it returns.</summary>
/// <param name="PopBytes">Distinct <c>ret imm16</c> values found (0 for a plain <c>ret</c>).</param>
/// <param name="IndirectThunk">The function (or a tail jump it takes) ends in <c>jmp [absolute]</c> (an import thunk): the real callee is unknown here.</param>
/// <param name="OpaqueIndirect">A <c>jmp reg</c> / register-based <c>jmp [..]</c> whose targets are unknown.</param>
/// <param name="Undecoded">Control reached bytes the lifter did not decode.</param>
public sealed record ExportReturns(IReadOnlySet<int> PopBytes, bool IndirectThunk, bool OpaqueIndirect, bool Undecoded)
{
    public int Found => PopBytes.Count;

    /// <summary>The single pop count when the evidence is complete and unanimous, else null.</summary>
    public int? Unanimous => PopBytes.Count == 1 && !IndirectThunk && !OpaqueIndirect && !Undecoded ? PopBytes.First() : null;

    public override string ToString() =>
        $"ret pops: {(PopBytes.Count == 0 ? "none found" : string.Join("/", PopBytes.Order().Select(p => p == 0 ? "ret" : "ret " + p)))}"
        + (IndirectThunk ? ", ends in an indirect jump through memory (import thunk)" : "") + (OpaqueIndirect ? ", has a register-indirect jump" : "") + (Undecoded ? ", reaches undecoded bytes" : "");
}

/// <summary>
/// Infers the calling convention of a function from its own code, never from a name or a guess: every <c>ret</c> reachable from the entry through direct jumps,
/// conditional branches, fall-through and jump tables (callees are not followed; a call returns to its next instruction) is collected.
/// Plain <c>ret</c> only means the caller cleans the arguments (cdecl); one <c>ret N</c> value means the callee pops N bytes (stdcall/thiscall).
/// </summary>
public static class ExportReturnScanner
{
    private const int MaxInstructions = 200_000, MaxTableEntries = 1024;

    public static ExportReturns Scan(ControlFlowGraph cfg, PeImage image, byte[] mapped, uint entryRva)
    {
        var pops = new HashSet<int>();
        bool thunk = false, opaque = false, undecoded = false;
        var seen = new HashSet<uint>();
        var work = new Stack<uint>();
        work.Push(entryRva);
        while (work.Count > 0 && seen.Count < MaxInstructions)
        {
            var rva = work.Pop();
            while (seen.Add(rva))
            {
                if (!cfg.Instructions.TryGetValue(rva, out var gi)) { undecoded = true; break; }
                var ins = gi.Instr;
                switch (ins.FlowControl)
                {
                    case FlowControl.Return:
                        pops.Add(ins.Op0Kind is OpKind.Immediate16 ? ins.Immediate16 : 0);
                        goto next;
                    case FlowControl.UnconditionalBranch:
                        if (ins.Op0Kind == OpKind.NearBranch32 && ins.NearBranchTarget >= image.ImageBase) { rva = (uint)(ins.NearBranchTarget - image.ImageBase); continue; }
                        opaque = true;
                        goto next;
                    case FlowControl.ConditionalBranch:
                        if (ins.Op0Kind == OpKind.NearBranch32 && ins.NearBranchTarget >= image.ImageBase) work.Push((uint)(ins.NearBranchTarget - image.ImageBase));
                        else opaque = true;
                        rva = gi.NextRva; continue;
                    case FlowControl.IndirectBranch:
                        if (ins.Op0Kind == OpKind.Memory && ins.MemoryBase == Register.None && ins.MemoryIndex == Register.None) thunk = true;
                        else if (ins.Op0Kind == OpKind.Memory && ins.MemoryBase == Register.None && ins.MemoryIndexScale == 4)
                        {
                            var table = ins.MemoryDisplacement32;
                            var n = 0;
                            for (; n < MaxTableEntries; n++)
                            {
                                var slot = (long)table - image.ImageBase + 4L * n;
                                if (slot < 0 || slot + 4 > mapped.Length) break;
                                var target = BitConverter.ToUInt32(mapped, (int)slot);
                                if (target < image.ImageBase || !cfg.Instructions.ContainsKey(target - image.ImageBase)) break;
                                work.Push(target - image.ImageBase);
                            }
                            if (n == 0) opaque = true;
                        }
                        else opaque = true;
                        goto next;
                    case FlowControl.Interrupt:
                    case FlowControl.Exception:
                        goto next;
                    default: // Next, Call, IndirectCall, XbeginXabort...: execution continues after the instruction
                        rva = gi.NextRva; continue;
                }
            }
            next:;
        }
        return new ExportReturns(pops, thunk, opaque, undecoded);
    }
}
