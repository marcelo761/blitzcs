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

    [Theory]
    [InlineData("Print\n")]
    [InlineData("Print:Print 1")]
    [InlineData("Print")]
    [InlineData("If 1 Then Print Else Print \"else\"")]
    [InlineData("If 1 Then\nPrint\nElse\nPrint \"else\"\nEndIf")]
    public void EmptyPrintWritesAnEmptyLineAtStatementBoundaries(string source)
    {
        var output = BasicCompiler.Compile(source, "empty-print.bb");

        Assert.Contains("System.Console.WriteLine(\"\");", output);
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
    [InlineData("Graphics3D 640,480")]
    [InlineData("Print 1,2")]
    [InlineData("Print \"a\" - 1")]
    public void UnsupportedSyntaxIsRejected(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "unsupported.bb"));

    [Theory]
    [InlineData("2147483647", 2147483647)]
    [InlineData("2147483648", 2147483647)]
    [InlineData("4294967295", 2147483647)]
    [InlineData("99999999999999999999999999999999999999999999999999", 2147483647)]
    [InlineData("-2147483648", -2147483647)]
    [InlineData("-99999999999999999999999999999999999999999999999999", -2147483647)]
    public void DecimalLiteralsSaturateLikeOriginalCompiler(string literal, int expected) =>
        ArrayCompilerTests.WithAssembly($"Global result%={literal}", assembly =>
            Assert.Equal(expected, assembly.GetType("BB")!.GetField("result")!.GetValue(null)));

    [Fact]
    public void ConstantDivisionByZeroIsRejectedDuringSemanticAnalysis() =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Print 4 / (2-2)", "zero.bb"));

    [Fact]
    public void EmptySourceIsValid() => Assert.Contains("static void Main()", BasicCompiler.Compile("; empty\n", "empty.bb"));

    [Theory]
    [InlineData("%110001", 49)]
    [InlineData("%11111111111111111111111111111111", -1)]
    [InlineData("%100000000000000000000000000000000", 0)]
    [InlineData("%100000000000000000000000000000001", 1)]
    public void BinaryLiteralsUseHistoricalBitAccumulation(string literal, int expected) =>
        ArrayCompilerTests.WithAssembly($"Global result%={literal}", assembly =>
            Assert.Equal(expected, assembly.GetType("BB")!.GetField("result")!.GetValue(null)));

    [Fact]
    public void ValidateSyntaxAcceptsParsedSourceWithoutSemanticAnalysis()
    {
        BasicCompiler.ValidateSyntax("Print missingFunction(1)", "syntax.bb");
    }

    [Fact]
    public void ValidateSyntaxReportsParserErrors()
    {
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.ValidateSyntax("Print (1+)", "broken.bb"));

        Assert.Contains("Expected an expression", error.Message);
    }

    [Fact]
    public void SourcePathAndBackslashesAreEscaped()
    {
        var output = BasicCompiler.Compile("Print \"C:\\temp\"", "folder/\"name.bb");
        Assert.Contains("\"C:\\\\temp\"", output);
        Assert.Contains("#line 1 \"folder/\\\"name.bb\"", output);
    }
}
