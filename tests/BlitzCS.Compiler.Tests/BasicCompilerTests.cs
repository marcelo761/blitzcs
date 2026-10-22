using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public class BasicCompilerTests
{
    [Fact]
    public void PrintGeneratesMappedConsoleStatement()
    {
        var output = BasicCompiler.Compile("Print \"Hello; world\" ; comment\r\nEnd", "hello.bb");
        Assert.Contains("System.Console.WriteLine(\"Hello; world\");", output);
        Assert.Contains("#line 1 \"hello.bb\"", output);
        Assert.Contains("return;", output);
    }

    [Fact]
    public void IdentifiersIgnoreCaseAndOptionalIntegerSuffix()
    {
        var output = BasicCompiler.Compile("Score%=4:PRINT sCoRe + 2", "case.bb");
        Assert.Contains("int v0 = 0;", output);
        Assert.Contains("v0 = 4;", output);
        Assert.Contains("System.Console.WriteLine(unchecked((v0 + 2)));", output);
        Assert.DoesNotContain("v1", output);
    }

    [Fact]
    public void MissingVariablesAreImplicitlyInitializedToZero()
    {
        var output = BasicCompiler.Compile("Print missing", "zero.bb");
        Assert.Contains("int v0 = 0;", output);
        Assert.Contains("System.Console.WriteLine(v0);", output);
    }

    [Fact]
    public void ArithmeticPreservesPrecedenceParenthesesAndUnarySigns()
    {
        var output = BasicCompiler.Compile("Print -7 / 2 + (3 - 1) * +4", "math.bb");
        Assert.Contains("unchecked(((unchecked((-7)) / 2) + unchecked((unchecked((3 - 1)) * unchecked((+4))))))", output);
    }

    [Theory]
    [InlineData("\nPrint (1+)", 2, 10)]
    [InlineData("Print \"unfinished", 1, 7)]
    [InlineData("\r\nPrint @", 2, 7)]
    public void MalformedInputReportsOriginalPosition(string source, int line, int column)
    {
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "broken.bb"));
        Assert.Contains($"broken.bb({line},{column})", error.Message);
    }

    [Theory]
    [InlineData("For i=1 To 3")]
    [InlineData("For=1")]
    [InlineData("Print Next")]
    [InlineData("Print Print")]
    [InlineData("Print End")]
    [InlineData("Print ")]
    [InlineData("Print 2147483648")]
    [InlineData("Graphics3D 640,480")]
    [InlineData("Print 1,2")]
    [InlineData("Print \"a\" - 1")]
    public void UnsupportedSyntaxIsRejected(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "unsupported.bb"));

    [Fact]
    public void ConstantDivisionByZeroIsRejectedDuringSemanticAnalysis() =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Print 4 / (2-2)", "zero.bb"));

    [Fact]
    public void EmptySourceIsValid() => Assert.Contains("static void Main()", BasicCompiler.Compile("; empty\n", "empty.bb"));

    [Fact]
    public void SourcePathAndBackslashesAreEscaped()
    {
        var output = BasicCompiler.Compile("Print \"C:\\temp\"", "folder/\"name.bb");
        Assert.Contains("\"C:\\\\temp\"", output);
        Assert.Contains("#line 1 \"folder/\\\"name.bb\"", output);
    }
}
