using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.CodeGen;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// One translation of guest code made at run time (docs/binary-dynamic-code.md). <see cref="SnapshotSha256"/> identifies the bytes of
/// [<see cref="RegionStart"/>, <see cref="RegionEnd"/>) the translation was made from; <see cref="Entries"/> is the number of entry addresses it was rooted at
/// (each new entry into the same run translates the run again: <see cref="Generation"/> counts those).
/// </summary>
public sealed record DynamicCodeTranslation(uint EntryAddress, uint RegionStart, uint RegionEnd, string SnapshotSha256, int Entries, int Instructions, int HostCodeBytes, int Generation, TimeSpan Elapsed);

/// <summary>
/// Code that appears at run time: the guest copies or generates instructions into memory it made executable (VirtualAlloc/VirtualProtect PAGE_EXECUTE*, an
/// executable heap) and jumps there. Nothing of it existed when the image was translated, so the miss routine calls <see cref="MissHandler"/>, which translates the
/// executable run of pages that holds the target with the normal translator and publishes it in the module registry like a module; the walk is then repeated.
/// The snapshot is the identity of the translation: its pages are write protected (host read-only) while it lives, a guest store that reaches a translated
/// instruction byte is a named fault, stores to data on those pages are performed (<see cref="DynamicCodeGuard"/>), and freeing the memory drops the translation.
/// </summary>
internal sealed unsafe partial class GuestProcessEnvironment
{
    private static readonly ConcurrentDictionary<nint, GuestProcessEnvironment> byRegistry = new();

    private sealed class DynamicUnit
    {
        public required uint Start, End;
        public required GuestRegion Region;
        public required byte[] Snapshot;
        public required bool[] CodeBytes;
        public required string Sha256;
        public required SortedSet<uint> Entries;
        public required nint HostMap;
        public required nuint HostLength;
        public required nint ExitCommon;
        public int Slot = -1;
    }

    private readonly object dynamicGate = new();
    private readonly List<DynamicUnit> dynamicUnits = [];
    private readonly List<(nint Map, nuint Length)> deadHostImages = [];   // replaced or dropped translations: kept mapped, a thread may still run in them
    private readonly List<DynamicCodeTranslation> dynamicLog = [];
    private readonly Dictionary<uint, string> dynamicFailures = [];
    private int dynamicGeneration;

    internal IReadOnlyList<DynamicCodeTranslation> DynamicTranslations { get { lock (dynamicGate) return [.. dynamicLog]; } }

    private void InitializeDynamicCode()
    {
        byRegistry[registry] = this;
        *(nint*)(registry + ModuleRegistryLayout.MissHandler) = (nint)(delegate* unmanaged<nint, uint, nint, uint>)&MissHandler;
    }

    private void DisposeDynamicCode()
    {
        *(nint*)(registry + ModuleRegistryLayout.MissHandler) = 0;
        byRegistry.TryRemove(registry, out _);
        lock (dynamicGate)
        {
            foreach (var u in dynamicUnits) { DynamicCodeGuard.RemoveRange(u.Start); deadHostImages.Add((u.HostMap, u.HostLength)); }
            dynamicUnits.Clear();
            foreach (var (map, length) in deadHostImages) { DynamicCodeGuard.RemoveHostCode(map); PlatformMemory.Current.Free(map, length); }
            deadHostImages.Clear();
        }
    }

    /// <summary>Called by the miss routine of translated code (native stack, guest state saved). Never throws: a failure is recorded for the trap message.</summary>
    [UnmanagedCallersOnly]
    private static uint MissHandler(nint registryAddress, uint va, nint callContext)
    {
        if (!byRegistry.TryGetValue(registryAddress, out var env)) return 0;
        try { return env.TranslateOnMiss(va) ? 1u : 0u; }
        catch (Exception e)
        {
            try { lock (env.dynamicGate) env.dynamicFailures[va] = $"translating the code generated at run time there failed: {e.GetType().Name}: {e.Message}"; } catch { }
            return 0;
        }
    }

    internal string? TakeDynamicCodeFailure(uint va)
    {
        lock (dynamicGate) return dynamicFailures.Remove(va, out var why) ? why : null;
    }

    private bool Fail(uint va, string why) { dynamicFailures[va] = why; return false; }

