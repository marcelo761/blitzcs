using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>The IPlatformMemory contract, identical on Linux (mmap) and Windows (VirtualAlloc): same assertions, different backend.</summary>
[Collection("GuestRuntime")]
public sealed class PlatformMemoryTests
{
    private static IPlatformMemory P => PlatformMemory.Current;

    [Fact]
    public void Selects_the_implementation_of_the_running_os()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "linux", P.Name);
        Assert.Equal(BinaryTargets.Host, P.TargetOs);
        Assert.True(P.PageSize >= 4096);
        Assert.True(P.AllocationGranularity >= P.PageSize);
        Assert.Equal(OperatingSystem.IsWindows() ? 0x20000000u : 0x1000u, P.MinimumImageBase);
    }

    [Fact]
    public void MapLow_returns_zeroed_read_write_memory_below_4GiB()
    {
        var length = (nuint)(P.PageSize * 3);
        var map = P.MapLow(length, out var error);
        Assert.True(map != 0, $"MapLow failed with OS error {error}");
        try
        {
            Assert.True((ulong)map + length <= uint.MaxValue);
            if (OperatingSystem.IsWindows()) Assert.True((ulong)map >= 0x78000000UL, "Windows arenas start at 0x78000000");
            Assert.Equal(0, (long)map % P.AllocationGranularity);
            Assert.Equal(0, Marshal.ReadInt32(map, 8));
            Marshal.WriteInt32(map, 8, 0x1234ABCD);
            Marshal.WriteInt32(map, (int)length - 4, 7);
            Assert.Equal(0x1234ABCD, Marshal.ReadInt32(map, 8));
        }
        finally { P.Free(map, length); }
    }

    [Fact]
    public void MapLow_regions_never_overlap()
    {
        var length = (nuint)P.PageSize;
        var maps = Enumerable.Range(0, 8).Select(_ => P.MapLow(length, out _)).ToArray();
        try
        {
            Assert.All(maps, m => Assert.NotEqual(0, m));
            Assert.Equal(maps.Length, maps.Distinct().Count());
        }
        finally { foreach (var m in maps) P.Free(m, length); }
    }

    [Fact]
    public void MapFixed_has_noreplace_semantics_and_reports_the_conflict()
    {
        var length = (nuint)(P.PageSize * 2);
        var probe = P.MapLow(length, out _);
        Assert.NotEqual(0, probe);
        P.Free(probe, length);
        var first = P.MapFixed(probe, length, out var error);
        Assert.True(first == probe, $"fixed map at 0x{probe:X} failed with OS error {error}");
        try
        {
            Marshal.WriteInt32(first, 0x55AA);
            Assert.Equal(0, P.MapFixed(probe, length, out var second));   // never replaces
            Assert.NotEqual(0, second);
            Assert.Equal(0x55AA, Marshal.ReadInt32(first));               // the original mapping is intact
            Assert.Equal(0, P.MapFixed(probe + P.PageSize, (nuint)P.PageSize, out _)); // partial overlap is refused too
            var ranges = P.QueryRanges((ulong)probe, (ulong)probe + length);
            Assert.Contains(ranges, r => r.Start <= (ulong)probe && r.End >= (ulong)probe + length);
            Assert.Contains("0x", AddressSpaceDescription((uint)probe, length));
        }
        finally { P.Free(first, length); }
    }

    private static string AddressSpaceDescription(uint start, nuint length) =>
        string.Join("; ", PlatformMemory.Current.QueryRanges(start, start + (ulong)length).Select(r => $"0x{r.Start:X}-0x{r.End:X} {r.Description}"));

    [Fact]
    public void Protect_changes_pages_and_never_offers_rwx()
    {
        Assert.DoesNotContain("ReadWriteExecute", Enum.GetNames<MemoryProtection>());
        var length = (nuint)(P.PageSize * 2);
        var map = P.Map(length, out _);
        Assert.NotEqual(0, map);
        try
        {
            Marshal.WriteByte(map, 0xC3);
            P.Protect(map, length, MemoryProtection.ReadExecute);
            Assert.Equal(0xC3, Marshal.ReadByte(map));
            P.Protect(map, (nuint)P.PageSize, MemoryProtection.None);        // guard page
            P.Protect(map, (nuint)P.PageSize, MemoryProtection.ReadWrite);
            Marshal.WriteByte(map, 1);
            P.Protect(map, length, MemoryProtection.Read);
            Assert.Equal(1, Marshal.ReadByte(map));
        }
        finally { P.Free(map, length); }
    }

    [Fact]
    public unsafe void Published_x64_code_executes_and_can_be_republished()
    {
        var length = (nuint)P.PageSize;
        var map = P.Map(length, out var error);
        Assert.True(map != 0, $"Map failed with OS error {error}");
        try
        {
            // mov eax, imm32; ret: same machine code and return ABI on both supported hosts.
            Marshal.Copy(new byte[] { 0xB8, 42, 0, 0, 0, 0xC3 }, 0, map, 6);
            P.Protect(map, length, MemoryProtection.ReadExecute);
            var invoke = (delegate* unmanaged<int>)map;
            Assert.Equal(42, invoke());
            P.Protect(map, length, MemoryProtection.ReadWrite);
            Marshal.WriteInt32(map, 1, 73);
            P.Protect(map, length, MemoryProtection.ReadExecute);
            Assert.Equal(73, invoke());
        }
        finally { P.Free(map, length); }
    }

    [Fact]
    public void Free_releases_the_range_and_ignores_null()
    {
        P.Free(0, 4096);
        var length = (nuint)P.PageSize;
        var map = P.MapLow(length, out _);
        P.Free(map, length);
        Assert.DoesNotContain(P.QueryRanges((ulong)map, (ulong)map + length), r => r.Start <= (ulong)map && r.End > (ulong)map);
    }

    [Fact]
    public void Protect_failure_is_an_explicit_GuestLoadException()
    {
        Assert.Throws<GuestLoadException>(() => P.Protect(0x1000, 4096, MemoryProtection.Read));
    }
}
