using System.Runtime.InteropServices;
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
    public const uint DefaultHighGuestBase = 0x30000000;

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
