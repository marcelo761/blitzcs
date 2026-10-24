namespace BlitzCS.BinaryCompiler.Win32;

internal sealed class VirtualState : IDisposable
{
    private readonly object gate = new();
    private readonly SortedDictionary<uint, IWin32Allocation> regions = [];

    public IWin32Allocation Add(IWin32Allocation a) { lock (gate) regions[a.Address] = a; return a; }
    public bool Remove(uint baseAddress, out IWin32Allocation? a) { lock (gate) { var ok = regions.Remove(baseAddress, out a); return ok; } }

    /// <summary>The region fully containing [address, address+size), or null.</summary>
    public IWin32Allocation? Containing(uint address, uint size)
    {
        lock (gate)
            foreach (var r in regions.Values)
                if (address >= r.Address && (ulong)address + size <= (ulong)r.Address + (ulong)r.Length) return r;
        return null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var r in regions.Values) { try { r.Dispose(); } catch (ObjectDisposedException) { } catch (Runtime.GuestException) { } }
            regions.Clear();
        }
    }
}

/// <summary>
/// KERNEL32 virtual memory. Regions are page-granular guest allocations. Documented approximations (Microsoft Learn, "VirtualAlloc"):
/// reserve and commit happen together (reserved pages are accessible), MEM_DECOMMIT zero-fills instead of unmapping, the allocation
/// granularity is 4 KiB (Windows: 64 KiB), page protections are validated but not enforced (VirtualProtect is therefore NOT implemented:
/// it needs a runtime hook to change host page protection), and a non-NULL lpAddress is honoured only inside a region this service already
/// allocated (guest low memory cannot be mapped at arbitrary addresses through the runtime API).
/// </summary>
public sealed class MemoryService : Win32Service
{
    public const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000;
    private const uint Page = 4096;
    public override string Name => "Kernel32.Memory";

    private static bool ValidProtect(uint p) => p is 1 or 2 or 4 or 8 or 0x10 or 0x20 or 0x40 or 0x80;

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "VirtualAlloc", 4, c =>
        {
            uint address = c[0], size = c[1], type = c[2], protect = c[3];
            if ((type & (MemCommit | MemReserve)) == 0 || (type & ~(MemCommit | MemReserve | 0x100000 | 0x400000 | 0x20000000)) != 0 || size == 0 || !ValidProtect(protect))
                return c.Ctx.Fail(Win32Error.InvalidParameter);
            if (address != 0)
            {
                var start = address & ~(Page - 1);
                var span = (uint)(((ulong)address + size + Page - 1) / Page * Page - start);
                if (c.Ctx.Virtual.Containing(start, span) is not { } region) return c.Ctx.Fail(Win32Error.InvalidAddress);
                if ((type & MemCommit) != 0 && (type & MemReserve) == 0) { /* committing already-accessible pages: nothing to do */ }
                _ = region;
                return start;
            }
            if (size > c.Ctx.Options.MaxAllocation) return c.Ctx.Fail(Win32Error.NotEnoughMemory);
            try
            {
                var rounded = checked((int)(((ulong)size + Page - 1) / Page * Page));
                return c.Ctx.Virtual.Add(c.Mem.Allocate(rounded)).Address;
            }
            catch (Exception e) when (e is Runtime.GuestException or ArgumentException or OutOfMemoryException or OverflowException)
            { return c.Ctx.Fail(Win32Error.NotEnoughMemory); }
        });
        Std(K, "VirtualFree", 3, c =>
        {
            uint address = c[0], size = c[1], type = c[2];
            if (type == MemRelease)
            {
                if (size != 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
                if (!c.Ctx.Virtual.Remove(address, out var region)) return c.Ctx.Fail(Win32Error.InvalidAddress); // not a base address, or already released
                region!.Dispose();
                return 1;
            }
            if (type == MemDecommit)
            {
                if (size == 0 && c.Ctx.Virtual.Containing(address, 1) is { } whole && whole.Address == address) { c.Mem.Fill(whole.Address, 0, (uint)whole.Length); return 1; }
                var start = address & ~(Page - 1);
                var span = (uint)(((ulong)address + size + Page - 1) / Page * Page - start);
                if (size == 0 || c.Ctx.Virtual.Containing(start, span) is null) return c.Ctx.Fail(Win32Error.InvalidAddress);
                c.Mem.Fill(start, 0, span);
                return 1;
            }
            return c.Ctx.Fail(Win32Error.InvalidParameter);
        });
    }
}
