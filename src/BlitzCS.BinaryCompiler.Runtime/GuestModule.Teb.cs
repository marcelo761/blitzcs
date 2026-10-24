using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Thread environment block support (docs/binary-teb-contract.md). Every (module, OS thread) owns one TEB page in low memory,
/// created at the thread's first guest call and alive until the module is disposed (or the thread object is collected). The
/// translated code reaches it through R15, which EnterGuest loads from the call context, so nested calls on a thread share
/// one TEB and calls on different threads never touch each other's; no lock is taken on the call path.
/// </summary>
public sealed unsafe partial class GuestModule
{
    internal const uint TebExceptionList = 0x00, TebStackBase = 0x04, TebStackLimit = 0x08, TebSelf = 0x18, TebProcessId = 0x20, TebThreadId = 0x24,
        TebTlsPointer = 0x2C, TebPeb = 0x30, TebLastError = 0x34, TebTlsSlots = 0xE10, TebTlsSlotCount = 64;
    internal const uint PebBeingDebugged = 0x02, PebImageBase = 0x08;
    private const uint DllProcessDetach = 0, DllProcessAttach = 1, DllThreadAttach = 2, DllThreadDetach = 3;

    /// <summary>Owner of one thread's TEB and TLS mappings. The finalizer frees them if the thread dies without the module being disposed first.</summary>
    private sealed class TebBlock(GuestModule owner, nint map, nuint length, nint tlsMap, nuint tlsLength)
    {
        public readonly nint Map = map, TlsMap = tlsMap;
        public readonly nuint Length = length, TlsLength = tlsLength;
        public uint Address => (uint)Map;
        ~TebBlock() => owner.ReleaseTeb(Map);
    }

    // Addresses only: a strong reference to the block would keep it from being finalized when its thread dies.
    private readonly Dictionary<nint, (nuint Length, nint TlsMap, nuint TlsLength)> tebRegistry = [];
    private nint pebMap;
    private nuint pebLength;
    private PeTls? tlsDirectory;
    private uint[] tlsCallbacks = [];
    private bool tlsProcessAttached;
    // True while loading and while disposing: threads first seen then do not get DLL_THREAD_ATTACH.
    private volatile bool initializing = true;

    /// <summary>Guest address of the PEB shared by all threads of this module (0 before the module is loaded).</summary>
    public uint PebAddress => (uint)pebMap;

    /// <summary>Guest address of the TEB of the calling thread, or 0 when this thread has not called into the module yet.</summary>
    public uint CurrentThreadTeb => threadState.IsValueCreated ? threadState.Value!.Teb?.Address ?? 0 : 0;

    private void AllocatePeb(PeImage image)
    {
        var length = (nuint)Math.Max(4096, PlatformMemory.Current.PageSize);
        var map = PlatformMemory.Current.MapLow(length, out var errno);
        if (map == 0) throw new GuestAddressSpaceException($"Cannot allocate the guest PEB in low memory (OS error {errno}).");
        pebMap = map; pebLength = length;
        *(byte*)(map + (nint)PebBeingDebugged) = 0;
        *(uint*)(map + (nint)PebImageBase) = artifact.GuestImageBase;
        Memory.Add(new GuestRegion((ulong)map, (ulong)map + length, null));
        tlsDirectory = image.Tls;
    }

    private static (nint Map, nuint Length) AllocateLow(nuint bytes, string what)
    {
        var ps = (nuint)PlatformMemory.Current.PageSize;
        var length = (bytes + ps - 1) / ps * ps;
        var map = PlatformMemory.Current.MapLow(length, out var errno);
        if (map == 0) throw new GuestAddressSpaceException($"Cannot allocate the guest {what} in low memory (OS error {errno}).");
        return (map, length);
    }

