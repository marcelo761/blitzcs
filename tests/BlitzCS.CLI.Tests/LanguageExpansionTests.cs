using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LanguageExpansionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-language-expansion-" + Guid.NewGuid());

    public LanguageExpansionTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("Print $FF", "255")]
    [InlineData("Print $80000000", "-2147483648")]
    [InlineData("Print $FFFFFFFF", "-1")]
    [InlineData("Print $100000000", "0")]
    [InlineData("Const MASK%=$FF\nPrint MASK", "255")]
    public async Task HexLiteralsUseSignedInt32Wraparound(string source, string expected) =>
        Assert.Equal(Lines(expected), await Execute(source));

    [Theory]
    [InlineData("Print Sum()", "8")]
    [InlineData("Print Sum(4)", "9")]
    [InlineData("Print Sum(4,2)", "6")]
    public async Task OptionalParametersSupplyDeclaredDefaults(string source, string expected)
    {
        var program = """
            Function Sum%(a%=3,b%=5)
              Return a%+b%
            End Function
            """ + "\n" + source;
        Assert.Equal(Lines(expected), await Execute(program));
    }

    [Fact]
    public async Task TypedOptionalDefaultsAreConvertedAndUsedForTrailingParameters()
    {
        var result = await Execute("""
            Function Format$(value%=7, ratio#=1.5, suffix$="!")
              Return Str(value%)+":"+Str(ratio#)+suffix$
            End Function
            Print Format$()
            Print Format$(2)
            Print Format$(2,3.25)
            """);

        Assert.Equal(Lines("7:1.5!\n2:1.5!\n2:3.25!"), result);
    }

    [Fact]
    public async Task FieldInitializersRemainAtOriginalAllocationDefaults()
    {
        var result = await Execute("""
            Type Child
              Field value%=99
            End Type
            Type Holder
              Field child.Child=New Child
              Field values%[1]
            End Type
            holder.Holder=New Holder
            Print holder\child=Null
            Print holder\values[1]
            """);

        Assert.Equal(Lines("1\n0"), result);
    }

    [Theory]
    [InlineData("Function Bad%(a%=1,b%)\nReturn 0\nEnd Function\nPrint Bad()")]
    [InlineData("Function Bad%(a%=1)\nReturn 0\nEnd Function\nPrint Bad(,2)")]
    [InlineData("Function Bad%(a%=1)\nReturn 0\nEnd Function\nPrint Bad(1,2)")]
    [InlineData("Function Bad%(a%=F())\nReturn 0\nEnd Function\nFunction F%()\nReturn 1\nEnd Function")]
    public async Task InvalidOptionalParameterFormsFailCompilation(string source)
    {
        var path = Path.Combine(directory, "invalid.bb");
        await File.WriteAllTextAsync(path, source);
        var errors = new StringWriter();
        var status = CliApplication.Run(["build", path, "-o", Path.Combine(directory, "invalid.dll")], new StringWriter(), errors);
        Assert.NotEqual(0, status);
    }

    [Theory]
    [InlineData("Show (1),2", "3")]
    [InlineData("Show (1+1),1", "3")]
    [InlineData("Show(1,2)", "3")]
    [InlineData("Ping:Ping", "9\n9")]
    [InlineData("If 1 Then Ping Else Ping", "9")]
    public async Task CommandArgumentsPreserveLegacyGroupingAndStatementBoundaries(string command, string expected)
    {
        var source = """
            Function Show%(a%,b%)
              Print a+b
              Return 0
            End Function
            Function Ping%()
              Print 9
              Return 0
            End Function
            """ + "\n" + command;
        Assert.Equal(Lines(expected), await Execute(source));
    }

    private async Task<string> Execute(string source)
    {
        var path = Path.Combine(directory, Guid.NewGuid() + ".bb");
        var assembly = Path.ChangeExtension(path, ".dll");
        await File.WriteAllTextAsync(path, source);
        var errors = new StringWriter();
        var result = CliApplication.Run(["build", path, "-o", assembly], new StringWriter(), errors);
        Assert.True(result == 0, errors.ToString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", assembly)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, stderr);
        Assert.Equal("", stderr);
        return stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Lines(string value) => value.Replace("\n", Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine;
    public void Dispose() => Directory.Delete(directory, true);
}
