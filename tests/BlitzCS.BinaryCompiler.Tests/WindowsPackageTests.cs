using System.Diagnostics;
using System.Reflection.PortableExecutable;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class WindowsPackageTests
{
    [PublishFact]
    public async Task Default_package_contains_and_runs_a_translated_PE32_library()
    {
        var root = Directory.CreateTempSubdirectory("blitzcs-binary-package-").FullName;
        try
        {
            var library = new PeFixtureBuilder().WithImageBase(0x60000000)
                .WithCode([0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0xC3])
                .Export("_sum", 0).Build(); // mov eax,[esp+4]; add eax,[esp+8]; ret
            File.WriteAllBytes(Path.Combine(root, "sum.dll"), library);
            var source = Path.Combine(root, "Main.bb");
            File.WriteAllText(source, "Print AddInts(20, 22)\n");
            var catalog = Path.Combine(root, "catalog.json");
            File.WriteAllText(catalog, """
                {"functions":[{"name":"AddInts","parameters":["Integer","Integer"],"returnType":"Integer",
                "binary":{"library":"sum.dll","export":"_sum","convention":"cdecl"}}]}
                """);
            // A separate CLI process exercises SDK publishing without changing the test process directory.
            var cli = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliApplication).Assembly.Location, "build", source, "--externals", catalog })
                cli.ArgumentList.Add(argument);
            var built = await Run(cli);
            Assert.True(built.ExitCode == 0, built.Output + built.Error);
            var package = Path.Combine(root, "dist", "Main");
            var executable = Path.Combine(package, "Main.exe");
            using (var pe = new PEReader(File.OpenRead(executable)))
            {
                Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
                Assert.Equal(PEMagic.PE32Plus, pe.PEHeaders.PEHeader!.Magic);
            }
            Assert.True(File.Exists(Path.Combine(package, "coreclr.dll")));
            Assert.Equal(library, File.ReadAllBytes(Path.Combine(package, "sum.dll")));
            var artifact = new ArtifactSerializer().Deserialize(File.ReadAllBytes(Path.Combine(package, "sum.dll.bcx")));
            Assert.Equal(BinaryTargets.WindowsX64, artifact.TargetOs);
            Assert.True(File.Exists(Path.Combine(package, "BlitzCS.BinaryCompiler.Runtime.dll")));
            if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BLITZCS_TEST_WINE") == "1")
            {
                var launch = new ProcessStartInfo(OperatingSystem.IsWindows() ? executable : "wine")
                {
                    WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (!OperatingSystem.IsWindows()) launch.ArgumentList.Add(executable);
                var ran = await Run(launch);
                Assert.True(ran.ExitCode == 0, ran.Output + ran.Error);
                Assert.Equal("42\n", ran.Output.Replace("\r\n", "\n"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private sealed class PublishFactAttribute : FactAttribute
    {
        public PublishFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BLITZCS_TEST_PUBLISH") != "1")
                Skip = "Set BLITZCS_TEST_PUBLISH=1 for SDK package coverage; BLITZCS_TEST_WINE=1 also executes it under Wine.";
        }
    }
}
