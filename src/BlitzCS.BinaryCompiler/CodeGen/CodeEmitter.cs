using Iced.Intel;

namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>Collects x64 instructions with symbolic labels and encodes them with Iced's BlockEncoder (RIP = 0 at offset 0).</summary>
internal sealed class CodeEmitter
{
    // Virtual instruction pointers live far away from any real address so RIP-relative absolute targets never alias them.
    private const ulong IpBase = 1UL << 62;
    private readonly List<Instruction> insts = [];
    private readonly Dictionary<ulong, ulong> alias = [];
    private readonly Dictionary<ulong, int> index = [];
    private ulong next = IpBase;
    private ulong pending;

    public int Count => insts.Count;
    public IReadOnlyList<Instruction> Instructions => insts;

    public ulong NewLabel() => next++;

    /// <summary>Instruction indexes that should start at a multiple of <see cref="AlignBytes"/> in the final code (best effort).</summary>
    private readonly HashSet<int> alignBefore = [];
    public const int AlignBytes = 16;
    private const int MaxLayoutPasses = 16;

    /// <summary>Requests that the next emitted instruction be aligned (padding is a multi-byte NOP executed only on fall-through).</summary>
    public void AlignNext() => alignBefore.Add(insts.Count);

    public void Place(ulong label)
    {
        index[label] = insts.Count;
        if (pending == 0) pending = label; else alias[label] = pending;
    }

    public void Add(Instruction instruction)
    {
        instruction.IP = pending != 0 ? pending : next++;
        pending = 0;
        insts.Add(instruction);
    }

    public sealed class Result(byte[] code, Dictionary<ulong, uint> labelOffsets)
    {
        public byte[] Code { get; } = code;
        public uint OffsetOf(ulong label) => labelOffsets[label];
    }

    private sealed class ListWriter : CodeWriter
    {
        public readonly List<byte> Bytes = [];
        public override void WriteByte(byte value) => Bytes.Add(value);
    }

    private Dictionary<int, int> finalPads = [], finalAlignOf = [];

    /// <param name="seed">Emitter of an earlier pass over the same instruction sequence; its alignment padding is the starting point.</param>
    public Result Encode(CodeEmitter? seed = null)
    {
        if (pending != 0) Add(Instruction.Create(Code.Int3));
        for (var n = 0; n < insts.Count; n++)
        {
            var ins = insts[n];
            if (ins.Op0Kind == OpKind.NearBranch64 && alias.TryGetValue(ins.NearBranch64, out var real))
            {
                ins.NearBranch64 = real;
                insts[n] = ins;
            }
        }
        // Alignment: encode, measure how much NOP padding each aligned instruction needs, re-encode with the padding. Padding changes
        // later offsets (and branch sizes), so iterate a few times; the result is always correct, alignment is best effort.
        // A previous generation pass of the identical instruction sequence (the translator lays code out twice) seeds the padding, so this one
        // usually needs a single verifying encode instead of the full iteration.
        var pads = seed is null ? new Dictionary<int, int>() : new Dictionary<int, int>(seed.finalPads);
        var alignOf = seed is null ? new Dictionary<int, int>() : new Dictionary<int, int>(seed.finalAlignOf);
        ListWriter writer; BlockEncoderResult result; List<Instruction> list; int[] newIndex;
        for (var pass = 0; ; pass++)
        {
            (writer, result, list, newIndex) = EncodeOnce(pads);
            if (alignBefore.Count == 0 || pass == MaxLayoutPasses - 1) break;
            if (alignOf.Count == 0) ChooseAlignments(alignOf, result, list, newIndex, writer.Bytes.Count);
            var changed = false;
            var shift = 0;                                   // growth caused by pads already re-decided in this pass (earlier code moves later code)
            foreach (var at in alignBefore.Order())
            {
                var padAt = pads.GetValueOrDefault(at);
                var start = (int)result.NewInstructionOffsets[newIndex[at]] - padAt + shift;   // where this padding would begin
                var want = (-start) & (alignOf[at] - 1);
                while (want < padAt) want += alignOf[at];   // pads only grow: branch distances then only grow too (short->near, never back), which guarantees convergence
                if (want != padAt) { pads[at] = want; changed = true; shift += want - padAt; }
            }
            if (!changed) break;
        }
        finalPads = pads; finalAlignOf = alignOf;
        var offsets = new Dictionary<ulong, uint>();
        foreach (var (label, at) in index) offsets[label] = result.NewInstructionOffsets[newIndex[at]];
        return new Result(writer.Bytes.ToArray(), offsets);
    }


