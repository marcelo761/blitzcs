using System.Runtime.Loader;
using BlitzCS.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public class ArrayCompilerTests
{
    [Theory]
    [InlineData("Dim a(2)\na(2)=42\nGlobal result=a(0)+a(2)", 42)]
    [InlineData("Dim a#(2)\na(1.5)=41.6\nGlobal result=a(2)", 42)]
    [InlineData("Dim a$(1)\na(1)=\"42\"\nGlobal result=Int(a(0)+a(1))", 42)]
    [InlineData("Dim a(2,3,4)\na(2,3,4)=42\nGlobal result=a(2,3,4)+a(0,0,0)", 42)]
    [InlineData("Dim a#(1)\na(0)=42\nDim a(2)\nGlobal result=Int(a(0))+42", 42)]
    [InlineData("If 1 Then\nDim a(0)\nEndIf\na(0)=42\nGlobal result=a(0)", 42)]
    [InlineData("Dim a(1)\na(1)=42\nResize()\nGlobal result=a(1)+42\nFunction Resize()\nDim a(3)\nEnd Function", 42)]
    [InlineData("Dim a(1.6)\na(2)=42\nGlobal result=a(2)", 42)]
    [InlineData("Global result=0\nDim a(0)\nFor a(0)=1 To 3\nresult=result+a(0)\nNext\nresult=result+a(0)", 10)]
    [InlineData("Global result=0\nDim a(1)\nLocal p=0\nFor a(p)=1 To 2\np=1\nNext\nresult=a(0)*10+a(1)", 13)]
    [InlineData("Global result=0\nDim a#(0)\nFor a(0)=1 To 2 Step .5\nresult=result+1\nNext", 3)]
    public void TypedInclusiveArraysExecute(string source, int expected) => WithAssembly(source, assembly => Assert.Equal(expected, assembly.GetType("BB")!.GetField("result")!.GetValue(null)));

    [Theory]
    [InlineData("3", "i And 3", "0")]
    [InlineData("3,3", "i And 3,i And 3", "0,0")]
    [InlineData("3,3,3,3,3", "i And 3,i And 3,i And 3,i And 3,i And 3", "0,0,0,0,0")]
    public void RepeatedArrayAccessDoesNotAllocateIndexBuffers(string bounds, string indices, string zero)
    {
        var source = $$"""
            <?cs module internal static class AllocationProbe { internal static long Start; } ?>
            Global result=0, allocated=0
            Dim values({{bounds}})
            values({{zero}})=1
            result=values({{zero}})
            <?cs AllocationProbe.Start = System.GC.GetAllocatedBytesForCurrentThread(); ?>
            For i=0 To 99999
                values({{indices}})=i
                result=values({{indices}})
            Next
            <?cs BB.allocated = checked((int)(System.GC.GetAllocatedBytesForCurrentThread() - AllocationProbe.Start)); ?>
            """;
        WithAssembly(source, assembly =>
        {
            var globals = assembly.GetType("BB")!;
            Assert.Equal(99999, globals.GetField("result")!.GetValue(null));
            Assert.Equal(0, globals.GetField("allocated")!.GetValue(null));
        });
    }

    [Fact]
    public void ArrayIndicesEvaluateOnceFromLeftToRightBeforeTheAssignedValue()
    {
        const string source = """
            Global sequence=0, result=0
            Dim values(1,2)
            values(Index(),Index())=Value()
            result=sequence*100+values(1,2)
            Function Index()
                sequence=sequence+1
                Return sequence
            End Function
            Function Value()
                sequence=sequence+1
                Return 42
            End Function
            """;
        WithAssembly(source, assembly => Assert.Equal(342, assembly.GetType("BB")!.GetField("result")!.GetValue(null)));
    }

    [Theory]
    [InlineData("Dim a()")]
    [InlineData("Dim a$(0)\nFor a(0)=1 To 2\nNext")]
    [InlineData("Dim a(1)\nDim a(1,2)")]
    [InlineData("Dim a#(1)\nDim a$(1)")]
    [InlineData("Dim a(1)\nPrint a(0,1)")]
    [InlineData("Dim a#(1)\nPrint a$(0)")]
    [InlineData("Print a(0)\nDim a(1)")]
    [InlineData("Function F()\nPrint a(0)\nEnd Function\nDim a(1)")]
    [InlineData("Function F()\nDim a(1)\nEnd Function")]
    [InlineData("Dim a(1)\nFunction F()\nLocal a=0\nReturn a(0)\nEnd Function")]
    [InlineData("a=1\nDim a(1)")]
    [InlineData("Dim a(1)\nPrint a")]
    public void InvalidArrayProgramsHaveBasicDiagnostics(string source) => Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "invalid-array.bb"));

    [Theory]
    [InlineData("Dim a(-1)")]
    [InlineData("Dim a(2147483647)")]
    [InlineData("Dim a(65535,65535)")]
    [InlineData("Dim a(1,1)\na(2,0)=1")]
    [InlineData("Dim a(1)\nPrint a(-1)")]
    [InlineData("If 0 Then\nDim a(1)\nEndIf\nPrint a(0)")]
    [InlineData("Dim a(1)\na(0)=1\nDim a(a(0))")]
    public void UnsafeArrayOperationsReportNameAndBasicLocation(string source)
    {
        WithAssembly(source, _ => { }, "array-guards.bb", error =>
        {
            Assert.Contains("a", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("array-guards.bb(", error.Message);
        });
    }

    internal static void WithAssembly(string source, Action<System.Reflection.Assembly> assert, string path = "arrays.bb", Action<Exception>? assertError = null)
    {
        var code = BasicCompiler.Compile(source, path);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(code)], refs, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + code);
        stream.Position = 0;
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            if (assertError is null) { assembly.EntryPoint!.Invoke(null, null); assert(assembly); }
            else { var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => assembly.EntryPoint!.Invoke(null, null)); assertError(error.InnerException!); }
        }
        finally { context.Unload(); }
    }
}
