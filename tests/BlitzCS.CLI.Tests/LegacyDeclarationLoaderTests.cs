using System.Text;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyDeclarationLoaderTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-decls-").FullName;

    private string Write(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(directory, "api.decls");
        File.WriteAllText(path, text, Encoding.GetEncoding(1252));
        return path;
    }

    [Fact]
    public void PreservesLibraryAliasesPointersUnmarkedIntegersAndSource()
    {
        var path = Write(".LiB \"zlibwapi.dll\" ; comment\nCompress%(dest*, size*, source*, length%, level): \"compress2\" ; more\n.lib \" \"\nNotify(text$)\n");
        var rows = LegacyDeclarationLoader.Read(path);
        Assert.Equal(2, rows.Count);
        var row = rows[0];
        Assert.Equal("Compress", row["name"]!.GetValue<string>());
        Assert.Equal("Integer", row["returnType"]!.GetValue<string>());
        Assert.Equal(5, row["parameters"]!.AsArray().Count);
        Assert.All(row["parameters"]!.AsArray(), p => Assert.Equal("Integer", p!.GetValue<string>()));
        var metadata = row["legacyDeclaration"]!;
        Assert.Equal(path, metadata["source"]!.GetValue<string>());
        Assert.Equal(2, metadata["line"]!.GetValue<int>());
        Assert.Equal("zlibwapi.dll", metadata["library"]!.GetValue<string>());
        Assert.Equal("compress2", metadata["export"]!.GetValue<string>());
        Assert.Equal("stdcall", metadata["callingConvention"]!.GetValue<string>());
        Assert.True(metadata["parameters"]![0]!["isPointer"]!.GetValue<bool>());
        Assert.Equal("dest*", metadata["parameters"]![0]!["raw"]!.GetValue<string>());
        Assert.False(metadata["parameters"]![4]!["isPointer"]!.GetValue<bool>());
        Assert.Equal("Void", rows[1]["returnType"]!.GetValue<string>());
        Assert.Equal(" ", rows[1]["legacyDeclaration"]!["library"]!.GetValue<string>());
        Assert.Equal("Notify", rows[1]["legacyDeclaration"]!["export"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesTypedTrailingDefaultsAndQuotedDelimitersInCp1252()
    {
        var rows = LegacyDeclarationLoader.Read(Write("Values#(required, n%=-3, f#=1.25e2, text$=\"ação;,(\"\"ok\"\")\"): \"entry;point\""));
        var row = Assert.Single(rows);
        Assert.Equal("Float", row["returnType"]!.GetValue<string>());
        Assert.Equal(-3, row["defaults"]![0]!.GetValue<int>());
        Assert.Equal(125f, row["defaults"]![1]!.GetValue<float>());
        Assert.Equal("ação;,(\"ok\")", row["defaults"]![2]!.GetValue<string>());
        Assert.Equal("entry;point", row["legacyDeclaration"]!["export"]!.GetValue<string>());
        Assert.Equal("", row["legacyDeclaration"]!["library"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(".unknown \"x\"")]
    [InlineData("Broken(a%,)")]
    [InlineData("Broken(a.bad)")]
    [InlineData("Broken(a%=1,b%)")]
    [InlineData("Broken(a#=1e999)")]
    [InlineData("Broken(a%=2147483648)")]
    [InlineData("Broken(a$=42)")]
    [InlineData("Broken(a*=0)")]
    [InlineData("Broken(a%) trailing")]
    [InlineData("Broken(a%) : \"unterminated")]
    [InlineData(".lib \"x\" trailing")]
    public void UnsupportedSyntaxFailsAtExactSourceLine(string syntax)
    {
        var path = Write("; header\n" + syntax);
        var error = Assert.Throws<ArgumentException>(() => LegacyDeclarationLoader.Read(path));
        Assert.Contains(path + "(2)", error.Message);
    }

    [Fact]
    public void DuplicateNamesReportBothSourceLocations()
    {
        var path = Write("One%()\none%()");
        var error = Assert.Throws<ArgumentException>(() => LegacyDeclarationLoader.Read(path));
        Assert.Contains(path + "(1)", error.Message);
        Assert.Contains(path + "(2)", error.Message);
    }

    [Fact]
    public void PreservesDuplicateParameterNamesAsPositionalAbiMetadata()
    {
        var row = Assert.Single(LegacyDeclarationLoader.Read(Write("Legacy%(w%, W%)")));
        Assert.Equal(2, row["parameters"]!.AsArray().Count);
        Assert.Equal("w", row["legacyDeclaration"]!["parameters"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("W", row["legacyDeclaration"]!["parameters"]![1]!["name"]!.GetValue<string>());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
