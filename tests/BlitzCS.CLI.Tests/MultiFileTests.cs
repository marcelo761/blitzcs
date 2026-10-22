using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class MultiFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-multifile-" + Guid.NewGuid());
    public MultiFileTests() => Directory.CreateDirectory(directory);
    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    private (int Code, string Error, string Assembly) Build(string root)
    {
        var assembly = Path.Combine(directory, "program.dll");
        var errors = new StringWriter();
        return (CliApplication.Run(["build", root, "-o", assembly], new StringWriter(), errors), errors.ToString(), assembly);
    }
    private static async Task<(int Code, string Output, string Error)> Execute(string assembly)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task HybridProjectEmitsOneAssemblyAndCallsInBothDirections()
    {
        Write("state.bb", "Global answer%=0\n");
        Write("functions.bb", "Function Add%(value%)\nanswer=answer+value\nReturn answer\nEnd Function\n");
        Write("cs/bridge.bb", "<?cs module public static class Bridge { public static int Compute() => BB.add(40); } ?>\n");
        var root = Write("main.bb", "Include \"state.bb\"\nInclude \"functions.bb\"\nInclude \"cs\\bridge.bb\"\nanswer=CS::Bridge.Compute()\n<?cs BB.answer+=2; ?>\nPrint answer\n");
        var built = Build(root);
        Assert.True(built.Code == 0, built.Error);
        Assert.Single(Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories));
        var run = await Execute(built.Assembly);
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal("42" + Environment.NewLine, run.Output);
    }

    [Theory]
    [InlineData("<?cs module public static class Bridge { public static int Compute() => missing; } ?>")]
    [InlineData("<?cs int n = missing; ?>")]
    [InlineData("Print CS::Missing.Method()")]
    public void IncludedCSharpDiagnosticsPointToOriginalSource(string source)
    {
        var child = Write("child.bb", source);
        var built = Build(Write("main.bb", "Include \"child.bb\"\n"));
        Assert.Equal(1, built.Code);
        Assert.Contains(child + "(1,", built.Error);
        Assert.False(File.Exists(built.Assembly));
    }

    [Theory]
    [InlineData("Dim values%(0)\nPrint values(1)", 2)]
    [InlineData("Type Node\nField value%\nEnd Type\np.Node=Null\nPrint p\\value", 5)]
    public async Task IncludedRuntimeGuardsPointToOriginalFile(string source, int line)
    {
        var child = Write("child.bb", source);
        var built = Build(Write("main.bb", "Include \"child.bb\"\n"));
        Assert.True(built.Code == 0, built.Error);
        var run = await Execute(built.Assembly);
        Assert.NotEqual(0, run.Code);
        Assert.Contains(child + $"({line},", run.Error);
    }

    [Fact]
    public async Task RepeatedIncludesHaveOneSetOfGlobalsFunctionsAndCSharpModules()
    {
        Write("definitions.bb", "Global answer%=42\nFunction ReadAnswer%()\nReturn answer\nEnd Function\n<?cs module public static class Api { public static int Read() => BB.readanswer(); } ?>\n");
        var built = Build(Write("main.bb", "Include \"definitions.bb\"\nInclude \"./DEFINITIONS.BB\"\nPrint CS::Api.Read()\n"));
        Assert.True(built.Code == 0, built.Error);
        var run = await Execute(built.Assembly);
        Assert.Equal(0, run.Code);
        Assert.Equal("42" + Environment.NewLine, run.Output);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
