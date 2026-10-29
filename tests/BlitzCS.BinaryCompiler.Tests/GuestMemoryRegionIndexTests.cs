using BlitzCS.BinaryCompiler.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class GuestMemoryRegionIndexTests(ITestOutputHelper output)
{
    private static GuestModule Load()
    {
        var guest = FakeGuest.Create();
        var resolver = new DictionaryGuestImportResolver()
            .Add("HOST.DLL", "hook", GuestImportBuilder.Stdcall(value => value));
        return GuestModule.Load(guest.Artifact, guest.Image, resolver);
    }

    [Fact]
    public void PersistentIndexesMatchInsertionOrderedLookupAcrossRandomMutations()
    {
        using var module = Load();
        var memory = module.Memory;
        var model = new List<(GuestRegion Region, long Order)>();
        var random = new Random(0x51A7);
        long order = 0;

        for (var operation = 0; operation < 500; operation++)
        {
            if (model.Count == 0 || random.Next(3) != 0)
            {
                // Deliberate duplicate starts and overlaps exercise the historical
                // insertion-order choice for the first mapping covering a query.
                var start = 0xD0000000UL + (uint)random.Next(128) * 64;
                var length = (uint)random.Next(1, 49);
                var region = new GuestRegion(start, start + length, null)
                {
                    PageReadable = [random.Next(2) != 0]
                };
                memory.Add(region);
                model.Add((region, ++order));
            }
            else
            {
                var start = model[random.Next(model.Count)].Region.Start;
                memory.Remove(start);
                model.RemoveAll(item => item.Region.Start == start);
            }

            for (var query = 0; query < 3; query++)
            {
                var address = (uint)(0xD0000000UL + (uint)random.Next(128) * 64 + (uint)random.Next(64));
                var length = (uint)random.Next(0, 65);
                Assert.Equal(Expected(model, address, length), memory.IsMapped(address, length));
            }
        }
    }

    [Fact]
    public void OverlappingMappingsRetainFirstInsertedRegionSemantics()
    {
        using var module = Load();
        var start = 0xD0000000UL;
        module.Memory.Add(new GuestRegion(start + 0x40, start + 0x80, null)
        {
            PageReadable = [false]
        });
        module.Memory.Add(new GuestRegion(start, start + 0x100, null));

        Assert.True(module.Memory.IsMapped((uint)(start + 0x10), 4)); // Only the broad region covers this address.
        Assert.False(module.Memory.IsMapped((uint)(start + 0x50), 4)); // Earlier-inserted region denies reads.
    }

    [Fact]
    public void RegionAddRemoveAllocationGrowthTracksTreeHeightRatherThanRegionCount()
    {
        var small = MeasureMutationBytes(1_000);
        var large = MeasureMutationBytes(10_000);
        output.WriteLine($"Average GuestMemory.Add+Remove allocations: 1,000 mappings={small:N0} B; 10,000 mappings={large:N0} B.");

        // A tenfold increase in tree size should add logarithmic path-copy work, not
        // another full pair of region arrays. The loose bound tolerates runtime noise.
        Assert.True(large <= small * 3 + 128, $"Mutation allocation grew from {small} B to {large} B per Add+Remove pair.");
    }

    private static long MeasureMutationBytes(int regionCount)
    {
        using var module = Load();
        var memory = module.Memory;
        const ulong first = 0x10000000;
        for (var i = 0; i < regionCount; i++)
        {
            var start = first + (ulong)i * 64;
            memory.Add(new GuestRegion(start, start + 16, null));
        }

        var mutationStart = first + (ulong)regionCount * 64;
        var transient = new GuestRegion(mutationStart, mutationStart + 16, null);
        for (var i = 0; i < 3; i++)
        {
            memory.Add(transient);
            memory.Remove(mutationStart);
        }

        const int iterations = 24;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            memory.Add(transient);
            memory.Remove(mutationStart);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return allocated / iterations;
    }

    private static bool Expected(List<(GuestRegion Region, long Order)> regions, uint address, uint length)
    {
        var end = (ulong)address + length;
        (GuestRegion Region, long Order)? candidate = null;
        foreach (var item in regions)
        {
            if (address >= item.Region.Start && end <= item.Region.End)
            {
                candidate = item;
                break;
            }
        }
        if (candidate is not { } selected) return false;
        var effectiveEnd = Math.Max(end, (ulong)address + 1);
        return end <= selected.Region.End
            && address >= selected.Region.Start
            && effectiveEnd <= selected.Region.End
            && (selected.Region.PageReadable is null || selected.Region.PageReadable[0]);
    }
}
