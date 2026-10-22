using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class ExternalFunctionTests
{
    private static ExternalFunction Function(string name = "nativeabs") =>
        new(name, typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!);
    private static ExternalFunctionRegistry Registry() => new([Function()]);

    [Fact]
    public void EmitsDirectTypedCallAndLegacyConversion()
    {
        var code = BasicCompiler.Compile("Global result=NativeAbs(3.6)", "external.bb", Registry());
        Assert.Contains("global::@System.@Math.@Abs", code);
        Assert.Contains("__BlitzInteger", code);
        Assert.DoesNotContain("MethodInfo", code);
        Assert.DoesNotContain(".Invoke(", code);
    }

    [Fact]
    public void UnknownExternalCarriesOriginalSourcePosition()
    {
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile("Print MissingService(1)", "external.bb"));
        Assert.Equal("external.bb", error.SourcePath);
        Assert.Equal(1, error.Line);
        Assert.Contains("Unknown BASIC function or external symbol", error.Message);
    }

    [Theory]
    [InlineData("Global result=NativeAbs()")]
    [InlineData("Global result=NativeAbs(1,2)")]
    [InlineData("Global result=NativeAbs$(1)")]
    [InlineData("Type Node\nEnd Type\np.Node=New Node\nGlobal result=NativeAbs(p)")]
    [InlineData("Function NativeAbs%(n%)\nReturn n\nEnd Function")]
    [InlineData("Const NativeAbs%=1")]
    [InlineData("Global NativeAbs%=1")]
    public void InvalidExternalCallsAndCollisionsAreSourceDiagnostics(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "external.bb", Registry()));

    [Theory]
    [InlineData("Print")]
    [InlineData("bad.name")]
    [InlineData("x; injected")]
    [InlineData("_private")]
    public void InvalidNamesAreRejectedAtRegistration(string name) =>
        Assert.Throws<ArgumentException>(() => Function(name));

    [Fact]
    public void DuplicateNamesAreCaseInsensitive() =>
        Assert.Throws<ArgumentException>(() => new ExternalFunctionRegistry([Function("NativeAbs"), Function("NATIVEABS")]));

    [Fact]
    public void UnsupportedSignaturesAreRejectedAtRegistration()
    {
        Assert.Throws<ArgumentException>(() => new ExternalFunction("doubleabs", typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!));
        Assert.Throws<ArgumentException>(() => new ExternalFunction("tostring", typeof(object).GetMethod(nameof(ToString))!));
        Assert.Throws<ArgumentException>(() => new ExternalFunction("write", typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!));
    }

    [Fact]
    public void RegistrationSnapshotDoesNotChangeWithInputList()
    {
        var functions = new List<ExternalFunction> { Function() };
        var registry = new ExternalFunctionRegistry(functions);
        functions.Clear();
        Assert.Single(registry.Functions);
        Assert.Contains("@Abs", BasicCompiler.Compile("NativeAbs -42", "command.bb", registry));
    }

    [Fact]
    public void SignatureOnlyDeclarationsSupportSemanticValidationButCannotBuild()
    {
        var signature = new ExternalFunctionSignature("nativeabs", ExternalValueType.Integer,
            [ExternalValueType.Integer]);
        var registry = ExternalFunctionRegistry.FromSignatures([signature]);

        BasicCompiler.ValidateSemantics("Global result=NativeAbs(3.6)", "external.bb", registry);
        var error = Assert.Throws<BasicDiagnosticException>(() =>
            BasicCompiler.Compile("Global result=NativeAbs(3.6)", "external.bb", registry));
        Assert.Equal(1, error.Line);
        Assert.Contains("has a declared signature but no implementation", error.Message);
        Assert.NotEmpty(BasicCompiler.Compile("Print 1", "external.bb", registry));
    }

    [Fact]
    public void SignatureOnlyDeclarationsStillDiagnoseTypeAndArityErrors()
    {
        var registry = ExternalFunctionRegistry.FromSignatures([
            new ExternalFunctionSignature("nativeabs", ExternalValueType.Integer, [ExternalValueType.Integer])
        ]);

        Assert.Throws<BasicDiagnosticException>(() =>
            BasicCompiler.ValidateSemantics("Global result=NativeAbs()", "external.bb", registry));
        Assert.Throws<BasicDiagnosticException>(() =>
            BasicCompiler.ValidateSemantics("Global result=NativeAbs#(1)", "external.bb", registry));
    }

    [Fact]
    public void BuildReportsOnlyUsedUnboundCallsAtTheirOwnSourcePosition()
    {
        var registry = ExternalFunctionRegistry.FromDeclarations(
            [new ExternalFunctionSignature("nativeabs", ExternalValueType.Integer, [ExternalValueType.Integer]),
             new ExternalFunctionSignature("missing", ExternalValueType.Integer, [ExternalValueType.Integer]),
             new ExternalFunctionSignature("unused", ExternalValueType.Integer, [])],
            [Function()]);

        var error = Assert.Throws<BasicDiagnosticException>(() =>
            BasicCompiler.Compile("Print NativeAbs(1)\nPrint Missing(2)", "external.bb", registry));
        Assert.Equal(2, error.Line);
        Assert.Contains("missing", error.Message);
        Assert.NotEmpty(BasicCompiler.Compile("Print 1", "external.bb", registry));
    }
}
