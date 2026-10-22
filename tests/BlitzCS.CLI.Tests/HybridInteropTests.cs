using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class HybridInteropTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-hybrid-" + Guid.NewGuid());

    public HybridInteropTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task BasicCallsCSharpWhichCallsBasicAndSharesGlobalStorage()
    {
        var basic = """
            Global answer%=0
            <?cs module
            public static class MathBridge
            {
                public static int Compute() => BB.add(19, 22);
            }
            ?>
            answer = CS::MathBridge.Compute()
            <?cs
            BB.answer += 1;
            ?>
            Print ANSWER
            End
            Function Add%(left%, right%)
                Return left + right
            End Function
            """;
        Assert.Equal(Lines("42"), await BuildAndRun(basic));
    }

    [Fact]
    public async Task FunctionLocalsShadowGlobalsWithoutCapturingMainLocals()
    {
        var basic = """
            Global total%=40
            x=99
            Print Update(7)
            Print total
            Print Isolated()
            Print x
            Function Update%(n%)
                Local total%=n
                <?cs BB.total += 2; ?>
                Return total
            End Function
            Function Isolated%()
                Return x
            End Function
            """;
        Assert.Equal(Lines("7", "42", "0", "99"), await BuildAndRun(basic));
    }

    [Fact]
    public async Task GlobalMutationsAreVisibleAcrossBasicAndCSharpCalls()
    {
        var basic = """
            Global total%=1
            Print AdvanceTotal(20)
            <?cs BB.total = BB.advancetotal(21); ?>
            Print TOTAL
            Function AdvanceTotal%(n%)
                total=total+n
                Return total
            End Function
            """;
        Assert.Equal(Lines("21", "42"), await BuildAndRun(basic));
    }

    [Fact]
    public async Task ClosingDelimitersInsideCSharpLiteralsAndCommentsAreIgnored()
    {
        var basic = """"
            Global answer%=0
            <?cs
            var regular = "?>";
            var verbatim = @"?>";
            var raw = """?>""";
            var interpolated = $"before {"?>"} after";
            var interpolatedVerbatim = $@"before {"?>"} after";
            var interpolatedRaw = $$"""before {{"?>"}} after""";
            // ?> does not close the block
            /* ?> does not close the block */
            BB.answer = 42;
            ?>
            Print answer
            """";
        Assert.Equal(Lines("42"), await BuildAndRun(basic));
    }

    [Fact]
    public async Task BlocksInsideFunctionsHaveTheirOwnCSharpLocalScope()
    {
        var basic = """
            Global answer%=0
            <?cs int scratch = 1; BB.answer = scratch; ?>
            <?cs int scratch = 41; BB.answer += scratch; ?>
            Print answer
            """;
        Assert.Equal(Lines("42"), await BuildAndRun(basic));
    }

    [Fact]
    public async Task CSharpCanBindTypedDelegateToBasicFunction()
    {
        var basic = """
            Global total%=40
            <?cs
            System.Func<int, int> callback = BB.bump;
            BB.total = callback(2);
            ?>
            Print total
            Function Bump%(n%)
                Return total+n
            End Function
            """;
        Assert.Equal(Lines("42"), await BuildAndRun(basic));
    }

    [Theory]
    [InlineData("Print Empty()\nFunction Empty%()\nEnd Function", "0")]
    [InlineData("Function ReadValue%()\nReturn total\nEnd Function\nGlobal total%=42\nPrint ReadValue()", "42")]
    [InlineData("Print Forward(20,22)\nFunction Forward%(a%,b%)\nReturn Second(a,b)\nEnd Function\nFunction Second%(x%,y%)\nReturn x+y\nEnd Function", "42")]
    public async Task FunctionDeclarationOrderAndDefaultReturnArePreserved(string basic, string expected)
    {
        Assert.Equal(Lines(expected), await BuildAndRun(basic));
    }

    [Fact]
    public async Task ModuleMainMethodDoesNotReplaceBasicEntryPoint()
    {
        var basic = """
            <?cs module
            public static class Other
            {
                public static void Main() => System.Console.WriteLine(99);
            }
            ?>
            Print 42
            """;
        Assert.Equal(Lines("42"), await BuildAndRun(basic));
    }

    [Theory]
    [InlineData("BB.answer = \"wrong\";", "CS0029")]
    [InlineData("BB.answer = 1.5;", "CS0266")]
    [InlineData("BB.missing = 42;", "CS0117")]
    public void CSharpBindingErrorsPointToBasicSource(string csharp, string diagnostic)
    {
        var (code, errors, assembly) = Build($"Global answer%=0\n<?cs\n{csharp}\n?>\nPrint answer");
        Assert.Equal(1, code);
        Assert.Contains("hybrid.bb(3,", errors);
        Assert.Contains(diagnostic, errors);
        Assert.False(File.Exists(assembly));
    }

    [Fact]
    public void ModuleErrorsAlsoPointToBasicSource()
    {
        var (code, errors, _) = Build("<?cs module\npublic class Broken { public int X => \"wrong\"; }\n?>");
        Assert.Equal(1, code);
        Assert.Contains("hybrid.bb(2,", errors);
        Assert.Contains("CS0029", errors);
    }

    [Fact]
    public void FunctionLocalsAreNotExposedThroughTheGlobalBridge()
    {
        var (code, errors, _) = Build("Function F%()\nLocal secret%=1\n<?cs BB.secret=42; ?>\nReturn secret\nEnd Function\nPrint F()");
        Assert.Equal(1, code);
        Assert.Contains("CS0117", errors);
    }

    [Fact]
    public async Task BasicCanPrintStringResultFromCSharp()
    {
        var source = "<?cs module\npublic static class Text { public static string Get() => \"42\"; }\n?>\nPrint CS::Text.Get()";
        Assert.Equal(Lines("42"), await BuildAndRun(source));
    }

    [Theory]
    [InlineData("Print Add(1)\nFunction Add%(a%,b%)\nReturn a+b\nEnd Function")]
    [InlineData("Global n%=1\nGlobal N%=2")]
    [InlineData("Function Add%(x%,X%)\nReturn x\nEnd Function")]
    [InlineData("Global n%=1\n<?cs\nBB.n=42;")]
    public void InvalidHybridProgramsProduceDiagnostics(string basic)
    {
        var (code, errors, assembly) = Build(basic);
        Assert.Equal(1, code);
        Assert.Contains("hybrid.bb", errors);
        Assert.False(File.Exists(assembly));
    }

    private (int Code, string Errors, string Assembly) Build(string source)
    {
        var path = Path.Combine(directory, "hybrid.bb");
        var assembly = Path.Combine(directory, "hybrid.dll");
        File.WriteAllText(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        return (code, errors.ToString(), assembly);
    }

    private async Task<string> BuildAndRun(string source)
    {
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
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
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr);
        return await stdout;
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    public void Dispose() => Directory.Delete(directory, true);
}
