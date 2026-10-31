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

    [Fact]
    public void FixedRankArrayAccessAvoidsPerAccessIndexArrayAllocations()
    {
        const string source = """
            Dim one%(0)
            Dim two%(0,0)
            Global check1%=0
            Global check2%=0
            <?cs module
            public static class Probe {
              public static long AllocatedBytes1;
              public static long AllocatedBytes2;
              public static int Checksum;
              public static int Run() {
                for (int i = 0; i < 32; i++) { BB.work1(); BB.work2(); }
                System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
                var before1 = System.GC.GetAllocatedBytesForCurrentThread();
                BB.work1();
                AllocatedBytes1 = System.GC.GetAllocatedBytesForCurrentThread() - before1;
                var before2 = System.GC.GetAllocatedBytesForCurrentThread();
                BB.work2();
                AllocatedBytes2 = System.GC.GetAllocatedBytesForCurrentThread() - before2;
                Checksum = BB.check1 + BB.check2;
                return 0;
              }
            }
            ?>
            CS::Probe.Run()
            Function work1()
              For i=1 To 100000
                one(0)=one(0)+1
              Next
              check1=one(0)
            End Function
            Function work2()
              For i=1 To 100000
                two(0,0)=two(0,0)+1
              Next
              check2=two(0,0)
            End Function
            """;

        WithReleaseAssembly(source, assembly =>
        {
            var probe = assembly.GetType("Probe")!;
            Assert.Equal(0L, probe.GetField("AllocatedBytes1")!.GetValue(null));
            Assert.Equal(0L, probe.GetField("AllocatedBytes2")!.GetValue(null));
            Assert.Equal(6_600_000, probe.GetField("Checksum")!.GetValue(null));
        });
    }

    [Fact]
    public void FixedRankArrayAccessKeepsIndexEvaluationOrderAndSingleEvaluation()
    {
        const string source = """
            Global calls%=0
            Global result%=0
            Dim one%(1)
            Dim two%(1,1)
            one(NextIndex(1))=42
            two(NextIndex(0),NextIndex(1))=7
            result=calls*1000+one(1)*10+two(0,1)
            Function NextIndex%(value%)
              calls=calls*10+value+1
              Return value
            End Function
            """;

        WithAssembly(source, assembly => Assert.Equal(212_427, assembly.GetType("BB")!.GetField("result")!.GetValue(null)));
    }

    [Theory]
    [InlineData("Dim a(1)\nPrint a(2)", "index 2 on axis 1 is outside 0..1.")]
    [InlineData("Dim a(1,1)\nPrint a(0,2)", "index 2 on axis 2 is outside 0..1.")]
    public void FixedRankBoundsFailuresKeepAxisDiagnosticAndBasicLocation(string source, string diagnostic)
    {
        WithAssembly(source, _ => { }, "rank-bounds.bb", error =>
        {
            Assert.Contains(diagnostic, error.Message);
            Assert.Contains("rank-bounds.bb(2,", error.Message);
        });
    }

    [Fact]
    public void GeneratedArrayAccessUsesFixedArityOverloadsAndKeepsHigherRankFallback()
    {
        var code = BasicCompiler.Compile("Dim one(0)\nDim two(0,0)\nDim three(0,0,0)\nPrint one(0)\nPrint two(0,0)\nPrint three(0,0,0)", "array-overloads.bb");

        Assert.Contains("__BlitzArrays.a0.Element(0, \"array-overloads.bb(", code);
        Assert.Contains("__BlitzArrays.a1.Element(0, 0, \"array-overloads.bb(", code);
        Assert.Contains("__BlitzArrays.a2.Element([0, 0, 0], \"array-overloads.bb(", code);
    }

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

    private static void WithReleaseAssembly(string source, Action<System.Reflection.Assembly> assert)
    {
        var code = BasicCompiler.Compile(source, "array-allocation.bb");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(code)], refs,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, checkOverflow: false));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + code);
        stream.Position = 0;
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            assembly.EntryPoint!.Invoke(null, null);
            assert(assembly);
        }
        finally { context.Unload(); }
    }
}
