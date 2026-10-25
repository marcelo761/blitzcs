using BlitzCS.BinaryCompiler.Runtime;
namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// One Win32 heap built on guest-visible chunks. Blocks are sub-allocated host-side (first fit, coalescing free list) inside chunks obtained from
/// <see cref="IWin32Memory.Allocate"/>, so the guest only ever sees guest addresses and ownership is checked on every free.
/// Large blocks get their own chunk, which is unmapped on free (use-after-free of big blocks faults explicitly).
/// </summary>
internal sealed class GuestHeap : IDisposable
{
    private const uint Header = 64;          // keeps every block address different from the heap handle (= first chunk base)
    private const int DefaultChunk = 1 << 20;
    private const int DedicatedThreshold = 256 * 1024;

    private sealed class Chunk(IWin32Allocation allocation, bool dedicated)
    {
        public IWin32Allocation Allocation = allocation;
        public bool Dedicated = dedicated;
        public readonly List<(uint Start, uint Size)> Free = [];
    }

    private sealed class Block(Chunk chunk, uint size, uint capacity) { public Chunk Chunk = chunk; public uint Size = size; public uint Capacity = capacity; }

    private readonly object gate = new();
    private readonly IWin32Memory memory;
    private readonly List<Chunk> chunks = [];
    private readonly Dictionary<uint, Block> blocks = [];
    private readonly long maximum; // 0 = growable
    private long used;
    private bool destroyed;

    public GuestHeap(IWin32Memory memory, long initialSize, long maximumSize, uint flags)
    {
        this.memory = memory;
        maximum = maximumSize;
        Flags = flags;
        var size = (int)Math.Clamp(initialSize, 64 * 1024, maximumSize != 0 ? Math.Max(maximumSize, 64 * 1024) : DefaultChunk);
        AddChunk(size, false, true);
        Handle = chunks[0].Allocation.Address;
    }

    public uint Handle { get; }
    public uint Flags { get; }
    public bool IsProcessHeap { get; init; }

    private Chunk AddChunk(int size, bool dedicated, bool first = false)
    {
        var allocation = memory.Allocate(size);
        var chunk = new Chunk(allocation, dedicated);
        if (!dedicated) chunk.Free.Add((first ? allocation.Address + Header : allocation.Address, (uint)allocation.Length - (first ? Header : 0u)));
        chunks.Add(chunk);
        return chunk;
    }

    private static uint Align8(uint n) => (n + 7u) & ~7u;

    /// <summary>Returns 0 when the request cannot be satisfied (limit or address-space exhaustion).</summary>
    public uint Alloc(uint size, bool zero)
    {
        lock (gate)
        {
            if (destroyed) return 0;
            var need = Align8(Math.Max(size, 1u));
            if (maximum != 0 && used + need > maximum) return 0;
            try
            {
                var address = size >= DedicatedThreshold ? AllocDedicated(size, need) : AllocFromFree(need);
                if (address == 0) return 0;
                blocks[address].Size = size;
                if (zero) memory.Fill(address, 0, size);
                used += need;
                return address;
            }
            catch (Exception e) when (e is OutOfMemoryException or ArgumentException or OverflowException or Runtime.GuestException) { return 0; }
        }
    }

    private uint AllocDedicated(uint size, uint need)
    {
        var chunk = AddChunk(checked((int)need), true);
        var address = chunk.Allocation.Address;
        blocks[address] = new Block(chunk, size, need);
        return address;
    }

