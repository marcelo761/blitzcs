using System.Diagnostics;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class FixedVectorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-fixed-vectors-" + Guid.NewGuid());

    public FixedVectorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task GlobalPrimitiveVectorsIncludeTheirDeclaredUpperBoundAndZeroInitialize()
    {
        var result = await BuildAndRun("""
            Global integers%[2]
            Global ratios#[1]
            Global labels$[1]
            Print integers%[0]
            Print integers%[2]
            Print ratios#[1]
            Print labels$[1]
            integers%[2]=42
            ratios#[1]=3.5
            labels$[1]="ready"
            Print integers%[2]
            Print ratios#[1]
            Print labels$[1]
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("0", "0", "0.0", "", "42", "3.5", "ready"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task FieldPrimitiveVectorsAllocatePerObjectAndClearOnDelete()
    {
        var result = await BuildAndRun("""
            Type Stats
              Field scores%[2]
              Field ratios#[1]
              Field names$[1]
            End Type
            a.Stats=New Stats
            b.Stats=New Stats
            a\scores%[2]=42
            a\ratios#[1]=3.5
            a\names$[1]="first"
            Print a\scores%[2]
            Print b\scores%[2]
            Print a\ratios#[1]
            Print b\names$[1]
            Delete a
            Print b\scores%[2]
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42", "0", "3.5", "", "0"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task LocalVectorsAreZeroInitializedOnEveryFunctionCall()
    {
        var result = await BuildAndRun("""
            Function ReadFresh%()
              Local values%[0]
              previous%=values%[0]
              values%[0]=42
              Return previous%
            End Function
            Print ReadFresh%()
            Print ReadFresh%()
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("0", "0"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task LocalVectorIsNotReinitializedOnEachLoopIteration()
    {
        var result = await BuildAndRun("""
            Function CountIterations%()
              Local values%[0]
              For i%=0 To 1
                values%[0]=values%[0]+1
              Next
              Return values%[0]
            End Function
            Print CountIterations%()
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("2"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task FixedVectorIndexFunctionIsEvaluatedOnce()
    {
        var result = await BuildAndRun("""
            Global calls%
            Global values%[1]
            Function NextIndex%()
              calls%=calls%+1
              Return calls%-1
            End Function
            values%[NextIndex%()]=42
            Print calls%
            Print values%[0]
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("1", "42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task ObjectVectorFieldsHoldReferencesWithoutDeletingReferencedObjects()
    {
        var result = await BuildAndRun("""
            Type Item
              Field value%
            End Type
            Type Holder
              Field items.Item[2]
            End Type
            holder.Holder=New Holder
            item.Item=New Item
            holder\items[2]=item
            holder\items[2]\value%=42
            Delete holder
            Print item\value%
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task NegativeFixedVectorBoundHasSourceDiagnostic()
    {
        var result = await BuildWithoutRun("Global values%[-1]\nPrint 1\n", "invalid-vector.bb");

        Assert.NotEqual(0, result.BuildExitCode);
        Assert.Contains("invalid-vector.bb", result.BuildStderr);
        Assert.Contains("negative", result.BuildStderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonConstantFixedVectorBoundIsRejectedDuringCompilation()
    {
        var result = await BuildWithoutRun("Global count%\nGlobal values%[count%]\n", "dynamic-vector-bound.bb");

        Assert.NotEqual(0, result.BuildExitCode);
        Assert.Contains("dynamic-vector-bound.bb", result.BuildStderr);
    }

    [Fact]
    public async Task FixedVectorUpperBoundFailureReportsOriginalLocationAndPriorOutput()
    {
        var result = await BuildAndRun("""
            Global values%[1]
            idx%=2
            Print "before"
            Print values%[idx%]
            """, "vector-bounds.bb");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(Lines("before"), result.Stdout);
        Assert.Contains("vector-bounds.bb", result.Stderr);
        Assert.Contains("values", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> BuildAndRun(string source, string name = "vectors.bb")
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

    public void Dispose() => Directory.Delete(directory, true);
}
