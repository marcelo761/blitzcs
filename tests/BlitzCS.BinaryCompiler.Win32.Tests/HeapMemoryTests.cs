using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class HeapTests
{
    private static uint Create(World w, uint flags = 0, uint initial = 0, uint max = 0) => w.Kernel("HeapCreate", flags, initial, max);

    [Fact]
    public void AllocSizeFreeRoundTrip()
    {
        using var w = new World();
        var heap = Create(w);
        Assert.NotEqual(0u, heap);
        var p = w.Kernel("HeapAlloc", heap, 0, 100);
        Assert.NotEqual(0u, p);
        Assert.Equal(0u, p % 8);
        Assert.Equal(100u, w.Kernel("HeapSize", heap, 0, p));
        Assert.Equal(1u, w.Kernel("HeapFree", heap, 0, p));
        Assert.Equal(0u, w.Kernel("HeapAlloc", heap, 0, 0) == 0 ? 1u : 0u); // size 0 still yields a valid block
    }

    [Fact]
    public void ZeroMemoryFlagZeroesReusedBlocks()
    {
        using var w = new World();
        var heap = Create(w);
        var a = w.Kernel("HeapAlloc", heap, 0, 64);
        w.Mem.Fill(a, 0xAB, 64);
        w.Kernel("HeapFree", heap, 0, a);
        var b = w.Kernel("HeapAlloc", heap, 8, 64);
        Assert.Equal(a, b); // first fit reuses the block
        Assert.All(w.Bytes(b, 64), x => Assert.Equal(0, x));
    }

    [Fact]
    public void DoubleFreeAndForeignPointersFailLikeWin32()
    {
        using var w = new World();
        var heap = Create(w);
        var p = w.Kernel("HeapAlloc", heap, 0, 32);
        Assert.Equal(1u, w.Kernel("HeapFree", heap, 0, p));
        Assert.Equal(0u, w.Kernel("HeapFree", heap, 0, p));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("HeapFree", heap, 0, 0x1234));
        Assert.Equal(0xFFFFFFFFu, w.Kernel("HeapSize", heap, 0, p));
        Assert.Equal(0u, w.Kernel("HeapReAlloc", heap, 0, p, 10));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(1u, w.Kernel("HeapFree", heap, 0, 0)); // freeing NULL succeeds
    }

    [Fact]
    public void BlockBelongsToOneHeapOnly()
    {
        using var w = new World();
        var h1 = Create(w); var h2 = Create(w);
        var p = w.Kernel("HeapAlloc", h1, 0, 16);
        Assert.Equal(0u, w.Kernel("HeapFree", h2, 0, p));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(1u, w.Kernel("HeapFree", h1, 0, p));
    }

    [Fact]
    public void InvalidHeapHandleSetsInvalidHandle()
    {
        using var w = new World();
        Assert.Equal(0u, w.Kernel("HeapAlloc", 0x1234, 0, 8));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(0xFFFFFFFFu, w.Kernel("HeapSize", 0x1234, 0, 8));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void ReallocPreservesDataAndHonoursInPlaceOnly()
    {
        using var w = new World();
        var heap = Create(w);
        var a = w.Kernel("HeapAlloc", heap, 0, 16);
        var b = w.Kernel("HeapAlloc", heap, 0, 16); // blocks growth of a
        w.Mem.Write(a, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        Assert.Equal(0u, w.Kernel("HeapReAlloc", heap, 0x10, a, 4096));
        Assert.Equal(Win32Error.NotEnoughMemory, w.LastError);
        var moved = w.Kernel("HeapReAlloc", heap, 8, a, 4096);
        Assert.NotEqual(a, moved);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (byte)i), w.Bytes(moved, 16));
        Assert.All(w.Bytes(moved + 16, 4080), x => Assert.Equal(0, x)); // HEAP_ZERO_MEMORY zeroes the growth
        Assert.Equal(4096u, w.Kernel("HeapSize", heap, 0, moved));
        Assert.Equal(0xFFFFFFFFu, w.Kernel("HeapSize", heap, 0, a)); // old block is gone
        var shrunk = w.Kernel("HeapReAlloc", heap, 0x10, moved, 8);
        Assert.Equal(moved, shrunk);
        Assert.Equal(8u, w.Kernel("HeapSize", heap, 0, moved));
        Assert.Equal(1u, w.Kernel("HeapFree", heap, 0, b));
    }

    [Fact]
    public void ReallocGrowsInPlaceIntoFollowingFreeSpace()
    {
        using var w = new World();
        var heap = Create(w);
        var a = w.Kernel("HeapAlloc", heap, 0, 16);
        Assert.Equal(a, w.Kernel("HeapReAlloc", heap, 0x10, a, 1000));
    }

    [Fact]
    public void FixedSizeHeapEnforcesItsMaximum()
    {
        using var w = new World();
        var heap = Create(w, 0, 0x1000, 0x2000);
        Assert.NotEqual(0u, w.Kernel("HeapAlloc", heap, 0, 0x1800));
        Assert.Equal(0u, w.Kernel("HeapAlloc", heap, 0, 0x1000));
        Assert.Equal(Win32Error.NotEnoughMemory, w.LastError);
    }

    [Fact]
    public void GenerateExceptionsRaisesInsteadOfReturningNull()
    {
        using var w = new World();
        var heap = Create(w, HeapService.GenerateExceptions, 0, 0x1000);
        Assert.Throws<Win32UsageException>(() => w.Kernel("HeapAlloc", heap, 0, 0x2000));
        var plain = Create(w, 0, 0, 0x1000);
        Assert.Throws<Win32UsageException>(() => w.Kernel("HeapAlloc", plain, HeapService.GenerateExceptions, 0x2000));
    }

    [Fact]
    public void HeapCreateValidatesArguments()
    {
        using var w = new World();
        Assert.Equal(0u, Create(w, 0x1000));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, Create(w, 0, 0x2000, 0x1000));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, Create(w, 0, 0x7FFFFFFF, 0));
        Assert.Equal(Win32Error.NotEnoughMemory, w.LastError);
    }

    [Fact]
    public void DestroyReleasesMemoryAndInvalidatesTheHandle()
    {
        using var w = new World();
        var heap = Create(w);
        var p = w.Kernel("HeapAlloc", heap, 0, 64);
        Assert.True(w.Mem.IsMapped(p, 64));
        Assert.Equal(1u, w.Kernel("HeapDestroy", heap));
        Assert.False(w.Mem.IsMapped(p, 64));
        Assert.Equal(0u, w.Kernel("HeapDestroy", heap));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("HeapAlloc", heap, 0, 8));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void ProcessHeapIsStableAndCannotBeDestroyed()
    {
        using var w = new World();
        var ph = w.Kernel("GetProcessHeap");
        Assert.NotEqual(0u, ph);
        Assert.Equal(ph, w.Kernel("GetProcessHeap"));
        Assert.Equal(0u, w.Kernel("HeapDestroy", ph));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.NotEqual(0u, w.Kernel("HeapAlloc", ph, 0, 8));
    }

    [Fact]
    public void LargeBlocksAreUnmappedWhenFreed()
    {
        using var w = new World();
        var heap = Create(w);
        var big = w.Kernel("HeapAlloc", heap, 0, 600_000);
        w.Mem.WriteByte(big + 599_999, 7);
        Assert.Equal(1u, w.Kernel("HeapFree", heap, 0, big));
        Assert.False(w.Mem.IsMapped(big, 1));
        Assert.Equal(0u, w.Kernel("HeapFree", heap, 0, big));
    }

    [Fact]
    public void FreedSpaceCoalescesBackIntoOneBlock()
    {
        using var w = new World();
        var heap = Create(w);
        var blocks = Enumerable.Range(0, 50).Select(_ => w.Kernel("HeapAlloc", heap, 0, 1000)).ToList();
        foreach (var b in blocks.OrderBy(_ => Guid.NewGuid())) Assert.Equal(1u, w.Kernel("HeapFree", heap, 0, b));
        var again = w.Kernel("HeapAlloc", heap, 0, 50_000); // only possible after coalescing
        Assert.Equal(blocks.Min(), again);
    }

    [Fact]
    public void ValidateReportsOwnership()
    {
        using var w = new World();
        var heap = Create(w);
        var p = w.Kernel("HeapAlloc", heap, 0, 8);
        Assert.Equal(1u, w.Kernel("HeapValidate", heap, 0, 0));
        Assert.Equal(1u, w.Kernel("HeapValidate", heap, 0, p));
        Assert.Equal(0u, w.Kernel("HeapValidate", heap, 0, p + 1000));
    }

    [Fact]
    public void GlobalAndLocalAllocFixedOnly()
    {
        using var w = new World();
        foreach (var (alloc, free) in new[] { ("GlobalAlloc", "GlobalFree"), ("LocalAlloc", "LocalFree") })
        {
            var p = w.Kernel(alloc, 0x40, 32);
            Assert.NotEqual(0u, p);
            Assert.All(w.Bytes(p, 32), x => Assert.Equal(0, x));
            Assert.Equal(0u, w.Kernel(free, p));          // NULL = success
            Assert.Equal(p, w.Kernel(free, p));           // second free returns the handle = failure
            Assert.Equal(Win32Error.InvalidHandle, w.LastError);
            Assert.Equal(0u, w.Kernel(alloc, 2, 32));      // GMEM_MOVEABLE is refused explicitly
            Assert.Equal(Win32Error.NotSupported, w.LastError);
        }
    }

    [Fact]
    public void ConcurrentAllocFreeKeepsBlocksDisjointAndIntact()
    {
        using var w = new World();
        var heap = Create(w);
        var errors = new List<string>();
        Parallel.For(0, 8, t =>
        {
            var rnd = new Random(t);
            var live = new List<(uint P, int N, byte Tag)>();
            for (var i = 0; i < 1500; i++)
            {
                if (live.Count > 0 && rnd.Next(3) == 0)
                {
                    var k = rnd.Next(live.Count); var (p, n, tag) = live[k];
                    if (w.Bytes(p, n).Any(x => x != tag)) lock (errors) errors.Add($"corruption in thread {t}");
                    if (w.Kernel("HeapFree", heap, 0, p) != 1) lock (errors) errors.Add("free failed");
                    live.RemoveAt(k);
                }
                else
                {
                    var n = rnd.Next(1, 3000); var tag = (byte)(t + 1);
                    var p = w.Kernel("HeapAlloc", heap, 0, (uint)n);
                    if (p == 0) { lock (errors) errors.Add("alloc failed"); continue; }
                    w.Mem.Fill(p, tag, (uint)n);
                    live.Add((p, n, tag));
                }
            }
            foreach (var (p, n, tag) in live) { if (w.Bytes(p, n).Any(x => x != tag)) lock (errors) errors.Add("late corruption"); w.Kernel("HeapFree", heap, 0, p); }
        });
        Assert.Empty(errors);
    }
}

