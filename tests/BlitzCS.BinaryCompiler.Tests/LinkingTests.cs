using System.Diagnostics;
using System.Text.Json;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.CLI;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Transparent linking: an unchanged .bb calls exports of an unchanged PE32 DLL through the generated binding.</summary>
[Collection("GuestRuntime")]
public sealed class LinkingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitzcs-linking").FullName;
    private readonly string? previousCache = Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE");

    public LinkingTests() => Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", Path.Combine(root, "cache"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", previousCache);
        Directory.Delete(root, recursive: true);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BlitzCS.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Builds samples/BinaryLinking/binlib.c as a PE32 DLL; null when the 32-bit C toolchain is unavailable.</summary>
    private byte[]? BuildBinlib()
    {
        var repo = RepoRoot();
        var output = Path.Combine(root, "c-build");
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(repo, "tools/x86_oracle/build.sh"));
        info.ArgumentList.Add(Path.Combine(repo, "samples/BinaryLinking/binlib.c"));
        info.ArgumentList.Add("-");
        info.ArgumentList.Add(output);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) return null;
        var builder = new PeFixtureBuilder().WithCode(File.ReadAllBytes(Path.Combine(output, "binlib.code.bin")));
        var data = File.ReadAllBytes(Path.Combine(output, "binlib.data.bin"));
        if (data.Length > 0) builder.WithData(data.Concat(new byte[0x100]).ToArray());
        using var exports = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "binlib.exports.json")));
        foreach (var export in exports.RootElement.GetProperty("exports").EnumerateArray())
            builder.Export(export.GetProperty("name").GetString()!, export.GetProperty("offset").GetUInt32());
        return builder.Build();
    }

    private string Cli(string source, string catalog, out int exit, string outName = "main.dll")
    {
        File.WriteAllText(Path.Combine(root, "main.bb"), source);
        File.WriteAllText(Path.Combine(root, "catalog.json"), catalog);
        var stdout = new StringWriter(); var stderr = new StringWriter();
        exit = CliApplication.Run(["build", Path.Combine(root, "main.bb"), "-o", Path.Combine(root, "out", outName),
            "--externals", Path.Combine(root, "catalog.json")], stdout, stderr);
        return stdout + stderr.ToString();
    }

    private string RunProgram(string name = "main.dll")
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(root, "out", name));
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdout.Replace("\r\n", "\n");
    }

    private static string Entry(string name, string export, string parameters, string returnType, string? convention = "cdecl", string library = "lib.dll", string extra = "") =>
        $"{{\"name\":\"{name}\",\"parameters\":[{parameters}],\"returnType\":\"{returnType}\"{extra},\"binary\":{{\"library\":\"{library}\",\"export\":\"{export}\""
        + (convention is null ? "" : $",\"convention\":\"{convention}\"") + "}}";

    private static string Catalog(params string[] entries) => "{\"functions\":[" + string.Join(",", entries) + "]}";

    private static byte[] Sum2() => TranslatorHarness.Asm(a => { a.mov(eax, __dword_ptr[esp + 4]); a.add(eax, __dword_ptr[esp + 8]); a.ret(); });

    private void WriteLib(byte[] code, params (string Name, uint Offset)[] exports)
    {
        var builder = new PeFixtureBuilder().WithCode(code);
        foreach (var (name, offset) in exports) builder.Export(name, offset);
        File.WriteAllBytes(Path.Combine(root, "lib.dll"), builder.Build());
    }

    [Fact]
    public void SampleProgramCallsTheUnchangedDllEndToEnd()
    {
        var dll = BuildBinlib();
        if (dll is null) return; // 32-bit C toolchain unavailable on this machine
        var repo = RepoRoot();
        File.WriteAllBytes(Path.Combine(root, "binlib.dll"), dll);
        var source = File.ReadAllText(Path.Combine(repo, "samples/BinaryLinking/main.bb"));
        var catalog = File.ReadAllText(Path.Combine(repo, "samples/BinaryLinking/binlib.json"));
        var dllBefore = File.ReadAllBytes(Path.Combine(root, "binlib.dll"));

        var first = Cli(source, catalog, out var exit);
        Assert.Equal(0, exit);
        Assert.Contains("translated", first);
        Assert.Equal("42\n15\n6\n10\n3\n65\n1065353216\n112\n", RunProgram());

        var bbBefore = File.ReadAllBytes(Path.Combine(root, "main.bb"));
        var second = Cli(source, catalog, out var exit2);
        Assert.Equal(0, exit2);
        Assert.Contains("cache hit", second);
        Assert.Equal(bbBefore, File.ReadAllBytes(Path.Combine(root, "main.bb")));
        Assert.Equal(dllBefore, File.ReadAllBytes(Path.Combine(root, "binlib.dll")));
        Assert.Equal(dllBefore, File.ReadAllBytes(Path.Combine(root, "out", "binlib.dll")));
        Assert.Equal("42\n15\n6\n10\n3\n65\n1065353216\n112\n", RunProgram());
        Assert.True(File.Exists(Path.Combine(root, "out", "BlitzCS.BinaryCompiler.Runtime.dll")));
    }

    [Fact]
    public void DefaultsAndExplicitArgumentsUseTheCatalogMechanism()
    {
        WriteLib(Sum2(), ("_sum", 0));
        var catalog = Catalog(Entry("AddTen", "_sum", "\"Integer\",\"Integer\"", "Integer", extra: ",\"defaults\":[10]"));
        Cli("Print AddTen(5)\nPrint AddTen(5, 1)\n", catalog, out var exit);
        Assert.Equal(0, exit);
        Assert.Equal("15\n6\n", RunProgram());
    }

    [Fact]
    public void MissingExportIsABuildErrorNamingLibraryAndExport()
    {
        WriteLib(Sum2(), ("_sum", 0));
        var text = Cli("Print Nope(1)\n", Catalog(Entry("Nope", "missing", "\"Integer\"", "Integer")), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("lib.dll", text); Assert.Contains("no export 'missing'", text);
        Assert.False(File.Exists(Path.Combine(root, "out", "main.dll")));
    }

    [Fact]
    public void AmbiguousExportIsRejected()
    {
        WriteLib(Sum2(), ("_f@8", 0), ("_f@4", 0));
        var text = Cli("Print F(1, 2)\n", Catalog(Entry("F", "_f", "\"Integer\",\"Integer\"", "Integer")), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("ambiguous", text);
    }

    [Fact]
    public void WrongConventionAgainstDecoratedExportIsRejected()
    {
        WriteLib(Sum2(), ("_f@8", 0));
        var text = Cli("Print F(1, 2)\n", Catalog(Entry("F", "_f@8", "\"Integer\",\"Integer\"", "Integer", "cdecl")), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("decoration says Stdcall", text);
    }

    [Fact]
    public void StdcallArgumentSizeMismatchIsRejected()
    {
        WriteLib(Sum2(), ("_f@8", 0));
        var text = Cli("Print F(1, 2, 3)\n", Catalog(Entry("F", "_f@8", "\"Integer\",\"Integer\",\"Integer\"", "Integer", null)), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("pops 8 byte(s)", text);
    }

    [Fact]
    public void UnknownConventionRequiresTheCatalog()
    {
        WriteLib(Sum2(), ("plain", 0));
        var text = Cli("Print F(1, 2)\n", Catalog(Entry("F", "plain", "\"Integer\",\"Integer\"", "Integer", null)), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("set binary.convention", text);
    }

    [Theory]
    [InlineData("Float", "float return from binary library unsupported")]
    [InlineData("String", "string return from binary library unsupported")]
    public void UnsupportedReturnTypesAreBuildErrors(string returnType, string message)
    {
        WriteLib(Sum2(), ("plain", 0));
        var text = Cli("Print F(1)\n", Catalog(Entry("F", "plain", "\"Integer\"", returnType)), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains(message, text);
    }

    [Fact]
    public void NonPe32FileIsRejected()
    {
        File.WriteAllBytes(Path.Combine(root, "lib.dll"), [1, 2, 3, 4]);
        var text = Cli("Print F(1)\n", Catalog(Entry("F", "plain", "\"Integer\"", "Integer")), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("not a PE32 x86 image", text);
    }

    [Fact]
    public void UnresolvedImportFailsAtRunTimeNamingTheImport()
    {
        var builder = new PeFixtureBuilder();
        var iat = builder.Import("missing.dll", "NeverProvided");
        builder.WithCode(TranslatorHarness.Asm(a => { a.call(__dword_ptr[(int)(PeFixtureBuilder.ImageBase + iat)]); a.ret(); })).Export("_f", 0);
        File.WriteAllBytes(Path.Combine(root, "lib.dll"), builder.Build());
        Cli("Print F(1)\n", Catalog(Entry("F", "_f", "\"Integer\"", "Integer")), out var exit);
        Assert.Equal(0, exit);
        var output = RunProgram();
        Assert.Contains("missing.dll!NeverProvided", output);
    }

    [Fact]
    public void StaleTranslationArtifactFailsAtRunTime()
    {
        WriteLib(Sum2(), ("_sum", 0));
        Cli("Print F(1, 2)\n", Catalog(Entry("F", "_sum", "\"Integer\",\"Integer\"", "Integer")), out var exit);
        Assert.Equal(0, exit);
        Assert.Equal("3\n", RunProgram());
        var changed = TranslatorHarness.Asm(a => { a.xor(eax, eax); a.ret(); });
        File.WriteAllBytes(Path.Combine(root, "out", "lib.dll"), new PeFixtureBuilder().WithCode(changed).Export("_sum", 0).Build());
        var output = RunProgram();
        Assert.Contains("does not match", output);
        Assert.Contains("lib.dll", output);
    }

    [Fact]
    public void ManagedBindingAndCheckSemanticsStillWork()
    {
        WriteLib(Sum2(), ("_sum", 0));
        File.WriteAllText(Path.Combine(root, "main.bb"), "Print F(1, 2)\n");
        File.WriteAllText(Path.Combine(root, "catalog.json"), Catalog(Entry("F", "_sum", "\"Integer\",\"Integer\"", "Integer")));
        var stdout = new StringWriter();
        var exit = CliApplication.Run(["check-semantics", Path.Combine(root, "main.bb"), "--externals", Path.Combine(root, "catalog.json")], stdout, new StringWriter());
        Assert.Equal(0, exit);
        Assert.Contains("0 external call(s) lack implementations", stdout.ToString());
    }
}
