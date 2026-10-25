namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Page protections the runtime ever asks for. There is deliberately no read-write-execute value: W^X is a hard rule.</summary>
public enum MemoryProtection { None, Read, ReadWrite, ReadExecute }

/// <summary>One mapped range reported for diagnostics. <see cref="Description"/> is a short human-readable label (protection plus backing).</summary>
public readonly record struct MappedRange(ulong Start, ulong End, string Description);

/// <summary>
/// Address-space services the guest runtime needs, with one implementation per host OS (<see cref="PlatformMemory.Current"/>).
/// Every mapping is private, zero-filled and created read-write; <see cref="Protect"/> then tightens it. Nothing is ever mapped over an existing mapping.
/// Mapping failures return 0 and an OS error code (errno / GetLastError) instead of throwing, so callers can name the conflict.
/// </summary>
public interface IPlatformMemory
{
    /// <summary>"linux" or "windows".</summary>
    string Name { get; }
    /// <summary>The binary-compiler target identifier whose artifacts this platform can run (<see cref="BinaryTargets"/>).</summary>
    string TargetOs { get; }
    int PageSize { get; }
    /// <summary>Granularity of base addresses (64 KiB on Windows, the page size on Linux).</summary>
    int AllocationGranularity { get; }
    /// <summary>Lowest guest image base the platform can honour (0x20000000 on Windows, where lower fixed addresses are not available under Wine).</summary>
    uint MinimumImageBase { get; }

    /// <summary>Maps <paramref name="length"/> bytes read-write exactly at <paramref name="address"/> with NOREPLACE semantics: returns 0 (and the OS error) if any page of the range is already mapped.</summary>
    nint MapFixed(nint address, nuint length, out int error);
    /// <summary>Maps read-write memory entirely below 4 GiB (guest-addressable): stacks, TEB/PEB, scratch buffers. Returns 0 on failure.</summary>
    nint MapLow(nuint length, out int error);
    /// <summary>Maps read-write memory at an address chosen by the OS (host code image, helpers).</summary>
    nint Map(nuint length, out int error);
    /// <summary>Changes the protection of whole pages inside one mapping; throws <see cref="GuestLoadException"/> on failure.</summary>
    void Protect(nint address, nuint length, MemoryProtection protection);
    /// <summary>Releases a mapping created by one of the Map methods. <paramref name="address"/> must be the address the Map call returned (Windows releases the whole allocation). Null is ignored.</summary>
    void Free(nint address, nuint length);
    /// <summary>Mapped ranges overlapping [start, end), for diagnostics only. May be empty when the OS does not expose them.</summary>
    IReadOnlyList<MappedRange> QueryRanges(ulong start, ulong end);
}

/// <summary>Selects the <see cref="IPlatformMemory"/> of the running OS; other systems throw <see cref="PlatformNotSupportedException"/>.</summary>
public static class PlatformMemory
{
    private static IPlatformMemory? current;

    public static IPlatformMemory Current => LazyInitializer.EnsureInitialized(ref current, Create)!;

    private static IPlatformMemory Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatformMemory();
        if (OperatingSystem.IsLinux()) return new LinuxPlatformMemory();
        throw new PlatformNotSupportedException("The binary compiler runtime supports Linux x86-64 and Windows x86-64 only.");
    }
}
