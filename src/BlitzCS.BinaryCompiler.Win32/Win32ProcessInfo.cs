using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// Policy and helpers that make the guest PEB, GetCommandLineW/GetModuleFileNameW and GetVersionEx describe the same process. The PEB is built by the runtime
/// (<see cref="GuestModuleOptions.Process"/>) from the same <see cref="Win32Options"/> the services read.
/// </summary>
public static class Win32ProcessInfo
{
    /// <summary>The PEB description of the process configured by <paramref name="options"/> (environment sorted like GetEnvironmentStrings).</summary>
    public static GuestProcessInfo From(Win32Options options) => new(
        options.MainModulePath, options.CommandLine,
        [.. options.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)],
        options.Version.Major, options.Version.Minor, (ushort)options.Version.Build, (ushort)(options.Version.ServicePackMajor << 8));

    /// <summary>
    /// The OS version policy for a program: the version the image declares in MajorOperatingSystemVersion/MinorOperatingSystemVersion (a program built
    /// for Vista, 6.0, must not be told it runs on XP), but never lower than <see cref="Win32Version"/>'s default 5.1.2600 SP3. Builds are those of the
    /// last service pack of each release (6.0 = 6002 SP2, 6.1 = 7601 SP1, 6.2 = 9200, 6.3 = 9600, 10.0 = 19045).
    /// </summary>
    public static Win32Version VersionForImage(uint major, uint minor)
    {
        var floor = new Win32Version();
        var v = (major, minor) switch
        {
            (5, 0) => new Win32Version(5, 0, 2195, "Service Pack 4", 4),
            (5, 2) => new Win32Version(5, 2, 3790, "Service Pack 2", 2),
            (6, 0) => new Win32Version(6, 0, 6002, "Service Pack 2", 2),
            (6, 1) => new Win32Version(6, 1, 7601, "Service Pack 1", 1),
            (6, 2) => new Win32Version(6, 2, 9200, "", 0),
            (6, 3) => new Win32Version(6, 3, 9600, "", 0),
            (10, _) => new Win32Version(10, 0, 19045, "", 0),
            _ => floor,
        };
        return (v.Major, v.Minor) is var (a, b) && (a > floor.Major || (a == floor.Major && b > floor.Minor)) ? v : floor;
    }
}
