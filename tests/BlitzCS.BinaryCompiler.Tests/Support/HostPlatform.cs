using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Support;

/// <summary>
/// Host detection for the test suite. The primary target is Windows x64; on a Linux machine the Windows build is exercised under Wine
/// (scripts/wine/run-tests.sh). Tests that depend on Linux-only facilities (the ELF32 oracle, /proc, a Linux dotnet layout, perf counters)
/// use <see cref="LinuxOnlyFactAttribute"/>/<see cref="LinuxOnlyTheoryAttribute"/> or <see cref="LinuxOnlyReason"/> so they skip with an explicit reason.
/// </summary>
public static class HostPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsX64 => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>True when running as a Windows process on top of Wine (ntdll exports wine_get_version).</summary>
    public static bool IsWine { get; } = DetectWine();

    /// <summary>Windows x64 cannot reserve the low 512 MiB (0x20000000) for a guest, so fixtures linked at 0x10000000 must be moved above it.</summary>
    public static bool RequiresHighGuestBase => IsWindows;
    public const uint DefaultHighGuestBase = 0x60000000;

    /// <summary>
    /// A base for a guest image that is free right now. Linux: <paramref name="wanted"/> unchanged. Windows: <paramref name="wanted"/> when the 64 KiB-aligned range
    /// [wanted, wanted+size) in the image window is free, else the next free run (a .NET process, and especially one under Wine, may already hold managed-heap or
    /// loader memory inside the window after heavy tests, so tests that need "some usable base" ask for a free one instead of assuming 0x20000000).
    /// </summary>
    public static uint FreeBase(uint wanted, uint size = 0x10000)
    {
        if (!IsWindows) return wanted;
        var ranges = OccupiedImageWindow();
        for (var b = (ulong)wanted; b + size <= 0x70000000; b += 0x10000)
            if (!ranges.Any(r => r.Start < b + size && r.End > b)) return (uint)b;
        return wanted;
    }

    /// <summary>Every mapping inside the Windows image window [0x20000000, 0x70000000). QueryRanges returns at most 64 regions per call, so it is called repeatedly.</summary>
    private static List<BlitzCS.BinaryCompiler.Runtime.MappedRange> OccupiedImageWindow()
    {
        var all = new List<BlitzCS.BinaryCompiler.Runtime.MappedRange>();
        for (ulong cursor = 0x20000000; cursor < 0x70000000;)
        {
            var part = BlitzCS.BinaryCompiler.Runtime.PlatformMemory.Current.QueryRanges(cursor, 0x70000000);
            if (part.Count == 0) break;
            all.AddRange(part);
            var next = part.Max(r => r.End);
            if (next <= cursor) break;
            cursor = next;
        }
        return all;
    }

    /// <summary>Ranges the base allocator must avoid (default reservations of the host target plus, on Windows, whatever is mapped in the image window now).</summary>
    public static IEnumerable<(uint Start, uint End)> AllocatorReserved()
    {
        if (!IsWindows) return ModuleBaseAllocator.DefaultReserved;
        var live = OccupiedImageWindow()
            .Select(r => ((uint)r.Start, (uint)Math.Min(r.End, 0x70000000UL)));
        // Under Wine (and on any Windows where VirtualAlloc(NULL) is served bottom-up) the runtime's own allocations made while a module loads fall into the lowest free
        // part of the window, i.e. exactly where a first-fit plan would place the NEXT module. Multi-module tests therefore plan from the upper half of the window.
        return [.. ModuleBaseAllocator.WindowsReserved, .. live, (0x20000000u, 0x50000000u)];
    }

    /// <summary>
    /// The x87 control word a freshly started thread has on this host: Windows initialises threads with 0x27F (53-bit precision), Linux with 0x37F (64-bit).
    /// The guest inherits the host thread's value, exactly like an original x86 process of that OS, and the oracle of each host runs at the same value.
    /// </summary>
    public static uint NaturalX87ControlWord => IsWindows ? 0x27Fu : 0x37Fu;

    /// <summary>Null on Linux x64, else the reason a Linux-only test cannot run here.</summary>
    public static string? LinuxOnlyReason(string what) =>
        IsLinux && IsX64 ? null : $"{what} requires Linux x86-64 (host: {RuntimeInformation.OSDescription}{(IsWine ? " under Wine" : "")}, {RuntimeInformation.ProcessArchitecture})";

    private static bool DetectWine()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!NativeLibrary.TryLoad("ntdll.dll", out var h)) return false;
            return NativeLibrary.TryGetExport(h, "wine_get_version", out _);
        }
        catch { return false; }
    }
}

/// <summary>[Fact] that is skipped with an explicit reason unless the host is Linux x86-64.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(string what = "this test") { Skip = HostPlatform.LinuxOnlyReason(what); }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxOnlyTheoryAttribute : TheoryAttribute
{
    public LinuxOnlyTheoryAttribute(string what = "this test") { Skip = HostPlatform.LinuxOnlyReason(what); }
}

/// <summary>[Fact] that is skipped with an explicit reason unless the host is Windows (including Wine).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string what = "this test") { Skip = HostPlatform.IsWindows ? null : $"{what} only applies to a Windows host (host: {RuntimeInformation.OSDescription})"; }
}
