using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class ExternalVoidTests
{
    private static ExternalFunctionRegistry Bound() => new([
        new ExternalFunction("nativewrite", typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!)
    ]);

    private static ExternalFunctionSignature Declaration() => new("nativewrite",
        ExternalValueType.Void, [ExternalValueType.String]);

    [Fact]
    public void StaticVoidMethodEmitsDirectCommandInvocation()
    {
        var registry = Bound();
        Assert.Equal("Void", registry.Functions[0].Signature.ReturnType.ToString());
        var code = BasicCompiler.Compile("NativeWrite \"hello\"\nNativeWrite(42)", "command.bb", registry);
        Assert.Contains("global::@System.@Console.@WriteLine(\"hello\");", code);
        Assert.DoesNotContain("__BlitzInteger(global::@System.@Console.@WriteLine", code);
    }

    [Theory]
    [InlineData("Print NativeWrite(\"x\")")]
    [InlineData("Global result=NativeWrite(\"x\")")]
    [InlineData("Global result#=NativeWrite(\"x\")")]
    [InlineData("Global result$=NativeWrite(\"x\")")]
    [InlineData("Print 1+NativeWrite(\"x\")")]
    [InlineData("Print \"a\"+NativeWrite(\"x\")")]
    [InlineData("Print -NativeWrite(\"x\")")]
    [InlineData("If NativeWrite(\"x\") Then Print 1")]
    [InlineData("While NativeWrite(\"x\")\nWend")]
    [InlineData("Print Int(NativeWrite(\"x\"))")]
    [InlineData("Print Float(NativeWrite(\"x\"))")]
    [InlineData("Print Str(NativeWrite(\"x\"))")]
    [InlineData("NativeWrite NativeWrite(\"x\")")]
    [InlineData("Print Take(NativeWrite(\"x\"))\nFunction Take(n)\nReturn n\nEnd Function")]
    [InlineData("Function Take()\nReturn NativeWrite(\"x\")\nEnd Function")]
    public void VoidCannotSupplyAnyScalarExpression(string source)
    {
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "void-use.bb", Bound()));
        Assert.Equal("void-use.bb", error.SourcePath);
        Assert.True(error.Line >= 1);
        Assert.Contains("Void", error.Message);
    }

    [Fact]
    public void VoidExpressionReportsItsOwnSourcePosition()
    {
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(
            "Print 1\nPrint NativeWrite(\"x\")", "void-location.bb", Bound()));
        Assert.Equal("void-location.bb", error.SourcePath);
        Assert.Equal(2, error.Line);
        Assert.True(error.Column > 1);
    }

    [Fact]
    public void DeclarationOnlyVoidCommandsValidateButUsedCommandsCannotBuild()
    {
        var registry = ExternalFunctionRegistry.FromSignatures([Declaration()]);
        BasicCompiler.ValidateSemantics("NativeWrite \"hello\"", "unbound.bb", registry);
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(
            "Print 1\nNativeWrite \"hello\"", "unbound.bb", registry));
        Assert.Equal(2, error.Line);
        Assert.Contains("has a declared signature but no implementation", error.Message);
        Assert.NotEmpty(BasicCompiler.Compile("Print 1", "unused.bb", registry));
    }

    [Fact]
    public void VoidIsRejectedAsAParameterType()
    {
        var type = ExternalValueType.Void;
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalFunctionSignature("invalid", type, [type]));
    }
}
