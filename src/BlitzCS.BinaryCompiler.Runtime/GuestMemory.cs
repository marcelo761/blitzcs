using System.Text;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>What backs a mapped guest range.</summary>
public enum GuestPageKind
{
    /// <summary>Nothing is mapped at the address.</summary>
    Unmapped,
    /// <summary>A page of an executable section of a translated image: the guest code the translator consumed.</summary>
    ImageCode,
    /// <summary>Any other page of a translated image (headers, data, import stubs).</summary>
    ImageData,
    /// <summary>A scratch buffer from <see cref="GuestMemory.Allocate"/> (the guest's VirtualAlloc and heap memory).</summary>
    Buffer,
    /// <summary>Stacks, TEB/PEB and other runtime-owned ranges.</summary>
    Other,
}

/// <summary>One page as the guest sees it. <see cref="Tag"/> is the caller-defined protection last stored by <see cref="GuestMemory.TryProtect"/> (0 = never set).</summary>
public readonly record struct GuestPageInfo(GuestPageKind Kind, uint RegionStart, uint RegionEnd, bool Readable, bool Writable, uint Tag, string? Owner);

/// <summary>A mapped guest range. PageWritable/PageReadable/PageTag (one entry per page from Start) are null while the whole range is readable and writable.</summary>
internal sealed record GuestRegion(ulong Start, ulong End, bool[]? PageWritable)
{
    public bool[]? PageWritable { get; set; } = PageWritable;
    internal bool[]? PageReadable { get; set; }
    internal uint[]? PageTag { get; set; }
    internal bool[]? PageExecutable { get; init; }
    internal GuestPageKind Kind { get; init; } = GuestPageKind.Other;
    internal string? Owner { get; init; }

    internal bool CoversWrite(ulong from, ulong to)
    {
        var w = PageWritable;
        if (w is null) return true;
        var ps = (ulong)PlatformMemory.Current.PageSize;
        for (var p = (from - Start) / ps; p <= (to - 1 - Start) / ps; p++)
            if (!w[p]) return false;
        return true;
    }

