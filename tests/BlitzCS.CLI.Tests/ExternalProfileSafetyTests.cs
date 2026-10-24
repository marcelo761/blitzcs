using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ExternalProfileSafetyTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-profile-safety-").FullName;

    private string Write(string relative, string text)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string Source(string text = "Print 42") => Write("main.bb", text);

    [Fact]
    public async Task NestedProfileResolvesManagedAssemblyRelativeToDeclaringCatalog()
    {
        var assembly = Path.Combine(directory, "managed", "Provider.dll");
        DotNetEmitter.Emit("public static class Provider { public static int Add(int a, int b) => a + b; public static void Main() {} }", assembly, "Provider");
        Write("profiles/nested/api.json", """
            {"functions":[{"name":"NestedAdd","parameters":["Integer","Integer"],"returnType":"Integer","assembly":"../../managed/Provider.dll","type":"Provider","method":"Add"}]}
            """);
        var catalog = Write("catalog.json", """{"profiles":["profiles/nested/api.json"]}""");
        var outputPath = Path.Combine(directory, "out", "program.dll");
        var result = Run("build", Source("Print NestedAdd(20,22)"), "-o", outputPath, "--externals", catalog);
        Assert.True(result.Code == 0, result.Error);
        Assert.True(File.Exists(outputPath));

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(outputPath);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("42\n", (await stdout).Replace("\r\n", "\n"));
        Assert.Equal("", await stderr);
    }

    [Fact]
    public void DiamondImportDeduplicatesSharedDeclaration()
    {
        Write("profiles/shared.json", """
            {"functions":[{"name":"SharedValue","parameters":[],"returnType":"Integer"}]}
            """);
        Write("profiles/left.json", """{"profiles":["shared.json"]}""");
        Write("profiles/right.json", """{"profiles":["shared.json"]}""");
        var catalog = Write("catalog.json", """{"profiles":["profiles/left.json","profiles/right.json"]}""");
        var result = Run("check-semantics", Source("Print SharedValue()"), "--externals", catalog);
        Assert.Equal(0, result.Code);
        Assert.Contains("1 external call(s) lack implementations", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void DistinctProviderBindingsForOneProfileNameAreRejectedWithoutOutput()
    {
        Write("profiles/api.json", """
            {"functions":[{"name":"BoundValue","parameters":[],"returnType":"Integer"}]}
            """);
        Write("profiles/left.json", """
            {"profiles":["api.json"],"functions":[{"name":"BoundValue","parameters":[],"returnType":"Integer","assembly":"../../managed/Left.dll","type":"Provider","method":"Value"}]}
            """);
        Write("profiles/right.json", """
            {"profiles":["api.json"],"functions":[{"name":"BoundValue","parameters":[],"returnType":"Integer","assembly":"../../managed/Right.dll","type":"Provider","method":"Value"}]}
            """);
        var catalog = Write("catalog.json", """{"profiles":["profiles/left.json","profiles/right.json"]}""");
        var outputPath = Path.Combine(directory, "out", "program.dll");
        var result = Run("build", Source("Print BoundValue()"), "-o", outputPath, "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("Duplicate external BASIC name 'BoundValue'", result.Error);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void ConflictingProviderSignatureIsRejectedWithoutOutput()
    {
        Write("profiles/api.json", """
            {"functions":[{"name":"BoundValue","parameters":["Integer"],"returnType":"Integer"}]}
            """);
        var catalog = Write("catalog.json", """
            {"profiles":["profiles/api.json"],"functions":[{"name":"BoundValue","parameters":["Float"],"returnType":"Integer","assembly":"missing.dll","type":"Provider","method":"Value"}]}
            """);
        var outputPath = Path.Combine(directory, "out", "program.dll");
        var result = Run("build", Source("Print BoundValue(1)"), "-o", outputPath, "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("conflicts with its profile signature", result.Error);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void ProfileInputCannotBeOverwrittenByOutputAssembly()
    {
        var profile = Write("api.dll", """
            {"functions":[]}
            """);
        var original = File.ReadAllBytes(profile);
        var catalog = Write("catalog.json", """{"profiles":["api.dll"]}""");
        var result = Run("build", Source(), "-o", profile, "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("Cannot overwrite compiler or external input files", result.Error);
        Assert.Equal(original, File.ReadAllBytes(profile));
    }

    [Fact]
    public void ScalarProfileIsRejectedWithoutOutput()
    {
        Write("profiles/scalar.json", "42");
        var catalog = Write("catalog.json", """{"profiles":["profiles/scalar.json"]}""");
        var outputPath = Path.Combine(directory, "out", "program.dll");
        var result = Run("build", Source(), "-o", outputPath, "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("must be an object", result.Error);
        Assert.False(File.Exists(outputPath));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("root")]
    public void NativeOptionsFromRootAndImportedProfilesAreRetained(string location)
    {
        var catalogDirectory = location == "profile" ? "profiles" : ".";
        var invalidLibrary = Path.Combine(directory, catalogDirectory, "bad-native.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(invalidLibrary)!);
        File.WriteAllBytes(invalidLibrary, [0x4d, 0x5a, 0, 0]);
        string catalog;
        if (location == "profile")
        {
            Write("profiles/native.json", """{"nativeLibraries":["bad-native.dll"]}""");
            catalog = Write("catalog.json", """{"profiles":["profiles/native.json"]}""");
        }
        else
        {
            catalog = Write("catalog.json", """{"nativeLibraries":["bad-native.dll"]}""");
        }
        var destination = Path.Combine(directory, "package");
        var result = Run("build", Source(), "-o", destination, "--target", "linux-x64", "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("bad-native.dll", result.Error);
        Assert.Contains("not Linux x64", result.Error);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void RootAndProfileBinaryOptionsMustAgree()
    {
        Write("profiles/options.json", """{"binaryProfile":"Debug"}""");
        var catalog = Write("catalog.json", """{"profiles":["profiles/options.json"],"binaryProfile":"Release"}""");
        var result = Run("check-semantics", Source(), "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("disagree about 'binaryProfile'", result.Error);
    }

    [Fact]
    public void RootAndProfileNativeDependenciesAreBothRetained()
    {
        Write("profiles/native/lib.dll", "fixture");
        Write("root/native/lib.dll", "fixture");
        Write("profiles/native/dependencies.json", """{"nativeLibraries":["lib.dll"]}""");
        var catalog = Write("root/catalog.json", """
            {"profiles":["../profiles/native/dependencies.json"],"nativeLibraries":["native/lib.dll"]}
            """);
        var destination = Path.Combine(directory, "package");
        var result = Run("build", Source(), "-o", destination, "--target", "windows-x64", "--externals", catalog);
        Assert.Equal(1, result.Code);
        Assert.Contains("distinct filenames", result.Error);
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
