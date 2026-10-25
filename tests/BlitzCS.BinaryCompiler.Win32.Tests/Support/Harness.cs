using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>Guests load at a fixed base, so every test that loads a module belongs to this collection (serialised within this assembly).</summary>
[CollectionDefinition("GuestRuntime", DisableParallelization = true)]
public sealed class GuestRuntimeCollection;

/// <summary>Sparse 32-bit address space for host-side tests. Unmapped access throws GuestMemoryException like the real GuestMemory.</summary>
public sealed class FakeMemory : IWin32Memory
{
    private sealed class Region(uint start, byte[] data, FakeMemory owner) : IWin32Allocation
    {
        public uint Start = start; public byte[] Data = data; public bool Disposed;
        public uint Address => Start;
        public int Length => Data.Length;
        public void Dispose() { if (!Disposed) { Disposed = true; owner.Remove(this); } }
    }

    private readonly object gate = new();
    private Region[] regions = [];
    private uint next = 0x30000000;
    public int LiveRegions => regions.Length;

    private void Remove(Region r) { lock (gate) regions = regions.Where(x => x != r).ToArray(); }

    public IWin32Allocation Allocate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        lock (gate)
        {
            var r = new Region(next, new byte[size], this);
            next += (uint)((size + 0xFFF) & ~0xFFF) + 0x1000; // guard gap between regions
            regions = [.. regions, r];
            return r;
        }
    }

    private (Region, int) Find(uint address, long length, bool write)
    {
        foreach (var r in regions)
            if (address >= r.Start && (long)address + length <= (long)r.Start + r.Data.Length) return (r, (int)(address - r.Start));
        throw new GuestMemoryException(address, length, write);
    }

    public bool IsMapped(uint address, uint length) { foreach (var r in regions) if (address >= r.Start && (long)address + length <= (long)r.Start + r.Data.Length) return true; return false; }
    public byte ReadByte(uint a) { var (r, o) = Find(a, 1, false); return r.Data[o]; }
    public uint ReadUInt32(uint a) { var (r, o) = Find(a, 4, false); return BitConverter.ToUInt32(r.Data, o); }
    public void WriteByte(uint a, byte v) { var (r, o) = Find(a, 1, true); r.Data[o] = v; }
    public void WriteUInt32(uint a, uint v) { var (r, o) = Find(a, 4, true); BitConverter.TryWriteBytes(r.Data.AsSpan(o), v); }
    public void Read(uint a, Span<byte> d) { var (r, o) = Find(a, d.Length, false); r.Data.AsSpan(o, d.Length).CopyTo(d); }
    public void Write(uint a, ReadOnlySpan<byte> s) { var (r, o) = Find(a, s.Length, true); s.CopyTo(r.Data.AsSpan(o)); }

    public string ReadAnsiString(uint a, int max = 65536)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i <= max; i++) { var b = ReadByte(a + (uint)i); if (b == 0) return sb.ToString(); sb.Append((char)b); }
        throw new GuestMemoryException(a, max, false);
    }
}

/// <summary>A resolver over a FakeMemory with convenience helpers.</summary>
public sealed class World : IDisposable
{
    public const string K = "KERNEL32.dll", C = "MSVCRT.dll";
    public FakeMemory Mem { get; } = new();
    public Win32ImportResolver R { get; }

    public World(Win32Options? options = null) => R = new Win32ImportResolver(options);

    public uint Call(string dll, string name, params uint[] args) => R.Call(Mem, dll, name, args);
    public uint Kernel(string name, params uint[] args) => R.Call(Mem, K, name, args);
    public uint Crt(string name, params uint[] args) => R.Call(Mem, C, name, args);
    public uint LastError => Kernel("GetLastError");

    public uint Buf(int size) => Mem.Allocate(size).Address;

    public uint Str(string latin1)
    {
        var p = Buf(latin1.Length + 1);
        Mem.Write(p, [.. latin1.Select(ch => (byte)ch), (byte)0]);
        return p;
    }

    public uint Wide(string s)
    {
        var p = Buf(s.Length * 2 + 2);
        Mem.Write(p, (s + "\0").SelectMany(ch => new[] { (byte)ch, (byte)(ch >> 8) }).ToArray());
        return p;
    }

    public byte[] Bytes(uint p, int n) => Mem.ReadBytes(p, n);
    public string Cstr(uint p) => Mem.ReadAnsiString(p);
    public static uint Dbl(double v, out uint hi) { var b = BitConverter.DoubleToUInt64Bits(v); hi = (uint)(b >> 32); return (uint)b; }

    public void Dispose() => R.Dispose();
}
