using System.Diagnostics;
using BlitzCS.CLI;
using BlitzCS.NativeInterop.ManagedBinding;
using Xunit;

namespace BlitzCS.NativeInterop.Tests;

public sealed class LegacyMemoryConcurrencyTests
{
    [Theory]
    [InlineData("pointer")]
    [InlineData("bytes")]
    [InlineData("race")]
    public async Task NativeWriteAndCloseRespectBankLifetimeLockOrder(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-bank-concurrency-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BankCloseConcurrency.cs.txt"));
            var output = Path.Combine(directory, "BankCloseConcurrency.dll");
            DotNetEmitter.Emit(source, output, "Program", [typeof(LegacyMemoryBindings).Assembly.Location],
                [Path.Combine(AppContext.BaseDirectory, "native", "libblitzcs_native_buffer.so")]);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(output);
            start.ArgumentList.Add(scenario);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario}, exit {process.ExitCode}: {await stderr}");
            Assert.Equal("bank-close-complete" + Environment.NewLine, await stdout);
            Assert.Equal("", await stderr);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
