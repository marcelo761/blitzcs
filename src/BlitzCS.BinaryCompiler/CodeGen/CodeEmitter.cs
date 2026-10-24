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

    public Result Encode()
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
        var writer = new ListWriter();
        var block = new InstructionBlock(writer, insts, 0);
        if (!BlockEncoder.TryEncode(64, block, out var error, out var result, BlockEncoderOptions.ReturnNewInstructionOffsets))
            throw new InvalidOperationException("Host code encoding failed: " + error);
        var offsets = new Dictionary<ulong, uint>();
        foreach (var (label, at) in index) offsets[label] = result.NewInstructionOffsets[at];
        return new Result(writer.Bytes.ToArray(), offsets);
    }

    /// <summary>Validates that one instruction is encodable in 64-bit mode without touching emitter state.</summary>
    public static bool TryEncodeProbe(in Instruction instruction, out string error)
    {
        var enc = Encoder.Create(64, new ListWriter());
        if (enc.TryEncode(instruction, 0x1000, out _, out var e)) { error = ""; return true; }
        error = e ?? "unencodable";
        return false;
    }
}
