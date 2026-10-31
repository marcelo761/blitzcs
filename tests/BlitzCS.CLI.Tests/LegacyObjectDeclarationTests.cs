using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyObjectDeclarationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-object-declaration-").FullName;
    private const string ObjectSource = "Type MemoryStatus\nField length%\nEnd Type\nLocal m.MemoryStatus=New MemoryStatus\nNativeCall m\n";

    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name);
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

    [Fact]
    public void HistoricalPointerDeclarationAcceptsObjectDataButStillRequiresAProviderToBuild()
    {
        var source = Write("Main.bb", ObjectSource);
        var declaration = Write("native.decls", ".lib \"kernel32.dll\"\nNativeCall(buffer*)\n");
        var target = Path.Combine(directory, "program.dll");

        var check = Run("check-semantics", source, "--externals", declaration);

        Assert.True(check.Code == 0, check.Error);
        Assert.Equal("", check.Error);
        Assert.Contains("1 external call(s) lack implementations", check.Output);
        var build = Run("build", source, "-o", target, "--externals", declaration);
        Assert.Equal(1, build.Code);
        Assert.Contains("no implementation", build.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot convert", build.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void HistoricalManagedBankAdapterDoesNotImplicitlyMarshalCustomObjectData()
    {
        DotNetEmitter.Emit("public static class Provider { public static void Call(int bank) {} public static void Main() {} }",
            Path.Combine(directory, "Provider.dll"), "Provider");
        Write("native.decls", ".lib \"native.dll\"\nNativeCall(buffer*)\n");
        var catalog = Write("catalog.json", """
            {"declarations":["native.decls"],"functions":[{"name":"NativeCall","parameters":["Integer"],"returnType":"Void","legacyAdapter":true,"assembly":"Provider.dll","type":"Provider","method":"Call"}]}
            """);
        var source = Write("Main.bb", ObjectSource);
        var target = Path.Combine(directory, "program.dll");

        var result = Run("build", source, "-o", target, "--externals", catalog);

        Assert.Equal(1, result.Code);
        Assert.Contains("object", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adapter", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot convert", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