    /// <summary>
    /// 16-byte alignment by default; a loop whose body (label to its farthest backward branch) is small enough to fit one 32-byte
    /// instruction-fetch window gets 32-byte alignment so the whole loop stays in a single window (the translation is a little
    /// longer than the original, which can push a loop that used to fit across a window boundary).
    /// </summary>
    private void ChooseAlignments(Dictionary<int, int> alignOf, BlockEncoderResult result, List<Instruction> list, int[] newIndex, int totalBytes)
    {
        var lastBranchFrom = new Dictionary<ulong, int>();                      // target IP -> farthest list index jumping to it
        for (var n = 0; n < list.Count; n++)
            if (list[n].Op0Kind == OpKind.NearBranch64 && list[n].FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
                lastBranchFrom[list[n].NearBranch64] = n;
        int EndOf(int i) => i + 1 < list.Count ? (int)result.NewInstructionOffsets[i + 1] : totalBytes;
        foreach (var at in alignBefore)
        {
            var head = list[newIndex[at]];
            var align = AlignBytes;
            if (lastBranchFrom.TryGetValue(head.IP, out var from) && from > newIndex[at])
            {
                var extent = EndOf(from) - (int)result.NewInstructionOffsets[newIndex[at]];
                if (extent <= 32) align = 32;
            }
            alignOf[at] = align;
        }
    }

    private (ListWriter, BlockEncoderResult, List<Instruction>, int[]) EncodeOnce(Dictionary<int, int> pads)
    {
        var list = new List<Instruction>(insts.Count + pads.Count);
        var newIndex = new int[insts.Count + 1];
        for (var n = 0; n < insts.Count; n++)
        {
            if (pads.TryGetValue(n, out var pad) && pad > 0)
                for (var left = pad; left > 0;)
                {
                    var take = Math.Min(left, 11);
                    var nop = Instruction.CreateDeclareByte(NopBytes[take]);
                    nop.IP = next++;
                    list.Add(nop); left -= take;
                }
            newIndex[n] = list.Count;
            list.Add(insts[n]);
        }
        newIndex[insts.Count] = list.Count;
        var writer = new ListWriter();
        var block = new InstructionBlock(writer, list, 0);
        if (!BlockEncoder.TryEncode(64, block, out var error, out var result, BlockEncoderOptions.ReturnNewInstructionOffsets))
            throw new InvalidOperationException("Host code encoding failed: " + error);
        return (writer, result, list, newIndex);
    }

    /// <summary>Recommended multi-byte NOPs (Intel SDM / GNU as), 1..11 bytes; a pad of 12..15 bytes uses two instructions.</summary>
    private static readonly byte[][] NopBytes =
    [
        [],
        [0x90],
        [0x66, 0x90],
        [0x0F, 0x1F, 0x00],
        [0x0F, 0x1F, 0x40, 0x00],
        [0x0F, 0x1F, 0x44, 0x00, 0x00],
        [0x66, 0x0F, 0x1F, 0x44, 0x00, 0x00],
        [0x0F, 0x1F, 0x80, 0x00, 0x00, 0x00, 0x00],
        [0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x66, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x66, 0x2E, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x66, 0x66, 0x2E, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
    ];

    /// <summary>Validates that one instruction is encodable in 64-bit mode without touching emitter state.</summary>
    public static bool TryEncodeProbe(in Instruction instruction, out string error)
    {
        var enc = Encoder.Create(64, new ListWriter());
        if (enc.TryEncode(instruction, 0x1000, out _, out var e)) { error = ""; return true; }
        error = e ?? "unencodable";
        return false;
    }
}
