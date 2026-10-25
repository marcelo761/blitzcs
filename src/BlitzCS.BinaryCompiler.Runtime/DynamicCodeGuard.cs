using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Write guard of code translated at run time (docs/binary-dynamic-code.md), Windows only. The pages of such code are host read-only; a store into them raises an
/// access violation that a vectored exception handler (installed first in the chain) inspects:
/// <list type="bullet">
/// <item>the stored bytes overlap a translated instruction byte and the store comes from translated code: the guest call is abandoned with trap kind
/// <see cref="TrapSelfModifyingCode"/> (RIP is moved to the shared exit of the call, R14 is its call context), which surfaces as <see cref="GuestSelfModifyingCodeException"/>;</item>
/// <item>otherwise (data next to the code, as compilers that emit one blob of code and data do): the page is made writable, the store is single-stepped (TF) and the page is
/// protected again on the single-step exception.</item>
/// </list>
/// Every other exception is passed on unchanged. Limits: while one thread single-steps a store, another thread's store to the same page is not checked.
/// </summary>
internal static unsafe class DynamicCodeGuard
{
    /// <summary>Trap kind written into the call context for a store into translated dynamic code.</summary>
    internal const uint TrapSelfModifyingCode = 6;

    internal static bool Supported => OperatingSystem.IsWindows();

    private sealed record Range(uint Start, uint End, bool[] CodeBytes, nint ExitCommon);

    private static readonly object gate = new();
    private static volatile Range[] ranges = [];
    private static volatile (nint Start, nint End)[] hostCode = [];
    private static nint handle;

    [ThreadStatic] private static List<nint>? pendingPages;
    private static long dataWrites, refusedWrites;

    /// <summary>Guest stores into write-protected dynamic-code pages that only touched data and were performed (single-stepped), and stores refused as self-modifying code.</summary>
    internal static (long DataWrites, long Refused) Counters => (Interlocked.Read(ref dataWrites), Interlocked.Read(ref refusedWrites));

    internal static void AddHostCode(nint start, nuint length) { lock (gate) hostCode = [.. hostCode, (start, start + (nint)length)]; }
    internal static void RemoveHostCode(nint start) { lock (gate) hostCode = [.. hostCode.Where(h => h.Start != start)]; }

    internal static void AddRange(uint start, uint end, bool[] codeBytes, nint exitCommon)
    {
        lock (gate)
        {
            ranges = [.. ranges.Where(r => r.Start != start), new Range(start, end, codeBytes, exitCommon)];
            if (handle == 0 && Supported)
            {
                handle = AddVectoredExceptionHandler(1, (nint)(delegate* unmanaged<nint, int>)&Handler);
                if (handle == 0) throw new GuestLoadException("AddVectoredExceptionHandler failed: the write guard of code translated at run time cannot be installed.");
            }
        }
    }

    internal static void RemoveRange(uint start) { lock (gate) ranges = [.. ranges.Where(r => r.Start != start)]; }

    private const int ExceptionContinueExecution = -1, ExceptionContinueSearch = 0;
    private const uint AccessViolation = 0xC0000005, SingleStep = 0x80000004;
    // x64 CONTEXT offsets
    private const int CtxEFlags = 0x44, CtxRax = 0x78, CtxR14 = 0xE8, CtxRip = 0xF8;

    [UnmanagedCallersOnly]
    private static int Handler(nint pointers)
    {
        try
        {
            var record = *(byte**)pointers;
            var context = *(byte**)(pointers + 8);
            var code = *(uint*)record;
            if (code == SingleStep)
            {
                var pending = pendingPages;
                if (pending is null || pending.Count == 0) return ExceptionContinueSearch;
                var live = ranges;
                foreach (var page in pending)
                    if (live.Any(r => (ulong)page >= r.Start && (ulong)page < r.End)) PlatformMemory.Current.Protect(page, 4096, MemoryProtection.Read);
                pending.Clear();
                *(uint*)(context + CtxEFlags) &= ~0x100u;
                return ExceptionContinueExecution;
            }
            if (code != AccessViolation || *(uint*)(record + 24) < 2 || *(ulong*)(record + 32) != 1) return ExceptionContinueSearch;
            var address = *(ulong*)(record + 40);
            if (address > uint.MaxValue) return ExceptionContinueSearch;
            Range? hit = null;
            foreach (var r in ranges) if (address >= r.Start && address < r.End) { hit = r; break; }
            if (hit is null) return ExceptionContinueSearch;
            var rip = *(nint*)(context + CtxRip);
            var size = StoreSize(rip);
            var overlaps = false;
            for (var a = address; a < address + size && a < hit.End; a++)
                if (hit.CodeBytes[a - hit.Start]) { overlaps = true; break; }
            if (overlaps)
            {
                var fromTranslatedCode = false;
                foreach (var h in hostCode) if (rip >= h.Start && rip < h.End) { fromTranslatedCode = true; break; }
                if (!fromTranslatedCode) return ExceptionContinueSearch;   // a host library wrote into translated code: not ours to name; the process faults as it would without the guard
                Interlocked.Increment(ref refusedWrites);
                var callContext = *(byte**)(context + CtxR14);
                *(uint*)(callContext + 16) = TrapSelfModifyingCode;
                *(ulong*)(callContext + 24) = address;
                *(ulong*)(context + CtxRax) = 0;
                *(nint*)(context + CtxRip) = hit.ExitCommon;
                return ExceptionContinueExecution;
            }
            Interlocked.Increment(ref dataWrites);
            var pageAddress = (nint)(address & ~0xFFFUL);
            PlatformMemory.Current.Protect(pageAddress, 4096, MemoryProtection.ReadWrite);
            (pendingPages ??= []).Add(pageAddress);
            *(uint*)(context + CtxEFlags) |= 0x100u;   // TF: re-protect after this one store
            return ExceptionContinueExecution;
        }
        catch { return ExceptionContinueSearch; }
    }

    /// <summary>Bytes the host instruction at <paramref name="rip"/> stores (its memory operand size); 16 when it cannot be told.</summary>
    private static ulong StoreSize(nint rip)
    {
        try
        {
            var bytes = new byte[15];
            Marshal.Copy(rip, bytes, 0, 15);
            var ins = Iced.Intel.Decoder.Create(64, bytes).Decode();
            if (ins.IsInvalid) return 16;
            var size = Iced.Intel.MemorySizeExtensions.GetSize(ins.MemorySize);
            return size > 0 ? (ulong)size : 16;
        }
        catch { return 16; }
    }

    [DllImport("kernel32.dll")]
    private static extern nint AddVectoredExceptionHandler(uint first, nint handler);
}
