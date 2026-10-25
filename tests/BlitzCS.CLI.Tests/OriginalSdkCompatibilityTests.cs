using System.Diagnostics;
using System.Text.Json.Nodes;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class OriginalSdkCompatibilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-original-sdk-" + Guid.NewGuid());

    public OriginalSdkCompatibilityTests() => Directory.CreateDirectory(directory);

    [OriginalSdkTheory]
    [InlineData("tutorials/basic_tuts/variables1.bb", "1111\nVic 20\n")]
    [InlineData("tutorials/basic_tuts/variables2.bb", "1111\nVic 20\n")]
    [InlineData("tutorials/basic_tuts/variables3.bb", "Welcome to the world of programming.\n")]
    public async Task OriginalVariableTutorialsExecuteUnchanged(string relativePath, string expected)
    {
        var original = Path.Combine(SdkRoot, relativePath);
        Assert.True(File.Exists(original), $"Original SDK sample does not exist: {original}");

        Assert.Equal(expected, await BuildAndRun(original));
    }

    [OriginalSdkTheory]
    [InlineData("Chr.bb", " The character for ASCII value 65 is: A\n")]
    [InlineData("Len.bb", "There are 12 characters in your name!\n")]
    [InlineData("Left.bb", "The left 3 letters of your name are: Sha\n")]
    [InlineData("Replace.bb", "Your name before replacing: Bill Wallace\nYour name with L changed to B: Bibb Wabbace\n")]
    public async Task OriginalStringExamplesExecuteThroughTheConfiguredManagedProvider(string fileName, string expected)
    {
        var original = Path.Combine(SdkRoot, "help", "commands", "2d_examples", fileName);
        Assert.True(File.Exists(original), $"Original SDK sample does not exist: {original}");

        Assert.Equal(expected, await BuildAndRun(original, StringCatalog()));
    }

    [OriginalSdkFact]
    public async Task OriginalPiExampleExecutesWithoutAnEngineProvider()
    {
        var source = Path.Combine(SdkRoot, "help", "commands", "2d_examples", "Pi.bb");
        Assert.Equal("The Value of Pi is:3.14159\n", await BuildAndRun(source));
    }

    [OriginalSdkFact]
    public void OriginalReadFileExamplePassesOnlyTheProfileSemanticCheck()
    {
        var source = Path.Combine(SdkRoot, "help", "commands", "2d_examples", "ReadFile.bb");
        Assert.True(File.Exists(source), $"Original SDK sample does not exist: {source}");
        var profile = Path.Combine(RepositoryRoot(), "profiles", "blitz3d.json");
        Assert.True(File.Exists(profile), $"Blitz3D API profile does not exist: {profile}");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var status = CliApplication.Run(["check-semantics", source, "--externals", profile], output, error);

        Assert.Equal(0, status);
        Assert.Contains("Semantic validation succeeded", output.ToString());
        Assert.Contains("lack implementations", output.ToString());
        Assert.Equal("", error.ToString());
    }

    private static string SdkRoot => Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_SDK")!;

    private string StringCatalog()
    {
        var root = RepositoryRoot();
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "samples", "LegacyStrings", "runtime.json")))!.AsObject();
        var assembly = typeof(Blitz3DCS.Runtime.LegacyStrings).Assembly.Location;
        foreach (var function in document["functions"]!.AsArray())
            function!["assembly"] = assembly;
        document["profiles"] = new JsonArray(JsonValue.Create(Path.Combine(root, "profiles", "blitz3d.json")));
        var catalog = Path.Combine(directory, "blitz3d-string-catalog.json");
        File.WriteAllText(catalog, document.ToJsonString());
        return catalog;
    }

    private async Task<string> BuildAndRun(string source, string? catalog = null)
    {
        var output = Path.Combine(directory, "program-" + Guid.NewGuid().ToString("N") + ".dll");
        using var errors = new StringWriter();
        var arguments = catalog is null
            ? new[] { "build", source, "-o", output }
            : new[] { "build", source, "-o", output, "--externals", catalog };
        var status = CliApplication.Run(arguments, TextWriter.Null, errors);
        Assert.True(status == 0, errors.ToString());

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }

        Assert.Equal(0, process.ExitCode);
        var result = await stdout;
        Assert.Equal("", await stderr);
        return result.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BlitzCS.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate BlitzCS.slnx from the test output directory.");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

public sealed class OriginalSdkFactAttribute : FactAttribute
{
    public OriginalSdkFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_SDK")))
            Skip = "Set BLITZCS_ORIGINAL_SDK to an extracted Blitz3D V1.118 SDK directory.";
    }
}

public sealed class OriginalSdkTheoryAttribute : TheoryAttribute
{
    public OriginalSdkTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_SDK")))
            Skip = "Set BLITZCS_ORIGINAL_SDK to an extracted Blitz3D V1.118 SDK directory.";
    }
}
