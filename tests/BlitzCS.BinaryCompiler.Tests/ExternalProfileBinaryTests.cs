using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class ExternalProfileBinaryTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blitzcs-profile-binary-").FullName;

    [Theory]
    [InlineData(".bcx")]
    [InlineData(".bcx.tmp")]
    public void BinaryPublishingCannotOverwriteIncludedCatalogOrTemporaryInput(string suffix)
    {
        File.WriteAllBytes(Path.Combine(directory, "Foo.dll"), Library());
        Directory.CreateDirectory(Path.Combine(directory, "out"));
        var protectedProfile = Path.Combine(directory, "out", "Foo.dll" + suffix);
        const string profile = "{\"functions\":[]}";
        File.WriteAllText(protectedProfile, profile);
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new { profiles = new[] { "out/Foo.dll" + suffix }, binaryLibraries = new[] { "Foo.dll" } }));
        var source = Path.Combine(directory, "main.bb");
        File.WriteAllText(source, "Print 42");
        var output = Path.Combine(directory, "out", "program.dll");
        using var errors = new StringWriter();
        var result = CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors);
        Assert.Equal(1, result);
        Assert.Contains("overwrite", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(profile, File.ReadAllText(protectedProfile));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ProfileDeclarationBindsToRealTranslatedExportAndExecutesWithHistoricalDefault()
    {
        var library = Library();
        File.WriteAllBytes(Path.Combine(directory, "sum.dll"), library);
        File.WriteAllText(Path.Combine(directory, "api.json"), """
            {"functions":[{"name":"AddInts","returnType":"Integer","parameters":["Integer","Integer"],"defaults":[22],
            "contract":{"provider":"sum.runtime","resolution":"export","symbol":"_sum","callingConvention":"cdecl"}}]}
            """);
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, """
            {"profiles":["api.json"],"functions":[{"name":"AddInts","parameters":["Integer","Integer"],
            "binary":{"library":"sum.dll","export":"_sum","convention":"cdecl"}}]}
            """);
        var source = Path.Combine(directory, "main.bb");
        File.WriteAllText(source, "Print AddInts(20)");
        var output = Path.Combine(directory, "out", "program.dll");
        using var error = new StringWriter();
        // The test executes on its host; application package defaults remain Windows x64.
        var target = OperatingSystem.IsWindows() ? "windows-x64" : "linux-sysv";
        Assert.True(CliApplication.Run(["build", source, "-o", output, "--externals", catalog, "--target", target], TextWriter.Null, error) == 0, error.ToString());
        Assert.True(File.Exists(Path.Combine(directory, "out", "sum.dll.bcx")));
        Assert.Equal(library, File.ReadAllBytes(Path.Combine(directory, "out", "sum.dll")));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("42\n", (await stdout).Replace("\r\n", "\n"));
        Assert.Equal("", await stderr);
    }

    private static byte[] Library() => new PeFixtureBuilder().WithImageBase(0x60000000)
        .WithCode([0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0xC3]).Export("_sum", 0).Build();
    public void Dispose() => Directory.Delete(directory, true);
}
