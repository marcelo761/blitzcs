using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class LegacyPointerContractTests
{
    private const string ObjectSource = "Type MemoryStatus\nField length%\nEnd Type\nLocal m.MemoryStatus=New MemoryStatus\nNativeCall m\n";

    private static ExternalFunctionSignature Signature(ExternalValueType result) =>
        new("NativeCall", result, [ExternalValueType.Integer]) { DataPointerParameters = new[] { 0 } };

    [Theory]
    [InlineData(ExternalValueType.Void)]
    [InlineData(ExternalValueType.Integer)]
    public void UnboundPointerContractsAcceptCustomObjectDataForSemanticValidation(ExternalValueType result)
    {
        var registry = ExternalFunctionRegistry.FromSignatures([Signature(result)]);

        var calls = BasicCompiler.CheckSemantics(ObjectSource, "objects.bb", registry);

        var call = Assert.Single(calls);
        Assert.Equal("nativecall", call.Name);
        Assert.Equal("objects.bb", call.SourcePath);
        Assert.Equal(5, call.Line);
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(ObjectSource, "objects.bb", registry));
        Assert.Contains("no implementation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot convert", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ExternalValueType.Void)]
    [InlineData(ExternalValueType.Integer)]
    public void UnboundPointerContractsAlsoAcceptIntegerBankHandles(ExternalValueType result)
    {
        var registry = ExternalFunctionRegistry.FromSignatures([Signature(result)]);

        Assert.Single(BasicCompiler.CheckSemantics("Local bank%=7\nNativeCall bank\n", "banks.bb", registry));
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("NativeCall 7", "banks.bb", registry));
        Assert.Contains("no implementation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NativeCall 1.5")]
    [InlineData("NativeCall \"data\"")]
    public void PointerSlotsDoNotCoerceFloatsOrStringsToIntegerHandles(string source)
    {
        var registry = ExternalFunctionRegistry.FromSignatures([Signature(ExternalValueType.Void)]);

        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.ValidateSemantics(source, "invalid-pointer.bb", registry));
    }

    [Fact]
    public void OrdinaryIntegerSlotsStillRejectCustomObjects()
    {
        var registry = ExternalFunctionRegistry.FromSignatures([
            new ExternalFunctionSignature("NativeCall", ExternalValueType.Void, [ExternalValueType.Integer])
        ]);

        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.ValidateSemantics(ObjectSource, "ordinary.bb", registry));

        Assert.Contains("Cannot convert", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundIntegerMethodCannotReceiveCustomObjectDataWithoutAnExplicitAdapter()
    {
        var binding = new ExternalFunction("NativeCall", typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!);
        var registry = ExternalFunctionRegistry.FromDeclarations([Signature(ExternalValueType.Integer)], [binding]);

        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(ObjectSource, "bound.bb", registry));

        Assert.Equal("bound.bb", error.SourcePath);
        Assert.Equal(5, error.Line);
        Assert.Contains("object", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adapter", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot convert", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
