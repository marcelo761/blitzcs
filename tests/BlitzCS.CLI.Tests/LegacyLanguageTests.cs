using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyLanguageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-legacy-" + Guid.NewGuid());

    public LegacyLanguageTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("If 1 Then\nPrint 42\nElse\nPrint 0\nEndIf", "42")]
    [InlineData("If 0 Then\nPrint 0\nElseIf 2<3 Then\nPrint 42\nElse\nPrint -1\nEndIf", "42")]
    [InlineData("If 0 Then Print 0 Else Print 42", "42")]
    [InlineData("If 1 Then : Print 42 : EndIf", "42")]
    [InlineData("If 0.4 Then Print 0 Else Print 42", "42")]
    [InlineData("If \"2tail\" Then Print 42", "42")]
    [InlineData("i=0\nWhile i<3\nPrint i\ni=i+1\nWend", "0\n1\n2")]
    [InlineData("For i=1 To 3\nPrint i\nNext\nPrint i", "1\n2\n3\n4")]
    [InlineData("For i=3 To 1 Step -1\nPrint i\nNext", "3\n2\n1")]
    [InlineData("For f#=0.5 To 1.5 Step 0.5\nPrint f\nNext", "0.5\n1.0\n1.5")]
    [InlineData("For i=1 To 2 Step 0\nPrint 99\nNext\nPrint 42", "42")]
    [InlineData("For i=2 To 1 Step 0\nPrint 42\nExit\nNext", "42")]
    [InlineData("i=0\nRepeat\ni=i+1\nPrint i\nUntil i=3", "1\n2\n3")]
    [InlineData("Repeat\nPrint 42\nExit\nForever", "42")]
    [InlineData("Select 2\nCase 1\nPrint 1\nCase 2,3\nPrint 42\nDefault\nPrint 0\nEnd Select", "42")]
    [InlineData("Select \"b\"\nCase \"a\"\nPrint 0\nDefault\nPrint 42\nEnd Select", "42")]
    [InlineData("Select 2.5\nCase 2.5\nPrint 42\nEnd Select", "42")]
    [InlineData("While 1\nSelect 1\nCase 1\nPrint 42\nExit\nEnd Select\nPrint 99\nWend", "42")]
    [InlineData("For i=1 To 2\nFor j=1 To 3\nPrint i*10+j\nExit\nNext\nNext", "11\n21")]
    [InlineData("Print 1 Or 2 And 4\nPrint Not 1 And 0\nPrint -2^2\nPrint 2^3^2", "0\n1\n4.0\n64.0")]
    [InlineData("Print 2<3\nPrint 2<>3\nPrint 2>=3\nPrint \"a\"<\"b\"", "1\n1\n0\n1")]
    public async Task ControlFlowAndPrecedenceExecuteAsExpected(string source, string expected)
    {
        Assert.Equal(Lines(expected.Split('\n')), await Execute(source));
    }

    [Fact]
    public async Task ConditionalReturnsDoNotSuppressRemainingFunctionPaths()
    {
        var source = """
            Print Choose(1)
            Print Choose(0)
            Function Choose%(n%)
                If n>0 Then
                    Return 42
                EndIf
                Return 7
            End Function
            """;
        Assert.Equal(Lines("42", "7"), await Execute(source));
    }

    [Fact]
    public async Task LoopBoundIsReevaluatedAndBooleanOperatorsAreEager()
    {
        var source = """
            Global hits%=0
            limit=4
            For i=1 To limit
                Print i
                limit=2
            Next
            If 0 And Touch() Then Print 99
            If 1 Or Touch() Then Print hits
            Function Touch%()
                hits=hits+1
                Return 1
            End Function
            """;
        Assert.Equal(Lines("1", "2", "2"), await Execute(source));
    }

    [Fact]
    public async Task SelectEvaluatesSelectorOnceAndStopsAtFirstMatchingCase()
    {
        var source = """
            Global hits%=0
            Select Pick()
                Case 1,2
                    Print "match"
                Case Pick()
                    Print "wrong"
            End Select
            Print hits
            Function Pick%()
                hits=hits+1
                Return 2
            End Function
            """;
        Assert.Equal(Lines("match", "1"), await Execute(source));
    }

    [Fact]
    public async Task CSharpCallsAndInlineBlocksWorkInsideNestedBasicControlFlow()
    {
        var source = """
            Global total%=0
            <?cs module
            public static class LoopBridge { public static int Value(int n) => BB.twice(n); }
            ?>
            For i=1 To 3
                If i>0 Then
                    total=total+CS::LoopBridge.Value(i)
                    <?cs BB.total += 1; ?>
                EndIf
            Next
            Print total
            Function Twice%(n%)
                Return n*2
            End Function
            """;
        Assert.Equal(Lines("15"), await Execute(source));
    }

    [Theory]
    [InlineData("If 1 Then\nPrint 42")]
    [InlineData("While 1\nPrint 42")]
    [InlineData("For i=1 To 3\nPrint i")]
    [InlineData("Repeat\nPrint 42")]
    [InlineData("Select 1\nCase 1\nPrint 42")]
    [InlineData("Exit")]
    [InlineData("Select 1\nCase 1\nExit\nEnd Select")]
    [InlineData("stepValue=1\nFor i=1 To 3 Step stepValue\nNext")]
    [InlineData("For s$=\"a\" To \"b\"\nNext")]
    [InlineData("Else\nPrint 1")]
    [InlineData("Wend")]
    [InlineData("Next")]
    [InlineData("For i=1 To 3\nNext i")]
    public void InvalidControlFlowHasMappedDiagnostics(string source)
    {
        var (code, errors, assembly) = Build(source);
        Assert.Equal(1, code);
        Assert.Contains("legacy.bb", errors);
        Assert.False(File.Exists(assembly));
    }

    private (int Code, string Errors, string Assembly) Build(string source)
    {
        var path = Path.Combine(directory, "legacy.bb");
        var assembly = Path.Combine(directory, "legacy.dll");
        File.WriteAllText(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        return (code, errors.ToString(), assembly);
    }

    private async Task<string> Execute(string source)
    {
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
        var (exitCode, stdout, stderr) = await Run(assembly);
        Assert.True(exitCode == 0, stderr);
        return stdout;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Run(string assembly)
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    public void Dispose() => Directory.Delete(directory, true);
}
