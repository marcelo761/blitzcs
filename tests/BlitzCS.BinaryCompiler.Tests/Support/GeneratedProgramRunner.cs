using System.Diagnostics;

namespace BlitzCS.BinaryCompiler.Tests.Support;

/// <summary>
/// Starts a generated .NET program (the output of <c>blitzcs build</c>) in a child process. On Linux that is the current dotnet host. A published
/// self-contained Windows test run has no <c>dotnet</c> host, so there it is <c>WinTestRunner.exe --run &lt;assembly.dll&gt;</c> (tools/WinTestRunner),
/// found through <c>BLITZCS_WINTESTRUNNER</c> or next to the test assemblies (scripts/wine/run-tests.sh puts it there).
/// This file is shared by file with other test projects.
/// </summary>
internal static class GeneratedProgramRunner
{
    public const string RunnerVariable = "BLITZCS_WINTESTRUNNER";

    public static ProcessStartInfo Create(string assemblyPath)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            var runner = Environment.GetEnvironmentVariable(RunnerVariable);
            if (string.IsNullOrWhiteSpace(runner)) runner = Path.Combine(AppContext.BaseDirectory, "WinTestRunner.exe");
            if (!File.Exists(runner)) throw new FileNotFoundException($"WinTestRunner.exe not found (set {RunnerVariable} or place it next to the test assemblies): {runner}");
            info = new ProcessStartInfo(runner);
            info.ArgumentList.Add("--run");
        }
        else info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet");
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.UseShellExecute = false;
        info.ArgumentList.Add(assemblyPath);
        return info;
    }
}
