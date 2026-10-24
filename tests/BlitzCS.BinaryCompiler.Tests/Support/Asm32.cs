namespace BlitzCS.BinaryCompiler.Tests.Support;

/// <summary>Tiny hand assembler for relocatable fixture code: absolute 32-bit operands are recorded as HIGHLOW relocations.</summary>
internal sealed class Asm32
{
    private readonly List<byte> bytes = [];
    private readonly List<uint> relocs = [];
    public uint Here => (uint)bytes.Count;
    public byte[] Bytes => [.. bytes];
    public IReadOnlyList<uint> Relocs => relocs;

    public Asm32 Raw(params byte[] b) { bytes.AddRange(b); return this; }
    public Asm32 U32(uint v) { bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    /// <summary>Absolute address of an RVA at the fixture's preferred base, with a relocation entry.</summary>
    public Asm32 Abs(uint rva) { relocs.Add(PeFixtureBuilder.TextRva + Here); return U32(PeFixtureBuilder.ImageBase + rva); }

    public Asm32 CallIat(uint iatSlotRva) => Raw(0xFF, 0x15).Abs(iatSlotRva);           // call dword ptr [abs]
    public Asm32 LoadEaxAbs(uint rva) => Raw(0xA1).Abs(rva);                              // mov eax, [abs]
    public Asm32 StoreEaxAbs(uint rva) => Raw(0xA3).Abs(rva);                             // mov [abs], eax
    public Asm32 IncAbs(uint rva) => Raw(0xFF, 0x05).Abs(rva);                            // inc dword ptr [abs]
    public Asm32 Ret() => Raw(0xC3);
    public Asm32 Ret(ushort pop) => Raw(0xC2, (byte)pop, (byte)(pop >> 8));
    public Asm32 AddEsp(byte n) => Raw(0x83, 0xC4, n);
    public Asm32 PushArgSlot(byte disp) => Raw(0xFF, 0x74, 0x24, disp);                    // push dword ptr [esp+disp]
    public Asm32 MovEaxImm(uint v) => Raw(0xB8).U32(v);
    /// <summary>Pushes the n 32-bit stack arguments of the current cdecl/stdcall function again, last argument first (same order, same values).</summary>
    public Asm32 ForwardArgs(int n) { for (var i = 0; i < n; i++) PushArgSlot((byte)(4 * n)); return this; }
    /// <summary>Starts a function: returns its offset in the code.</summary>
    public uint Func() => Here;
}
