
namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// What the modules of one guest process share (docs/binary-multimodule.md, docs/binary-teb-contract.md): the module registry that translated code consults
/// to jump or return into another module, the PEB and process parameters, and one TEB per OS thread (SEH chain, TLS slots, stack bounds, the implicit-TLS
/// pointer array with a data block per module that has a TLS directory). A <see cref="GuestModuleSet"/> owns one environment for all its modules; a module that
/// is loaded on its own creates a private one and disposes it with itself. All members are thread safe.
/// </summary>
internal sealed unsafe partial class GuestProcessEnvironment : IDisposable
{
    /// <summary>Records in the module registry. A record is never reused (a stale reader can then never see a different module in its slot).</summary>
    internal const int RegistryCapacity = 1024;
    /// <summary>Indices of the implicit-TLS pointer array (one page of 4-byte slots).</summary>
    private const int MaxTlsModules = 1024;

    private readonly object gate = new();
    private readonly List<GuestModule> modules = [];
    private readonly List<GuestRegion> sharedRegions = [];
    private readonly bool ownedBySet;
    private bool disposed;

    // ---- module registry (native memory read by the translated miss routine) ----
    private readonly nint registry;
    private readonly List<(string Name, uint Start, uint End)> retired = [];

    // ---- PEB / process parameters ----
    private nint pebMap, processBlockMap;
    private nuint pebLength, processBlockLength;

    // ---- TEBs ----
    // Addresses only: a strong reference to the block would keep it from being finalized when its thread dies.
    private readonly Dictionary<nint, TebResources> tebRegistry = [];
    private readonly ThreadLocal<TebBlock?> threadTeb = new(() => null);
    private readonly List<TlsModule> tlsModules = [];
    private int activeThreads;

