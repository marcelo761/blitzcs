using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class ExternalObjectAdapterTests
{
    public static int TakeObject(object value) => value is int n ? n : 1;
    public static int TakeInteger(int value) => value;

    [Fact]
    public void ExplicitObjectAdapterRetainsHistoricalIntegerSignatureAndRealObjectExpression()
    {
        var binding = new ExternalFunction("NativeCall", typeof(ExternalObjectAdapterTests).GetMethod(nameof(TakeObject))!, false, [0]);
        Assert.Equal(ExternalValueType.Integer, Assert.Single(binding.Signature.ParameterTypes));
        var signature = new ExternalFunctionSignature("NativeCall", ExternalValueType.Integer, [ExternalValueType.Integer]) { DataPointerParameters = new[] { 0 } };
        var registry = ExternalFunctionRegistry.FromDeclarations([signature], [binding]);
        var code = BasicCompiler.Compile("Type Item\nField value%\nEnd Type\nLocal x.Item=New Item\nPrint NativeCall(x)\nPrint NativeCall(7)", "objects.bb", registry);
        Assert.Contains("@TakeObject(", code);
    }

    [Theory]
    [InlineData("NativeCall 1.5")]
    [InlineData("NativeCall \"text\"")]
    public void ExplicitObjectAdapterDoesNotPermitFloatOrStringPointerCoercion(string source)
    {
        var binding = new ExternalFunction("NativeCall", typeof(ExternalObjectAdapterTests).GetMethod(nameof(TakeObject))!, false, [0]);
        var signature = new ExternalFunctionSignature("NativeCall", ExternalValueType.Integer, [ExternalValueType.Integer]) { DataPointerParameters = new[] { 0 } };
        var registry = ExternalFunctionRegistry.FromDeclarations([signature], [binding]);
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "invalid.bb", registry));
    }

    [Fact]
    public void ObjectAdapterRejectsDuplicateIndices() =>
        Assert.Throws<ArgumentException>(() => new ExternalFunction("NativeCall", typeof(ExternalObjectAdapterTests).GetMethod(nameof(TakeObject))!, false, [0, 0]));

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ObjectAdapterRejectsOutOfRangeIndices(int index) =>
        Assert.Throws<ArgumentException>(() => new ExternalFunction("NativeCall", typeof(ExternalObjectAdapterTests).GetMethod(nameof(TakeObject))!, false, [index]));

    [Fact]
    public void ObjectParametersRequireExplicitOptIn() =>
        Assert.Throws<ArgumentException>(() => new ExternalFunction("NativeCall", typeof(ExternalObjectAdapterTests).GetMethod(nameof(TakeObject))!));

    [Fact]
    public void AdapterIndexMustIdentifyActualSystemObjectParameter() =>
        Assert.Throws<ArgumentException>(() => new ExternalFunction("NativeCall", typeof(ExternalObjectAdapterTests).GetMethod(nameof(TakeInteger))!, false, [0]));
}
