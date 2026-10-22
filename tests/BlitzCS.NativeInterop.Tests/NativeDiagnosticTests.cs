using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using BlitzCS.NativeInterop.ManagedBinding;
using Xunit;

namespace BlitzCS.NativeInterop.Tests;

public sealed class NativeDiagnosticTests
{
    [Fact]
    public async Task NativeLoadFailureKeepsBasicCallSiteInRuntimeDiagnostic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-native-diagnostic-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "native-failure.bb");
            File.WriteAllText(source, "; original BASIC call site\nPrint NativeAdd(20,22)\n");
            var catalog = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalog, JsonSerializer.Serialize(new { functions = new[] { new {
                name="NativeAdd", assembly=typeof(NativeDemoBindings).Assembly.Location,
                type=typeof(NativeDemoBindings).FullName, method="Add", parameters=new[]{"Integer","Integer"}
            }}}));
            var output = Path.Combine(directory, "out", "app.dll");
            var errors = new StringWriter();
            Assert.Equal(0, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors));
            Assert.Equal("", errors.ToString());
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
                RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=Path.GetDirectoryName(output)!
            };
            start.ArgumentList.Add(output);
            start.Environment["BLITZCS_NATIVEINTEROP_LIBRARY"] = Path.Combine(directory, "absent-native.so");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotEqual(0, process.ExitCode);
            Assert.Equal("", await stdout);
            var diagnostic = await stderr;
            Assert.Contains("absent-native.so", diagnostic);
            Assert.Contains("native-failure.bb:line 2", diagnostic);
        }
        finally { Directory.Delete(directory, true); }
    }
}