    internal bool CoversRead(ulong from, ulong to)
    {
        var r = PageReadable;
        if (r is null) return true;
        var ps = (ulong)PlatformMemory.Current.PageSize;
        for (var p = (from - Start) / ps; p <= (to - 1 - Start) / ps; p++)
            if (!r[p]) return false;
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
    private volatile GuestMemory[] peers = [];

    internal GuestMemory(GuestModule module) => this.module = module;

    /// <summary>
    /// Marks this module as the owner of process-wide allocations made on behalf of every module of its set (the shared Win32 state): the set refuses to unload it
    /// while other modules are loaded, because their heap blocks and handles live in its address space.
    /// </summary>
    public void PinAsProcessAnchor() => module.IsProcessAnchor = true;

    internal void Add(GuestRegion region)
    {
        lock (gate) regions = [.. regions, region];
    }

    internal void Remove(ulong start)
    {
        lock (gate) regions = [.. regions.Where(r => r.Start != start)];
    }

    /// <summary>Makes the regions of <paramref name="peer"/> (another module of the same <see cref="GuestModuleSet"/>) addressable here: one process, one guest address space.</summary>
    internal void LinkPeer(GuestMemory peer)
    {
        lock (gate) if (!peers.Contains(peer)) peers = [.. peers, peer];
    }

    internal void UnlinkPeer(GuestMemory peer)
    {
        lock (gate) peers = [.. peers.Where(p => p != peer)];
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
                return Accessible(r, address, end, write) ? r : null;
        foreach (var peer in peers)
            foreach (var r in peer.regions)
                if (address >= r.Start && end <= r.End)
                    return Accessible(r, address, end, write) ? r : null;
        return null;
    }

    private static bool Accessible(GuestRegion r, uint address, ulong end, bool write)
    {
        var to = Math.Max(end, address + 1UL);
        return (write ? r.CoversWrite(address, to) : r.CoversRead(address, to)) && (!write || r.CoversRead(address, to));
    }

    private static GuestPageInfo Describe(GuestRegion r, uint page, uint ps)
    {
        var i = (int)((page - r.Start) / ps);
        var writable = r.PageWritable is null || r.PageWritable[i];
        var readable = r.PageReadable is null || r.PageReadable[i];
        var kind = r.Kind == GuestPageKind.ImageData && r.PageExecutable is { } x && x[i] ? GuestPageKind.ImageCode : r.Kind;
        return new GuestPageInfo(kind, (uint)r.Start, (uint)r.End, readable, writable, r.PageTag?[i] ?? 0, r.Owner);
    }

    private GuestRegion? FindRegion(uint address, out GuestMemory owner)
    {
        foreach (var r in regions)
            if (address >= r.Start && address < r.End) { owner = this; return r; }
        foreach (var peer in peers)
            foreach (var r in peer.regions)
                if (address >= r.Start && address < r.End) { owner = peer; return r; }
        owner = this;
        return null;
    }

    /// <summary>Describes the page containing <paramref name="address"/>; false (Kind Unmapped) when nothing is mapped there.</summary>
    public bool TryQueryPage(uint address, out GuestPageInfo info)
    {
        module.EnterCall();
        try
        {
            var ps = (uint)PlatformMemory.Current.PageSize;
            var page = address & ~(ps - 1);
            if (FindRegion(page, out _) is { } r) { info = Describe(r, page, ps); return true; }
            info = new GuestPageInfo(GuestPageKind.Unmapped, 0, 0, false, false, 0, null);
            return false;
        }
        finally { module.ExitCall(); }
    }

    /// <summary>The lowest mapped address above <paramref name="address"/> (own and peer regions), or 0x100000000 when there is none. For VirtualQuery of free space.</summary>
    public ulong NextMappedAddress(uint address)
    {
        module.EnterCall();
        try
        {
            var best = 0x1_0000_0000UL;
            foreach (var r in regions) if (r.Start > address && r.Start < best) best = r.Start;
            foreach (var peer in peers) foreach (var r in peer.regions) if (r.Start > address && r.Start < best) best = r.Start;
            return best;
        }
        finally { module.ExitCall(); }
    }

    /// <summary>
    /// Changes the host protection of whole pages of one region and records <paramref name="tag"/> for them. Only scratch buffers and non-code image pages can change:
    /// pages of translated code are refused (<paramref name="refusal"/> says why), and host protection never includes execute, so a request for an executable
    /// data page changes the guest-visible tag only. Nothing is changed when any page is refused.
    /// </summary>
    public bool TryProtect(uint address, uint length, MemoryProtection protection, uint tag, out string? refusal)
    {
        module.EnterCall();
        try
        {
            if (protection == MemoryProtection.ReadExecute) throw new ArgumentException("Host execute permission is never granted to guest data.", nameof(protection));
            var platform = PlatformMemory.Current;
            var ps = (uint)platform.PageSize;
            if (length == 0 || (address & (ps - 1)) != 0 || (length & (ps - 1)) != 0 || (ulong)address + length > 0x1_0000_0000UL) { refusal = "range is not page aligned"; return false; }
            var region = FindRegion(address, out var owner);
            if (region is null) { refusal = "range is not mapped"; return false; }
            if (owner != this) return owner.TryProtect(address, length, protection, tag, out refusal);
            lock (gate)
            {
                var last = address + length - ps;
                if (last >= region.End) { refusal = "range leaves the allocation"; return false; }
                if (region.Kind is not (GuestPageKind.Buffer or GuestPageKind.ImageData)) { refusal = "memory of this kind cannot change protection"; return false; }
                var first = (int)((address - region.Start) / ps);
                var count = (int)(length / ps);
                for (var i = 0; i < count; i++)
                    if (region.PageExecutable is { } x && x[first + i]) { refusal = "translated code"; return false; }
                var pages = (int)((region.End - region.Start + ps - 1) / ps);
                var write = protection == MemoryProtection.ReadWrite;
                var read = protection != MemoryProtection.None;
                platform.Protect((nint)address, length, protection);
                var w = region.PageWritable ?? Enumerable.Repeat(true, pages).ToArray();
                var rd = region.PageReadable ?? Enumerable.Repeat(true, pages).ToArray();
                var tags = region.PageTag ?? new uint[pages];
                for (var i = 0; i < count; i++) { w[first + i] = write; rd[first + i] = read; tags[first + i] = tag; }
                region.PageTag = tags; region.PageReadable = rd; region.PageWritable = w;
                refusal = null;
                return true;
            }
        }
        finally { module.ExitCall(); }
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
