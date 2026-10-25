using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyObjectAdapterTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-object-adapter-").FullName;

    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name); File.WriteAllText(path, text); return path;
    }

    private string Catalog(string declaration = "buffer*", bool adapter = true, int[]? indices = null)
    {
        DotNetEmitter.Emit("""
            public static class Provider {
                public static void Call(object data) {
                    if (data is int bank) { System.Console.WriteLine(bank); return; }
                    if (data is null) { System.Console.WriteLine("null"); return; }
                    var field=data.GetType().GetField("value", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (field is null || field.FieldType != typeof(int)) throw new System.ArgumentException("Expected integer value field");
                    field.SetValue(data, 73);
                }
                public static void Main() {}
            }
            """, Path.Combine(directory, "Provider.dll"), "Provider");
        Write("native.decls", $".lib \"native.dll\"\nNativeCall({declaration})\n");
        return Write("catalog.json", JsonSerializer.Serialize(new {
            declarations = new[] { "native.decls" },
            functions = new[] { new {
                name = "NativeCall", parameters = new[] { "Integer" }, returnType = "Void",
                legacyAdapter = adapter, objectAdapterParameters = indices ?? [0],
                assembly = "Provider.dll", type = "Provider", method = "Call"
            }}
        }));
    }

    private (int Code, string Error, string OutputPath) Build(string catalog, string source)
    {
        var input = Write("Main.bb", source);
        var output = Path.Combine(directory, "program.dll");
        using var errors = new StringWriter();
        var code = CliApplication.Run(["build", input, "-o", output, "--externals", catalog], new StringWriter(), errors);
        return (code, errors.ToString(), output);
    }

    [Fact]
    public async Task ExplicitObjectAdapterWritesBackToTheActualBasicObjectAndAcceptsBoxedBankTokens()
    {
        var result = Build(Catalog(), "Type Item\nField value%\nEnd Type\nLocal item.Item=New Item\nNativeCall item\nPrint item\\value\nNativeCall 123\nNativeCall Null\n");
        Assert.True(result.Code == 0, result.Error);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(result.OutputPath);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Equal("73\n123\nnull\n", (await stdout).Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("buffer%", true, 0)]
    [InlineData("buffer*", false, 0)]
    [InlineData("buffer*", true, -1)]
    [InlineData("buffer*", true, 1)]
    public void ObjectAdapterMetadataCannotEscapeHistoricalPointerContracts(string declaration, bool adapter, int index)
    {
        var result = Build(Catalog(declaration, adapter, [index]), "NativeCall 7");
        Assert.Equal(1, result.Code);
        Assert.Contains("objectAdapterParameters", result.Error);
        Assert.False(File.Exists(result.OutputPath));
    }

    [Fact]
    public void ObjectAdapterMetadataRequiresAnActualHistoricalDeclaration()
    {
        var path = Catalog();
        var catalog = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        catalog.Remove("declarations");
        File.WriteAllText(path, catalog.ToJsonString());
        var result = Build(path, "NativeCall 7");
        Assert.Equal(1, result.Code);
        Assert.Contains("objectAdapterParameters", result.Error);
        Assert.False(File.Exists(result.OutputPath));
    }

    [Fact]
    public void ObjectAdapterMetadataRejectsDuplicateIndices()
    {
        var result = Build(Catalog(indices: [0, 0]), "NativeCall 7");
        Assert.Equal(1, result.Code);
        Assert.Contains("objectAdapterParameters", result.Error);
        Assert.False(File.Exists(result.OutputPath));
    }

    [Fact]
    public void UnmarkedSystemObjectMethodStillCannotBindAsHistoricalInteger()
    {
        var result = Build(Catalog(indices: []), "NativeCall 7");
        Assert.Equal(1, result.Code);
        Assert.Contains("System.Object", result.Error);
        Assert.False(File.Exists(result.OutputPath));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
