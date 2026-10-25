using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ApplicationPackageTests
{
    private const string PublishOptIn = "BLITZCS_TEST_PUBLISH";

    [PublishFact]
    public async Task BuildWithoutOutputPublishesSelfContainedWindowsX64Application()
    {
        if (Environment.GetEnvironmentVariable(PublishOptIn) != "1") return;

        using var fixture = new PackageFixture();
        var result = await fixture.RunCli("build", fixture.SourcePath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);

        var package = Path.Combine(fixture.WorkingDirectory, "dist", "hello");
        var executable = Path.Combine(package, "hello.exe");
        Assert.True(File.Exists(executable), $"Expected package executable at {executable}");
        AssertPeX64Executable(executable, 3);
        Assert.True(File.Exists(Path.Combine(package, "coreclr.dll")), "Self-contained package must include coreclr.dll.");
        Assert.True(File.Exists(Path.Combine(package, "hostfxr.dll")), "Self-contained package must include hostfxr.dll.");

        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "hello.runtimeconfig.json")));
        var options = runtimeConfig.RootElement.GetProperty("runtimeOptions");
        Assert.True(options.TryGetProperty("includedFrameworks", out _), "Self-contained runtimeconfig must list included frameworks.");
        Assert.False(options.TryGetProperty("framework", out _), "Self-contained runtimeconfig must not require an installed shared framework.");

        var guiSource = Path.Combine(fixture.WorkingDirectory, "window.bb");
        await File.WriteAllTextAsync(guiSource, "Print 42\n");
        var guiBuild = await fixture.RunCli("build", guiSource, "--gui");
        Assert.Equal(0, guiBuild.ExitCode);
        var guiExecutable = Path.Combine(fixture.WorkingDirectory, "dist", "window", "window.exe");
        AssertPeX64Executable(guiExecutable, 2);
    }

    [PublishFact]
    public async Task BuildWithLinuxTargetPublishesLinuxX64Executable()
    {
        if (Environment.GetEnvironmentVariable(PublishOptIn) != "1") return;

        using var fixture = new PackageFixture();
        var result = await fixture.RunCli("build", fixture.SourcePath, "--target", "linux-x64");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);

        var executable = Path.Combine(fixture.WorkingDirectory, "dist", "hello", "hello");
        Assert.True(File.Exists(executable), $"Expected package executable at {executable}");
        AssertElfX64Executable(executable);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "libcoreclr.so")), "Self-contained Linux package must include libcoreclr.so.");
        if (OperatingSystem.IsLinux())
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = fixture.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("42\n", NormalizeLineEndings(await stdout));
            Assert.Empty(await stderr);
        }
    }

    [WinePackageFact]
    public async Task DefaultAndAssetPackagesRunConsoleFilesAndWindowsNativeAbiUnderWine()
    {
        using var fixture = new PackageFixture();
        var defaultSource = Path.Combine(fixture.WorkingDirectory, "default.bb");
        await File.WriteAllTextAsync(defaultSource, "Print 42\n");

        // Exercise the no-flags default package before the explicit asset package.
        var defaultBuild = await fixture.RunCli("build", defaultSource);
        Assert.Equal(0, defaultBuild.ExitCode);
        var defaultExe = Path.Combine(fixture.WorkingDirectory, "dist", "default", "default.exe");
        var defaultRun = await fixture.RunWine(defaultExe, "");
        Assert.Equal(0, defaultRun.ExitCode);
        Assert.Equal("42\n", NormalizeLineEndings(defaultRun.StandardOutput));
        var originalExecutable = await File.ReadAllBytesAsync(defaultExe);

        var rebuild = await fixture.RunCli("build", defaultSource);
        Assert.Equal(0, rebuild.ExitCode);
        Assert.Equal(originalExecutable, await File.ReadAllBytesAsync(defaultExe));

        await File.WriteAllTextAsync(defaultSource, "Print (\n");
        var failedRebuild = await fixture.RunCli("build", defaultSource);
        Assert.Equal(1, failedRebuild.ExitCode);
        Assert.Equal(originalExecutable, await File.ReadAllBytesAsync(defaultExe));

        var assets = Path.Combine(fixture.WorkingDirectory, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "data"));
        await File.WriteAllTextAsync(Path.Combine(assets, "data", "asset.txt"), "asset-value");
        var source = Path.Combine(fixture.WorkingDirectory, "probe.bb");
        await File.WriteAllTextAsync(source, "Print CS::WindowsPackageProbe.Run()\n" + "<?cs module\n" +
            "using System;\nusing System.IO;\nusing System.Runtime.InteropServices;\n" +
            "public static class WindowsPackageProbe {\n" +
            "  [DllImport(\"kernel32.dll\", ExactSpelling=true)] private static extern uint GetCurrentProcessId();\n" +
            "  public static string Run() {\n" +
            "    var input = Console.ReadLine() ?? \"<eof>\";\n" +
            "    var asset = File.ReadAllText(\"data/asset.txt\");\n" +
            "    File.WriteAllText(\"roundtrip.txt\", input + \"|\" + asset);\n" +
            "    var roundtrip = File.ReadAllText(\"roundtrip.txt\");\n" +
            "    return input + \"|\" + asset + \"|\" + roundtrip + \"|\" + GetCurrentProcessId();\n" +
            "  }\n}\n?>\n");

        var build = await fixture.RunCli("build", source, "--assets", assets);
        Assert.Equal(0, build.ExitCode);
        Assert.Empty(build.StandardError);

        var package = Path.Combine(fixture.WorkingDirectory, "dist", "probe");
        Assert.Equal("asset-value", await File.ReadAllTextAsync(Path.Combine(package, "data", "asset.txt")));
        var run = await fixture.RunWine(Path.Combine(package, "probe.exe"), "console-input");
        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("Unhandled Exception", run.StandardError, StringComparison.OrdinalIgnoreCase);
        var output = Assert.Single(run.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        var fields = output.Split('|');
        Assert.Equal(new[] { "console-input", "asset-value", "console-input", "asset-value" }, fields[..4]);
        Assert.True(uint.TryParse(fields[4], out var processId) && processId > 0, $"Expected a nonzero kernel32 process id, got '{fields[4]}'.");
        Assert.Equal("console-input|asset-value", await File.ReadAllTextAsync(Path.Combine(package, "roundtrip.txt")));

        var catalog = Path.Combine(fixture.WorkingDirectory, "managed.json");
        var catalogDocument = new
        {
            functions = new[]
            {
                new
                {
                    name = "PackagedFileSize",
                    assembly = typeof(LegacyFiles).Assembly.Location,
                    type = typeof(LegacyFiles).FullName,
                    method = "FileSize",
                    parameters = new[] { "String" },
                    returnType = "Integer"
                }
            }
        };
        await File.WriteAllTextAsync(catalog, JsonSerializer.Serialize(catalogDocument));
        var managedSource = Path.Combine(fixture.WorkingDirectory, "managed.bb");
        await File.WriteAllTextAsync(managedSource, "Print PackagedFileSize(\"data/asset.txt\")\n");
        var managedBuild = await fixture.RunCli("build", managedSource, "--externals", catalog, "--assets", assets);
        Assert.Equal(0, managedBuild.ExitCode);
        var managedPackage = Path.Combine(fixture.WorkingDirectory, "dist", "managed");
        Assert.True(File.Exists(Path.Combine(managedPackage, "Blitz3DCS.Runtime.dll")), "The catalog's managed dependency must be included in the package.");
        var managedRun = await fixture.RunWine(Path.Combine(managedPackage, "managed.exe"), "");
        Assert.Equal(0, managedRun.ExitCode);
        Assert.Equal("11\n", NormalizeLineEndings(managedRun.StandardOutput));
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("windows-arm64")]
    public void BuildRejectsUnsupportedTargetWithoutCreatingOutput(string target)
    {
        var errors = new StringWriter();
        var code = BlitzCS.CLI.CliApplication.Run(["build", "missing.bb", "--target", target], TextWriter.Null, errors);

        Assert.Equal(2, code);
        Assert.Contains("--target expects linux-x64 or windows-x64", errors.ToString());
    }

    private static void AssertPeX64Executable(string path, ushort expectedSubsystem)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal((byte)'Z', bytes[1]);
        var peOffset = BitConverter.ToInt32(bytes, 0x3c);
        Assert.Equal(0x00004550, BitConverter.ToInt32(bytes, peOffset));
        Assert.Equal((ushort)0x8664, BitConverter.ToUInt16(bytes, peOffset + 4));
        Assert.Equal(expectedSubsystem, BitConverter.ToUInt16(bytes, peOffset + 24 + 68));
    }

    private static void AssertElfX64Executable(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }, bytes[..4]);
        Assert.Equal((byte)2, bytes[4]); // ELFCLASS64
        Assert.Equal((ushort)62, BitConverter.ToUInt16(bytes, 18)); // EM_X86_64
        if (!OperatingSystem.IsWindows())
            Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode(path) & UnixFileMode.UserExecute);
    }

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class PackageFixture : IDisposable
    {
        public string WorkingDirectory { get; } = Path.Combine(Path.GetTempPath(), "blitzcs-package-" + Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(WorkingDirectory, "hello.bb");

        public PackageFixture()
        {
            Directory.CreateDirectory(WorkingDirectory);
            File.WriteAllText(SourcePath, "Print 42\n");
        }

        public async Task<ProcessResult> RunCli(params string[] arguments)
        {
            var cliAssembly = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "BlitzCS.CLI.dll");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(cliAssembly);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }

        public async Task<ProcessResult> RunWine(string executable, string standardInput)
        {
            var start = new ProcessStartInfo("wine")
            {
                WorkingDirectory = WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(executable);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (standardInput.Length > 0) await process.StandardInput.WriteLineAsync(standardInput);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }

        public void Dispose() => Directory.Delete(WorkingDirectory, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class PublishFactAttribute : FactAttribute
    {
        public PublishFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(PublishOptIn) != "1")
                Skip = $"Set {PublishOptIn}=1 to run this publish integration test.";
        }
    }

    private sealed class WinePackageFactAttribute : FactAttribute
    {
        public WinePackageFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(PublishOptIn) != "1" ||
                Environment.GetEnvironmentVariable("BLITZCS_TEST_WINE") != "1")
                Skip = $"Run scripts/wine/verify-application-package.sh to execute package runtime coverage under Wine.";
        }
    }
}
