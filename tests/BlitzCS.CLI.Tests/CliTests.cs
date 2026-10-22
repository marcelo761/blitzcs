using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-cli-" + Guid.NewGuid());

    public CliTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task BasicBuildRunsAsStandaloneDotNetProgram()
    {
        var source = Path.Combine(directory, "legacy.bb");
        var assembly = Path.Combine(directory, "legacy.dll");
        await File.WriteAllTextAsync(source,
            "; BlitzBasic original syntax\r\nValue% = 2 + 3 * 4\r\npRiNt vAlUe\r\nPrint \"Hello; BlitzCS\" : End\r\nPrint 99\r\n");
        var errors = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["build", source, "-o", assembly], new StringWriter(), errors));
        Assert.Equal("", errors.ToString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("14" + Environment.NewLine + "Hello; BlitzCS" + Environment.NewLine, await stdout);
        Assert.Equal("", await stderr);
    }

    [Fact]
    public void InvalidInputReturnsDiagnosticWithoutAssembly()
    {
        var source = Path.Combine(directory, "invalid.bb");
        var assembly = Path.Combine(directory, "invalid.dll");
        File.WriteAllText(source, "Print (\n");
        var errors = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["build", source, "-o", assembly], new StringWriter(), errors));
        Assert.Contains("invalid.bb", errors.ToString());
        Assert.False(File.Exists(assembly));
    }

    [Fact]
    public void InvalidArgumentsReturnUsageError()
    {
        var errors = new StringWriter();
        Assert.Equal(2, CliApplication.Run(["build"], new StringWriter(), errors));
        Assert.Contains("Usage:", errors.ToString());
    }

    [Theory]
    [InlineData("Print 2 + 3 * 4", "14")]
    [InlineData("Print (2 + 3) * 4", "20")]
    [InlineData("Print -7 / 2", "-3")]
    [InlineData("Print 7 / -2", "-3")]
    [InlineData("Print missing%", "0")]
    [InlineData("a=8 : A%=a+1 : Print a", "9")]
    [InlineData("Print 2147483647 + 1", "-2147483648")]
    [InlineData("Print \"C:\\temp;file\"", "C:\\temp;file")]
    public async Task GeneratedProgramComputesExpectedValues(string basic, string expected)
    {
        var source = Path.Combine(directory, "with spaces.bb");
        var assembly = Path.Combine(directory, "with spaces.dll");
        await File.WriteAllTextAsync(source, basic);
        var errors = new StringWriter();
        var code = CliApplication.Run(["build", source, "-o", assembly], new StringWriter(), errors);
        Assert.True(code == 0, errors.ToString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(expected + Environment.NewLine, await stdout);
        Assert.Equal("", await stderr);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
