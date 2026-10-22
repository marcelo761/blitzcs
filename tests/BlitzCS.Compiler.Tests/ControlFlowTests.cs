using System.Runtime.Loader;
using BlitzCS.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public class ControlFlowTests
{
    [Theory]
    [InlineData("If 0 Then\nReturn 1\nElseIf 2 Then\nReturn 42\nElse\nReturn 3\nEndIf", 42)]
    [InlineData("If 0 Then Return 1 Else Return 42", 42)]
    [InlineData("If 1 Then\nLocal x=40\nEnd If\nx=x+2\nReturn x", 42)]
    [InlineData("Local x=0\nWhile x<42\nx=x+1\nWend\nReturn x", 42)]
    [InlineData("Local x=0\nFor i=5 To 1 Step -2\nx=x+i\nNext\nReturn x", 9)]
    [InlineData("Local limit=3,x=0\nFor i=1 To limit\nx=x+i\nlimit=limit-1\nNext\nReturn x", 3)]
    [InlineData("Local x=0\nRepeat\nx=x+1\nUntil x=42\nReturn x", 42)]
    [InlineData("Local x=40\nRepeat\nx=x+2\nExit\nForever\nReturn x", 42)]
    [InlineData("Select 2\nCase 1\nReturn 1\nCase 2,3\nReturn 42\nDefault\nReturn 4\nEnd Select", 42)]
    [InlineData("While 1\nSelect 2\nCase 2\nExit\nEndSelect\nReturn 1\nWend\nReturn 42", 42)]
    [InlineData("For i=1 To 2\nFor j=1 To 3\nExit\nNext\nNext\nReturn i*10+j", 31)]
    [InlineData("If 0 Then Return 1\nReturn 42", 42)]
    [InlineData("If 0 Then Return 42", 0)]
    [InlineData("Local x=0\nFor i=1 To 2 Step (Str(1000000.0)=\"1000000.0\")\nx=x+i\nNext\nReturn x", 3)]
    [InlineData("If .5 Then Return 1 Else Return 42", 42)]
    [InlineData("Local flag#=.6\nIf flag Then Return 42 Else Return 1", 42)]
    [InlineData("Local x=0\nFor i=1 To 0 Step 3e9\nx=42\nNext\nReturn x", 42)]
    [InlineData("Local x=0\nFor i=1 To 5 Step \"2tail\"\nx=x+i\nNext\nReturn x", 9)]
    [InlineData("Local x=0\nFor i=1 To 2 Step (\"a\"=\"a\")\nx=x+i\nNext\nReturn x", 3)]
    public void ExecutesNestedLegacyControls(string body, int expected) => Assert.Equal(expected, Execute(body));

    [Theory]
    [InlineData("1 Or 2 And 4", 0)]
    [InlineData("Not 1 And 0", 1)]
    [InlineData("1<2 And 3>=3", 1)]
    [InlineData("2=<2 And 2=>1 And 1><2", 1)]
    [InlineData("2^3^2", 64)]
    [InlineData("-2^2", 4)]
    [InlineData("1+2 Shl 2*2", 33)]
    [InlineData("-1 Shr 31", 1)]
    [InlineData("-8 Sar 2", -2)]
    [InlineData("7 Mod 3", 1)]
    [InlineData("\"a\"<\"b\"", 1)]
    [InlineData("\"2\"=2", 1)]
    public void OperatorsUseLegacyPrecedenceAndTypes(string expression, int expected) => Assert.Equal(expected, Execute($"Return {expression}"));

    [Fact]
    public void BitwiseOperandsAreEagerAndSelectValueIsEvaluatedOnce() => Assert.Equal(42, Execute("Local x=0\nx=0 And Touch()\nSelect Touch()\nCase 1,2\nReturn count*21\nEndSelect", "Global count=0\nFunction Touch()\ncount=count+1\nReturn count\nEnd Function"));

    [Theory]
    [InlineData("Exit")]
    [InlineData("Print 1 And Not 0")]
    [InlineData("Print Not Not 0")]
    [InlineData("Print \"2\"^2")]
    [InlineData("Select 1\nCase 1\nExit\nEndSelect")]
    [InlineData("If 1 Then\nPrint 1")]
    [InlineData("While 1\nPrint 1")]
    [InlineData("Repeat\nPrint 1")]
    [InlineData("For i=1 To 2 Step i\nNext")]
    [InlineData("For i$=1 To 2\nNext")]
    [InlineData("Select 1\nDefault\nCase 1\nEndSelect")]
    [InlineData("While 1\nFunction G()\nEnd Function\nWend")]
    [InlineData("If 1 Then\n<?cs module public static class X {} ?>\nEndIf")]
    public void MalformedOrInvalidControlsProduceBasicDiagnostic(string source) => Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "invalid.bb"));

    private static int Execute(string body, string prefix = "")
    {
        var code = BasicCompiler.Compile($"{prefix}\nFunction TestValue()\n{body}\nEnd Function", "controls.bb");
        var tree = CSharpSyntaxTree.ParseText(code);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(Guid.NewGuid().ToString("N"), [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + code);
        stream.Position = 0;
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), true);
        try { return (int)context.LoadFromStream(stream).GetType("BB")!.GetMethod("testvalue")!.Invoke(null, null)!; }
        finally { context.Unload(); }
    }
}
