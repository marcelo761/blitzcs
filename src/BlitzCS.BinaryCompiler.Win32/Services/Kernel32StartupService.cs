using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Fiber-local storage: per-thread values keyed by slot, with the callback given to FlsAlloc.</summary>
internal sealed class FlsState
{
    public const int Slots = 4080; // FLS_MAXIMUM_AVAILABLE
    private readonly object gate = new();
    private readonly bool[] allocated = new bool[Slots];
    private readonly uint[] callbacks = new uint[Slots];
    private readonly Dictionary<int, Dictionary<int, uint>> values = [];

    public int Alloc(uint callback)
    {
        lock (gate)
            for (var i = 0; i < Slots; i++)
                if (!allocated[i]) { allocated[i] = true; callbacks[i] = callback; values.Remove(i); return i; }
        return -1;
    }

    /// <summary>Frees the slot and returns its callback with the non-null values of every thread (the callback runs for each).</summary>
    public bool Free(uint index, out uint callback, out List<uint> live)
    {
        callback = 0; live = [];
        lock (gate)
        {
            if (index >= Slots || !allocated[index]) return false;
            callback = callbacks[index];
            if (values.Remove((int)index, out var perThread)) live = [.. perThread.Values.Where(v => v != 0)];
            allocated[index] = false; callbacks[index] = 0;
            return true;
        }
    }

    public bool TryGet(uint index, out uint value)
    {
        value = 0;
        lock (gate)
        {
            if (index >= Slots || !allocated[index]) return false;
            if (values.TryGetValue((int)index, out var perThread)) perThread.TryGetValue(Environment.CurrentManagedThreadId, out value);
            return true;
        }
    }

    public bool TrySet(uint index, uint value)
    {
        lock (gate)
        {
            if (index >= Slots || !allocated[index]) return false;
            if (!values.TryGetValue((int)index, out var perThread)) values[(int)index] = perThread = [];
            perThread[Environment.CurrentManagedThreadId] = value;
            return true;
        }
    }
}

/// <summary>
/// KERNEL32 process start-up set of an MSVC (VS2015+ UCRT) executable, with Microsoft-documented behaviour:
/// <list type="bullet">
/// <item>GetStartupInfoW (STARTUPINFOW, 68 bytes: only <c>cb</c> set, dwFlags 0 = no window or handle overrides), GetCommandLineW, GetModuleFileNameW, GetModuleHandleW and
/// GetModuleHandleExW/A (flags PIN 1, UNCHANGED_REFCOUNT 2, FROM_ADDRESS 4; FROM_ADDRESS maps an address in a mapped image page to the registered module or main module with the highest
/// base at or below it), LoadLibraryW/ExW (the same module table as LoadLibraryA; LOAD_LIBRARY_AS_DATAFILE-style loading flags are refused with ERROR_NOT_SUPPORTED, search-path flags are accepted
/// because only emulated or host-registered modules exist).</item>
/// <item>GetSystemTimeAsFileTime (host UTC clock as 100 ns FILETIME), GetTickCount64 (milliseconds since process start, the same clock as GetTickCount; the high half goes to EDX),
/// IsDebuggerPresent (always FALSE: the PEB's BeingDebugged is 0), IsProcessorFeaturePresent (PF_* answered from the host CPU's CPUID; PF_FASTFAIL_AVAILABLE is FALSE because this runtime
/// does not implement the <c>int 29h</c> fast-fail trap, so the CRT takes its documented fallback; unknown or non-x86 features are FALSE as on Windows).</item>
/// <item>UnhandledExceptionFilter returns EXCEPTION_CONTINUE_SEARCH (0): there is no debugger, no crash dialog and no structured-exception dispatcher (docs/binary-seh.md), and the filter stored by
/// SetUnhandledExceptionFilter is not invoked by the host.</item>
/// <item>EncodePointer/DecodePointer: reversible, per-process obfuscation (XOR with a random cookie, rotated by cookie mod 32), as Windows does with a process secret.</item>
/// <item>InitializeSListHead/InterlockedPushEntrySList/InterlockedPopEntrySList/InterlockedFlushSList/QueryDepthSList on the 32-bit SLIST_HEADER (Next:u32, Depth:u16, Sequence:u16; 8-byte
/// aligned, else the call is refused). The operations are atomic against each other through one gate; entries are guest memory the caller owns.</item>
/// <item>FlsAlloc/FlsGetValue/FlsSetValue/FlsFree: 4080 slots, a new slot reads 0 on every thread, invalid indexes fail with ERROR_INVALID_PARAMETER, FlsGetValue sets ERROR_SUCCESS on success.
/// Fibers do not exist, so storage is per thread. The callback runs for every thread's non-NULL value when the slot is freed (FlsFree); thread-exit and process-exit callbacks are not delivered (this runtime has no thread-exit notification).</item>
/// <item>HeapSetInformation: HeapEnableTerminationOnCorruption (class 1, NULL info, size 0) and HeapCompatibilityInformation (class 0, value 0, 1 or 2) are accepted (every free here is validated and the low-fragmentation setting has no observable effect); other classes fail with ERROR_INVALID_PARAMETER.</item>
/// <item>InitializeCriticalSectionEx accepts the documented flag bits (the debug-info, spin and static-init hints change nothing observable) and initialises like InitializeCriticalSection.</item>
/// </list>
/// RaiseException and RtlUnwind stay absent: the runtime has no structured-exception dispatcher, and a named fault at the call is the honest answer.
/// </summary>
public sealed class Kernel32StartupService : Win32Service
{
    public override string Name => "Kernel32.Startup";

