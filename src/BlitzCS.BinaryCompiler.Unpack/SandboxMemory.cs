using System.Buffers.Binary;

namespace BlitzCS.BinaryCompiler.Unpack;

/// <summary>The unpacker refuses to continue. Code is a stable <c>UNPACK_*</c> identifier; the message names the evidence.</summary>
public sealed class UnpackRefusalException(string code, string message, uint? address = null) : Exception(message)
{
    public string Code { get; } = code;
    public uint? Address { get; } = address;
}

/// <summary>One flat, zero-initialised region of the sandbox address space. Permissions are deliberately not modelled: the sandbox is a managed byte array, never native memory.</summary>
internal sealed class SandboxRegion(string name, uint baseAddress, byte[] data, bool track)
{
    public string Name { get; } = name;
    public uint Base { get; } = baseAddress;
    public byte[] Data { get; } = data;
    public uint End => Base + (uint)Data.Length;
    public bool Track { get; } = track;
    /// <summary>Per 4 KiB page: written / executed / decoded (decode cache must be dropped on a write to a decoded page).</summary>
    public bool[] Written { get; } = new bool[(data.Length + 4095) / 4096];
    public bool[] Executed { get; } = new bool[(data.Length + 4095) / 4096];
    public bool[] Decoded { get; } = new bool[(data.Length + 4095) / 4096];
    /// <summary>Read offsets that betray a debugger probe (PEB.BeingDebugged and NtGlobalFlag); null for ordinary regions.</summary>
    public Func<uint, string?>? ReadWatch { get; init; }
    public long BytesWritten;
}

internal sealed class SandboxMemory
{
    private readonly List<SandboxRegion> regions = [];
    private SandboxRegion? last;
    public Action? OnDecodedPageWritten { get; set; }

    public IReadOnlyList<SandboxRegion> Regions => regions;

    public SandboxRegion Add(SandboxRegion r)
    {
        foreach (var o in regions)
            if (r.Base < o.End && o.Base < r.End) throw new UnpackRefusalException("UNPACK_ADDRESS_CONFLICT", $"Sandbox region '{r.Name}' (0x{r.Base:X8}-0x{r.End:X8}) overlaps '{o.Name}' (0x{o.Base:X8}-0x{o.End:X8}).");
        regions.Add(r);
        return r;
    }

    public bool Overlaps(uint start, uint length) => regions.Any(o => start < o.End && o.Base < (ulong)start + length);

    public SandboxRegion? Find(uint address)
    {
        var l = last;
        if (l is not null && address >= l.Base && address < l.End) return l;
        foreach (var r in regions)
            if (address >= r.Base && address < r.End) return last = r;
        return null;
    }

    private SandboxRegion Locate(uint address, int length, bool write)
    {
        var r = Find(address);
        if (r is null || (ulong)address + (uint)length > r.End)
            throw new UnpackRefusalException("UNPACK_UNMAPPED_ACCESS", $"The stub {(write ? "wrote" : "read")} {length} byte(s) at unmapped address 0x{address:X8}.", address);
        return r;
    }

    public uint Read(uint address, int size)
    {
        var r = Locate(address, size, false);
        var o = (int)(address - r.Base);
        if (r.ReadWatch is not null && r.ReadWatch(address - r.Base) is { } why)
            throw new UnpackRefusalException("UNPACK_ANTI_DEBUG", $"The stub reads {why} (0x{address:X8}); anti-debug probes are refused rather than answered.", address);
        return size switch
        {
            1 => r.Data[o],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(r.Data.AsSpan(o)),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(r.Data.AsSpan(o)),
        };
    }

    public void Write(uint address, int size, uint value)
    {
        var r = Locate(address, size, true);
        var o = (int)(address - r.Base);
        switch (size)
        {
            case 1: r.Data[o] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(r.Data.AsSpan(o), (ushort)value); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(r.Data.AsSpan(o), value); break;
        }
        if (r.Track) Mark(r, o, size);
    }

    public void WriteBytes(uint address, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        var r = Locate(address, bytes.Length, true);
        var o = (int)(address - r.Base);
        bytes.CopyTo(r.Data.AsSpan(o));
        if (r.Track) Mark(r, o, bytes.Length);
    }

    public byte[] ReadBytes(uint address, int length)
    {
        var r = Locate(address, length, false);
        return r.Data.AsSpan((int)(address - r.Base), length).ToArray();
    }

    public string? ReadCString(uint address, int max = 260)
    {
        var r = Find(address);
        if (r is null) return null;
        var o = (int)(address - r.Base);
        var e = o;
        while (e < r.Data.Length && r.Data[e] != 0 && e - o < max) e++;
        if (e >= r.Data.Length || r.Data[e] != 0) return null;
        return System.Text.Encoding.Latin1.GetString(r.Data, o, e - o);
    }

    private void Mark(SandboxRegion r, int offset, int length)
    {
        r.BytesWritten += length;
        for (var p = offset >> 12; p <= (offset + length - 1) >> 12; p++)
        {
            r.Written[p] = true;
            if (r.Decoded[p]) { Array.Clear(r.Decoded); OnDecodedPageWritten?.Invoke(); }
        }
    }
}