    private uint AllocFromFree(uint need)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.Dedicated) continue;
            var address = TakeFree(chunk, need);
            if (address != 0) return address;
        }
        var fresh = AddChunk(DefaultChunk, false);
        var a = TakeFree(fresh, need);
        return a;
    }

    private uint TakeFree(Chunk chunk, uint need)
    {
        for (var i = 0; i < chunk.Free.Count; i++)
        {
            var (start, length) = chunk.Free[i];
            if (length < need) continue;
            if (length == need) chunk.Free.RemoveAt(i); else chunk.Free[i] = (start + need, length - need);
            // the Block object is registered by the caller path below through Register
            blocks[start] = new Block(chunk, 0, need);
            return start;
        }
        return 0;
    }

    private static int FindInsert(List<(uint Start, uint Size)> free, uint start)
    {
        int lo = 0, hi = free.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (free[mid].Start < start) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private void ReturnFree(Chunk chunk, uint start, uint length)
    {
        var free = chunk.Free;
        var at = FindInsert(free, start);
        if (at < free.Count && free[at].Start == start + length) { length += free[at].Size; free.RemoveAt(at); }
        if (at > 0 && free[at - 1].Start + free[at - 1].Size == start) { start = free[at - 1].Start; length += free[at - 1].Size; free.RemoveAt(at - 1); at--; }
        free.Insert(at, (start, length));
    }

    /// <summary>False when the pointer is not a live block of this heap (including double free).</summary>
    public bool Free(uint address)
    {
        lock (gate)
        {
            if (destroyed || !blocks.Remove(address, out var block)) return false;
            used -= block.Capacity;
            Release(block, address);
            return true;
        }
    }

    private void Release(Block block, uint address)
    {
        if (block.Chunk.Dedicated) { chunks.Remove(block.Chunk); block.Chunk.Allocation.Dispose(); }
        else ReturnFree(block.Chunk, address, block.Capacity);
    }

    public bool TrySize(uint address, out uint size)
    {
        lock (gate)
        {
            if (!destroyed && blocks.TryGetValue(address, out var b)) { size = b.Size; return true; }
            size = 0; return false;
        }
    }

    public bool Owns(uint address) { lock (gate) return !destroyed && blocks.ContainsKey(address); }

    /// <summary>Sets the requested size of a block just returned by Alloc.</summary>
    public void SetRequestedSize(uint address, uint size) { lock (gate) blocks[address].Size = size; }

    public enum ReallocResult { Ok, NotOwned, NoMemory }

    public ReallocResult Realloc(uint address, uint size, bool zero, bool inPlaceOnly, out uint newAddress)
    {
        newAddress = 0;
        lock (gate)
        {
            if (destroyed || !blocks.TryGetValue(address, out var block)) return ReallocResult.NotOwned;
            var need = Align8(Math.Max(size, 1u));
            var oldSize = block.Size;
            if (need <= block.Capacity)
            {
                if (zero && size > oldSize) memory.Fill(address + oldSize, 0, size - oldSize);
                block.Size = size; newAddress = address; return ReallocResult.Ok;
            }
            if (!block.Chunk.Dedicated && TryGrowInPlace(block, address, need))
            {
                if (zero) memory.Fill(address + oldSize, 0, size - oldSize);
                block.Size = size; newAddress = address; return ReallocResult.Ok;
            }
            if (inPlaceOnly) return ReallocResult.NoMemory;
            var fresh = AllocLocked(size, false);
            if (fresh == 0) return ReallocResult.NoMemory;
            memory.Copy(fresh, address, Math.Min(oldSize, size));
            if (zero && size > oldSize) memory.Fill(fresh + oldSize, 0, size - oldSize);
            blocks.Remove(address); used -= block.Capacity;
            Release(block, address);
            newAddress = fresh; return ReallocResult.Ok;
        }
    }

    private uint AllocLocked(uint size, bool zero)
    {
        var need = Align8(Math.Max(size, 1u));
        if (maximum != 0 && used + need > maximum) return 0;
        try
        {
            var a = size >= DedicatedThreshold ? AllocDedicated(size, need) : AllocFromFree(need);
            if (a == 0) return 0;
            blocks[a].Size = size;
            if (zero) memory.Fill(a, 0, size);
            used += need;
            return a;
        }
        catch (Exception e) when (e is OutOfMemoryException or ArgumentException or OverflowException or Runtime.GuestException) { return 0; }
    }

    private bool TryGrowInPlace(Block block, uint address, uint need)
    {
        var end = address + block.Capacity;
        var free = block.Chunk.Free;
        var at = FindInsert(free, end);
        if (at >= free.Count || free[at].Start != end) return false;
        var extra = need - block.Capacity;
        if (free[at].Size < extra) return false;
        if (maximum != 0 && used + extra > maximum) return false;
        if (free[at].Size == extra) free.RemoveAt(at); else free[at] = (end + extra, free[at].Size - extra);
        block.Capacity = need; used += extra;
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (destroyed) return;
            destroyed = true;
            foreach (var c in chunks) { try { c.Allocation.Dispose(); } catch (ObjectDisposedException) { } catch (Runtime.GuestException) { } }
            chunks.Clear(); blocks.Clear();
        }
    }
}

