using BlitzCS.BinaryCompiler.Runtime;

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
/// reserve and commit happen together (reserved pages are accessible), MEM_DECOMMIT zero-fills instead of unmapping (and makes the pages read-write again), the allocation
/// granularity is 4 KiB (Windows: 64 KiB), and a non-NULL lpAddress is honoured only inside a region this service already
/// allocated (guest low memory cannot be mapped at arbitrary addresses through the runtime API).
/// Page protections are enforced for guest data (VirtualAlloc/heap buffers and the non-code pages of the translated image) through <see cref="GuestMemory.TryProtect"/>; the host never
/// gets execute permission for them, so an executable data page is recorded for VirtualQuery only (jumping into it traps as an untranslated target).
/// Pages of translated code never become writable: <see cref="Win32SelfModifyingCodeException"/>. Full rules: docs/binary-win32.md.
/// </summary>
public sealed class MemoryService : Win32Service
{
    public const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000;
    private const uint Page = 4096;
    public override string Name => "Kernel32.Memory";

    private static bool ValidProtect(uint p) => p is 1 or 2 or 4 or 8 or 0x10 or 0x20 or 0x40 or 0x80;

    private const uint NoAccess = 1, ReadOnly = 2, ReadWrite = 4, WriteCopy = 8, Execute = 0x10, ExecuteRead = 0x20, ExecuteReadWrite = 0x40, ExecuteWriteCopy = 0x80,
        Guard = 0x100, NoCache = 0x200, WriteCombine = 0x400, MemFree = 0x10000, MemPrivate = 0x20000, MemImage = 0x1000000;

    private static bool WritableRequest(uint baseProtect) => baseProtect is ReadWrite or WriteCopy or ExecuteReadWrite or ExecuteWriteCopy;

    /// <summary>The Windows protection constant of a page: the value last set through VirtualAlloc/VirtualProtect, else the one its backing implies.</summary>
    private static uint CurrentProtect(in GuestPageInfo i) =>
        i.Tag != 0 ? i.Tag
        : i.Kind == GuestPageKind.ImageCode ? ExecuteRead
        : !i.Readable ? NoAccess
        : i.Writable ? ReadWrite : ReadOnly;

    /// <summary>Host protection for a guest protection. Execute is never granted to the host: executable guest data stays readable (and writable if the request was).</summary>
    private static MemoryProtection HostProtection(uint baseProtect) => baseProtect switch
    {
        NoAccess => MemoryProtection.None,
        ReadOnly or Execute or ExecuteRead => MemoryProtection.Read,
        _ => MemoryProtection.ReadWrite,
    };

    private static void ApplyAllocationProtect(Win32Call c, uint start, uint span, uint protect)
    {
        // Memory without protection support (the host-side test fake) keeps its previous behaviour; the real adapter always supports scratch buffers.
        c.Mem.TryProtect(start, span, HostProtection(protect), protect, out _);
    }

    private static uint VirtualProtect(Win32Call c)
    {
        uint address = c[0], size = c[1], requested = c[2], oldPtr = c[3];
        var baseProtect = requested & 0xFF;
        var modifiers = requested & (Guard | NoCache | WriteCombine);
        if (oldPtr == 0) return c.Ctx.Fail(Win32Error.NoAccess);
        if (size == 0 || (requested & ~(0xFFu | Guard | NoCache | WriteCombine)) != 0 || !ValidProtect(baseProtect)
            || (modifiers != 0 && baseProtect == NoAccess) || ((modifiers & NoCache) != 0 && (modifiers & WriteCombine) != 0))
            return c.Ctx.Fail(Win32Error.InvalidParameter);
        if ((modifiers & Guard) != 0)
            throw new Win32UsageException($"VirtualProtect(0x{address:X8}, {size}, 0x{requested:X}): PAGE_GUARD is not supported. A guard page raises STATUS_GUARD_PAGE_VIOLATION on first access and the guest's structured exception handling cannot be driven from translated code.");
        if (!c.Mem.TryQueryPage(oldPtr, out var oldPage) || !oldPage.Writable) return c.Ctx.Fail(Win32Error.NoAccess);

        var end = (ulong)address + size;
        if (end > 0x1_0000_0000UL) return c.Ctx.Fail(Win32Error.InvalidAddress);
        var first = address & ~(Page - 1);
        var pageEnd = (end + Page - 1) / Page * Page;
        var infos = new List<GuestPageInfo>();
        for (var p = (ulong)first; p < pageEnd; p += Page)
        {
            if (!c.Mem.TryQueryPage((uint)p, out var info) || (infos.Count > 0 && info.RegionStart != infos[0].RegionStart))
                return c.Ctx.Fail(Win32Error.InvalidAddress);   // unmapped page, or a range that spans two allocations: nothing is changed
            infos.Add(info);
        }

        var codeModule = infos.FirstOrDefault(i => i.Kind == GuestPageKind.ImageCode).Owner;
        var hasCode = infos.Any(i => i.Kind == GuestPageKind.ImageCode);
        if (hasCode && WritableRequest(baseProtect))
            throw new Win32SelfModifyingCodeException(codeModule ?? "a translated module", first, (uint)(pageEnd - 1), requested);
        if (hasCode && baseProtect == NoAccess)
            throw new Win32UsageException($"VirtualProtect(0x{address:X8}, {size}, PAGE_NOACCESS) on translated code of {codeModule}: translated code is not read from the guest pages, so the guest-visible protection cannot be enforced; refused instead of ignored.");
        if (infos.Any(i => i.Kind == GuestPageKind.Other))
            throw new Win32UsageException($"VirtualProtect(0x{address:X8}, {size}, 0x{requested:X}): the range lies in runtime-owned memory (guest stacks, TEB/PEB) whose protection cannot be changed.");

        var old = CurrentProtect(infos[0]);
        // Translated-code pages (READONLY/EXECUTE/EXECUTE_READ requested) keep their protection: such a change cannot alter behaviour. Everything else is applied run by run.
        for (var i = 0; i < infos.Count;)
        {
            if (infos[i].Kind == GuestPageKind.ImageCode) { i++; continue; }
            var j = i;
            while (j + 1 < infos.Count && infos[j + 1].Kind != GuestPageKind.ImageCode) j++;
            if (!c.Mem.TryProtect(first + (uint)i * Page, (uint)(j - i + 1) * Page, HostProtection(baseProtect), requested, out _))
                return c.Ctx.Fail(Win32Error.InvalidAddress);
            i = j + 1;
        }
        c.Mem.WriteUInt32(oldPtr, old);
        return 1;
    }

