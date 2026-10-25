using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class OutputSafetyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-output-" + Guid.NewGuid());

    public OutputSafetyTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(".dll")]
    [InlineData(".pdb")]
    [InlineData(".generated.cs")]
    [InlineData(".runtimeconfig.json")]
    public void SymlinkedOutputArtifactCannotOverwriteNativeInput(string extension)
    {
        var library = Path.Combine(directory, "input.native");
        File.WriteAllText(library, "preserve native input");
        var output = Path.Combine(directory, "program.dll");
        File.CreateSymbolicLink(Path.ChangeExtension(output, extension), library);

        Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit("System.Console.WriteLine(42);", output,
            null, null, [library]));

        Assert.Equal("preserve native input", File.ReadAllText(library));
        Assert.False(File.Exists(Path.ChangeExtension(output, extension == ".dll" ? ".pdb" : ".dll")));
    }

    [Fact]
    public void OutputCannotOverwriteHostAssemblyThroughDirectorySymlink()
    {
        var alias = Path.Combine(directory, "host");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(typeof(DotNetEmitter).Assembly.Location)!);
        var output = Path.Combine(alias, Path.GetFileName(typeof(DotNetEmitter).Assembly.Location));
        // Invalid code proves the preflight runs before compilation, without risking host files.
        var error = Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit("Missing();", output));
        Assert.Contains("overwrite", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliRejectsOutputArtifactAliasingSourceOrCatalog(bool catalogCollision)
    {
        var source = Path.Combine(directory, "main.bb");
        File.WriteAllText(source, "Print 42");
        var catalog = Path.Combine(directory, "bindings.json");
        File.WriteAllText(catalog, "{\"functions\": []}");
        var input = catalogCollision ? catalog : source;
        var original = File.ReadAllBytes(input);
        var output = Path.Combine(directory, "program.dll");
        File.CreateSymbolicLink(Path.ChangeExtension(output, ".runtimeconfig.json"), input);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = CliApplication.Run(["build", source, "-o", output, "--externals", catalog], stdout, stderr);

        Assert.Equal(1, result);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("Print 42")]
    [InlineData("; only a comment\n")]
    [InlineData("")]
    public void CliRejectsOutputArtifactAliasingIncludedSource(string includedSource)
    {
        var source = Path.Combine(directory, "main.bb");
        var include = Path.Combine(directory, "included.bb");
        File.WriteAllText(source, "Include \"included.bb\"\nPrint 42");
        File.WriteAllText(include, includedSource);
        var output = Path.Combine(directory, "program.dll");
        File.CreateSymbolicLink(Path.ChangeExtension(output, ".pdb"), include);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = CliApplication.Run(["build", source, "-o", output], stdout, stderr);

        Assert.Equal(1, result);
        Assert.Equal(includedSource, File.ReadAllText(include));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(".dll")]
    [InlineData(".pdb")]
    [InlineData(".generated.cs")]
    [InlineData(".runtimeconfig.json")]
    public void PublishingOutputArtifactPreservesHardlinkedNativeInput(string extension)
    {
        var input = Path.Combine(directory, "input.native");
        File.WriteAllText(input, "preserve native input");
        var output = Path.Combine(directory, "program.dll");
        CreateHardLink(Path.ChangeExtension(output, extension), input);

        DotNetEmitter.Emit("System.Console.WriteLine(42);", output, null, null, [input]);

        Assert.Equal("preserve native input", File.ReadAllText(input));
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void PublishingCopiedNativeAssetPreservesHardlinkedInput()
    {
        var inputs = Path.Combine(directory, "inputs");
        var outputs = Path.Combine(directory, "outputs");
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(outputs);
        var copied = Path.Combine(inputs, "first.native");
        var protectedInput = Path.Combine(inputs, "second.native");
        File.WriteAllText(copied, "first native bytes");
        File.WriteAllText(protectedInput, "preserve second native bytes");
        var target = Path.Combine(outputs, "first.native");
        CreateHardLink(target, protectedInput);

        DotNetEmitter.Emit("System.Console.WriteLine(42);", Path.Combine(outputs, "program.dll"),
            null, null, [copied, protectedInput]);

        Assert.Equal("preserve second native bytes", File.ReadAllText(protectedInput));
        Assert.Equal("first native bytes", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(outputs, "*.tmp"));
    }

    private static void CreateHardLink(string link, string target)
    {
        var start = new ProcessStartInfo("ln") { RedirectStandardError = true };
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(link);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "Hardlink creation timed out.");
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
