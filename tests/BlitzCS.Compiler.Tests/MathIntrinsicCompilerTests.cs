using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class MathIntrinsicCompilerTests
{
    [Fact]
    public void PiIsTheSinglePrecisionLanguageConstantInExpressions()
    {
        ArrayCompilerTests.WithAssembly("Global value#=Pi\nGlobal doubled#=2*pI", assembly =>
        {
            var globals = assembly.GetType("BB")!;
            Assert.Equal(unchecked((int)0x40490FDB), BitConverter.SingleToInt32Bits((float)globals.GetField("value")!.GetValue(null)!));
            Assert.Equal(2 * MathF.PI, globals.GetField("doubled")!.GetValue(null));
        }, "pi-original-contract.bb");
    }

    [Fact]
    public void AbsAndSgnExpressionsCompileInIntegerAndFloatContexts()
    {
        ArrayCompilerTests.WithAssembly("""
            Global integerAbs%=Abs(-4)
            Global floatAbs#=Abs(-4.5)
            Global integerSign%=Sgn(-4)
            Global floatSign#=Sgn(-4.5)
            Global integerMinAbs%=Abs($80000000)
            Global floatZeroAbs#=Abs(-0.0)
            """, assembly =>
        {
            var globals = assembly.GetType("BB")!;
            Assert.Equal(4, globals.GetField("integerabs")!.GetValue(null));
            Assert.Equal(4.5f, globals.GetField("floatabs")!.GetValue(null));
            Assert.Equal(-1, globals.GetField("integersign")!.GetValue(null));
            Assert.Equal(-1.0f, globals.GetField("floatsign")!.GetValue(null));
            Assert.Equal(int.MinValue, globals.GetField("integerminabs")!.GetValue(null));
            Assert.True(float.IsNegative((float)globals.GetField("floatzeroabs")!.GetValue(null)!));
            Assert.Equal(typeof(int), globals.GetField("integerabs")!.FieldType);
            Assert.Equal(typeof(float), globals.GetField("floatabs")!.FieldType);
        }, "math-intrinsics.bb");
    }
}
