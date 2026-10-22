using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class RuntimeErrorContractTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-runtime-errors-" + Guid.NewGuid());

    public RuntimeErrorContractTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task SuccessfulProgramExitsZeroAndKeepsStandardErrorEmpty()
    {
        var result = await BuildAndRun("Print 42");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task ArrayBoundsFailureExitsNonzeroAndReportsSourceAndPriorOutput()
    {
        var result = await BuildAndRun("Dim values%(1)\nPrint \"before failure\"\nPrint values(2)", "upper-bound-error.bb");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(Lines("before failure"), result.Stdout);
        Assert.Contains("Array 'values'", result.Stderr);
        Assert.Contains("upper-bound-error.bb", result.Stderr);
    }

    [Fact]
    public async Task ExceptionFromCSharpBridgeExitsNonzeroAndReportsException()
    {
        var source = """
            <?cs module
            public static class FailureBridge
            {
                public static int ThrowNow() => throw new System.InvalidOperationException("hybrid failure");
            }
            ?>
            Print "before C#"
            result%=CS::FailureBridge.ThrowNow()
            Print result
            """;

        var result = await BuildAndRun(source, "hybrid-error.bb");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(Lines("before C#"), result.Stdout);
        Assert.Contains("InvalidOperationException", result.Stderr);
        Assert.Contains("hybrid failure", result.Stderr);
        Assert.Contains("hybrid-error.bb", result.Stderr);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> BuildAndRun(string source, string name = "runtime-error.bb")
    {
        var sourcePath = Path.Combine(directory, name);
        var assembly = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".dll");
        await File.WriteAllTextAsync(sourcePath, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", sourcePath, "-o", assembly], new StringWriter(), errors);
        Assert.True(code == 0, errors.ToString());

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    public void Dispose() => Directory.Delete(directory, true);
}
