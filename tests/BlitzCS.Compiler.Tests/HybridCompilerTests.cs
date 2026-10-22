using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public class HybridCompilerTests
{
    [Fact]
    public void GlobalsAndFunctionsExposeCanonicalBridgeNames()
    {
        var code = BasicCompiler.Compile("Global Answer%=0\nPrint ADD(20,22)\nFunction Add%(Left%,Right%)\nReturn Left+Right\nEnd Function", "hybrid.bb");
        Assert.Contains("public static class BB", code);
        Assert.Contains("public static int @answer", code);
        Assert.Contains("public static int @add(int v0, int v1)", code);
        Assert.Contains("global::BB.@add(20, 22)", code);
    }

    [Fact]
    public void InlineAndModuleCSharpKeepCodeAndOriginalLineMappings()
    {
        var code = BasicCompiler.Compile("Global answer%=0\n<?cs module public static class MathBridge { public static int Compute() => BB.add(20,22); } ?>\n<?cs BB.answer = MathBridge.Compute(); ?>\nPrint answer\nFunction Add%(a%,b%)\nReturn a+b\nEnd Function", "hybrid.bb");
        Assert.Contains("public static class MathBridge", code);
        Assert.Contains("BB.answer = MathBridge.Compute();", code);
        Assert.Contains("#line 3 \"hybrid.bb\"", code);
        Assert.Contains("System.Console.WriteLine(global::BB.@answer);", code);
    }

    [Theory]
    [InlineData("\"?>\"")]
    [InlineData("@\"?>\"")]
    [InlineData("$\"text ?> {42}\"")]
    [InlineData("$\"{\"?>\"}\"")]
    [InlineData("\"\"\"?>\"\"\"")]
    [InlineData("$$\"\"\"?> {{42}}\"\"\"")]
    public void ClosingDelimiterInsideCSharpStringsIsPreserved(string expression)
    {
        var body = $"System.Console.WriteLine({expression});";
        Assert.Contains(body, BasicCompiler.Compile($"<?cs {body} ?>\nPrint 42", "strings.bb"));
    }

    [Fact]
    public void ClosingDelimiterInsideCommentsIsPreserved()
    {
        const string body = "// ?>\n/* ?> */ System.Console.WriteLine(42);";
        Assert.Contains(body, BasicCompiler.Compile($"<?cs {body} ?>", "comments.bb"));
    }

    [Fact]
    public void BasicCanCallCaseSensitiveCSharpStaticMethod()
    {
        var code = BasicCompiler.Compile("<?cs module public static class GameLogic { public static int Add(int a,int b)=>a+b; } ?>\nPrint CS::GameLogic.Add(20,22)", "calls.bb");
        Assert.Contains("global::GameLogic.Add(20, 22)", code);
        Assert.Contains("__BlitzInteger", code);
    }

    [Fact]
    public void LocalInitializerReadsGlobalBeforeShadowDeclaration()
    {
        var code = BasicCompiler.Compile("Global answer=42\nFunction ReadValue()\nLocal answer=answer\nReturn answer\nEnd Function", "scope.bb");
        Assert.Contains("v0 = global::BB.@answer;", code);
        Assert.Contains("return v0;", code);
    }

    [Fact]
    public void CSharpAfterEndStillReachesRoslynForValidation()
    {
        var code = BasicCompiler.Compile("End\n<?cs MissingMethod(); ?>", "end.bb");
        Assert.Contains("MissingMethod();", code);
        Assert.True(code.IndexOf("return;", StringComparison.Ordinal) < code.IndexOf("MissingMethod();", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Function F()\nGlobal x=0\nEnd Function")]
    [InlineData("Function F()\nFunction G()\nEnd Function\nEnd Function")]
    [InlineData("Return 1")]
    [InlineData("Function F(a,a)\nEnd Function")]
    [InlineData("Function F()\nEnd Function\nPrint F(1)")]
    [InlineData("Print unknown()")]
    [InlineData("x=1\nGlobal x=2")]
    [InlineData("Global x=x")]
    [InlineData("Local x=x")]
    [InlineData("<?cs System.Console.WriteLine(42);")]
    [InlineData("End\nLocal x%\nx#=1")]
    public void InvalidScopesAndUnsupportedTypesAreRejected(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "bad.bb"));
}
