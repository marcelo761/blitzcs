using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ExternalFallbackSafetyTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-fallback-safety-").FullName;

    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private void Provider() => DotNetEmitter.Emit("""
        public static class Provider {
            public static int Command(int x) { System.Console.WriteLine("side:" + x); return 123; }
            public static int Value(int x) => x;
            public static float FloatValue(int x) => x;
            public static void Main() {}
        }
        """, Path.Combine(directory, "Provider.dll"), "Provider");

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task ManagedIntegerResultCanBeDiscardedWhileTheActualCommandSideEffectExecutes()
    {
        Provider();
        Write("api.decls", ".lib \"native.dll\"\nNativeCommand(x%=7)\n");
        Write("fallback.json", """
            {"functions":[{"name":"NativeCommand","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Command"}]}
            """);
        var catalog = Write("project.json", """{"declarations":["api.decls"],"fallbackProfiles":["fallback.json"]}""");
        var source = Write("Main.bb", "NativeCommand\nPrint 9\n");
        var target = Path.Combine(directory, "out", "program.dll");

        var result = Run("build", source, "-o", target, "--externals", catalog);

        Assert.True(result.Code == 0, result.Error);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await error);
        Assert.Equal("side:7\n9\n", (await output).Replace("\r\n", "\n"));
    }

    [Fact]
    public void IncompatibleFallbackDoesNotHideACompatibleSiblingContract()
    {
        Provider();
        Write("base.json", """{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"]},{"name":"Bar","returnType":"Integer","parameters":["Integer"]}]}""");
        Write("fallback.json", """
            {"functions":[{"name":"Foo","returnType":"Float","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"FloatValue"},{"name":"Bar","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Value"}]}
            """);
        var catalog = Write("project.json", """{"profiles":["base.json"],"fallbackProfiles":["fallback.json"]}""");
        var source = Write("Main.bb", "Print Foo(1)\nPrint Bar(2)\n");

        var result = Run("audit-dependencies", source, "--externals", catalog);

        Assert.Equal("", result.Error);
        using var report = JsonDocument.Parse(result.Output);
        var dependencies = report.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Equal(new[] { "abi_incompatible", "bound" }, dependencies.Select(d => d.GetProperty("status").GetString()));
        Assert.Equal(1, result.Code);
    }

    [Fact]
    public void ManagedFallbackCannotShareHandlesWithASelectedBinaryProviderForTheSameLibrary()
    {
        Write("api.decls", ".lib \"native.dll\"\nCreateHandle%()\nUseHandle(handle%)\n");
        Write("fallback.json", """
            {"functions":[{"name":"UseHandle","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Command"}]}
            """);
        var catalog = Write("project.json", """
            {"declarations":["api.decls"],"functions":[{"name":"CreateHandle","returnType":"Integer","parameters":[],"binary":{"library":"native.dll","export":"CreateHandle","convention":"stdcall"}}],"fallbackProfiles":["fallback.json"]}
            """);

        // Composition must reject a mixed resource owner before inspecting either executable provider.
        var document = ExternalCatalogDocument.Read(catalog);
        var function = document.Root.GetProperty("functions").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "UseHandle");

        Assert.Equal("abi_incompatible", function.GetProperty("resolutionStatus").GetString());
        Assert.False(function.TryGetProperty("method", out _));
        Assert.Contains("ownership", function.GetProperty("bindingFailure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompatibleFallbackAfterARejectedCandidateClearsTheEarlierFailure()
    {
        Provider();
        Write("base.json", """{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"]}]}""");
        Write("bad.json", """{"functions":[{"name":"Foo","returnType":"Float","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"FloatValue"}]}""");
        Write("good.json", """{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Value"}]}""");
        var catalog = Write("project.json", """{"profiles":["base.json"],"fallbackProfiles":["bad.json","good.json"]}""");
        var source = Write("Main.bb", "Print Foo(1)\n");

        var result = Run("audit-dependencies", source, "--externals", catalog);

        Assert.Equal("", result.Error);
        using var report = JsonDocument.Parse(result.Output);
        var dependency = Assert.Single(report.RootElement.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("bound", dependency.GetProperty("status").GetString());
        Assert.True(dependency.GetProperty("usableExecutableBinding").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dependency.GetProperty("reason").ValueKind);
        Assert.Equal(0, result.Code);
    }

    [Fact]
    public void FallbackWithIncompatibleClrReturnDoesNotPreventSelectingALaterCompatibleProvider()
    {
        Provider();
        Write("base.json", """{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"]}]}""");
        Write("bad.json", """{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"FloatValue"}]}""");
        Write("good.json", """{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Value"}]}""");
        var catalog = Write("project.json", """{"profiles":["base.json"],"fallbackProfiles":["bad.json","good.json"]}""");
        var source = Write("Main.bb", "Print Foo(1)\n");

        var result = Run("audit-dependencies", source, "--externals", catalog);

        Assert.Equal("", result.Error);
        using var report = JsonDocument.Parse(result.Output);
        var dependency = Assert.Single(report.RootElement.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("bound", dependency.GetProperty("status").GetString());
        Assert.Equal("Value", dependency.GetProperty("bindingMethod").GetString());
        Assert.Equal(0, result.Code);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
