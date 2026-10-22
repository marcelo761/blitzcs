using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class MathIntrinsicTests
{
    [Theory]
    [InlineData("Global min%=$80000000\nPrint Abs(-7)\nPrint Abs(min)\nPrint Sgn(min)", "7\n-2147483648\n-1")]
    [InlineData("Print Abs(7)", "7")]
    [InlineData("Print Sgn(-7)", "-1")]
    [InlineData("Print Sgn(0)", "0")]
    [InlineData("Print Sgn(7)", "1")]
    [InlineData("Print Abs(-7.25)", "7.25")]
    [InlineData("Print Sgn(-7.25)", "-1.0")]
    [InlineData("Print Sgn(0.0)", "0.0")]
    [InlineData("Print Sgn(7.25)", "1.0")]
    [InlineData("Const absZero#=Abs(-0.0)\nGlobal zero#=-0.0\nPrint absZero\nPrint zero\nPrint Abs(zero)\nPrint Sgn(zero)", "-0.0\n-0.0\n0.0\n0.0")]
    public async Task AbsAndSgnKeepTheOperandNumericType(string source, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-math-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "main.bb");
            var output = Path.Combine(directory, "main.dll");
            await File.WriteAllTextAsync(input, source);
            using var errors = new StringWriter();
            var result = CliApplication.Run(["build", input, "-o", output], TextWriter.Null, errors);
            Assert.True(result == 0, errors.ToString());

            var start = new System.Diagnostics.ProcessStartInfo(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(output);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, stderr);
            Assert.Equal("", stderr);
            Assert.Equal(expected + Environment.NewLine, stdout);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
