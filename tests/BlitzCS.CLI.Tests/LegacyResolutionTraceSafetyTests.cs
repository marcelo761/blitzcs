using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyResolutionTraceSafetyTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-trace-safety-").FullName;

    private string Write(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [LinuxFact]
    public void TraceThroughAHardLinkPreservesMainSourceBytes()
    {
        var source = Write("Main.bb", "; preserve this original source\nPrint 1\n");
        var original = File.ReadAllBytes(source);
        var trace = Path.Combine(directory, "trace.json");
        var start = new ProcessStartInfo("ln") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(trace);
        using (var process = Process.Start(start)!)
        {
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Hardlink creation timed out.");
            }
            Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        }

        var result = Run("audit-dependencies", source, "--trace-resolution", trace);

        Assert.Equal(original, File.ReadAllBytes(source));
        // Both rejection and safe atomic replacement preserve the source contract.
        Assert.Contains(result.Code, new[] { 0, 1 });
        if (result.Code == 0)
        {
            using var report = JsonDocument.Parse(File.ReadAllText(trace));
            Assert.Equal("dependency_audit", report.RootElement.GetProperty("stage").GetString());
        }
    }

    [Fact]
    public void PackageTraceCannotOverwriteAnAssetBeforePublishing()
    {
        var source = Write("Main.bb", "Print 1\n");
        var asset = Write("assets/data.json", "{\"player\":\"original\",\"score\":42}\n");
        var original = File.ReadAllBytes(asset);
        // An unrelated existing destination keeps a broken preflight from launching SDK publishing.
        var sentinel = Write("package/unowned.txt", "existing unrelated output");

        var result = Run("build", source, "-o", Path.GetDirectoryName(sentinel)!,
            "--assets", Path.GetDirectoryName(asset)!, "--trace-resolution", asset);

        Assert.Equal(original, File.ReadAllBytes(asset));
        Assert.Equal(1, result.Code);
        Assert.Contains("trace", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("existing unrelated output", File.ReadAllText(sentinel));
    }

    [Fact]
    public void TraceCannotOverwriteARejectedPointerProviderCandidateAssembly()
    {
        var provider = Path.Combine(directory, "Candidate.dll");
        DotNetEmitter.Emit("public static class Provider { public static int Foo(int bank) => bank; public static void Main() {} }", provider, "Provider");
        var original = File.ReadAllBytes(provider);
        Write("api.decls", ".lib \"native.dll\"\nFoo%(bank*)\n");
        var catalog = Write("catalog.json", """
            {"declarations":["api.decls"],"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Candidate.dll","type":"Provider","method":"Foo"}]}
            """);
        var source = Write("Main.bb", "Print Foo(1)\n");

        var result = Run("audit-dependencies", source, "--externals", catalog, "--trace-resolution", provider);

        Assert.Equal(original, File.ReadAllBytes(provider));
        Assert.Equal(1, result.Code);
        Assert.Contains("input", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OmittedProviderReturnTypeCannotReplaceHistoricalFloatContractWithInteger()
    {
        DotNetEmitter.Emit("public static class Provider { public static int Foo(int x) => x; public static void Main() {} }",
            Path.Combine(directory, "Provider.dll"), "Provider");
        Write("api.decls", ".lib \"native.dll\"\nFoo#(x%)\n");
        var catalog = Write("catalog.json", """
            {"declarations":["api.decls"],"functions":[{"name":"Foo","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Foo"}]}
            """);
        var source = Write("Main.bb", "Print Foo(1)\n");

        var result = Run("audit-dependencies", source, "--externals", catalog);

        Assert.Equal("", result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var dependency = Assert.Single(document.RootElement.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("abi_incompatible", dependency.GetProperty("status").GetString());
        Assert.Equal("Float", dependency.GetProperty("returnType").GetString());
        Assert.False(dependency.GetProperty("usableExecutableBinding").GetBoolean());
        Assert.Equal(1, result.Code);
    }

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Requires Linux ln to create the hardlink fixture.";
        }
    }

    public void Dispose() => Directory.Delete(directory, true);
}
