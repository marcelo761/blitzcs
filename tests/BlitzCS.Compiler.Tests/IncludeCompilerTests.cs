using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class IncludeCompilerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-includes-" + Guid.NewGuid());
    public IncludeCompilerTests() => Directory.CreateDirectory(directory);
    private string FileAt(string name, string source)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }

    [Fact]
    public void NestedIncludesUseMainDirectoryAndPreserveExecutionOrder()
    {
        FileAt("sub/one.bb", "result=result*10+2\nInclude \"two.bb\"\n");
        FileAt("two.bb", "result=result*10+3\n");
        FileAt("sub/two.bb", "result=999\n");
        var source = "Global result=1\nInclude \"sub\\one.bb\"\nresult=result*10+4\n";
        var root = FileAt("main.bb", source);
        ArrayCompilerTests.WithAssembly(source, a => Assert.Equal(1234, a.GetType("BB")!.GetField("result")!.GetValue(null)), root);
    }

    [Fact]
    public void DuplicateCanonicalPathsAndNonRootCyclesAreSkipped()
    {
        FileAt("a.bb", "result=result+1\nInclude \"b.bb\"\n");
        FileAt("b.bb", "result=result+10\nInclude \"A.BB\"\n");
        var source = "Global result=0\nInclude \"a.bb\"\nInclude \"./A.BB\"\n";
        var root = FileAt("main.bb", source);
        ArrayCompilerTests.WithAssembly(source, a => Assert.Equal(11, a.GetType("BB")!.GetField("result")!.GetValue(null)), root);
    }

    [Fact]
    public void RootIsNotAutomaticallyMarkedIncluded()
    {
        FileAt("child.bb", "Include \"main.bb\"\n");
        var source = "Global result=0\nInclude \"child.bb\"\n";
        var root = FileAt("main.bb", source);
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, root));
        Assert.Contains("Duplicate", error.Message);
        Assert.Equal(root, error.SourcePath);
    }

    [Theory]
    [InlineData("Function F%(x.Unknown)\nEnd Function", 1)]
    [InlineData("Function F%(x%, x%)\nEnd Function", 1)]
    [InlineData("Print @", 1)]
    [InlineData("Print 1+", 1)]
    [InlineData("Local n.Missing=Null", 1)]
    [InlineData("Include \"missing.bb\"", 1)]
    public void IncludedDiagnosticsNameOriginalFile(string child, int line)
    {
        var path = FileAt("child.bb", child);
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Include \"child.bb\"", Path.Combine(directory, "main.bb")));
        Assert.Equal(path, error.SourcePath);
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public void IncludeInFunctionInheritsScopeAndSharesArrayDeclarations()
    {
        FileAt("arrays.bb", "Dim values%(1)\nvalues(1)=42\n");
        FileAt("body.bb", "Return values(1)\n");
        var source = "Include \"arrays.bb\"\nGlobal result=ReadValue()\nFunction ReadValue%()\nInclude \"body.bb\"\nEnd Function\n";
        ArrayCompilerTests.WithAssembly(source, a => Assert.Equal(42, a.GetType("BB")!.GetField("result")!.GetValue(null)), FileAt("main.bb", source));
    }

    [Theory]
    [InlineData("If 1\n", "EndIf\n")]
    [InlineData("Function F%()\n", "End Function\n")]
    public void BlocksCannotCrossIncludeBoundary(string child, string parentEnd)
    {
        var path = FileAt("child.bb", child);
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Include \"child.bb\"\n" + parentEnd, Path.Combine(directory, "main.bb")));
        Assert.Equal(path, error.SourcePath);
    }

    [Fact]
    public void IncludeInsideFunctionCannotDeclareGlobalType()
    {
        var path = FileAt("types.bb", "Type Node\nEnd Type\n");
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Function F%()\nInclude \"types.bb\"\nEnd Function", Path.Combine(directory, "main.bb")));
        Assert.Equal(path, error.SourcePath);
    }

    [Fact]
    public void IncludeTextInsideCommentsStringsAndCSharpIsOpaque()
    {
        var source = "Print \"Include missing.bb\"\n; Include \"missing.bb\"\n<?cs var text = \"Include missing.bb\"; ?>\n";
        var generated = BasicCompiler.Compile(source, Path.Combine(directory, "main.bb"));
        Assert.Contains("Include missing.bb", generated);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
