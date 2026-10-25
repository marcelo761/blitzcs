using System.Collections.Concurrent;
using System.Diagnostics;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>A World with a chosen waveOut backend, an observer tap and helpers to build WAVEFORMATEX / WAVEHDR structures in fake guest memory.</summary>
public sealed class WaveRig : IDisposable
{
    public const string W = "WINMM.dll";
    public readonly World World;
    public readonly ConcurrentQueue<WaveOutWrite> Writes = new();
    public readonly List<string> Diagnostics = [];

    public WaveRig(WaveOutBackendKind kind = WaveOutBackendKind.VirtualSink, string? record = null)
    {
        World = new World(new Win32Options { WaveOut = kind, WaveOutRecordPath = record, WaveOutObserver = Writes.Enqueue, DebugOutput = s => { lock (Diagnostics) Diagnostics.Add(s); } });
    }

    public FakeMemory Mem => World.Mem;
    public uint Call(string name, params uint[] args) => World.Call(W, name, args);

    public uint Format(ushort channels, uint rate, ushort bits, ushort tag = 1)
    {
        var p = World.Buf(32);
        var block = (ushort)(channels * bits / 8);
        Mem.Write(p, [.. BitConverter.GetBytes(tag), .. BitConverter.GetBytes(channels), .. BitConverter.GetBytes(rate), .. BitConverter.GetBytes(rate * block), .. BitConverter.GetBytes(block), .. BitConverter.GetBytes(bits), 0, 0]);
        return p;
    }

    /// <summary>Opens device 0 (or <paramref name="device"/>); returns (result, handle).</summary>
    public (uint Result, uint Handle) Open(uint format, uint flags = 0, uint callback = 0, uint instance = 0, uint device = 0)
    {
        var phwo = World.Buf(4);
        var r = Call("waveOutOpen", phwo, device, format, callback, instance, flags);
        return (r, Mem.ReadUInt32(phwo));
    }

    public uint Header(uint data, uint length, uint flags = 0, uint loops = 0, uint user = 0)
    {
        var h = World.Buf(32);
        Mem.WriteUInt32(h, data); Mem.WriteUInt32(h + 4, length); Mem.WriteUInt32(h + 12, user); Mem.WriteUInt32(h + 16, flags); Mem.WriteUInt32(h + 20, loops);
        return h;
    }

    public (uint Header, uint Data) Buffer(byte[] pcm, uint flags = 0, uint loops = 0)
    {
        var d = World.Buf(Math.Max(pcm.Length, 1));
        Mem.Write(d, pcm);
        return (Header(d, (uint)pcm.Length, flags, loops), d);
    }

    public uint Flags(uint header) => Mem.ReadUInt32(header + 16);

    public bool WaitFlags(uint header, uint mask, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if ((Flags(header) & mask) == mask) return true; Thread.Sleep(1); }
        return (Flags(header) & mask) == mask;
    }

    public uint Position(uint hwo, uint type, out uint typeReturned)
    {
        var mmt = World.Buf(12);
        Mem.WriteUInt32(mmt, type);
        Assert.Equal(0u, Call("waveOutGetPosition", hwo, mmt, 12));
        typeReturned = Mem.ReadUInt32(mmt);
        return Mem.ReadUInt32(mmt + 4);
    }

    public void Dispose() => World.Dispose();
}