internal sealed class HeapState(Win32Context ctx) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<uint, GuestHeap> heaps = [];
    private GuestHeap? processHeap, crtHeap;

    public GuestHeap Process(IWin32Memory mem) { lock (gate) return processHeap ??= Register(new GuestHeap(mem, 0, 0, 0) { IsProcessHeap = true }); }
    /// <summary>The heap behind malloc/calloc/realloc of MSVCRT (separate from the process heap, like a private CRT heap).</summary>
    public GuestHeap Crt(IWin32Memory mem) { lock (gate) return crtHeap ??= Register(new GuestHeap(mem, 0, 0, 0)); }

    public GuestHeap Register(GuestHeap heap) { lock (gate) heaps[heap.Handle] = heap; return heap; }
    public GuestHeap? Find(uint handle) { lock (gate) return heaps.GetValueOrDefault(handle); }

    public bool Destroy(uint handle)
    {
        GuestHeap? h;
        lock (gate)
        {
            if (!heaps.TryGetValue(handle, out h) || h.IsProcessHeap || h == crtHeap) return false;
            heaps.Remove(handle);
        }
        h.Dispose();
        return true;
    }

    /// <summary>Finds the heap owning a live block (used by LocalFree/GlobalFree and tools).</summary>
    public GuestHeap? OwnerOf(uint address) { lock (gate) return heaps.Values.FirstOrDefault(h => h.Owns(address)); }

    public void Dispose()
    {
        lock (gate) { foreach (var h in heaps.Values) h.Dispose(); heaps.Clear(); }
        _ = ctx;
    }
}

/// <summary>
/// KERNEL32 heap functions. Relied-on documented behaviour (Microsoft Learn, "Heap Functions"):
/// HeapCreate returns NULL on failure; maximum 0 = growable; HeapAlloc returns NULL (or raises STATUS_NO_MEMORY with HEAP_GENERATE_EXCEPTIONS);
/// HEAP_ZERO_MEMORY zeroes; HeapSize returns the requested size or (SIZE_T)-1; HeapFree/HeapReAlloc/HeapSize on a foreign or already freed pointer
/// are undefined on Windows - here they fail with FALSE/NULL/-1 and ERROR_INVALID_PARAMETER; HEAP_REALLOC_IN_PLACE_ONLY honoured;
/// HeapDestroy of the process heap fails. Alignment is 8 bytes. HEAP_NO_SERIALIZE is accepted but every heap is serialised (stricter, never weaker).
/// </summary>
public sealed class HeapService : Win32Service
{
    public const uint NoSerialize = 1, GenerateExceptions = 4, ZeroMemory = 8, ReallocInPlaceOnly = 0x10, CreateEnableExecute = 0x40000;
    public override string Name => "Kernel32.Heap";

    private static GuestHeap? Lookup(Win32Call c, uint handle) => c.Ctx.Heaps.Find(handle);

