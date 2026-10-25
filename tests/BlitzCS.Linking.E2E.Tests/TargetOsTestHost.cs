using System.Runtime.CompilerServices;

namespace BlitzCS.TestHost;

/// <summary>The CLI defaults to windows-x64 artifacts; tests that build with the CLI and then run the result in this process must target the host OS.</summary>
internal static class TargetOsTestHost
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable("BLITZCS_BINARY_TARGET", OperatingSystem.IsWindows() ? "windows-x64" : "linux-sysv");
}
