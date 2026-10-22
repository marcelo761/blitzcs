using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class FixedVectorCompilerTests
{
    [Theory]
    [InlineData("Global v%[6]\nv[6]=42\nGlobal result=v[6]", 42)]
    [InlineData("Global v%[2.8]\nv[3]=42\nGlobal result=v[3]", 42)]
    [InlineData("Global v%[2.5]\nv[2]=42\nGlobal result=v[2]", 42)]
    [InlineData("Local v%[1+2*3]\nv[7]=42\nGlobal result=v[7]", 42)]
    [InlineData("If 0 Then\nLocal v%[1]\nEndIf\nGlobal result=v[1]", 0)]
    [InlineData("Type Holder\nField v%[1]\nEnd Type\np.Holder=New Holder\nGlobal result=0\nFor p\\v[0]=1 To 3\nresult=result+p\\v[0]\nNext", 6)]
    public void FixedVectorsPreserveBoundsAndScopeInitialization(string source, int expected) =>
        ArrayCompilerTests.WithAssembly(source, a => Assert.Equal(expected, a.GetType("BB")!.GetField("result")!.GetValue(null)));

    [Theory]
    [InlineData("Global v%[-1]")]
    [InlineData("Global v%[1,2]")]
    [InlineData("Global v%[n]")]
    [InlineData("Global v%[1]\nPrint v[2]")]
    [InlineData("Global v%[1]\nPrint v[-1]")]
    [InlineData("Global v%[1]\nv=1")]
    [InlineData("Global v%[1], other%[1]\nv=other")]
    [InlineData("Global v%[1]\nPrint v+1")]
    [InlineData("Global v%[1]\nPrint v")]
    [InlineData("Global v%[1]\nPrint CS::System.Math.Abs(v)")]
    [InlineData("Global v%[1]\nv$(0)=\"text\"")]
    public void InvalidVectorsProduceLanguageDiagnostics(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "fixed-vectors.bb"));
}