    private const uint ExceptionContinueSearch = 0;
    // GetModuleHandleEx flags
    private const uint GetModuleHandlePin = 1, GetModuleHandleUnchangedRefcount = 2, GetModuleHandleFromAddress = 4;

    // PF_* (winnt.h)
    private const uint PfCompareExchangeDouble = 2, PfMmx = 3, PfXmmi = 6, PfRdtsc = 8, PfPae = 9, PfXmmi64 = 10, PfNx = 12, PfSse3 = 13, PfCx16 = 14, PfXsave = 17,
        PfRdrand = 28, PfSsse3 = 36, PfSse41 = 37, PfSse42 = 38, PfAvx = 39, PfAvx2 = 40, PfAvx512F = 41, PfErms = 42;

    /// <summary>Answers a PF_* constant from the CPUID of the host. Features the runtime itself cannot offer or that do not exist on this architecture are false.</summary>
    internal static bool ProcessorFeature(uint feature)
    {
        if (!X86Base.IsSupported) return false;
        var l1 = X86Base.CpuId(1, 0);
        var l0 = X86Base.CpuId(0, 0);
        var l7 = l0.Eax >= 7 ? X86Base.CpuId(7, 0) : default;
        var ext = X86Base.CpuId(unchecked((int)0x80000000), 0);
        var l81 = (uint)ext.Eax >= 0x80000001 ? X86Base.CpuId(unchecked((int)0x80000001), 0) : default;
        bool Edx1(int bit) => (l1.Edx >> bit & 1) != 0;
        bool Ecx1(int bit) => (l1.Ecx >> bit & 1) != 0;
        return feature switch
        {
            PfCompareExchangeDouble => Edx1(8),
            PfMmx => Edx1(23),
            PfXmmi => Edx1(25),
            PfRdtsc => Edx1(4),
            PfPae => Edx1(6),
            PfXmmi64 => Edx1(26),
            PfNx => (l81.Edx >> 20 & 1) != 0,
            PfSse3 => Ecx1(0),
            PfCx16 => Ecx1(13),
            PfXsave => Ecx1(27),
            PfRdrand => Ecx1(30),
            PfSsse3 => Ecx1(9),
            PfSse41 => Ecx1(19),
            PfSse42 => Ecx1(20),
            PfAvx => Ecx1(28) && Ecx1(27) && Avx.IsSupported,
            PfAvx2 => (l7.Ebx >> 5 & 1) != 0 && Avx2.IsSupported,
            PfAvx512F => (l7.Ebx >> 16 & 1) != 0 && Avx512F.IsSupported,
            PfErms => (l7.Ebx >> 9 & 1) != 0,
            _ => false,
        };
    }

    /// <summary>GetModuleHandleExW/A: <paramref name="nameOrAddress"/> is a name pointer, or an address with GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS.</summary>
    private static uint ModuleHandleEx(Win32Call c, bool wide)
    {
        uint flags = c[0], arg = c[1], result = c[2];
        if (result == 0 || (flags & ~7u) != 0 || (flags & GetModuleHandlePin) != 0 && (flags & GetModuleHandleUnchangedRefcount) != 0)
            return c.Ctx.Fail(Win32Error.InvalidParameter);
        uint handle;
        if ((flags & GetModuleHandleFromAddress) != 0) handle = ModuleOfAddress(c, arg);
        else if (arg == 0) handle = c.Ctx.Options.MainModuleBase;
        else
        {
            var name = wide ? c.Mem.ReadWideString(arg, 260) : c.Mem.ReadAnsiString(arg, 260);
            handle = c.Ctx.Process.Find(name)?.Handle ?? 0;
            if (handle == 0 && c.Ctx.Options.GuestLibraries is { } libs && libs.FindLoaded(c, name) is { } lib)
            {
                handle = lib.Handle;
                libs.Touch(handle, (flags & GetModuleHandlePin) != 0, (flags & GetModuleHandleUnchangedRefcount) != 0);
            }
        }
        c.Mem.WriteUInt32(result, handle);
        return handle == 0 ? c.Ctx.Fail(Win32Error.ModNotFound) : 1;
    }

