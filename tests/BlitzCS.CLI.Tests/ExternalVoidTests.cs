using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ExternalVoidTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-void-" + Guid.NewGuid());
    public ExternalVoidTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task BoundVoidCatalogBuildsAndExecutesDirectCommandWithDefaults()
    {
        var assembly = Path.Combine(directory, "VoidFixture.dll");
        DotNetEmitter.Emit("""
            public static class VoidFixture {
                public static void Write(string text, int count=1) {
                    for (int i=0; i<count; i++) System.Console.WriteLine(text);
                }
                public static void Main() {}
            }
            """, assembly, "VoidFixture");
        var catalog = Path.Combine(directory, "externals.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new {
            functions = new[] { new {
                name = "NativeWrite", assembly, type = "VoidFixture", method = "Write",
                returnType = "Void", parameters = new[] { "String", "Integer" }, defaults = new object[] { 2 }
            } }
        }));
        var source = WriteSource("NativeWrite \"hello\"");
        var output = Path.Combine(directory, "out", "program.dll");
        using var errors = new StringWriter();
        Assert.True(CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors) == 0, errors.ToString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal($"hello{Environment.NewLine}hello{Environment.NewLine}", await stdout);
        Assert.Equal("", await stderr);
    }

    [Fact]
    public void DeclarationOnlyVoidCatalogChecksButCannotBuildUsedCommand()
    {
        var source = WriteSource("NativeWrite \"hello\"");
        var catalog = WriteCatalog("""{"functions":[{"name":"NativeWrite","returnType":"Void","parameters":["String"]}]}""");
        using var errors = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["check-semantics", source, "--externals", catalog], TextWriter.Null, errors));
        var output = Path.Combine(directory, "program.dll");
        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors));
        Assert.Contains("has a declared signature but no implementation", errors.ToString());
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void VoidExpressionIsDiagnosedAtBasicSourceBeforeAssemblyEmission()
    {
        var source = WriteSource("Print 1\nPrint NativeWrite(\"hello\")");
        var catalog = WriteCatalog("""{"functions":[{"name":"NativeWrite","returnType":"Void","parameters":["String"]}]}""");
        using var errors = new StringWriter();
        var output = Path.Combine(directory, "program.dll");
        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors));
        Assert.Contains("main.bb(2,", errors.ToString());
        Assert.Contains("Void", errors.ToString());
        Assert.DoesNotContain("CS1503", errors.ToString());
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void CatalogRejectsVoidParameterTypes()
    {
        var source = WriteSource("NativeWrite 1");
        var catalog = WriteCatalog("""{"functions":[{"name":"NativeWrite","returnType":"Void","parameters":["Void"]}]}""");
        using var errors = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["check-semantics", source, "--externals", catalog], TextWriter.Null, errors));
        Assert.Contains("External parameter types must be Integer, Float or String", errors.ToString());
    }

    private string WriteSource(string text)
    {
        var path = Path.Combine(directory, "main.bb");
        File.WriteAllText(path, text);
        return path;
    }
    private string WriteCatalog(string text)
    {
        var path = Path.Combine(directory, "externals.json");
        File.WriteAllText(path, text);
        return path;
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