    /// <summary>Creates the calling thread's TEB (and implicit-TLS block) and runs DLL_THREAD_ATTACH TLS callbacks for threads that join after load.</summary>
    private void CreateThreadTeb(ThreadState state)
    {
        var (map, length) = AllocateLow(0x1000, "TEB");
        nint tlsMap = 0; nuint tlsLength = 0;
        try
        {
            *(uint*)(map + (nint)TebExceptionList) = 0xFFFFFFFFu;
            *(uint*)(map + (nint)TebSelf) = (uint)map;
            *(uint*)(map + (nint)TebProcessId) = (uint)Environment.ProcessId;
            *(uint*)(map + (nint)TebThreadId) = (uint)Environment.CurrentManagedThreadId;
            *(uint*)(map + (nint)TebPeb) = (uint)pebMap;
            if (tlsDirectory is { } tls)
            {
                // [array of pointers (index 0 only; the loader writes index 0 to the image's TLS index)][16-aligned data = template + zero fill]
                var template = tls.EndRaw - tls.StartRaw;
                var total = (nuint)template + tls.SizeOfZeroFill;
                (tlsMap, tlsLength) = AllocateLow(16 + ((total + 15) & ~(nuint)15), "TLS data");
                var data = tlsMap + 16;
                if (template != 0) Buffer.MemoryCopy((void*)(guestMap + (nint)(tls.StartRaw - artifact.GuestImageBase)), (void*)data, template, template);
                *(uint*)tlsMap = (uint)data;
                *(uint*)(map + (nint)TebTlsPointer) = (uint)tlsMap;
            }
        }
        catch { PlatformMemory.Current.Free(map, length); PlatformMemory.Current.Free(tlsMap, tlsLength); throw; }
        var block = new TebBlock(this, map, length, tlsMap, tlsLength);
        lock (resourceLock) tebRegistry[map] = (length, tlsMap, tlsLength);
        Memory.Add(new GuestRegion((ulong)map, (ulong)map + 0x1000, null));
        if (tlsMap != 0) Memory.Add(new GuestRegion((ulong)tlsMap, (ulong)tlsMap + tlsLength, null));
        state.Teb = block;
        if (!initializing && tlsProcessAttached) RunTlsCallbacks(DllThreadAttach);
    }

    private void ReleaseTeb(nint map)
    {
        (nuint Length, nint TlsMap, nuint TlsLength) entry;
        lock (resourceLock)
        {
            if (!tebRegistry.Remove(map, out entry)) return; // the module already freed it
        }
        Memory.Remove((ulong)map);
        if (entry.TlsMap != 0) Memory.Remove((ulong)entry.TlsMap);
        PlatformMemory.Current.Free(map, entry.Length);
        PlatformMemory.Current.Free(entry.TlsMap, entry.TlsLength);
    }

    /// <summary>Called for every outermost entry: the TEB mirrors the stack the call runs on and starts with an empty SEH chain.</summary>
    private static void PrepareTebForCall(TebBlock teb, uint stackTop, uint stackLow)
    {
        *(uint*)(teb.Map + (nint)TebExceptionList) = 0xFFFFFFFFu;
        *(uint*)(teb.Map + (nint)TebStackBase) = stackTop;
        *(uint*)(teb.Map + (nint)TebStackLimit) = stackLow;
    }

    private void RunTlsCallbacks(uint reason)
    {
        foreach (var rva in tlsCallbacks)
        {
            var target = ResolveEntry(rva - artifact.GuestImageBase);
            InvokeCore(target, [artifact.GuestImageBase, reason, 0u]);
        }
    }

    private void InitializeTls(PeImage image, byte[] mapped)
    {
        if (image.Tls is not { } tls) return;
        // Index 0 is written before the image is protected; the pointer array of every thread has the data block at slot 0.
        *(uint*)(guestMap + (nint)(tls.IndexAddress - artifact.GuestImageBase)) = 0;
        var list = new List<uint>();
        if (tls.CallbacksVa != 0)
            for (var at = tls.CallbacksVa - artifact.GuestImageBase; (ulong)at + 4 <= (ulong)mapped.Length && list.Count < 64; at += 4)
            {
                var va = BitConverter.ToUInt32(mapped, (int)at);
                if (va == 0) break;
                list.Add(va);
            }
        tlsCallbacks = [.. list];
    }

    private void FreeAllTebs()
    {
        KeyValuePair<nint, (nuint Length, nint TlsMap, nuint TlsLength)>[] blocks;
        lock (resourceLock) { blocks = [.. tebRegistry]; tebRegistry.Clear(); }
        foreach (var (map, e) in blocks)
        {
            PlatformMemory.Current.Free(map, e.Length);
            PlatformMemory.Current.Free(e.TlsMap, e.TlsLength);
        }
        PlatformMemory.Current.Free(pebMap, pebLength);
        pebMap = 0;
    }
}