    private static uint ModuleOfAddress(Win32Call c, uint address)
    {
        if (!c.Mem.TryQueryPage(address, out var page) || page.Kind is not (GuestPageKind.ImageCode or GuestPageKind.ImageData)) return 0;
        if (c.Ctx.Options.GuestLibraries?.FindByAddress(address) is { } guestLib) return guestLib.Handle;
        var best = c.Ctx.Options.MainModuleBase <= address ? c.Ctx.Options.MainModuleBase : 0u;
        foreach (var m in c.Ctx.Process.GuestModules())
            if (m.Handle <= address && m.Handle > best) best = m.Handle;
        return best;
    }

    // LOAD_LIBRARY_AS_DATAFILE (2), DONT_RESOLVE_DLL_REFERENCES (1), AS_IMAGE_RESOURCE (0x20) and AS_DATAFILE_EXCLUSIVE (0x40) need an image mapper that does not run the module.
    private const uint UnsupportedLoadFlags = 0x63;

    private static uint LoadLibraryNamed(Win32Call c, string name, uint file, uint flags)
    {
        if (name.Length == 0 || file != 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if ((flags & UnsupportedLoadFlags) != 0) return c.Ctx.Fail(Win32Error.NotSupported);
        return LibraryApi.Load(c, name);
    }

    /// <summary>The gate that makes list operations atomic; refuses a misaligned header like Windows (which raises an alignment fault).</summary>
    private static object SListGate(Win32Call c, uint head)
    {
        if ((head & 7) != 0) throw new Win32UsageException($"SLIST_HEADER at 0x{head:X8} is not 8-byte aligned (MEMORY_ALLOCATION_ALIGNMENT).");
        return c.Ctx.SListGate;
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "GetStartupInfoW", 1, c => { var si = new byte[68]; si[0] = 68; c.Mem.Write(c[0], si); return 0; });
        Std(K, "GetCommandLineW", 0, c => c.Ctx.Process.CommandLineW(c.Mem));
        Std(K, "GetModuleFileNameW", 3, c =>
        {
            var path = ProcessService.ModulePath(c, c[0]);
            return path is null ? c.Ctx.Fail(Win32Error.ModNotFound) : ProcessService.CopyWide(c, path, c[1], c[2]);
        });
        Std(K, "GetModuleHandleW", 1, c => LibraryApi.ModuleHandle(c, c[0] == 0 ? null : c.Mem.ReadWideString(c[0], 260)));
        Std(K, "GetModuleHandleExW", 3, c => ModuleHandleEx(c, true));
        Std(K, "GetModuleHandleExA", 3, c => ModuleHandleEx(c, false));
        Std(K, "LoadLibraryW", 1, c => LoadLibraryNamed(c, c.Mem.ReadWideString(c[0], 260), 0, 0));
        Std(K, "LoadLibraryExW", 3, c => LoadLibraryNamed(c, c.Mem.ReadWideString(c[0], 260), c[1], c[2]));
        Std(K, "LoadLibraryExA", 3, c => LoadLibraryNamed(c, c.Mem.ReadAnsiString(c[0], 260), c[1], c[2]));

        Std(K, "GetSystemTimeAsFileTime", 1, c => { c.Mem.WriteUInt64(c[0], (ulong)DateTime.UtcNow.ToFileTimeUtc()); return 0; });
        Std(K, "GetTickCount64", 0, c =>
        {
            var ms = (ulong)Stopwatch.GetElapsedTime(ProcessState.StartTicks).TotalMilliseconds;
            c.ReturnHigh((uint)(ms >> 32));
            return (uint)ms;
        });
        Std(K, "IsDebuggerPresent", 0, _ => 0);
        Std(K, "IsProcessorFeaturePresent", 1, c => ProcessorFeature(c[0]) ? 1u : 0u);
        Std(K, "UnhandledExceptionFilter", 1, _ => ExceptionContinueSearch);

        Std(K, "EncodePointer", 1, c => BitOperations.RotateRight(c[0] ^ c.Ctx.PointerCookie, (int)(c.Ctx.PointerCookie & 31)));
        Std(K, "DecodePointer", 1, c => BitOperations.RotateLeft(c[0], (int)(c.Ctx.PointerCookie & 31)) ^ c.Ctx.PointerCookie);

        Std(K, "InitializeSListHead", 1, c => { lock (SListGate(c, c[0])) { c.Mem.WriteUInt32(c[0], 0); c.Mem.WriteUInt32(c[0] + 4, 0); } return 0; });
        Std(K, "InterlockedPushEntrySList", 2, c =>
        {
            lock (SListGate(c, c[0]))
            {
            if (c[1] == 0 || (c[1] & 7) != 0) throw new Win32UsageException($"SLIST_ENTRY at 0x{c[1]:X8} is NULL or not 8-byte aligned.");
            var first = c.Mem.ReadUInt32(c[0]); var meta = c.Mem.ReadUInt32(c[0] + 4);
            c.Mem.WriteUInt32(c[1], first);
            var depth = (ushort)(meta + 1); var seq = (ushort)((meta >> 16) + 1);
            c.Mem.WriteUInt32(c[0], c[1]); c.Mem.WriteUInt32(c[0] + 4, (uint)seq << 16 | depth);
            return first;
            }
        });
        Std(K, "InterlockedPopEntrySList", 1, c =>
        {
            lock (SListGate(c, c[0]))
            {
            var first = c.Mem.ReadUInt32(c[0]);
            if (first == 0) return 0u;
            var meta = c.Mem.ReadUInt32(c[0] + 4);
            c.Mem.WriteUInt32(c[0], c.Mem.ReadUInt32(first));
            c.Mem.WriteUInt32(c[0] + 4, (uint)(ushort)((meta >> 16) + 1) << 16 | (ushort)(meta - 1));
            return first;
            }
        });
        Std(K, "InterlockedFlushSList", 1, c =>
        {
            lock (SListGate(c, c[0]))
            {
            var first = c.Mem.ReadUInt32(c[0]); var meta = c.Mem.ReadUInt32(c[0] + 4);
            c.Mem.WriteUInt32(c[0], 0); c.Mem.WriteUInt32(c[0] + 4, (uint)(ushort)((meta >> 16) + 1) << 16);
            return first;
            }
        });
        Std(K, "QueryDepthSList", 1, c => { lock (SListGate(c, c[0])) return (ushort)c.Mem.ReadUInt32(c[0] + 4); });

        Std(K, "FlsAlloc", 1, c => { var i = c.Ctx.Fls.Alloc(c[0]); return i < 0 ? c.Ctx.Fail(Win32Error.NoMoreItems, 0xFFFFFFFF) : (uint)i; });
        Std(K, "FlsGetValue", 1, c =>
        {
            if (!c.Ctx.Fls.TryGet(c[0], out var v)) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Ctx.LastError = Win32Error.Success;
            return v;
        });
        Std(K, "FlsSetValue", 2, c => c.Ctx.Fls.TrySet(c[0], c[1]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
        Std(K, "FlsFree", 1, c =>
        {
            if (!c.Ctx.Fls.Free(c[0], out var callback, out var live)) return c.Ctx.Fail(Win32Error.InvalidParameter);
            if (callback != 0) foreach (var v in live) c.CallGuest(callback, v);
            return 1;
        });

        Std(K, "HeapSetInformation", 4, c =>
        {
            if (c[0] != 0) { c.Ctx.Heaps.Process(c.Mem); if (c.Ctx.Heaps.Find(c[0]) is null) return c.Ctx.Fail(Win32Error.InvalidHandle); }
            switch (c[1])
            {
                case 1 when c[2] == 0 && c[3] == 0: return 1; // HeapEnableTerminationOnCorruption
                case 0 when c[2] != 0 && c[3] >= 4 && c.Mem.ReadUInt32(c[2]) <= 2: return 1; // HeapCompatibilityInformation: standard, look-aside, low-fragmentation
                default: return c.Ctx.Fail(Win32Error.InvalidParameter);
            }
        });
        Std(K, "InitializeCriticalSectionEx", 3, c =>
        {
            const uint known = 0x01000000 | 0x02000000 | 0x04000000 | 0x08000000 | 0x10000000;
            if ((c[2] & ~known) != 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            SyncService.Init(c, c[0]);
            return 1;
        });
    }
}
