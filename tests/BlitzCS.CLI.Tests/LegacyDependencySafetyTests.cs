using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyDependencySafetyTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-legacy-safety-").FullName;

    private string Write(string name, string source)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private void CreateProvider() => DotNetEmitter.Emit(
        "public static class Provider { public static int Call(int x) => x; public static void Main() {} }",
        Path.Combine(directory, "Provider.dll"), "Provider");

    [Fact]
    public void ProviderDefaultsCannotMakeHistoricalRequiredArgumentsOptional()
    {
        CreateProvider();
        Write("api.decls", ".lib \"native.dll\"\nFoo%(x%)\n");
        var catalog = Write("catalog.json", """
            {"declarations":["api.decls"],"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"defaults":[7],"assembly":"Provider.dll","type":"Provider","method":"Call"}]}
            """);
        var source = Write("Main.bb", "Print Foo()\n");
        var target = Path.Combine(directory, "out/program.dll");

        var result = Run("build", source, "-o", target, "--externals", catalog);

        Assert.Equal(1, result.Code);
        Assert.Contains("requires argument 1", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task HistoricalDefaultsRemainAuthoritativeWhenTheProviderDeclaresNone()
    {
        CreateProvider();
        Write("api.decls", ".lib \"native.dll\"\nFoo%(x%=7)\n");
        var catalog = Write("catalog.json", """
            {"declarations":["api.decls"],"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"defaults":[],"assembly":"Provider.dll","type":"Provider","method":"Call"}]}
            """);
        var source = Write("Main.bb", "Print Foo()\n");
        var target = Path.Combine(directory, "out/program.dll");

        var result = Run("build", source, "-o", target, "--externals", catalog);

        Assert.True(result.Code == 0, result.Error);
        Assert.True(File.Exists(target));
        await AssertOutput(target, "7" + Environment.NewLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ApplicationFunctionCanImplementAnUnboundBlankLibraryDeclaration(string library)
    {
        var declaration = Write("api.decls", $".lib \"{library}\"\nFoo%(x%)\n");
        var source = Write("Main.bb", "Print Foo(7)\nFunction Foo%(x%)\nReturn x\nEnd Function\n");
        var target = Path.Combine(directory, "out/program.dll");

        var result = Run("build", source, "-o", target, "--externals", declaration);

        Assert.True(result.Code == 0, result.Error);
        Assert.True(File.Exists(target));
        await AssertOutput(target, "7" + Environment.NewLine);
    }

    [Theory]
    [InlineData("native.dll", false)]
    [InlineData("", true)]
    public void ApplicationFunctionStillConflictsWithNativeContractsOrExplicitProviders(string library, bool bindProvider)
    {
        Write("api.decls", $".lib \"{library}\"\nFoo%(x%)\n");
        var catalog = bindProvider
            ? Write("catalog.json", """
                {"declarations":["api.decls"],"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Call"}]}
                """)
            : Path.Combine(directory, "api.decls");
        if (bindProvider) CreateProvider();
        var source = Write("Main.bb", "Print Foo(7)\nFunction Foo%(x%)\nReturn x\nEnd Function\n");
        var target = Path.Combine(directory, "out/program.dll");

        var result = Run("build", source, "-o", target, "--externals", catalog);

        Assert.Equal(1, result.Code);
        Assert.Contains("Duplicate function", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void LegacyAdapterFlagCannotTurnBankHandlesIntoRawBinaryPointers()
    {
        Write("api.decls", ".lib \"native.dll\"\nFoo%(bank*):\"Foo\"\n");
        var catalog = Write("catalog.json", """
            {"declarations":["api.decls"],"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"legacyAdapter":true,"binary":{"library":"native.dll","export":"Foo","convention":"stdcall"}}]}
            """);

        // Composition must reject unsafe forwarding before native library inspection or loading.
        var result = ExternalCatalogDocument.Read(catalog);
        var function = Assert.Single(result.Root.GetProperty("functions").EnumerateArray());

        Assert.True(function.TryGetProperty("resolutionStatus", out var status), function.ToString());
        Assert.Equal("adapter_missing", status.GetString());
        Assert.False(function.TryGetProperty("binary", out _));
        Assert.True(function.GetProperty("bindingCandidate").TryGetProperty("binary", out _));
        Assert.Contains("adapter", function.GetProperty("bindingFailure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertOutput(string assembly, string expected)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await error);
        Assert.Equal(expected, await output);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
