using System.Text.Json;
using BlitzCS.CLI;
using Xunit;
namespace BlitzCS.CLI.Tests;

public sealed class LegacySignatureOverrideTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-abi-override-").FullName;
    private string Catalog(string metadata)
    {
        File.WriteAllText(Path.Combine(directory,"native.decls"), ".lib \"native.dll\"\nNativeCall%(module%, buffer$, count%):\"GetBuffer\"\n");
        var path = Path.Combine(directory,"catalog.json");
        var root=System.Text.Json.Nodes.JsonNode.Parse("""{"declarations":["native.decls"],"functions":[{"name":"NativeCall","parameters":["Integer","Integer","Integer"],"returnType":"Void","assembly":"Provider.dll","type":"Provider","method":"Call"}]}""")!;
        foreach(var item in System.Text.Json.Nodes.JsonNode.Parse("{"+metadata+"}")!.AsObject()) root["functions"]![0]![item.Key]=item.Value?.DeepClone();
        File.WriteAllText(path,root.ToJsonString());
        return path;
    }
    [Fact]
    public void ExplicitAbiAdapterPreservesOriginalDeclarationAndCorrectedSignature()
    {
        var root=ExternalCatalogDocument.Read(Catalog("""
            "legacyAdapter":true,"legacySignatureOverride":{"parameters":["Integer","Integer","Integer"],"returnType":"Void","reason":"Native API writes a byte buffer and returns void; legacy catalog is erroneous."}
            """)).Root;
        var call=root.GetProperty("functions")[0];
        Assert.Equal("Integer",call.GetProperty("parameters")[1].GetString());
        Assert.Equal("Void",call.GetProperty("returnType").GetString());
        Assert.Equal("String",call.GetProperty("legacyDeclaration").GetProperty("parameters")[1].GetProperty("basicType").GetString());
        Assert.Contains("legacy-signature-override",call.GetProperty("bindingAdaptation").GetString());
        Assert.True(call.TryGetProperty("method",out _));
    }
    [Theory]
    [InlineData("\"legacySignatureOverride\":{\"returnType\":\"Void\",\"reason\":\"known ABI\"}")]
    [InlineData("\"legacyAdapter\":true,\"legacySignatureOverride\":{\"returnType\":\"Void\"}")]
    [InlineData("\"legacyAdapter\":true,\"legacySignatureOverride\":{\"parameters\":[\"Integer\"],\"reason\":\"known ABI\"}")]
    [InlineData("\"legacyAdapter\":true,\"legacySignatureOverride\":{\"parameters\":[\"Integer\",\"Object\",\"Integer\"],\"reason\":\"known ABI\"}")]
    public void InvalidOrUnexplainedOverridesFail(string metadata)
        => Assert.Throws<ArgumentException>(()=>ExternalCatalogDocument.Read(Catalog(metadata)));
    public void Dispose()=>Directory.Delete(directory,true);
}
