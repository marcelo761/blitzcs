using System.Text.Json;
using BlitzCS.CLI;
using Xunit;
namespace BlitzCS.CLI.Tests;
public sealed class ExternalFallbackProfileTests : IDisposable
{
    private readonly string directory=Directory.CreateTempSubdirectory("blitzcs-fallback-").FullName;
    private string Write(string n,string text) { var p=Path.Combine(directory,n);File.WriteAllText(p,text);return p; }
    [Fact]
    public void FallbackCompletesUnboundContractAndNeverOverridesSelectedPrimaryProvider()
    {
        DotNetEmitter.Emit("public static class Provider { public static int A(int x)=>x+1; public static int B(int x)=>999; public static void Main(){} }",Path.Combine(directory,"Provider.dll"),"Provider");
        Write("base.json","""{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"]}]}""");
        Write("first.json","""{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"A"}]}""");
        Write("second.json","""{"functions":[{"name":"Foo","returnType":"Integer","parameters":["Integer"],"assembly":"Provider.dll","type":"Provider","method":"B"}]}""");
        var config=Write("project.json","""{"profiles":["base.json"],"fallbackProfiles":["first.json","second.json"]}""");
        var source=Write("main.bb","Print Foo(1)");using var output=new StringWriter();using var error=new StringWriter();
        Assert.Equal(0,CliApplication.Run(["audit-dependencies",source,"--externals",config],output,error));Assert.Equal("",error.ToString());
        using var json=JsonDocument.Parse(output.ToString());var call=json.RootElement.GetProperty("dependencies")[0];
        Assert.Equal("bound",call.GetProperty("status").GetString());
        Assert.Equal("A",call.GetProperty("bindingMethod").GetString());
        Assert.Equal(3,call.GetProperty("catalogSources").GetArrayLength());
    }
    [Fact]
    public void BinaryIntegerReturnCanBeDiscardedByAHistoricalVoidCommandWithoutInventingAResult()
    {
        Write("native.decls",".lib \"native.dll\"\nNativeCommand(x%):\"_NativeCommand@4\"\n");
        var config=Write("project.json","""{"declarations":["native.decls"],"functions":[{"name":"NativeCommand","parameters":["Integer"],"returnType":"Integer","binary":{"library":"native.dll","export":"_NativeCommand@4","convention":"stdcall"}}]}""");
        var document=ExternalCatalogDocument.Read(config);var function=document.Root.GetProperty("functions")[0];
        Assert.Equal("Void",function.GetProperty("returnType").GetString());
        Assert.True(function.TryGetProperty("binary",out _));
        Assert.Equal("discard-integer-return",function.GetProperty("bindingAdaptation").GetString());
    }
    public void Dispose()=>Directory.Delete(directory,true);
}
