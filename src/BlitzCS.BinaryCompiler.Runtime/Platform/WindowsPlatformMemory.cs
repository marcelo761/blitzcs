using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Windows x64 implementation over kernel32 VirtualAlloc/VirtualProtect/VirtualFree/VirtualQuery.
/// Arena layout (see docs/binary-windows-host.md): guest images live in [0x20000000, 0x70000000) (ModuleBaseAllocator), the runtime's own
/// low arenas (guest stacks, TEB/PEB, scratch buffers) are probed from 0x78000000 up to 4 GiB using VirtualQuery to find free runs,
/// honouring the 64 KiB allocation granularity. Addresses below 0x20000000 are never used (VirtualAlloc cannot get them under Wine).
/// </summary>
internal sealed class WindowsPlatformMemory : IPlatformMemory
{
    internal const ulong ArenaStart = 0x78000000UL, ArenaEnd = 0x1_0000_0000UL;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, MEM_FREE = 0x10000;
    private const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04, PAGE_EXECUTE_READ = 0x20;
    private const ulong Granularity = 0x10000;
    private readonly object gate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    public string Name => "windows";
    public string TargetOs => BinaryTargets.WindowsX64;
    public int PageSize => Environment.SystemPageSize;
    public int AllocationGranularity => (int)Granularity;
    public uint MinimumImageBase => 0x20000000;

    public nint MapFixed(nint address, nuint length, out int error)
    {
        var r = VirtualAlloc(address, length, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        error = r == 0 ? Marshal.GetLastPInvokeError() : 0;
        if (r != 0 && r != address) { VirtualFree(r, 0, MEM_RELEASE); error = 487; return 0; } // ERROR_INVALID_ADDRESS
        return r;
    }

    public nint Map(nuint length, out int error)
    {
        var r = VirtualAlloc(0, length, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        error = r == 0 ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }

    public nint MapLow(nuint length, out int error)
    {
        error = 0;
        if (length == 0 || length > ArenaEnd - ArenaStart) { error = 87; return 0; } // ERROR_INVALID_PARAMETER
        var span = ((ulong)length + Granularity - 1) / Granularity * Granularity;
        lock (gate)
        {
            var cursor = ArenaStart;
            var lastError = 487;
            while (cursor + span <= ArenaEnd)
            {
                if (VirtualQuery((nint)cursor, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0) { lastError = Marshal.GetLastPInvokeError(); break; }
                var regionStart = (ulong)info.BaseAddress;
                var regionEnd = regionStart + info.RegionSize;
                if (info.State == MEM_FREE)
                {
                    var candidate = (Math.Max(cursor, regionStart) + Granularity - 1) / Granularity * Granularity;
                    if (candidate + span <= regionEnd && candidate + span <= ArenaEnd)
                    {
                        var r = VirtualAlloc((nint)candidate, length, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
                        if (r != 0) return r;
                        lastError = Marshal.GetLastPInvokeError(); // raced with another allocator: keep scanning past it
                    }
                }
                cursor = Math.Max(regionEnd, cursor + Granularity);
            }
            error = lastError;
            return 0;
        }
    }

    public void Protect(nint address, nuint length, MemoryProtection protection)
    {
        var flags = protection switch
        {
            MemoryProtection.None => PAGE_NOACCESS,
            MemoryProtection.Read => PAGE_READONLY,
            MemoryProtection.ReadWrite => PAGE_READWRITE,
            MemoryProtection.ReadExecute => PAGE_EXECUTE_READ,
            _ => throw new ArgumentOutOfRangeException(nameof(protection)),
        };
        if (!VirtualProtect(address, length, flags, out _))
            throw new GuestLoadException($"VirtualProtect(0x{address:X}, {length}, {protection}) failed with error {Marshal.GetLastPInvokeError()}.");
        // Windows requires the publisher of generated code to make instruction-cache coherence explicit.
        // -1 is the current-process pseudo handle; no owned handle needs to be opened or closed.
        if (protection == MemoryProtection.ReadExecute && !FlushInstructionCache(-1, address, length))
            throw new GuestLoadException($"FlushInstructionCache(0x{address:X}, {length}) failed with error {Marshal.GetLastPInvokeError()}.");
    }

    public void Free(nint address, nuint length)
    {
        if (address != 0) VirtualFree(address, 0, MEM_RELEASE);
    }

    public IReadOnlyList<MappedRange> QueryRanges(ulong start, ulong end)
    {
        var hits = new List<MappedRange>();
        var cursor = start & ~(Granularity - 1);
        var size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        while (cursor < end && hits.Count < 64)
        {
            if (VirtualQuery((nint)cursor, out var info, size) == 0) break;
            var lo = (ulong)info.BaseAddress; var hi = lo + info.RegionSize;
            if (info.State != MEM_FREE && hi > start)
                hits.Add(new MappedRange(lo, hi, $"{(info.State == MEM_COMMIT ? "committed" : "reserved")} {ProtectName(info.Protect)} {TypeName(info.Type)}".Trim()));
            if (hi <= cursor) break;
            cursor = hi;
        }
        return hits;
    }

    private static string ProtectName(uint p) => (p & 0xFF) switch
    {
        PAGE_NOACCESS => "---", PAGE_READONLY => "r--", PAGE_READWRITE => "rw-", 0x08 => "rw- (copy-on-write)", PAGE_EXECUTE_READ => "r-x", 0x40 => "rwx", 0x80 => "rwx (copy-on-write)", 0x10 => "--x", 0 => "", _ => $"0x{p:X}",
    };

    private static string TypeName(uint t) => t switch { 0x1000000 => "image", 0x40000 => "mapped", 0x20000 => "private", _ => "" };
}
