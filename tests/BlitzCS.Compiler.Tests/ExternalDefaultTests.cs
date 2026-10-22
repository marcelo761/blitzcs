using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class ExternalDefaultTests
{
    [Fact]
    public void DefaultsAndParameterTypesAreImmutableSnapshots()
    {
        var types = new[] { ExternalValueType.Integer, ExternalValueType.Float, ExternalValueType.String };
        object[] defaults = [3, 1.5f, "text"];
        var signature = new ExternalFunctionSignature("NativeValue", ExternalValueType.Integer, types, defaults);
        types[0] = ExternalValueType.String;
        defaults[0] = 9;
        Assert.Equal(ExternalValueType.Integer, signature.ParameterTypes[0]);
        Assert.Equal(new object[] { 3, 1.5f, "text" }, signature.DefaultValues);
        Assert.Throws<NotSupportedException>(() => ((IList<object>)signature.DefaultValues)[0] = 7);
    }

    public static IEnumerable<object[]> InvalidDefaults()
    {
        yield return [ExternalValueType.Integer, new object[] { 1, 2 }];
        yield return [ExternalValueType.Integer, new object[] { null! }];
        yield return [ExternalValueType.Integer, new object[] { 1L }];
        yield return [ExternalValueType.Integer, new object[] { 1f }];
        yield return [ExternalValueType.Float, new object[] { 1d }];
        yield return [ExternalValueType.Float, new object[] { 1 }];
        yield return [ExternalValueType.Float, new object[] { float.NaN }];
        yield return [ExternalValueType.Float, new object[] { float.PositiveInfinity }];
        yield return [ExternalValueType.Float, new object[] { float.NegativeInfinity }];
        yield return [ExternalValueType.String, new object[] { true }];
        yield return [ExternalValueType.String, new object[] { null! }];
    }

    [Theory]
    [MemberData(nameof(InvalidDefaults))]
    public void RejectsDefaultsThatAreNotFiniteExactlyTypedTrailingConstants(ExternalValueType type, object[] defaults) =>
        Assert.Throws<ArgumentException>(() => new ExternalFunctionSignature("NativeValue", ExternalValueType.Integer, [type], defaults));

    [Fact]
    public void SignatureDefaultsValidateWithoutBindingAndBuildReportsSourceLocation()
    {
        var registry = ExternalFunctionRegistry.FromSignatures([
            new ExternalFunctionSignature("NativeValue", ExternalValueType.Integer,
                [ExternalValueType.Integer, ExternalValueType.Integer, ExternalValueType.Float, ExternalValueType.String], [3, 1.5f, "text"])
        ]);
        BasicCompiler.ValidateSemantics("Print NativeValue(2)", "defaults.bb", registry);
        BasicCompiler.ValidateSemantics("NativeValue 2,4,2.5,\"override\"", "defaults.bb", registry);
        var required = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.ValidateSemantics("NativeValue", "defaults.bb", registry));
        Assert.Contains("requires argument 1", required.Message);
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Print 1\nPrint NativeValue(2)", "defaults.bb", registry));
        Assert.Equal("defaults.bb", error.SourcePath);
        Assert.Equal(2, error.Line);
        Assert.Contains("no implementation", error.Message);
    }

    [Fact]
    public void DeclaredDefaultsOverrideClrDefaultsAndOmissionPreservesFallback()
    {
        var binding = new ExternalFunction("NativeValue", typeof(ExternalDefaultMethods).GetMethod(nameof(ExternalDefaultMethods.Add))!);
        var types = new[] { ExternalValueType.Integer, ExternalValueType.Integer };
        var explicitRegistry = ExternalFunctionRegistry.FromDeclarations([
            new ExternalFunctionSignature("NativeValue", ExternalValueType.Integer, types, [7])
        ], [binding]);
        Assert.Contains("(2, 7)", BasicCompiler.Compile("Print NativeValue(2)", "defaults.bb", explicitRegistry));
        var fallback = ExternalFunctionRegistry.FromDeclarations([
            new ExternalFunctionSignature("NativeValue", ExternalValueType.Integer, types)
        ], [binding]);
        Assert.Contains("(2, 4)", BasicCompiler.Compile("Print NativeValue(2)", "defaults.bb", fallback));
        var required = ExternalFunctionRegistry.FromDeclarations([
            new ExternalFunctionSignature("NativeValue", ExternalValueType.Integer, types, [])
        ], [binding]);
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.ValidateSemantics("Print NativeValue(2)", "defaults.bb", required));
    }
}

public static class ExternalDefaultMethods
{
    public static int Add(int value, int delta = 4) => value + delta;
}
