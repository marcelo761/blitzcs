using System.Diagnostics;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ConstantTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-constants-" + Guid.NewGuid());

    public ConstantTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task TypedConstantsSupportNumericExpressionsAndStringConcatenation()
    {
        var result = await BuildAndRun("""
            Const integer%=3, ratio#=1.5, title$="S"+"CP"
            Print integer%*2
            Print ratio#+2.0
            Print title$
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("6", "3.5", "SCP"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task EarlierConstantsCanComposeAndBeUsedInFunctionsAndVectorBounds()
    {
        var result = await BuildAndRun("""
            Const gridSize%=1
            Const lastIndex%=gridSize%+1
            Global values%[lastIndex%]
            Function GetGridSize%()
              Return gridSize%
            End Function
            values%[lastIndex%]=42
            Print GetGridSize%()
            Print values%[lastIndex%]
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("1", "42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task IncludedConstantsAreVisibleToSubsequentProgramCode()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "constants.bb"), "Const answer%=42\n");
        var result = await BuildAndRun("""
            Include "constants.bb"
            Print answer%
            """, "include-constant.bb");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task BasicFunctionCanExposeAConstantThroughTheCSharpBridge()
    {
        var result = await BuildAndRun("""
            Const answer%=42
            Function GetAnswer%()
              Return answer%
            End Function
            <?cs module
            public static class ConstantBridge
            {
                public static int ReadAnswer() => BB.getanswer();
            }
            ?>
            Print CS::ConstantBridge.ReadAnswer()
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Theory]
    [InlineData("Const answer%=42\nanswer%=43\n")]
    [InlineData("Const answer%=42\nanswer%=answer%+1\n")]
    public async Task ConstantsCannotBeAssigned(string source)
    {
        var result = await BuildWithoutRun(source, "constant-assignment.bb");

        Assert.NotEqual(0, result.BuildExitCode);
        AssertConstDiagnostic(result, "constant-assignment.bb");
    }

    [Theory]
    [InlineData("Function Invalid%()\n  Const local%=1\nEnd Function\n")]
    [InlineData("If 1 Then\n  Const nested%=1\nEndIf\n")]
    public async Task ConstantsAreRestrictedToMainProgramScope(string source)
    {
        var result = await BuildWithoutRun(source, "constant-scope.bb");

        Assert.NotEqual(0, result.BuildExitCode);
        AssertConstDiagnostic(result, "constant-scope.bb");
    }

    [Fact]
    public async Task ForwardConstantReferencesAreRejected()
    {
        var result = await BuildWithoutRun("Const later%=earlier%+1\nConst earlier%=1\n", "forward-constant.bb");

        Assert.NotEqual(0, result.BuildExitCode);
        AssertConstDiagnostic(result, "forward-constant.bb");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> BuildAndRun(string source, string name = "constants.bb")
    {
        var path = Path.Combine(directory, name);
        var assembly = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".dll");
        await File.WriteAllTextAsync(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        Assert.True(code == 0, errors.ToString());
        return await RunAssembly(assembly);
    }

    private async Task<(int BuildExitCode, string BuildStderr)> BuildWithoutRun(string source, string name)
    {
        var path = Path.Combine(directory, name);
        var assembly = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".dll");
        await File.WriteAllTextAsync(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        return (code, errors.ToString());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAssembly(string assembly)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static void AssertConstDiagnostic((int BuildExitCode, string BuildStderr) result, string sourcePath)
    {
        Assert.Contains(sourcePath, result.BuildStderr);
        Assert.DoesNotContain("Unsupported keyword 'const'", result.BuildStderr, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