    private bool TranslateOnMiss(uint va)
    {
        var entered = GuestModule.Current;
        var any = entered ?? AnyModule();
        if (any is null) return false;
        lock (dynamicGate)
        {
            var region = any.Memory.FindRegion(va, out _);
            if (region is null || region.Kind != GuestPageKind.Buffer) return false;   // not memory the guest allocated: the ordinary "untranslated code" trap
            var ps = (uint)PlatformMemory.Current.PageSize;
            var page = (int)((va - region.Start) / ps);
            if (region.PageGuestExecutable is not { } exec || !exec[page])
                return Fail(va, $"it lies in guest memory 0x{region.Start:X8}-0x{region.End:X8} that was allocated at run time but never made executable (PAGE_EXECUTE*), so it is not translated as code.");
            if (entered is not null && !entered.Options.DynamicCode)
                return Fail(va, $"it lies in executable memory the guest allocated at run time (0x{region.Start:X8}-0x{region.End:X8}), and translating code at run time is disabled (GuestModuleOptions.DynamicCode = false).");
            if (!DynamicCodeGuard.Supported)
                return Fail(va, $"it lies in executable memory the guest allocated at run time (0x{region.Start:X8}-0x{region.End:X8}); translating it needs the write guard that only the windows-x64 runtime has (a vectored exception handler that names stores into translated code), so code generated at run time is not run on {PlatformMemory.Current.Name}.");

            // The translation unit: the run of executable pages around the target, inside this allocation.
            var pages = exec.Length;
            var first = page; while (first > 0 && exec[first - 1]) first--;
            var last = page; while (last + 1 < pages && exec[last + 1]) last++;
            var start = (uint)(region.Start + (ulong)first * ps);
            var end = (uint)Math.Min(region.End, region.Start + (ulong)(last + 1) * ps);
            if ((end - start) % ps != 0) return Fail(va, $"the executable run 0x{start:X8}-0x{end:X8} does not end on a page boundary.");

            var entries = new SortedSet<uint> { va };
            var replaced = dynamicUnits.Where(u => u.Start < end && start < u.End).ToList();
            foreach (var u in replaced)
            {
                foreach (var e in u.Entries) if (e >= start && e < end) entries.Add(e);
            }
            var bytes = new byte[end - start];
            Marshal.Copy((nint)start, bytes, 0, bytes.Length);
            foreach (var u in replaced)   // the guard keeps translated instruction bytes stable; a difference means it was bypassed
                for (var k = 0; k < u.CodeBytes.Length; k++)
                    if (u.CodeBytes[k] && u.Start + (uint)k >= start && u.Start + (uint)k < end && bytes[u.Start + (uint)k - start] != u.Snapshot[k])
                        return Fail(va, $"the instruction byte at 0x{u.Start + (uint)k:X8} of code translated earlier from 0x{u.Start:X8}-0x{u.End:X8} has changed (self-modifying code that bypassed the write guard); the old translation would be stale.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var t = X64Translator.TranslateDynamic(bytes, start, entries, new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: PlatformMemory.Current.TargetOs));
            var errors = t.Artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                return Fail(va, $"the code generated at run time in 0x{start:X8}-0x{end:X8} could not be translated: " + string.Join("; ", errors.Take(3).Select(d => $"{d.Code} at 0x{start + (d.GuestRva ?? 0):X8}: {d.Message}")));
            var a = t.Artifact;
            var host = a.HostImage;
            var table = (int)a.DispatchTableOffset;
            uint Entry(uint rva) => BitConverter.ToUInt32(host, table + 4 * (int)rva);
            if (Entry(va - start) == 0)
                return Fail(va, $"the bytes at 0x{va:X8} (executable memory 0x{start:X8}-0x{end:X8} allocated at run time) do not decode as an IA-32 instruction the translator accepts.");
            var exitCommon = Entry(a.ExitGuestRva);
            // Only the snapshot is reachable through the registry: the synthetic exit and stub addresses after it are not this unit's to claim.
            var entryCount = (host.Length - table) / 4;
            for (var rva = (int)(end - start); rva < entryCount; rva++) BitConverter.TryWriteBytes(host.AsSpan(table + 4 * rva), 0u);

            var hostLength = ((nuint)host.Length + ps - 1) / ps * ps;
            var map = PlatformMemory.Current.Map(hostLength, out var errno);
            if (map == 0) return Fail(va, $"the host image of the translation could not be mapped (OS error {errno}).");
            Marshal.Copy(host, 0, map, host.Length);
            *(nint*)(map + (nint)(a.ContextOffset + ModuleRegistryLayout.CellOffset)) = registry;
            PlatformMemory.Current.Protect(map, (nuint)a.CodeSize, MemoryProtection.ReadExecute);
            DynamicCodeGuard.AddHostCode(map, (nuint)a.CodeSize);

            var unit = new DynamicUnit
            {
                Start = start, End = end, Region = region, Snapshot = bytes, CodeBytes = t.InstructionBytes, Sha256 = t.SnapshotSha256, Entries = entries,
                HostMap = map, HostLength = hostLength, ExitCommon = map + (nint)exitCommon,
            };
            // An older record of the same addresses would match first and miss: it is retired before the new one is published (a concurrent miss in between waits on this lock).
            foreach (var u in replaced)
            {
                RetireReplaced(u.Slot);
                dynamicUnits.Remove(u);
                DynamicCodeGuard.RemoveRange(u.Start);
                deadHostImages.Add((u.HostMap, u.HostLength));
            }
            Lock(unit, ps);
            unit.Slot = Publish(start, end - start, map, map + (nint)a.DispatchTableOffset);
            dynamicUnits.Add(unit);
            dynamicLog.Add(new DynamicCodeTranslation(va, start, end, t.SnapshotSha256, entries.Count, t.InstructionCount, a.CodeSize, ++dynamicGeneration, clock.Elapsed));
            return true;
        }
    }

    /// <summary>Write protects the pages of a unit (host read-only, guest-visible protection unchanged) and hands its instruction bytes to the guard.</summary>
    private static void Lock(DynamicUnit unit, uint ps)
    {
        var r = unit.Region;
        var pages = (int)((r.End - r.Start + ps - 1) / ps);
        var writable = r.PageWritable ?? Enumerable.Repeat(true, pages).ToArray();
        var before = r.PageWritableBeforeLock ?? (bool[])writable.Clone();
        var code = r.PageDynamicCode ?? new bool[pages];
        for (var p = (int)((unit.Start - r.Start) / ps); p < (int)((unit.End - r.Start) / ps); p++)
        {
            if (!code[p]) before[p] = writable[p];
            code[p] = true;
            writable[p] = false;
        }
        r.PageWritableBeforeLock = before; r.PageDynamicCode = code; r.PageWritable = writable;
        DynamicCodeGuard.AddRange(unit.Start, unit.End, unit.CodeBytes, unit.ExitCommon);
        if (r.PageReadable is { } readable && Enumerable.Range((int)((unit.Start - r.Start) / ps), (int)((unit.End - unit.Start) / ps)).Any(p => !readable[p])) return;
        PlatformMemory.Current.Protect((nint)unit.Start, unit.End - unit.Start, MemoryProtection.Read);
    }

    private static void Unlock(DynamicUnit unit, uint ps)
    {
        var r = unit.Region;
        DynamicCodeGuard.RemoveRange(unit.Start);
        if (r.PageDynamicCode is not { } code || r.PageWritable is not { } writable) return;
        for (var p = (int)((unit.Start - r.Start) / ps); p < (int)((unit.End - r.Start) / ps); p++)
        {
            if (!code[p]) continue;
            code[p] = false;
            writable[p] = r.PageGuestWritable(p);
            var readable = r.PageReadable?[p] ?? true;
            PlatformMemory.Current.Protect((nint)(r.Start + (ulong)p * ps), ps, !readable ? MemoryProtection.None : writable[p] ? MemoryProtection.ReadWrite : MemoryProtection.Read);
        }
    }

    /// <summary>Drops every translation overlapping the range (the guest frees or decommits it). Returns how many were dropped.</summary>
    internal int InvalidateDynamicCode(uint address, uint length)
    {
        if (length == 0) return 0;
        var end = (ulong)address + length;
        var ps = (uint)PlatformMemory.Current.PageSize;
        lock (dynamicGate)
        {
            var hit = dynamicUnits.Where(u => u.Start < end && address < u.End).ToList();
            foreach (var u in hit)
            {
                Retire(u.Slot, $"code generated at run time at 0x{u.Start:X8}-0x{u.End:X8} (freed)", u.Start, u.End - u.Start);
                dynamicUnits.Remove(u);
                Unlock(u, ps);
                deadHostImages.Add((u.HostMap, u.HostLength));
            }
            return hit.Count;
        }
    }

    /// <summary>
    /// A managed write into write-protected dynamic-code pages: refused by name when it reaches a translated instruction byte, otherwise performed with the pages
    /// briefly writable. False when the range is not (only) such pages, or a page of it is not writable for the guest.
    /// </summary>
    internal bool TryDynamicCodeDataWrite(GuestMemory memory, uint address, uint length, delegate*<byte*, void*, void> store, void* state)
    {
        var ps = (uint)PlatformMemory.Current.PageSize;
        lock (dynamicGate)
        {
            var end = (ulong)address + length;
            var region = memory.FindRegion(address, out _);
            if (region is null || end > region.End || region.PageDynamicCode is not { } code) return false;
            var locked = new List<int>();
            for (var p = (int)((address - region.Start) / ps); p <= (int)((end - 1 - region.Start) / ps); p++)
            {
                if (code[p]) { if (!region.PageGuestWritable(p)) return false; locked.Add(p); }
                else if (!region.CoversWrite(region.Start + (ulong)p * ps, region.Start + (ulong)(p + 1) * ps)) return false;
            }
            foreach (var u in dynamicUnits)
                for (var a = Math.Max(address, u.Start); a < Math.Min(end, u.End); a++)
                    if (u.CodeBytes[a - u.Start]) throw new GuestSelfModifyingCodeException(a, u.Start, u.End, u.Sha256);
            foreach (var p in locked) PlatformMemory.Current.Protect((nint)(region.Start + (ulong)p * ps), ps, MemoryProtection.ReadWrite);
            try { store((byte*)(nuint)address, state); }
            finally { foreach (var p in locked) PlatformMemory.Current.Protect((nint)(region.Start + (ulong)p * ps), ps, MemoryProtection.Read); }
            return true;
        }
    }

    /// <summary>The exception for a guest store the guard stopped (trap kind <see cref="DynamicCodeGuard.TrapSelfModifyingCode"/>).</summary>
    internal GuestSelfModifyingCodeException SelfModifyingCodeFault(uint address)
    {
        lock (dynamicGate)
            foreach (var u in dynamicUnits)
                if (address >= u.Start && address < u.End) return new GuestSelfModifyingCodeException(address, u.Start, u.End, u.Sha256);
        return new GuestSelfModifyingCodeException(address, address, address, "");
    }
}
