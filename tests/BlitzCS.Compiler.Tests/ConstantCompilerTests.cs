using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class ConstantCompilerTests
{
    [Theory]
    [InlineData("Const A%=6, B%=A*7\nGlobal result=B", 42)]
    [InlineData("Global result=ANSWER\nConst ANSWER%=42", 42)]
    [InlineData("Type Holder\nField slots%[LIMIT]\nEnd Type\nConst LIMIT%=2\np.Holder=New Holder\np\\slots[LIMIT]=42\nGlobal result=p\\slots[LIMIT]", 42)]
    [InlineData("Const A%=42\nGlobal result=F(1)\nFunction F%(a%)\nReturn a+1\nEnd Function", 2)]
    [InlineData("Const text$=\"4\"+\"2\"\nGlobal result=Int(text)", 42)]
    [InlineData("Const n%=2.5\nGlobal result=n", 2)]
    public void ConstantsFoldBeforeTypesAndMain(string source, int expected) =>
        ArrayCompilerTests.WithAssembly(source, a => Assert.Equal(expected, a.GetType("BB")!.GetField("result")!.GetValue(null)));

    [Theory]
    [InlineData("Const A")]
    [InlineData("Const A[1]=1")]
    [InlineData("Const A=B+1\nConst B=2")]
    [InlineData("Const A=1,A=2")]
    [InlineData("Const A=1\nA=2")]
    [InlineData("Const A=1\nFor A=1 To 3\nNext")]
    [InlineData("Const A=1\nGlobal A=2")]
    [InlineData("Const A=1\nDim A(1)")]
    [InlineData("Const A=F()\nFunction F%()\nReturn 1\nEnd Function")]
    [InlineData("Const A=CS::System.Math.Abs(-1)")]
    [InlineData("Const A=1\nPrint A$")]
    public void InvalidConstantsProduceSourceDiagnostics(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "constants.bb"));
}