    private static uint VirtualQuery(Win32Call c)
    {
        uint address = c[0], buffer = c[1], length = c[2];
        if (length < 28) return c.Ctx.Fail(Win32Error.BadLength);
        if (address >= 0xFFFF0000u) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if (!c.Mem.IsMapped(buffer, 28)) return c.Ctx.Fail(Win32Error.NoAccess);
        var page = address & ~(Page - 1);
        uint allocationBase, allocationProtect, state, protect, type;
        ulong regionEnd;
        if (!c.Mem.TryQueryPage(page, out var info))
        {
            allocationBase = 0; allocationProtect = 0; state = MemFree; protect = NoAccess; type = 0;
            regionEnd = c.Mem.NextMappedAddress(page);
        }
        else
        {
            allocationBase = info.RegionStart; state = 0x1000; protect = CurrentProtect(info);
            type = info.Kind is GuestPageKind.ImageCode or GuestPageKind.ImageData ? MemImage : MemPrivate;
            allocationProtect = c.Mem.TryQueryPage(info.RegionStart, out var head) ? CurrentProtect(head) : protect;
            var p = (ulong)page + Page;
            while (p < info.RegionEnd && c.Mem.TryQueryPage((uint)p, out var next) && next.RegionStart == info.RegionStart && CurrentProtect(next) == protect) p += Page;
            regionEnd = p;
        }
        Span<byte> b = stackalloc byte[28];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, page);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[4..], allocationBase);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[8..], allocationProtect);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)(regionEnd - page));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[16..], state);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[20..], protect);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[24..], type);
        c.Mem.Write(buffer, b);
        return 28;
    }

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
                if ((type & MemCommit) != 0) ApplyAllocationProtect(c, start, span, protect);
                return start;
            }
            if (size > c.Ctx.Options.MaxAllocation) return c.Ctx.Fail(Win32Error.NotEnoughMemory);
            try
            {
                var rounded = checked((int)(((ulong)size + Page - 1) / Page * Page));
                var allocation = c.Ctx.Virtual.Add(c.Mem.Allocate(rounded));
                ApplyAllocationProtect(c, allocation.Address, (uint)rounded, protect);
                return allocation.Address;
            }
            catch (Exception e) when (e is Runtime.GuestException or ArgumentException or OutOfMemoryException or OverflowException)
            { return c.Ctx.Fail(Win32Error.NotEnoughMemory); }
        });
        Std(K, "VirtualProtect", 4, VirtualProtect);
        Std(K, "VirtualQuery", 3, VirtualQuery);
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
                if (size == 0 && c.Ctx.Virtual.Containing(address, 1) is { } whole && whole.Address == address) { ApplyAllocationProtect(c, whole.Address, (uint)whole.Length, ReadWrite); c.Mem.Fill(whole.Address, 0, (uint)whole.Length); return 1; }
                var start = address & ~(Page - 1);
                var span = (uint)(((ulong)address + size + Page - 1) / Page * Page - start);
                if (size == 0 || c.Ctx.Virtual.Containing(start, span) is null) return c.Ctx.Fail(Win32Error.InvalidAddress);
                ApplyAllocationProtect(c, start, span, ReadWrite);
                c.Mem.Fill(start, 0, span);
                return 1;
            }
            return c.Ctx.Fail(Win32Error.InvalidParameter);
        });
    }
}
