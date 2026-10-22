using BlitzCS.Compiler;
using Xunit;
namespace BlitzCS.Compiler.Tests;

public class TypedCompilerTests
{
    [Fact]
    public void PrimitiveBridgeSignaturesKeepSuffixTypesAndCanonicalNames()
    {
        var code = BasicCompiler.Compile("Global Count%=1, Scale#=1.5, Title$=\"hello\"\nFunction Describe$(n%,scale#,text$)\nReturn text+n+scale\nEnd Function", "typed.bb");
        Assert.Contains("public static int @count", code);
        Assert.Contains("public static float @scale", code);
        Assert.Contains("public static string @title", code);
        Assert.Contains("public static string @describe(int v0, float v1, string v2)", code);
    }

    [Fact]
    public void StaticOverloadsUseActualPrimitiveArgumentAndReturnTypes()
    {
        var code = BasicCompiler.Compile("<?cs module public static class Api { public static string Pick(int x)=>\"int\"; public static float Pick(float x)=>x; } ?>\nPrint CS::Api.Pick(2)\nGlobal f#=CS::Api.Pick(2.5)", "overload.bb");
        Assert.Contains("global::Api.Pick(2)", code);
        Assert.Contains("global::Api.Pick(2.5f)", code);
    }

    [Fact]
    public void UnsupportedOverloadDoesNotHideAllowedFloatPromotion()
    {
        var code = BasicCompiler.Compile("<?cs module public static class Api { public static string Pick(long value)=>\"long\"; public static string Pick(float value)=>\"float\"; } ?>\nPrint CS::Api.Pick(2)", "overload.bb");
        Assert.Contains("global::Api.Pick(((float)2))", code);
    }

    [Theory]
    [InlineData("public static bool Bad()=>true;")]
    [InlineData("public static double Bad()=>1.0;")]
    [InlineData("public static object Bad()=>new object();")]
    [InlineData("public static int Bad(float x)=>0;")]
    public void InvalidBridgeCallsHaveBasicSourceDiagnostics(string member)
    {
        var error=Assert.Throws<BasicDiagnosticException>(()=>BasicCompiler.Compile($"<?cs module public static class Api {{ {member} }} ?>\nPrint CS::Api.Bad()", "bridge.bb"));
        Assert.Contains("bridge.bb(2,7)", error.Message);
    }

    [Theory]
    [InlineData("Local value#\nvalue%=1")]
    [InlineData("Global value$\nPrint value#")]
    [InlineData("Function Value#()\nEnd Function\nPrint Value%()")]
    [InlineData("Print -\"2\"")]
    [InlineData("Print +\"2\"")]
    [InlineData("Print 1.0/(2.0-2.0)")]
    [InlineData("Print CS::Missing.Nope()")]
    public void InvalidSuffixesOperatorsAndCallsAreRejected(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "invalid.bb"));

    [Fact]
    public void StringCallsCanBeUsedAsStatements()
    {
        var source = BasicCompiler.Compile("<?cs module public static class Api { public static string Text()=>\"text\"; } ?>\nCS::Api.Text()\nText()\nFunction Text$()\nReturn \"text\"\nEnd Function", "statements.bb");
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Statements", [tree], refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.ConsoleApplication));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("Print Int 3.5")]
    [InlineData("Print Float 2")]
    [InlineData("Print Str 2.0")]
    [InlineData("Print Int%(3.5)")]
    [InlineData("Print Float#(2)")]
    [InlineData("Print Str$(2.0)")]
    [InlineData("Print Int (2.5+1.0)")]
    public void ExplicitCastsAcceptOriginalUnarySyntaxAndMatchingSuffix(string source) =>
        Assert.NotEmpty(BasicCompiler.Compile(source, "casts.bb"));

    [Fact]
    public void PreviouslyUnsupportedPrimitiveProgramsCompile()
    {
        foreach(var source in new[]{"x=\"string\"", "x#=2", "Print 1.5", "Global x$=\"bad\"", "Global x#=1", "Function F()\nReturn \"no\"\nEnd Function", "End\nx=\"still checked\""})
            Assert.NotEmpty(BasicCompiler.Compile(source,"primitive.bb"));
    }
}
