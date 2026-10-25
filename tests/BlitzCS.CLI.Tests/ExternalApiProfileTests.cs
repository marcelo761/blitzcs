using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ExternalApiProfileTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-profiles-").FullName;
    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    private static string Contract => """
        {"profile":"test-api","provider":"test.runtime","resolution":"runtime-registration","symbol":"%NativeAdd%a%b=7","callingConvention":null}
        """;
    private string Profile() => Write("profiles/api.json", "{\"functions\":[{\"name\":\"NativeAdd\",\"parameters\":[\"Integer\",\"Integer\"],\"returnType\":\"Integer\",\"defaults\":[7],\"contract\":"+Contract+"}]}");
    private (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        return (CliApplication.Run(args, output, error), output.ToString(), error.ToString());
    }

    [Fact]
    public void ProfileResolvesContractsButBuildReportsRequiredProviderWithoutWritingPackage()
    {
        Profile();
        var catalog = Write("catalog.json", """{"profiles":["profiles/api.json"]}""");
        var source = Write("main.bb", "Print NativeAdd(2)");
        var semantic = Run("check-semantics",source,"--externals",catalog);
        Assert.True(semantic.Code == 0,semantic.Error);
        Assert.Contains("test.runtime",semantic.Output);
        Assert.Contains("no implementation",semantic.Output);
        var destination = Path.Combine(directory,"package");
        var build = Run("build",source,"-o",destination,"--externals",catalog);
        Assert.Equal(1,build.Code);
        Assert.Contains("test.runtime",build.Error);
        Assert.Contains("runtime-registration",build.Error);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task BindingAnIncludedContractUsesItsDefaultsAndExactManagedOverload()
    {
        Profile();
        DotNetEmitter.Emit("""
            public static class Provider {
                public static int Add(int a,int b)=>a+b;
                public static int Add(float a,float b)=>999;
                public static void Main() {}
            }
            """,Path.Combine(directory,"Provider.dll"),"Provider");
        var catalog = Write("catalog.json", """
            {"profiles":["profiles/api.json"],"functions":[{"name":"NativeAdd","parameters":["Integer","Integer"],"assembly":"Provider.dll","type":"Provider","method":"Add"}]}
            """);
        var source = Write("main.bb","Print NativeAdd(2)\nPrint NativeAdd(3,4)");
        var output = Path.Combine(directory,"out/program.dll");
        var build = Run("build",source,"-o",output,"--externals",catalog);
        Assert.True(build.Code==0,build.Error);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
        start.ArgumentList.Add(output);
        using var p=Process.Start(start)!;
        var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(timeout.Token); } catch { p.Kill(true); throw; }
        Assert.Equal(0,p.ExitCode);
        Assert.Equal("9\n7\n",(await stdout).Replace("\r\n","\n"));
        Assert.Equal("",await stderr);
    }

    [Fact]
    public void RequiredProfileArgumentsCannotBecomeOptionalThroughClrMetadata()
    {
        var profile = Profile();
        File.WriteAllText(profile, File.ReadAllText(profile).Replace("\"defaults\":[7]", "\"defaults\":[]"));
        DotNetEmitter.Emit("public static class Provider { public static int Add(int a,int b=7)=>a+b; public static void Main() {} }",
            Path.Combine(directory,"Provider.dll"),"Provider");
        var catalog=Write("catalog.json", """
            {"profiles":["profiles/api.json"],"functions":[{"name":"NativeAdd","parameters":["Integer","Integer"],"assembly":"Provider.dll","type":"Provider","method":"Add"}]}
            """);
        var output=Path.Combine(directory,"required.dll");
        var result=Run("build",Write("main.bb","Print NativeAdd(2)"),"-o",output,"--externals",catalog);
        Assert.Equal(1,result.Code);
        Assert.Contains("requires argument 2",result.Error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void BindingCannotEraseInheritedContractWithExplicitNull()
    {
        Profile();
        var catalog=Write("catalog.json", """
            {"profiles":["profiles/api.json"],"functions":[{"name":"NativeAdd","parameters":["Integer","Integer"],"assembly":"Provider.dll","type":"Provider","method":"Add","contract":null}]}
            """);
        var result=Run("check-semantics",Write("main.bb","Print NativeAdd(2)"),"--externals",catalog);
        Assert.Equal(1,result.Code);
        Assert.Contains("cannot change profile contract",result.Error);
    }

    [Fact]
    public void OverlayCannotChangeHistoricalParameterTypes()
    {
        Profile();
        var catalog=Write("catalog.json","""{"profiles":["profiles/api.json"],"functions":[{"name":"NativeAdd","parameters":["Float"],"returnType":"Integer"}]}""");
        var result=Run("check-semantics",Write("main.bb","Print NativeAdd(2)"),"--externals",catalog);
        Assert.Equal(1,result.Code);
        Assert.Contains("signature",result.Error,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogCyclesHaveExplicitDiagnostics()
    {
        var catalog=Write("a.json","""{"profiles":["b.json"]}""");
        Write("b.json","""{"profiles":["a.json"]}""");
        var result=Run("check-semantics",Write("main.bb","Print 1"),"--externals",catalog);
        Assert.Equal(1,result.Code);
        Assert.Contains("cycle",result.Error,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnusedProviderRequirementsDoNotBlockManagedBuild()
    {
        var result=Run("build",Write("pure.bb","Print 42"),"-o",Path.Combine(directory,"pure.dll"),"--externals",Profile());
        Assert.True(result.Code==0,result.Error);
    }

    [Theory]
    [InlineData("{\"provider\":\"\",\"resolution\":\"runtime-registration\",\"symbol\":\"X\"}")]
    [InlineData("{\"provider\":\"runtime\",\"resolution\":\"runtime-registration\",\"symbol\":\"X\",\"callingConvention\":\"guessed\"}")]
    public void InvalidContractMetadataIsRejected(string contract)
    {
        var catalog=Write("invalid.json","{\"functions\":[{\"name\":\"Value\",\"parameters\":[],\"returnType\":\"Integer\",\"contract\":"+contract+"}]}");
        var result=Run("check-semantics",Write("main.bb","Print Value()"),"--externals",catalog);
        Assert.Equal(1,result.Code);
        Assert.Contains("contract",result.Error,StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()=>Directory.Delete(directory,true);
}