    private static uint NoMemory(Win32Call c, uint flags)
    {
        if ((flags & GenerateExceptions) != 0) throw new Win32UsageException("Heap allocation failed with HEAP_GENERATE_EXCEPTIONS: STATUS_NO_MEMORY (0xC0000017).");
        return c.Ctx.Fail(Win32Error.NotEnoughMemory);
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "GetProcessHeap", 0, c => { var h = c.Ctx.Heaps.Process(c.Mem).Handle; GuestModule.Current?.SetProcessHeap(h); return h; });
        Std(K, "HeapCreate", 3, c =>
        {
            if ((c[0] & ~(NoSerialize | GenerateExceptions | CreateEnableExecute)) != 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            if (c[2] != 0 && c[1] > c[2]) return c.Ctx.Fail(Win32Error.InvalidParameter);
            if (c[1] > c.Ctx.Options.MaxAllocation || c[2] > c.Ctx.Options.MaxAllocation) return c.Ctx.Fail(Win32Error.NotEnoughMemory);
            try { return c.Ctx.Heaps.Register(new GuestHeap(c.Mem, c[1], c[2], c[0])).Handle; }
            catch (Exception e) when (e is Runtime.GuestException or ArgumentException or OutOfMemoryException) { return c.Ctx.Fail(Win32Error.NotEnoughMemory); }
        });
        Std(K, "HeapDestroy", 1, c => c.Ctx.Heaps.Destroy(c[0]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
        Std(K, "HeapAlloc", 3, c =>
        {
            if (Lookup(c, c[0]) is not { } heap) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (c[2] > c.Ctx.Options.MaxAllocation) return NoMemory(c, c[1] | heap.Flags);
            var a = heap.Alloc(c[2], (c[1] & ZeroMemory) != 0);
            if (a != 0) { heap.SetRequestedSize(a, c[2]); return a; }
            return NoMemory(c, c[1] | heap.Flags);
        });
        Std(K, "HeapReAlloc", 4, c =>
        {
            if (Lookup(c, c[0]) is not { } heap) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (c[3] > c.Ctx.Options.MaxAllocation) return NoMemory(c, c[1] | heap.Flags);
            switch (heap.Realloc(c[2], c[3], (c[1] & ZeroMemory) != 0, (c[1] & ReallocInPlaceOnly) != 0, out var moved))
            {
                case GuestHeap.ReallocResult.Ok: return moved;
                case GuestHeap.ReallocResult.NotOwned: return c.Ctx.Fail(Win32Error.InvalidParameter);
                default: return NoMemory(c, c[1] | heap.Flags);
            }
        });
        Std(K, "HeapFree", 3, c =>
        {
            if (Lookup(c, c[0]) is not { } heap) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (c[2] == 0) return 1; // documented: freeing NULL succeeds
            return heap.Free(c[2]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter);
        });
        Std(K, "HeapSize", 3, c =>
        {
            if (Lookup(c, c[0]) is not { } heap) return c.Ctx.Fail(Win32Error.InvalidHandle, 0xFFFFFFFF);
            return heap.TrySize(c[2], out var s) ? s : c.Ctx.Fail(Win32Error.InvalidParameter, 0xFFFFFFFF);
        });
        Std(K, "HeapValidate", 3, c =>
        {
            if (Lookup(c, c[0]) is not { } heap) return c.Ctx.Fail(Win32Error.InvalidHandle);
            return c[2] == 0 || heap.Owns(c[2]) ? 1u : 0u; // with a pointer: is it a live block of this heap
        });

        // GlobalAlloc/LocalAlloc: fixed memory only (flag 0 / GMEM_ZEROINIT 0x40). Movable handles are refused with ERROR_NOT_SUPPORTED.
        foreach (var (prefix, free) in new[] { ("Global", "GlobalFree"), ("Local", "LocalFree") })
        {
            Std(K, prefix + "Alloc", 2, c =>
            {
                if ((c[0] & ~0x40u) != 0) return c.Ctx.Fail(Win32Error.NotSupported); // GMEM_MOVEABLE/DDESHARE/... need handle tables
                var heap = c.Ctx.Heaps.Process(c.Mem);
                var a = heap.Alloc(c[1], (c[0] & 0x40) != 0);
                if (a == 0) return c.Ctx.Fail(Win32Error.NotEnoughMemory);
                heap.SetRequestedSize(a, c[1]);
                return a;
            });
            Std(K, free, 1, c => c[0] == 0 ? 0 : c.Ctx.Heaps.Process(c.Mem).Free(c[0]) ? 0u : c.Ctx.Fail(Win32Error.InvalidHandle, c[0])); // returns NULL on success, the handle on failure
        }
    }
}
