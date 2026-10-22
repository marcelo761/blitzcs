using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class EmitterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-" + Guid.NewGuid());

    public EmitterTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task EmittedAssemblyExecutesWithoutCompilerDependencies()
    {
        var output = Path.Combine(directory, "hello.dll");
        DotNetEmitter.Emit("System.Console.WriteLine(6 * 7);", output);
        Assert.True(File.Exists(Path.ChangeExtension(output, ".pdb")));
        Assert.True(File.Exists(Path.ChangeExtension(output, ".generated.cs")));
        var start = new ProcessStartInfo(DotNetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("42" + Environment.NewLine, await stdout);
        Assert.Equal("", await stderr);
    }

    [Fact]
    public void RoslynDiagnosticsMapToOriginalBasicFile()
    {
        var output = Path.Combine(directory, "invalid.dll");
        var error = Assert.Throws<InvalidOperationException>(() => DotNetEmitter.Emit(
            "#line 17 \"original.bb\"\nMissing();", output));
        Assert.Contains("original.bb(17,", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExplicitEntryPointAllowsModuleMainMethods()
    {
        var output = Path.Combine(directory, "entry.dll");
        DotNetEmitter.Emit("""
            public static class BlitzProgram { public static void Main() => System.Console.WriteLine(42); }
            public static class Other { public static void Main() => System.Console.WriteLine(99); }
            """, output, entryPointTypeName: "BlitzProgram");
        var start = new ProcessStartInfo(DotNetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("42" + Environment.NewLine, await stdout);
        Assert.Equal("", await stderr);
    }

    private static string DotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    public void Dispose() => Directory.Delete(directory, true);
}
