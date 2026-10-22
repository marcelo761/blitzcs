using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyObjectTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-object-" + Guid.NewGuid());
    private const string NodeType = """
        Type Node
            Field value%
            Field ratio#
            Field label$
            Field link.Node
        End Type
        """;
    private const string ThreeNodes = """
        a.Node=New Node
        a\value=1
        b.Node=New Node
        b\value=2
        c.Node=New Node
        c\value=3
        """;

    public LegacyObjectTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("a.Node=New Node\nPrint a\\value\nPrint a\\ratio\nPrint \"[\"+a\\label+\"]\"\nPrint a\\link=Null", "0\n0.0\n[]\n1")]
    [InlineData("a.Node=New Node\na\\value=42\na\\ratio=1.5\na\\label=\"hello\"\nPrint a\\value\nPrint a\\ratio\nPrint a\\label", "42\n1.5\nhello")]
    [InlineData("a.Node=New Node\nb.Node=a\nb\\value=42\nPrint a=b\nPrint a\\value", "1\n42")]
    [InlineData("a.Node=New Node\nb.Node=New Node\na\\link=b\na\\link\\value=42\nPrint b\\value\nPrint a\\link=b", "42\n1")]
    [InlineData("a.Node=New Node\na\\link=a\nPrint a\\link=a\nDelete a\nPrint a=Null", "1\n1")]
    [InlineData("a.Node=New Node\nb.Node=a\nDelete a\nPrint a=Null\nPrint b=Null\nDelete a\nDelete Null", "1\n1")]
    [InlineData("Print First Node=Null\nPrint Last Node=Null", "1\n1")]
    public async Task FieldsAndReferencesHaveExpectedLifetimeSemantics(string body, string expected)
    {
        Assert.Equal(Lines(expected.Split('\n')), await Execute(NodeType + "\n" + body));
    }

    [Theory]
    [InlineData("p.Node=First Node\nPrint p\\value\np=Last Node\nPrint p\\value\np=After a\nPrint p\\value\np=Before c\nPrint p\\value\nPrint Before a=Null\nPrint After c=Null", "1\n3\n2\n2\n1\n1")]
    [InlineData("For p.Node=Each Node\nPrint p\\value\nDelete p\nNext\nPrint First Node=Null", "1\n2\n3\n1")]
    [InlineData("For p.Node=Each Node\nPrint p\\value\nIf p=a Then Delete b\nNext", "1\n3")]
    [InlineData("For p.Node=Each Node\nPrint p\\value\nDelete Each Node\nNext\nPrint First Node=Null", "1\n1")]
    [InlineData("Delete b\np.Node=After b\nPrint p\\value\np=Before b\nPrint p\\value\np=After a\nPrint p\\value", "3\n1\n3")]
    [InlineData("Insert c Before a\nInsert a After b\nInsert c After c\nFor p.Node=Each Node\nPrint p\\value\nNext", "3\n2\n1")]
    [InlineData("total=0\nFor p.Node=Each Node\nFor q.Node=Each Node\ntotal=total+1\nNext\nNext\nPrint total", "9")]
    [InlineData("For p.Node=Each Node\nPrint p\\value\nIf p=a Then p=b\nNext", "1\n3")]
    public async Task TypeListsRespectOrderAndIterationMutations(string body, string expected)
    {
        Assert.Equal(Lines(expected.Split('\n')), await Execute(NodeType + "\n" + ThreeNodes + "\n" + body));
    }

    [Fact]
    public async Task NewTailObjectsAreVisitedByActiveForEach()
    {
        var source = NodeType + "\n" + """
            a.Node=New Node
            a\value=1
            b.Node=New Node
            b\value=2
            For p.Node=Each Node
                Print p\value
                If p=a Then
                    c.Node=New Node
                    c\value=3
                EndIf
            Next
            """;
        Assert.Equal(Lines("1", "2", "3"), await Execute(source));
    }

    [Fact]
    public async Task ObjectParametersReturnsAndCrossTypeFieldsAreNominal()
    {
        var source = """
            Type Holder
                Field item.Item
            End Type
            Type Item
                Field value%
            End Type
            h.Holder=New Holder
            h\item=MakeItem(42)
            Print ReadItem(h\item)
            Delete h
            p.Item=First Item
            Print p\value
            Function MakeItem.Item(n%)
                p.Item=New Item
                p\value=n
                Return p
            End Function
            Function ReadItem%(p.Item)
                Return p\value
            End Function
            """;
        Assert.Equal(Lines("42", "42"), await Execute(source));
    }

    [Fact]
    public async Task HybridScalarWrappersManageObjectsWithoutNativePointers()
    {
        var source = NodeType + "\n" + """
            Global total%=0
            <?cs module
            public static class ObjectsBridge
            {
                public static int Populate()
                {
                    BB.additem(20);
                    BB.additem(22);
                    return BB.sumitems();
                }
            }
            ?>
            total=CS::ObjectsBridge.Populate()
            <?cs BB.total += 1; ?>
            Print total
            Delete Each Node
            Print First Node=Null
            Function AddItem%(n%)
                p.Node=New Node
                p\value=n
                Return n
            End Function
            Function SumItems%()
                result=0
                For p.Node=Each Node
                    result=result+p\value
                Next
                Return result
            End Function
            """;
        Assert.Equal(Lines("43", "1"), await Execute(source));
    }

    [Theory]
    [InlineData("New Missing")]
    [InlineData("p.Missing=Null")]
    [InlineData("p.Node=New Node\nPrint p\\missing")]
    [InlineData("p.Node=New Node\nPrint p+1")]
    [InlineData("p.Node=New Node\nIf p Then Print 1")]
    [InlineData("p.Node=New Node\nSelect p\nCase Null\nPrint 1\nEnd Select")]
    [InlineData("Delete 1")]
    [InlineData("For p%=Each Node\nNext")]
    [InlineData("Type Other\nField value%\nEnd Type\np.Node=New Other")]
    [InlineData("Type Other\nField value%\nEnd Type\na.Node=New Node\nb.Other=New Other\nInsert a Before b")]
    [InlineData("Type Duplicate\nField v%\nField V$\nEnd Type")]
    [InlineData("Type Broken\nField value%")]
    [InlineData("Field value%")]
    public void InvalidTypesAndOperationsHaveMappedDiagnostics(string body)
    {
        var (code, errors, assembly) = Build(NodeType + "\n" + body);
        Assert.Equal(1, code);
        Assert.Contains("objects.bb", errors);
        Assert.False(File.Exists(assembly));
    }

    [Theory]
    [InlineData("p.Node=Null\nPrint p\\value")]
    [InlineData("p.Node=New Node\nDelete p\nPrint p\\value")]
    public async Task InvalidatedFieldAccessFailsWithLocatedObjectDiagnostic(string body)
    {
        var (code, errors, assembly) = Build(NodeType + "\n" + body);
        Assert.True(code == 0, errors);
        var result = await Run(assembly);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("objects.bb", result.Stderr);
    }

    private (int Code, string Errors, string Assembly) Build(string source)
    {
        var path = Path.Combine(directory, "objects.bb");
        var assembly = Path.Combine(directory, "objects.dll");
        File.WriteAllText(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        return (code, errors.ToString(), assembly);
    }

    private async Task<string> Execute(string source)
    {
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
        var result = await Run(assembly);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Stdout;
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
