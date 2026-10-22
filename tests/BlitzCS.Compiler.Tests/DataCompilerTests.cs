using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class DataCompilerTests
{
    [Theory]
    [InlineData("Local value%=1\nData value")]
    [InlineData("Data 1\nRestore missing")]
    [InlineData("Data 1\n.ReadHere\n.ReadHere")]
    [InlineData("Const value%=1\nData 2\nRead value")]
    [InlineData("Type Record\nEnd Type\nLocal value.Record=New Record\nData 1\nRead value")]
    [InlineData("Function F%()\nData 1\nEnd Function")]
    public void InvalidDataProgramsHaveBasicDiagnostics(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "invalid-data.bb"));
}