public sealed class VirtualMemoryTests
{
    [Fact]
    public void AllocatesZeroedPageRoundedRegions()
    {
        using var w = new World();
        var p = w.Kernel("VirtualAlloc", 0, 5000, 0x3000, 4);
        Assert.NotEqual(0u, p);
        Assert.Equal(0u, p % 4096);
        Assert.True(w.Mem.IsMapped(p, 8192));
        Assert.All(w.Bytes(p, 8192), x => Assert.Equal(0, x));
    }

    [Theory]
    [InlineData(0u, 0u, 0x3000u, 4u)]       // zero size
    [InlineData(0u, 4096u, 0u, 4u)]         // neither commit nor reserve
    [InlineData(0u, 4096u, 0x3000u, 3u)]    // invalid protection
    [InlineData(0u, 4096u, 0x3000u, 0u)]
    public void RejectsInvalidArguments(uint addr, uint size, uint type, uint protect)
    {
        using var w = new World();
        Assert.Equal(0u, w.Kernel("VirtualAlloc", addr, size, type, protect));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void HugeAndForeignRequestsFailWithDocumentedErrors()
    {
        using var w = new World();
        Assert.Equal(0u, w.Kernel("VirtualAlloc", 0, 0x7FFFFFFF, 0x3000, 4));
        Assert.Equal(Win32Error.NotEnoughMemory, w.LastError);
        Assert.Equal(0u, w.Kernel("VirtualAlloc", 0x12340000, 4096, 0x3000, 4));
        Assert.Equal(Win32Error.InvalidAddress, w.LastError);
    }

    [Fact]
    public void CommitInsideReservedRegionReturnsPageBase()
    {
        using var w = new World();
        var p = w.Kernel("VirtualAlloc", 0, 16384, 0x2000, 4);
        Assert.Equal(p + 4096, w.Kernel("VirtualAlloc", p + 4096 + 12, 100, 0x1000, 4));
    }

    [Fact]
    public void ReleaseRequiresBaseAndZeroSizeAndDetectsDoubleFree()
    {
        using var w = new World();
        var p = w.Kernel("VirtualAlloc", 0, 8192, 0x3000, 4);
        Assert.Equal(0u, w.Kernel("VirtualFree", p, 4096, 0x8000));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("VirtualFree", p + 4096, 0, 0x8000));
        Assert.Equal(Win32Error.InvalidAddress, w.LastError);
        Assert.Equal(1u, w.Kernel("VirtualFree", p, 0, 0x8000));
        Assert.False(w.Mem.IsMapped(p, 1));
        Assert.Equal(0u, w.Kernel("VirtualFree", p, 0, 0x8000));
        Assert.Equal(Win32Error.InvalidAddress, w.LastError);
        Assert.Equal(0u, w.Kernel("VirtualFree", p, 0, 0x4000 | 0x8000)); // invalid type combination
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void DecommitZeroesTheRange()
    {
        using var w = new World();
        var p = w.Kernel("VirtualAlloc", 0, 8192, 0x3000, 4);
        w.Mem.Fill(p, 0x55, 8192);
        Assert.Equal(1u, w.Kernel("VirtualFree", p + 4096, 4096, 0x4000));
        Assert.All(w.Bytes(p + 4096, 4096), x => Assert.Equal(0, x));
        Assert.All(w.Bytes(p, 4096), x => Assert.Equal(0x55, x));
        Assert.Equal(0u, w.Kernel("VirtualFree", 0x1000, 4096, 0x4000));
        Assert.Equal(Win32Error.InvalidAddress, w.LastError);
    }

    [Fact]
    public void VirtualProtectAndQueryAreImplementedWithEnforcedSemantics()
    {
        // enforced semantics are covered by MemoryProtectionTests
        Assert.Contains(Win32ImportResolver.Coverage.Implemented, a => a.Name == "VirtualProtect");
        Assert.Contains(Win32ImportResolver.Coverage.Implemented, a => a.Name == "VirtualQuery");
    }
}
