using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.CLI;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class CliBinaryIntegrationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitzcs-cli-bin").FullName;
    private readonly string? previousCache = Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE");

    public CliBinaryIntegrationTests() => Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", Path.Combine(root, "cache"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", previousCache);
        Directory.Delete(root, recursive: true);
    }

    private string Prepare(byte[] code, string? catalogExtras = null)
    {
        File.WriteAllText(Path.Combine(root, "main.bb"), "Print 1\n");
        File.WriteAllBytes(Path.Combine(root, "lib.dll"), new PeFixtureBuilder().WithCode(code).Export("_f", 0).Build());
        File.WriteAllText(Path.Combine(root, "catalog.json"), "{\"functions\":[],\"binaryLibraries\":[\"lib.dll\"]" + catalogExtras + "}");
        return Path.Combine(root, "out", "main.dll");
    }

    private static string Build(string root, string output, out int exit)
    {
        var stdout = new StringWriter(); var stderr = new StringWriter();
        exit = CliApplication.Run(["build", Path.Combine(root, "main.bb"), "-o", output, "--externals", Path.Combine(root, "catalog.json")], stdout, stderr);
        return stdout + stderr.ToString();
    }

    [Fact]
    public void BuildTranslatesOnceThenHitsCacheAndLibraryRuns()
    {
        var code = TranslatorHarness.Asm(a => { a.mov(eax, __dword_ptr[esp + 4]); a.add(eax, __dword_ptr[esp + 8]); a.ret(); });
        var output = Prepare(code);
        var original = File.ReadAllBytes(Path.Combine(root, "lib.dll"));

        var first = Build(root, output, out var exit1);
        Assert.Equal(0, exit1);
        Assert.Contains("translated", first);
        var second = Build(root, output, out var exit2);
        Assert.Equal(0, exit2);
        Assert.Contains("cache hit", second);

        var outDll = Path.Combine(root, "out", "lib.dll");
        Assert.Equal(original, File.ReadAllBytes(outDll)); // input bytes untouched
        using var module = TranslatedLibrary.Open(outDll, new DictionaryGuestImportResolver());
        Assert.Equal(7u, module.Bind("_f", BlitzCS.BinaryCompiler.GuestCallingConvention.Cdecl, 2).Call(3, 4));
    }

    [Fact]
    public void StaleArtifactIsRejected()
    {
        var output = Prepare(TranslatorHarness.Asm(a => a.ret()));
        Assert.Equal(0, BuildExit(output));
        var outDll = Path.Combine(root, "out", "lib.dll");
        File.WriteAllBytes(outDll, new PeFixtureBuilder().WithCode(TranslatorHarness.Asm(a => { a.xor(eax, eax); a.ret(); })).Export("_f", 0).Build());
        Assert.Throws<TranslatedLibraryException>(() => TranslatedLibrary.Open(outDll, new DictionaryGuestImportResolver()));
    }

    [Fact]
    public void UnsupportedInstructionFailsTheBuildWithDiagnostic()
    {
        var output = Prepare(TranslatorHarness.Asm(a => { a.db(0x65, 0x8B, 0x05, 0, 0, 0, 0); a.ret(); }));
        var text = Build(root, output, out var exit);
        Assert.Equal(1, exit);
        Assert.Contains("BC_SEGMENT_OVERRIDE", text);
        Assert.False(File.Exists(Path.Combine(root, "out", "lib.dll.bcx")));
    }

    private int BuildExit(string output) { Build(root, output, out var exit); return exit; }
}
