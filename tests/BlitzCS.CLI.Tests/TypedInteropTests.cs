using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class TypedInteropTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-typed-" + Guid.NewGuid());

    public TypedInteropTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task AllPrimitiveTypesRoundTripAndShareStorage()
    {
        var source = """
            Global number%=20
            Global fraction#=1.5
            Global message$="Blitz"
            <?cs module
            public static class TypedBridge
            {
                public static int Number(int value) => BB.plus(value, 2);
                public static float Fraction(float value) => BB.scale(value, 2.0f);
                public static string Message(string value) => BB.decorate(value);
            }
            ?>
            number=CS::TypedBridge.Number(number)
            fraction=CS::TypedBridge.Fraction(fraction)
            message=CS::TypedBridge.Message(message)
            <?cs
            BB.number += 20;
            BB.fraction += 0.5f;
            BB.message += " C#";
            ?>
            Print number
            Print fraction
            Print message
            Function Plus%(value%, delta%)
                Return value+delta
            End Function
            Function Scale#(value#, factor#)
                Return value*factor
            End Function
            Function Decorate$(value$)
                Return "["+value+"]"
            End Function
            """;
        Assert.Equal(Lines("42", "3.5", "[Blitz] C#"), await Execute(source));
    }

    [Theory]
    [InlineData("Print 0.0001", "1.e-004")]
    [InlineData("Print 2+0.5", "2.5")]
    [InlineData("Print 5.0/2", "2.5")]
    [InlineData("Print 5/2", "2")]
    [InlineData("Print Int(2.5)\nPrint Int(3.5)\nPrint Int(-2.5)\nPrint Int(-3.5)", "2\n4\n-2\n-4")]
    [InlineData("n%=3.5\nPrint n", "4")]
    [InlineData("n%=Float(\"-3.5\")\nPrint n", "-4")]
    [InlineData("Print Int(\"  -42tail\")\nPrint Int(\"none\")", "-42\n0")]
    [InlineData("Print Int(\" +42.9suffix\")\nPrint Int(\"1e2\")", "42\n1")]
    [InlineData("Print Float(\"  +3.5tail\")\nPrint Float(\"none\")", "3.5\n0.0")]
    [InlineData("Print Str(42)\nPrint Str(2.0)\nPrint Str(1.2345678)", "42\n2.0\n1.23457")]
    [InlineData("Print Int 3.5\nPrint Float 2\nPrint Str 2.0", "4\n2.0\n2.0")]
    [InlineData("Print Int%(3.5)\nPrint Float#(2)\nPrint Str$(2.0)", "4\n2.0\n2.0")]
    [InlineData("Print \"x\"+42\nPrint 2.5+\"x\"", "x42\n2.5x")]
    [InlineData("Global a%\nGlobal b#\nGlobal c$\nPrint a\nPrint b\nPrint \"[\"+c+\"]\"\n", "0\n0.0\n[]")]
    [InlineData("Print GetFloat()\nPrint \"[\"+GetText()+\"]\"\nFunction GetFloat#()\nEnd Function\nFunction GetText$()\nEnd Function", "0.0\n[]")]
    public async Task PrimitiveSemanticsMatchSourceDerivedExpectations(string source, string expected)
    {
        Assert.Equal(Lines(expected.Split('\n')), await Execute(source));
    }

    [Fact]
    public async Task PreviouslyRejectedFloatGlobalNowCompiles()
    {
        Assert.Equal("", await Execute("Global n#=1"));
    }

    [Fact]
    public async Task BasicFunctionArgumentsAndReturnUseLegacyConversions()
    {
        var source = """
            Print AsInteger(3.5)
            Print AsFloat("2.5tail")
            Print AsText(2.0)
            Function AsInteger%(n%)
                Return n
            End Function
            Function AsFloat#(n#)
                Return n
            End Function
            Function AsText$(n$)
                Return n
            End Function
            """;
        Assert.Equal(Lines("4", "2.5", "2.0"), await Execute(source));
    }

    [Fact]
    public async Task FloatStorageAndArithmeticUseSinglePrecision()
    {
        var source = """
            Global value#=16777216.0
            value=value+1.0
            Global tenth#=0.1
            <?cs
            if (BB.value != 16777216.0f) throw new System.Exception("float arithmetic precision");
            if (System.BitConverter.SingleToInt32Bits(BB.tenth) != 1036831949) throw new System.Exception("literal precision");
            System.Func<float, float> callback = BB.identity;
            if (callback(BB.tenth) != 0.1f) throw new System.Exception("float callback signature");
            System.Console.WriteLine("Single verified");
            ?>
            Function Identity#(v#)
                Return v
            End Function
            """;
        Assert.Equal(Lines("Single verified"), await Execute(source));
    }

    [Fact]
    public async Task NullFromCSharpReadsAsEmptyBasicString()
    {
        var source = """
            Global text$="initial"
            <?cs BB.text = null; ?>
            Print "["+text+"]"
            <?cs module
            public static class Nil { public static string Get() => null; }
            ?>
            Print "["+CS::Nil.Get()+"]"
            """;
        Assert.Equal(Lines("[]", "[]"), await Execute(source));
    }

    [Fact]
    public async Task CompatibilityConversionsDoNotDependOnDotNetCurrentCulture()
    {
        var source = """
            <?cs System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR"); ?>
            Global fraction#=1.5
            Global text$=fraction
            Print fraction
            Print text
            Print Float("2.5tail")
            """;
        Assert.Equal(Lines("1.5", "1.5", "2.5"), await Execute(source));
    }

    [Fact]
    public async Task SafeIntegralReturnWideningFromPreviousBridgeIsPreserved()
    {
        var source = """
            <?cs module
            public static class Small
            {
                public static char Character() => 'A';
                public static short Short() => 42;
            }
            ?>
            Print CS::Small.Character()
            Print CS::Small.Short()
            """;
        Assert.Equal(Lines("65", "42"), await Execute(source));
    }

    [Fact]
    public async Task OverloadsResolveByStaticPrimitiveArgumentTypes()
    {
        var source = """
            <?cs module
            public static class Overloads
            {
                public static string Pick(int value) => "int";
                public static string Pick(float value) => "float";
                public static string Pick(string value) => "string";
                public static float Widen(float value) => value + 0.5f;
            }
            ?>
            Print CS::Overloads.Pick(42)
            Print CS::Overloads.Pick(1.5)
            Print CS::Overloads.Pick("text")
            Print CS::Overloads.Widen(2)
            """;
        Assert.Equal(Lines("int", "float", "string", "2.5"), await Execute(source));
    }

    [Fact]
    public async Task UnsupportedDotNetOverloadDoesNotHideCompatiblePrimitiveOverload()
    {
        var source = """
            <?cs module
            public static class Mixed
            {
                public static string Pick(long value) => "unsupported long";
                public static string Pick(float value) => "float";
            }
            ?>
            Print CS::Mixed.Pick(2)
            """;
        Assert.Equal(Lines("float"), await Execute(source));
    }

    [Fact]
    public async Task FrameworkPrimitiveMethodsBindWithoutModuleDefinitions()
    {
        var source = "Print CS::System.MathF.Sqrt(4.0)\nPrint CS::System.String.Concat(\"a\",\"b\")";
        Assert.Equal(Lines("2.0", "ab"), await Execute(source));
    }

    [Theory]
    [InlineData("public static bool Bad() => true;", "CS::BadMethods.Bad()")]
    [InlineData("public static double Bad() => 1.0;", "CS::BadMethods.Bad()")]
    [InlineData("public static void Bad() {}", "CS::BadMethods.Bad()")]
    [InlineData("public static int Bad(int x) => x;", "CS::BadMethods.Bad(1.5)")]
    [InlineData("public static int Bad(int x) => x;", "CS::BadMethods.Missing()")]
    [InlineData("public int Bad() => 1;", "CS::BadMethods.Bad()")]
    public void UnsupportedMethodsHaveMappedCompileTimeDiagnostics(string method, string call)
    {
        var source = $"<?cs module\npublic class BadMethods {{ {method} }}\n?>\nPrint {call}";
        var (code, errors, assembly) = Build(source);
        Assert.Equal(1, code);
        Assert.Contains("typed.bb(4,", errors);
        Assert.False(File.Exists(assembly));
    }

    [Fact]
    public void EquallyGoodOverloadsAreDiagnosedAsAmbiguous()
    {
        var source = """
            <?cs module
            public static class Choice
            {
                public static int Pick(int a,float b) => 1;
                public static int Pick(float a,int b) => 2;
            }
            ?>
            Print CS::Choice.Pick(1,1)
            """;
        var (code, errors, assembly) = Build(source);
        Assert.Equal(1, code);
        Assert.Contains("ambig", errors, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("typed.bb", errors);
        Assert.False(File.Exists(assembly));
    }

    private (int Code, string Errors, string Assembly) Build(string source)
    {
        var path = Path.Combine(directory, "typed.bb");
        var assembly = Path.Combine(directory, "typed.dll");
        File.WriteAllText(path, source);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        return (code, errors.ToString(), assembly);
    }

    private async Task<string> Execute(string source)
    {
        var (code, errors, assembly) = Build(source);
        Assert.True(code == 0, errors);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr);
        return await stdout;
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    public void Dispose() => Directory.Delete(directory, true);
}
