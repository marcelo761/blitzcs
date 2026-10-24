namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Linux implementation over libc mmap/munmap/mprotect (MAP_FIXED_NOREPLACE, MAP_32BIT).</summary>
internal sealed class LinuxPlatformMemory : IPlatformMemory
{
    public string Name => "linux";
    public string TargetOs => BinaryTargets.LinuxSysV;
    public int PageSize => Environment.SystemPageSize;
    public int AllocationGranularity => PageSize;
    public uint MinimumImageBase => 0x1000;

    public nint MapFixed(nint address, nuint length, out int error)
    {
        var addr = Libc.Map(address, length, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_FIXED_NOREPLACE, out error);
        if (addr != Libc.MapFailed && addr != address)
        {
            Libc.Unmap(addr, length); // old kernels ignore NOREPLACE and treat the address as a hint
            addr = Libc.MapFailed;
            error = 17; // EEXIST
        }
        return addr == Libc.MapFailed ? 0 : addr;
    }

    public nint MapLow(nuint length, out int error)
    {
        var map = Libc.Map(0, length, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_32BIT, out error);
        if (map == Libc.MapFailed) return 0;
        if ((ulong)map + length > uint.MaxValue) { Libc.Unmap(map, length); return 0; }
        return map;
    }

    public nint Map(nuint length, out int error)
    {
        var map = Libc.Map(0, length, Libc.PROT_READ | Libc.PROT_WRITE, 0, out error);
        return map == Libc.MapFailed ? 0 : map;
    }

    public void Protect(nint address, nuint length, MemoryProtection protection) =>
        Libc.Protect(address, length, protection switch
        {
            MemoryProtection.None => Libc.PROT_NONE,
            MemoryProtection.Read => Libc.PROT_READ,
            MemoryProtection.ReadWrite => Libc.PROT_READ | Libc.PROT_WRITE,
            MemoryProtection.ReadExecute => Libc.PROT_READ | Libc.PROT_EXEC,
            _ => throw new ArgumentOutOfRangeException(nameof(protection)),
        });

    public void Free(nint address, nuint length) => Libc.Unmap(address, length);

    public IReadOnlyList<MappedRange> QueryRanges(ulong start, ulong end)
    {
        var hits = new List<MappedRange>();
        try
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                var range = parts[0].Split('-');
                if (range.Length != 2) continue;
                var lo = Convert.ToUInt64(range[0], 16); var hi = Convert.ToUInt64(range[1], 16);
                if (lo >= end || hi <= start) continue;
                hits.Add(new MappedRange(lo, hi, $"{(parts.Length > 1 ? parts[1] : "")} {(parts.Length > 5 ? parts[5].Trim() : "[anonymous]")}".Trim()));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
        return hits;
    }
}
