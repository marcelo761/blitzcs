using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyDependencyResolutionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-legacy-discovery-").FullName;
    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }
    private (int Code, string Output, string Error) Run(params string[] args)
    { using var output = new StringWriter(); using var error = new StringWriter(); return (CliApplication.Run(args, output, error), output.ToString(), error.ToString()); }

    [Fact]
    public void AuditDiscoversAllPointerDependenciesAndPreservesOriginalLibraryAndAlias()
    {
        Write("decls/User32.decls", ".lib \"user32.dll\"\napi_GetClientRect%(hwnd%, rect*):\"GetClientRect\"\n");
        Write("decls/zlib.decls", ".lib \"zlibwapi.dll\"\nZlibWapi_Compress2%(dest*,size*,src*,count%,level%):\"compress2\"\n");
        var catalog = Write("catalog.json", """{"declarations":["decls"]}""");
        var source = Write("Main.bb", "api_GetClientRect 1,2\nZlibWapi_Compress2 1,2,3,4,5\n");
        var result = Run("audit-dependencies", source, "--externals", catalog);
        Assert.Equal(1, result.Code); Assert.Equal("", result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var dependencies = document.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Equal(2, dependencies.Length);
        Assert.All(dependencies, d => Assert.Equal("provider_missing", d.GetProperty("status").GetString()));
        Assert.Equal("GetClientRect", dependencies[0].GetProperty("export").GetString());
        Assert.Equal("user32.dll", dependencies[0].GetProperty("library").GetString());
        Assert.True(dependencies[0].GetProperty("legacyDeclaration").GetProperty("parameters")[1].GetProperty("isPointer").GetBoolean());
        Assert.Contains("User32.decls", dependencies[0].GetProperty("legacyDeclaration").GetProperty("source").GetString());
        Assert.False(document.RootElement.GetProperty("semanticValidation").GetBoolean());
    }

    [Fact]
    public void MultipleCatalogsComposeAndTraceMissingDeclarationsSeparatelyFromMissingProviders()
    {
        var a = Write("a.json", """{"functions":[{"name":"FirstCall","returnType":"Void","parameters":[]}]}""");
        var b = Write("b.json", """{"functions":[{"name":"SecondCall","returnType":"Void","parameters":[]}]}""");
        var source = Write("Main.bb", "FirstCall\nSecondCall\nUnknownCall\n");
        var trace = Path.Combine(directory,"trace.json");
        var result = Run("audit-dependencies",source,"--externals",a,"--externals",b,"--trace-resolution",trace);
        Assert.Equal(1,result.Code); Assert.Equal("",result.Error);
        using var document = JsonDocument.Parse(File.ReadAllText(trace));
        var statuses = document.RootElement.GetProperty("dependencies").EnumerateArray().Select(d=>d.GetProperty("status").GetString()).ToArray();
        Assert.Equal(new[]{"provider_missing","provider_missing","declaration_missing"},statuses);
    }

    [Fact]
    public void PointerContractDoesNotSilentlyBindToAnIntegerMethodWithoutAnAdapter()
    {
        DotNetEmitter.Emit("public static class Provider { public static int Call(int x) => x; public static void Main() {} }",Path.Combine(directory,"Provider.dll"),"Provider");
        Write("bank.decls", ".lib \"native.dll\"\nNativeCall%(bank*):\"NativeCall\"\n");
        var catalog=Write("catalog.json","""{"declarations":["bank.decls"],"functions":[{"name":"NativeCall","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"Call"}]}""");
        var source=Write("Main.bb","Print NativeCall(1)"); var target=Path.Combine(directory,"out/program.dll");
        var build=Run("build",source,"-o",target,"--externals",catalog);
        Assert.Equal(1,build.Code); Assert.Contains("adapter",build.Error,StringComparison.OrdinalIgnoreCase); Assert.False(File.Exists(target));
    }

    [Fact]
    public void TraceCannotOverwriteAnIncludedSourceEvenWhenAuditFails()
    {
        var include=Write("empty.bb", ""); var source=Write("Main.bb", "Include \"empty.bb\"\nMissingCall\n");
        var result=Run("audit-dependencies",source,"--trace-resolution",include);
        Assert.Equal(1,result.Code); Assert.Contains("input",result.Error,StringComparison.OrdinalIgnoreCase); Assert.Equal("",File.ReadAllText(include));
    }
    public void Dispose() => Directory.Delete(directory,true);
}
