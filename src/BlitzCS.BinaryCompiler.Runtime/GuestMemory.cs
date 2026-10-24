using System.Text;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>A mapped guest range. PageWritable (one flag per page from Start) is null when the whole range is writable.</summary>
internal sealed record GuestRegion(ulong Start, ulong End, bool[]? PageWritable)
{
    internal bool CoversWrite(ulong from, ulong to)
    {
        if (PageWritable is null) return true;
        var ps = (ulong)Libc.PageSize;
        for (var p = (from - Start) / ps; p <= (to - 1 - Start) / ps; p++)
            if (!PageWritable[p]) return false;
        return true;
    }
}

/// <summary>
/// Bounds-checked access to guest memory. Guest address == host address, but managed code must never hold raw
/// pointers into it: every access is validated against the mapped ranges (image, stacks, scratch buffers) and, for
/// writes, against page writability, so a bad guest pointer becomes <see cref="GuestMemoryException"/> instead of SIGSEGV.
/// </summary>
public sealed unsafe class GuestMemory
{
    private readonly GuestModule module;
    private readonly object gate = new();
    private volatile GuestRegion[] regions = [];

    internal GuestMemory(GuestModule module) => this.module = module;

    internal void Add(GuestRegion region)
    {
        lock (gate) regions = [.. regions, region];
    }

    internal void Remove(ulong start)
    {
        lock (gate) regions = [.. regions.Where(r => r.Start != start)];
    }

    internal void Clear()
    {
        lock (gate) regions = [];
    }

    public bool IsMapped(uint address, uint length)
    {
        module.EnterCall();
        try { return Find(address, length, false) is not null; }
        finally { module.ExitCall(); }
    }

    private GuestRegion? Find(uint address, long length, bool write)
    {
        var end = (ulong)address + (ulong)length;
        foreach (var r in regions)
            if (address >= r.Start && end <= r.End)
                return !write || r.CoversWrite(address, Math.Max(end, address + 1UL)) ? r : null;
        return null;
    }

    private byte* Check(uint address, long length, bool write)
    {
        if (length < 0 || Find(address, length, write) is null) throw new GuestMemoryException(address, length, write);
        return (byte*)(nuint)address;
    }

    public byte ReadByte(uint address) { module.EnterCall(); try { return *Check(address, 1, false); } finally { module.ExitCall(); } }
    public ushort ReadUInt16(uint address) { module.EnterCall(); try { return Unaligned16(Check(address, 2, false)); } finally { module.ExitCall(); } }
    public uint ReadUInt32(uint address) { module.EnterCall(); try { return Unaligned32(Check(address, 4, false)); } finally { module.ExitCall(); } }
    public float ReadSingle(uint address) => BitConverter.UInt32BitsToSingle(ReadUInt32(address));

    public void WriteByte(uint address, byte value) { module.EnterCall(); try { *Check(address, 1, true) = value; } finally { module.ExitCall(); } }
    public void WriteUInt32(uint address, uint value) { module.EnterCall(); try { Store32(Check(address, 4, true), value); } finally { module.ExitCall(); } }
    public void WriteSingle(uint address, float value) => WriteUInt32(address, BitConverter.SingleToUInt32Bits(value));

    public void Read(uint address, Span<byte> destination)
    {
        module.EnterCall();
        try { new ReadOnlySpan<byte>(Check(address, destination.Length, false), destination.Length).CopyTo(destination); }
        finally { module.ExitCall(); }
    }

    public void Write(uint address, ReadOnlySpan<byte> source)
    {
        module.EnterCall();
        try { source.CopyTo(new Span<byte>(Check(address, source.Length, true), source.Length)); }
        finally { module.ExitCall(); }
    }

    /// <summary>Reads a NUL-terminated single-byte (Latin-1) string; throws if no terminator is found within <paramref name="maxLength"/> bytes or the mapping ends first.</summary>
    public string ReadAnsiString(uint address, int maxLength = 65536)
    {
        module.EnterCall();
        try
        {
            var sb = new StringBuilder();
            for (var i = 0; i <= maxLength; i++)
            {
                var b = *Check(address + (uint)i, 1, false);
                if (b == 0) return sb.ToString();
                sb.Append((char)b);
            }
            throw new GuestMemoryException(address, maxLength, false);
        }
        finally { module.ExitCall(); }
    }

    /// <summary>Allocates a zeroed scratch buffer in the low 4 GiB. Dispose the buffer (or the module) to free it.</summary>
    public GuestBuffer Allocate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256 * 1024 * 1024);
        module.EnterCall();
        try { return module.AllocateBuffer(size); }
        finally { module.ExitCall(); }
    }

    private static ushort Unaligned16(byte* p) => (ushort)(p[0] | p[1] << 8);
    private static uint Unaligned32(byte* p) => (uint)(p[0] | p[1] << 8 | p[2] << 16 | p[3] << 24);
    private static void Store32(byte* p, uint v) { p[0] = (byte)v; p[1] = (byte)(v >> 8); p[2] = (byte)(v >> 16); p[3] = (byte)(v >> 24); }
}

/// <summary>Owns one guest-visible scratch buffer. Data is copied in/out; no managed pointer is ever given to the guest.</summary>
public sealed class GuestBuffer : IDisposable
{
    private readonly GuestModule module;
    private readonly nint map;
    private readonly nuint mapLength;
    private int disposed;

    internal GuestBuffer(GuestModule module, nint map, nuint mapLength, int size)
    {
        this.module = module;
        this.map = map;
        this.mapLength = mapLength;
        Length = size;
        Address = checked((uint)map);
    }

    /// <summary>Guest address of the first byte (valid until disposal).</summary>
    public uint Address { get; }
    public int Length { get; }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Write(int offset, ReadOnlySpan<byte> source)
    {
        Bounds(offset, source.Length);
        module.Memory.Write(Address + (uint)offset, source);
    }

    public void Read(int offset, Span<byte> destination)
    {
        Bounds(offset, destination.Length);
        module.Memory.Read(Address + (uint)offset, destination);
    }

    public byte[] ToArray()
    {
        var bytes = new byte[Length];
        Read(0, bytes);
        return bytes;
    }

    private void Bounds(int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (offset < 0 || count < 0 || offset > Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range [{offset}, {offset + count}) is outside the {Length}-byte guest buffer.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        module.ReleaseBuffer(this, map, mapLength);
    }
}
