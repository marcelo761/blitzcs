using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class NativeFileRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-native-files-" + Guid.NewGuid());
    public NativeFileRuntimeTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void FileQueriesDistinguishFileDirectoryEmptyAndMissing()
    {
        var file = Path.Combine(directory, "data.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7]);
        var empty = Path.Combine(directory, "empty.bin");
        File.WriteAllBytes(empty, []);
        Assert.Equal(7, LegacyFiles.FileSize(file));
        Assert.Equal(1, LegacyFiles.FileType(file));
        Assert.Equal(7, LegacyFiles.FileSize(file + "\0ignored"));
        Assert.Equal(1, LegacyFiles.FileType(file + "\0ignored"));
        Assert.Equal(0, LegacyFiles.FileSize(file + "\\"));
        Assert.Equal(0, LegacyFiles.FileType(file + "\\"));
        Assert.Equal(2, LegacyFiles.FileType(directory + "\\"));
        Assert.Equal(0, LegacyFiles.FileSize(directory));
        Assert.Equal(2, LegacyFiles.FileType(directory));
        Assert.Equal(0, LegacyFiles.FileSize(empty));
        Assert.Equal(1, LegacyFiles.FileType(empty));
        Assert.Equal(0, LegacyFiles.FileSize(file + ".missing"));
        Assert.Equal(0, LegacyFiles.FileType(file + ".missing"));
        Assert.Equal(0, LegacyFiles.FileSize(""));
        Assert.Equal(0, LegacyFiles.FileType(""));
    }

    [Fact]
    public void FileSizePreservesLow32BitsOfLargeFiles()
    {
        var file = Path.Combine(directory, "large.bin");
        using (var stream = File.Create(file)) stream.SetLength(0x80000003L);
        Assert.Equal(unchecked((int)0x80000003), LegacyFiles.FileSize(file));
    }

    [Fact]
    public void LegacySeparatorsAndCaseResolveWithoutChangingSource()
    {
        var child = Directory.CreateDirectory(Path.Combine(directory, "MixedCase"));
        var file = Path.Combine(child.FullName, "Data.bin");
        File.WriteAllBytes(file, [42]);
        var spelling = Path.Combine(directory, "mixedcase", "DATA.BIN").Replace('/', '\\');
        Assert.Equal(1, LegacyFiles.FileSize(spelling));
        Assert.Equal(1, LegacyFiles.FileType(spelling));
    }

    [Fact]
    public void SystemQueriesReturnUsableCurrentDirectoryAndMissingEnvironmentDefault()
    {
        var current = LegacyFiles.CurrentDir();
        Assert.EndsWith("\\", current);
        Assert.Equal(2, LegacyFiles.FileType(current));
        Assert.Equal("", LegacySystem.GetEnv("BLITZCS_MISSING_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task NativeFileCallsPublishAndRunWithHybridRoundTrip()
    {
        var file = Path.Combine(directory, "payload.bin");
        File.WriteAllBytes(file, new byte[42]);
        var catalog = Path.Combine(directory, "native.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new { functions = new[] {
            new { name="FileSize", assembly=typeof(LegacyFiles).Assembly.Location, type=typeof(LegacyFiles).FullName, method="FileSize", parameters=new[]{"String"} },
            new { name="FileType", assembly=typeof(LegacyFiles).Assembly.Location, type=typeof(LegacyFiles).FullName, method="FileType", parameters=new[]{"String"} }
        }}));
        var source = Path.Combine(directory, "native.bb");
        File.WriteAllText(source, $$"""
            Global total%=0
            Function Measure%(path$)
              total=FileSize(path)
              Return total
            End Function
            <?cs module
            public static class HybridFiles {
              public static int Measure(string path) => BB.measure(path);
            }
            ?>
            Print CS::HybridFiles.Measure("{{file}}")
            Print FileType("{{file}}")
            Print total
            """);
        var outputDirectory = Path.Combine(directory, "published");
        var output = Path.Combine(outputDirectory, "game.dll");
        var errors = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, errors));
        Assert.Equal("", errors.ToString());
        Assert.True(File.Exists(Path.Combine(outputDirectory, "Blitz3DCS.Runtime.dll")));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory=outputDirectory, RedirectStandardOutput=true, RedirectStandardError=true
        };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Join(Environment.NewLine, "42", "1", "42", ""), await stdout);
        Assert.Equal("", await stderr);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
