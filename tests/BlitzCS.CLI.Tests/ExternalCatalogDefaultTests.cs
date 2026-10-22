using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ExternalCatalogDefaultTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-defaults-" + Guid.NewGuid());
    public ExternalCatalogDefaultTests() => Directory.CreateDirectory(directory);

    private string Catalog(string parameters, string defaults, string? binding = null)
    {
        var path = Path.Combine(directory, "catalog.json");
        File.WriteAllText(path, "{\"functions\":[{\"name\":\"NativeValue\",\"returnType\":\"Integer\",\"parameters\":" +
            parameters + ",\"defaults\":" + defaults + binding + "}]}");
        return path;
    }

    [Theory]
    [InlineData("[\"Integer\"]", "[null]")]
    [InlineData("[\"Integer\"]", "[1,2]")]
    [InlineData("[\"Integer\"]", "[1.5]")]
    [InlineData("[\"Integer\"]", "[2147483648]")]
    [InlineData("[\"Integer\"]", "[true]")]
    [InlineData("[\"Float\"]", "[1e100]")]
    [InlineData("[\"Float\"]", "[\"1.5\"]")]
    [InlineData("[\"String\"]", "[1]")]
    [InlineData("[\"Integer\"]", "null")]
    [InlineData("[\"Integer\"]", "{}")]
    public void InvalidCatalogDefaultsAreRejectedBeforeWritingOutput(string parameters, string defaults)
    {
        var source = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "program.dll");
        File.WriteAllText(source, "Print NativeValue(1)");
        using var errors = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", Catalog(parameters, defaults)], TextWriter.Null, errors));
        Assert.Contains("External catalog 'defaults'", errors.ToString());
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void UnboundDefaultsValidateButBuildCannotCreateAnAssembly()
    {
        var source = Path.Combine(directory, "declaration.bb");
        var output = Path.Combine(directory, "program.dll");
        var catalog = Catalog("[\"Integer\",\"Float\",\"String\"]", "[1.5,\"text\"]");
        File.WriteAllText(source, "Print 1\nPrint NativeValue(2)");
        using var messages = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["check-semantics", source, "--externals", catalog], messages, errors));
        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors));
        Assert.Contains("declaration.bb(2,", errors.ToString());
        Assert.Contains("no implementation", errors.ToString());
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task DeclaredDefaultsBindToRealMethodsAndInteropKeepsClrDefaults()
    {
        var assembly = Path.Combine(directory, "DefaultFixture.dll");
        DotNetEmitter.Emit("""
            public static class DefaultFixture {
                public static int Value(int n, int delta=4, float scale=2, string text="clr") => n+delta+(int)scale+text.Length;
                public static void Main() {}
            }
            """, assembly, "DefaultFixture");
        var binding = ",\"assembly\":" + JsonSerializer.Serialize(assembly) + ",\"type\":\"DefaultFixture\",\"method\":\"Value\"";
        var catalog = Catalog("[\"Integer\",\"Integer\",\"Float\",\"String\"]", "[7,1.5,\"bb\"]", binding);
        var source = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "out", "program.dll");
        File.WriteAllText(source, """
            Function BasicCall%(n%)
                Return NativeValue(n)
            End Function
            Print NativeValue(2)
            <?cs
            System.Console.WriteLine(DefaultFixture.Value(2));
            System.Console.WriteLine(BB.basiccall(2));
            ?>
            Print NativeValue(2,3,4.5,"x")
            """);
        using var errors = new StringWriter();
        Assert.True(CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors) == 0, errors.ToString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Join(Environment.NewLine, "12", "11", "12", "10", ""), await stdout);
        Assert.Equal("", await stderr);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
