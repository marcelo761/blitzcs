using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyArrayTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-array-" + Guid.NewGuid());

    public LegacyArrayTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("Dim a%(2)\na(0)=7\na(2)=42\nPrint a(0)\nPrint a(1)\nPrint a(2)", "7\n0\n42")]
    [InlineData("Dim a#(1)\na(1)=2.5\nPrint a(0)\nPrint a(1)", "0.0\n2.5")]
    [InlineData("Dim a$(1)\na(1)=\"text\"\nPrint \"[\"+a(0)+\"]\"\nPrint a(1)", "[]\ntext")]
    [InlineData("Dim grid%(1,2)\ngrid(1,2)=42\ngrid(0,2)=7\nPrint grid(1,2)\nPrint grid(0,2)\nPrint grid(1,0)", "42\n7\n0")]
    [InlineData("Dim cube(1,1,1)\ncube(1,1,1)=42\nPrint cube(1,1,1)\nPrint cube(0,0,0)", "42\n0")]
    [InlineData("Dim a(0)\na(0)=42\nPrint a(0)", "42")]
    [InlineData("Dim a(2)\na(2)=42\nDim a(3)\nPrint a(2)\nPrint a(3)", "0\n0")]
    [InlineData("Dim a$(2)\na(2)=\"old\"\nDim a(3)\nPrint \"[\"+a(2)+\"]\"", "[]")]
    [InlineData("Dim Values#(1)\nVALUES(1)=2.5\nPrint values(1)", "2.5")]
    [InlineData("Dim a(1.6)\na(1.5)=42\nPrint a(2)", "42")]
    [InlineData("Dim a$(1)\na(1)=2.5\nPrint a(1)", "2.5")]
    [InlineData("For i=1 To 2\nDim a(1)\na(1)=a(1)+i\nNext\nPrint a(1)", "2")]
    [InlineData("Dim a%(0)\nFor a(0)=1 To 3\nPrint a(0)\nNext\nPrint a(0)", "1\n2\n3\n4")]
    [InlineData("Dim a#(0)\nFor a(0)=0.5 To 1 Step 0.5\nPrint a(0)\nNext", "0.5\n1.0")]
    public async Task InclusiveTypedArraysAndRedimensioningExecute(string source, string expected)
    {
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
        var result = await Run(assembly);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(Lines(expected.Split('\n')), result.Stdout);
    }

    [Fact]
    public async Task FunctionsAccessAndRedimensionTheSameGlobalArray()
    {
        var source = """
            Dim values%(1)
            values(1)=7
            Print ReadValue(1)
            Resize()
            Print values(2)
            Print values(1)
            Function ReadValue%(i%)
                Return values(i)
            End Function
            Function Resize%()
                Dim values(2)
                values(2)=42
                Return 0
            End Function
            """;
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
        var result = await Run(assembly);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(Lines("7", "42", "0"), result.Stdout);
    }

    [Fact]
    public async Task ArraysAndTypedInteropWorkTogetherInsideLoops()
    {
        var source = """
            Global total%=0
            Dim values%(3)
            <?cs module
            public static class Fill { public static int Get(int n) => BB.doublevalue(n); }
            ?>
            For i=0 To 3
                values(i)=CS::Fill.Get(i)
                total=total+values(i)
                <?cs BB.total += 1; ?>
            Next
            Print values(3)
            Print total
            Function DoubleValue%(n%)
                Return n*2
            End Function
            """;
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
        var result = await Run(assembly);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(Lines("6", "16"), result.Stdout);
    }

    [Theory]
    [InlineData("Dim a(1,1)\nPrint a(0)")]
    [InlineData("Dim a(1)\nDim a(1,1)")]
    [InlineData("Dim a#(1)\nDim a$(1)")]
    [InlineData("Dim a#(1)\nPrint a%(0)")]
    [InlineData("Dim a()")]
    [InlineData("Print a(0)\nDim a(1)")]
    [InlineData("Function F%()\nDim a(1)\nReturn 0\nEnd Function")]
    [InlineData("Function F%()\nReturn a(0)\nEnd Function\nDim a(1)")]
    [InlineData("Dim a(1)\nFunction F%()\nLocal a%=1\nReturn a(0)\nEnd Function")]
    public void InvalidArrayDeclarationsAndRanksHaveSourceDiagnostics(string source)
    {
        var (code, errors, assembly) = Build(source);
        Assert.Equal(1, code);
        Assert.Contains("arrays.bb", errors);
        Assert.False(File.Exists(assembly));
    }

    [Theory]
    [InlineData("Dim a(1)\nPrint a(2)")]
    [InlineData("Dim a(1)\na(-1)=42")]
    [InlineData("Dim a(1,1)\nPrint a(0,2)")]
    [InlineData("n=-1\nDim a(n)")]
    [InlineData("If 0 Then\nDim a(1)\nEndIf\nPrint a(0)")]
    public async Task InvalidDimensionsAndIndicesFailWithArrayDiagnostic(string source)
    {
        var (code, errors, assembly) = Build(source);
        if (code != 0)
        {
            Assert.Contains("arrays.bb", errors);
            return;
        }
        var result = await Run(assembly);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("array", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private (int Code, string Errors, string Assembly) Build(string source)
    {
        var path = Path.Combine(directory, "arrays.bb");
        var assembly = Path.Combine(directory, "arrays.dll");
        File.WriteAllText(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        return (code, errors.ToString(), assembly);
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
