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
    public async Task EmittedGameEnablesJitOptimizationsAndKeepsPortableSymbols()
    {
        var output = Path.Combine(directory, "optimized.dll");
        DotNetEmitter.Emit("""
            using System.Diagnostics;
            using System.Reflection;
            System.Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false);
            """, output);
        Assert.True(File.Exists(Path.ChangeExtension(output, ".pdb")));
        var start = new ProcessStartInfo(DotNetHost()) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("False" + Environment.NewLine, await stdout);
        Assert.Equal("", await stderr);
    }

    [Fact]
    public async Task OptimizedFailureRetainsOriginalBasicSourceLine()
    {
        var output = Path.Combine(directory, "mapped.dll");
        DotNetEmitter.Emit("#line 23 \"original.bb\"\nthrow new System.InvalidOperationException(\"mapped failure\");", output);
        var start = new ProcessStartInfo(DotNetHost()) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, process.ExitCode);
        var error = await stderr;
        Assert.Contains("mapped failure", error);
        Assert.Contains("original.bb:line 23", error);
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

    [Fact]
    public async Task EmittedReleaseAssemblyPreservesLineNumbersInStackTrace()
    {
        var output = Path.Combine(directory, "release_stack.dll");
        DotNetEmitter.Emit("""
            #line 42 "game.bb"
            throw new System.InvalidOperationException("runtime failure");
            """, output, optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release);
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
        Assert.NotEqual(0, process.ExitCode);
        var err = await stderr;
        Assert.Contains("game.bb", err);
        Assert.Contains("42", err);
    }

    private static string DotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    public void Dispose() => Directory.Delete(directory, true);
}