    internal GuestProcessEnvironment(bool ownedBySet)
    {
        this.ownedBySet = ownedBySet;
        registry = (nint)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(ModuleRegistryLayout.HeaderSize + RegistryCapacity * ModuleRegistryLayout.RecordSize));
        *(uint*)(registry + 4) = RegistryCapacity;
        InitializeDynamicCode();
    }

    internal nint RegistryAddress => registry;

    /// <summary>Resources of one thread's TEB; owned by the registry so that they can be freed from the finalizer of the <see cref="TebBlock"/>.</summary>
    private sealed class TebResources
    {
        public nint Map, ArrayMap;
        public nuint Length, ArrayLength;
        public readonly Dictionary<int, (nint Map, nuint Length)> Data = [];
    }

    /// <summary>The calling thread's TEB. The finalizer frees the mappings if the thread dies without the environment being disposed first.</summary>
    internal sealed class TebBlock(GuestProcessEnvironment owner, nint map)
    {
        public readonly nint Map = map;
        /// <summary>Nesting of guest calls on this thread across all modules of the process (0 = the thread is outside guest code).</summary>
        public int Depth;
        public uint Address => (uint)Map;
        ~TebBlock() => owner.ReleaseTeb(Map);
    }

    private sealed record TlsModule(GuestModule Module, int Index, nint Template, uint TemplateSize, nuint TotalSize);

    // ---- membership ----

    /// <summary>
    /// Adds the module to the process: it sees the shared regions (PEB, TEBs, TLS blocks) and its memory is linked with the memory of every other module, before any of its guest
    /// code runs (its DllMain may already touch heap blocks that another module allocated: one process, one address space).
    /// </summary>
    internal void Attach(GuestModule module)
    {
        lock (gate)
        {
            foreach (var other in modules) { module.Memory.LinkPeer(other.Memory); other.Memory.LinkPeer(module.Memory); }
            modules.Add(module);
            foreach (var r in sharedRegions) module.Memory.Add(r);
        }
    }

    /// <summary>Removes the module; a private environment (not owned by a set) is disposed together with its only module.</summary>
    internal void Detach(GuestModule module)
    {
        lock (gate)
        {
            modules.Remove(module);
            foreach (var other in modules) { module.Memory.UnlinkPeer(other.Memory); other.Memory.UnlinkPeer(module.Memory); }
            foreach (var r in sharedRegions) module.Memory.Remove(r.Start);
        }
        if (!ownedBySet && modules.Count == 0) Dispose();
    }

    private void AddRegion(GuestRegion region)
    {
        lock (gate) { sharedRegions.Add(region); foreach (var m in modules) m.Memory.Add(region); }
    }

    private void RemoveRegion(ulong start)
    {
        lock (gate) { sharedRegions.RemoveAll(r => r.Start == start); foreach (var m in modules) m.Memory.Remove(start); }
    }

    // ---- registry ----

    /// <summary>
    /// Publishes a module so translated code of every module can reach it. The record is completed first, the table pointer (non-zero = live) next and the count last,
    /// each with release semantics, so a thread that is dispatching concurrently sees either no record or a complete one.
    /// </summary>
    internal int Publish(uint guestBase, uint bound, nint hostBase, nint table)
    {
        lock (gate)
        {
            var slot = (int)*(uint*)registry;
            if (slot >= RegistryCapacity)
                throw new GuestLoadException($"The module registry is full ({RegistryCapacity} modules were loaded into this process; slots are not reused so that a concurrent dispatch can never see another module in a slot). Unloaded modules still occupy their slot.");
            var rec = registry + ModuleRegistryLayout.HeaderSize + slot * ModuleRegistryLayout.RecordSize;
            *(uint*)(rec + ModuleRegistryLayout.GuestBase) = guestBase;
            *(uint*)(rec + ModuleRegistryLayout.Bound) = bound;
            *(nint*)(rec + ModuleRegistryLayout.HostBase) = hostBase;
            Volatile.Write(ref *(nint*)(rec + ModuleRegistryLayout.Table), table);
            Volatile.Write(ref *(uint*)registry, (uint)slot + 1);
            return slot;
        }
    }

    /// <summary>Marks the record dead: bound 0 first (no address can match any more), then the table pointer. The caller has already established that no other guest thread can be executing in the module.</summary>
    internal void Retire(int slot, string name, uint guestBase, uint bound)
    {
        if (slot < 0) return;
        lock (gate)
        {
            var rec = registry + ModuleRegistryLayout.HeaderSize + slot * ModuleRegistryLayout.RecordSize;
            Volatile.Write(ref *(uint*)(rec + ModuleRegistryLayout.Bound), 0);
            Volatile.Write(ref *(nint*)(rec + ModuleRegistryLayout.Table), 0);
            retired.Add((name, guestBase, guestBase + bound));
        }
    }

    /// <summary>Like <see cref="Retire"/> for a record that a newer one replaces (no "unloaded" entry is remembered for its range).</summary>
    private void RetireReplaced(int slot)
    {
        lock (gate)
        {
            var rec = registry + ModuleRegistryLayout.HeaderSize + slot * ModuleRegistryLayout.RecordSize;
            Volatile.Write(ref *(uint*)(rec + ModuleRegistryLayout.Bound), 0);
            Volatile.Write(ref *(nint*)(rec + ModuleRegistryLayout.Table), 0);
        }
    }

    private GuestModule? AnyModule() { lock (gate) return modules.Count > 0 ? modules[0] : null; }

    /// <summary>The name of an unloaded module whose address range contains <paramref name="address"/>, or null.</summary>
    internal string? FindRetired(uint address)
    {
        lock (gate)
        {
            for (var i = retired.Count - 1; i >= 0; i--)
                if (address >= retired[i].Start && address < retired[i].End) return retired[i].Name;
        }
        return null;
    }

    // ---- thread accounting (unload safety) ----

    internal void ThreadEntered() => Interlocked.Increment(ref activeThreads);
    internal void ThreadLeft() => Interlocked.Decrement(ref activeThreads);

    /// <summary>Number of threads other than the caller that are inside guest code of any module of this process.</summary>
    internal int OtherActiveThreads()
    {
        var mine = threadTeb.IsValueCreated && threadTeb.Value is { Depth: > 0 } ? 1 : 0;
        return Volatile.Read(ref activeThreads) - mine;
    }

    // ---- PEB ----

    internal uint PebAddress => (uint)pebMap;

    /// <summary>Creates the PEB on first use; the executable's image base wins over a DLL's for ImageBaseAddress.</summary>
    internal void EnsurePeb(GuestModule owner, uint imageBase, bool isExecutable, GuestProcessInfo info)
    {
        lock (gate)
        {
            if (pebMap == 0)
            {
                var length = (nuint)Math.Max(4096, PlatformMemory.Current.PageSize);
                var map = PlatformMemory.Current.MapLow(length, out var errno);
                if (map == 0) throw new GuestAddressSpaceException($"Cannot allocate the guest PEB in low memory (OS error {errno}).");
                pebMap = map; pebLength = length;
                *(byte*)(map + (nint)GuestModule.PebBeingDebugged) = 0;
                *(uint*)(map + (nint)GuestModule.PebImageBase) = imageBase;
                AddRegionLocked(new GuestRegion((ulong)map, (ulong)map + length, null));
                BuildProcessBlock(info);
            }
            else if (isExecutable) *(uint*)(pebMap + (nint)GuestModule.PebImageBase) = imageBase;
        }
    }

    private void AddRegionLocked(GuestRegion region) { sharedRegions.Add(region); foreach (var m in modules) m.Memory.Add(region); }

    internal void SetProcessHeap(uint handle)
    {
        if (pebMap != 0) *(uint*)(pebMap + (nint)GuestModule.PebProcessHeap) = handle;
    }

    internal static (nint Map, nuint Length) AllocateLow(nuint bytes, string what)
    {
        var ps = (nuint)PlatformMemory.Current.PageSize;
        var length = (bytes + ps - 1) / ps * ps;
        var map = PlatformMemory.Current.MapLow(length, out var errno);
        if (map == 0) throw new GuestAddressSpaceException($"Cannot allocate the guest {what} in low memory (OS error {errno}).");
        return (map, length);
    }

    private void BuildProcessBlock(GuestProcessInfo info)
    {
        static byte[] Wide(string s) => System.Text.Encoding.Unicode.GetBytes(s);
        var env = new List<byte>();
        foreach (var e in info.Environment) { env.AddRange(Wide(e)); env.AddRange(new byte[2]); }
        env.AddRange(new byte[info.Environment.Count == 0 ? 4 : 2]);
        var strings = new[] { Wide(info.ImagePath), Wide(info.CommandLine), Wide(info.CurrentDirectory), Wide("") };
        nuint total = 0x40 /* Ldr */ + GuestModule.ParamsSize + (nuint)env.Count + 8;
        foreach (var w in strings) total += (nuint)w.Length + 2 + 2;
        var (map, length) = AllocateLow(total, "process parameters");
        processBlockMap = map; processBlockLength = length;
        var baseAddr = (uint)map;
        var at = 0u;
        uint Take(uint n) { var r = baseAddr + at; at += (n + 3) & ~3u; return r; }

        // PEB_LDR_DATA: Length 0x30, Initialized, three empty LIST_ENTRY (Flink = Blink = the head itself).
        var ldr = Take(0x30);
        *(uint*)(ldr + 0x00) = 0x30; *(uint*)(ldr + 0x04) = 1;
        foreach (var head in new uint[] { 0x0C, 0x14, 0x1C }) { *(uint*)(ldr + head) = ldr + head; *(uint*)(ldr + head + 4) = ldr + head; }

        var envAddr = Take((uint)env.Count);
        env.ToArray().CopyTo(new Span<byte>((void*)envAddr, env.Count));
        uint PutString(byte[] w) { var a = Take((uint)w.Length + 2); w.CopyTo(new Span<byte>((void*)a, w.Length)); return a; }
        var parameters = Take(GuestModule.ParamsSize);
        void Unicode(uint offset, byte[] w)
        {
            var a = PutString(w);
            *(ushort*)(parameters + offset) = (ushort)w.Length; *(ushort*)(parameters + offset + 2) = (ushort)(w.Length + 2); *(uint*)(parameters + offset + 4) = a;
        }
        *(uint*)(parameters + GuestModule.ParamsMaximumLength) = GuestModule.ParamsSize; *(uint*)(parameters + GuestModule.ParamsLength) = GuestModule.ParamsSize;
        *(uint*)(parameters + GuestModule.ParamsFlags) = 1; // RTL_USER_PROC_PARAMS_NORMALIZED
        *(uint*)(parameters + GuestModule.ParamsStdInput) = info.StandardInput; *(uint*)(parameters + GuestModule.ParamsStdOutput) = info.StandardOutput; *(uint*)(parameters + GuestModule.ParamsStdError) = info.StandardError;
        Unicode(GuestModule.ParamsCurrentDirectory, strings[2]);
        Unicode(GuestModule.ParamsDllPath, strings[3]);
        Unicode(GuestModule.ParamsImagePathName, strings[0]);
        Unicode(GuestModule.ParamsCommandLine, strings[1]);
        *(uint*)(parameters + GuestModule.ParamsEnvironment) = envAddr;
        AddRegionLocked(new GuestRegion((ulong)map, (ulong)map + length, null));

        var peb = pebMap;
        *(uint*)(peb + (nint)GuestModule.PebLdr) = ldr;
        *(uint*)(peb + (nint)GuestModule.PebProcessParameters) = parameters;
        *(uint*)(peb + (nint)GuestModule.PebProcessHeap) = 0;
        *(uint*)(peb + (nint)GuestModule.PebNumberOfProcessors) = (uint)Environment.ProcessorCount;
        *(uint*)(peb + (nint)GuestModule.PebNtGlobalFlag) = 0;
        *(uint*)(peb + (nint)GuestModule.PebOsMajor) = info.OsMajor; *(uint*)(peb + (nint)GuestModule.PebOsMinor) = info.OsMinor;
        *(ushort*)(peb + (nint)GuestModule.PebOsBuild) = info.OsBuild; *(ushort*)(peb + (nint)GuestModule.PebOsCsdVersion) = info.OsServicePack;
        *(uint*)(peb + (nint)GuestModule.PebOsPlatformId) = 2; // VER_PLATFORM_WIN32_NT
    }

    // ---- implicit TLS ----

    /// <summary>
    /// Gives a module with a TLS directory the next TLS index of the process and a data block (template + zero fill) in the TEB of every thread that already has one.
    /// The pointer array of each TEB holds one block per index, as the Windows loader arranges it. Returns the index the loader stores at the directory's index address.
    /// </summary>
    internal int RegisterTls(GuestModule module, nint template, uint templateSize, nuint totalSize)
    {
        lock (gate)
        {
            var index = tlsModules.Count == 0 ? 0 : tlsModules.Max(t => t.Index) + 1;
            if (index >= MaxTlsModules) throw new GuestLoadException($"More than {MaxTlsModules} modules with a TLS directory in one process.");
            var tls = new TlsModule(module, index, template, templateSize, totalSize);
            foreach (var teb in tebRegistry.Values) AllocateTlsBlock(teb, tls);
            tlsModules.Add(tls);
            return index;
        }
    }

    internal void UnregisterTls(GuestModule module)
    {
        lock (gate)
        {
            foreach (var tls in tlsModules.Where(t => t.Module == module).ToList())
            {
                tlsModules.Remove(tls);
                foreach (var teb in tebRegistry.Values)
                    if (teb.Data.Remove(tls.Index, out var block))
                    {
                        if (teb.ArrayMap != 0) *(uint*)(teb.ArrayMap + 4 * tls.Index) = 0;
                        RemoveRegionLocked((ulong)block.Map);
                        PlatformMemory.Current.Free(block.Map, block.Length);
                    }
            }
        }
    }

    private void RemoveRegionLocked(ulong start) { sharedRegions.RemoveAll(r => r.Start == start); foreach (var m in modules) m.Memory.Remove(start); }

    private void AllocateTlsBlock(TebResources teb, TlsModule tls)
    {
        if (teb.ArrayMap == 0)
        {
            (teb.ArrayMap, teb.ArrayLength) = AllocateLow(4 * MaxTlsModules, "TLS pointer array");
            *(uint*)(teb.Map + (nint)GuestModule.TebTlsPointer) = (uint)teb.ArrayMap;
            AddRegionLocked(new GuestRegion((ulong)teb.ArrayMap, (ulong)teb.ArrayMap + teb.ArrayLength, null));
        }
        var (map, length) = AllocateLow(16 + ((tls.TotalSize + 15) & ~(nuint)15), "TLS data");
        var data = map + 16;
        if (tls.TemplateSize != 0) Buffer.MemoryCopy((void*)tls.Template, (void*)data, tls.TemplateSize, tls.TemplateSize);
        teb.Data[tls.Index] = (map, length);
        *(uint*)(teb.ArrayMap + 4 * tls.Index) = (uint)data;
        AddRegionLocked(new GuestRegion((ulong)map, (ulong)map + length, null));
    }

    // ---- TEB ----

    /// <summary>The calling thread's TEB, created on first use (SEH chain empty, PEB and a data block per TLS module in place).</summary>
    internal TebBlock GetOrCreateTeb()
    {
        if (threadTeb.Value is { } existing) return existing;
        var (map, length) = AllocateLow(0x1000, "TEB");
        var resources = new TebResources { Map = map, Length = length };
        lock (gate)
        {
            if (disposed) { PlatformMemory.Current.Free(map, length); throw new ObjectDisposedException(nameof(GuestProcessEnvironment)); }
            try
            {
                *(uint*)(map + (nint)GuestModule.TebExceptionList) = 0xFFFFFFFFu;
                *(uint*)(map + (nint)GuestModule.TebSelf) = (uint)map;
                *(uint*)(map + (nint)GuestModule.TebProcessId) = (uint)Environment.ProcessId;
                *(uint*)(map + (nint)GuestModule.TebThreadId) = (uint)Environment.CurrentManagedThreadId;
                *(uint*)(map + (nint)GuestModule.TebPeb) = (uint)pebMap;
                foreach (var tls in tlsModules) AllocateTlsBlock(resources, tls);
            }
            catch
            {
                foreach (var d in resources.Data.Values) PlatformMemory.Current.Free(d.Map, d.Length);
                if (resources.ArrayMap != 0) PlatformMemory.Current.Free(resources.ArrayMap, resources.ArrayLength);
                PlatformMemory.Current.Free(map, length);
                throw;
            }
            tebRegistry[map] = resources;
            AddRegionLocked(new GuestRegion((ulong)map, (ulong)map + 0x1000, null));
        }
        var block = new TebBlock(this, map);
        threadTeb.Value = block;
        return block;
    }

    private void ReleaseTeb(nint map)
    {
        TebResources? entry;
        lock (gate)
        {
            if (!tebRegistry.Remove(map, out entry)) return; // already freed by Dispose
            RemoveRegionLocked((ulong)map);
            FreeTeb(entry);
        }
    }

    private void FreeTeb(TebResources e)
    {
        foreach (var d in e.Data.Values) { RemoveRegionLocked((ulong)d.Map); PlatformMemory.Current.Free(d.Map, d.Length); }
        if (e.ArrayMap != 0) { RemoveRegionLocked((ulong)e.ArrayMap); PlatformMemory.Current.Free(e.ArrayMap, e.ArrayLength); }
        PlatformMemory.Current.Free(e.Map, e.Length);
    }

    /// <summary>Called for every outermost entry of a thread into the process: the TEB mirrors the stack the call runs on and starts with an empty SEH chain.</summary>
    internal static void PrepareTebForCall(TebBlock teb, uint stackTop, uint stackLow)
    {
        *(uint*)(teb.Map + (nint)GuestModule.TebExceptionList) = 0xFFFFFFFFu;
        *(uint*)(teb.Map + (nint)GuestModule.TebStackBase) = stackTop;
        *(uint*)(teb.Map + (nint)GuestModule.TebStackLimit) = stackLow;
    }

    public void Dispose()
    {
        List<TebResources> blocks;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            blocks = [.. tebRegistry.Values];
            tebRegistry.Clear();
            foreach (var b in blocks) FreeTeb(b);
            if (pebMap != 0) PlatformMemory.Current.Free(pebMap, pebLength);
            if (processBlockMap != 0) PlatformMemory.Current.Free(processBlockMap, processBlockLength);
            pebMap = 0; processBlockMap = 0;
            sharedRegions.Clear();
        }
        DisposeDynamicCode();
        System.Runtime.InteropServices.NativeMemory.Free((void*)registry);
        threadTeb.Dispose();
    }
}
