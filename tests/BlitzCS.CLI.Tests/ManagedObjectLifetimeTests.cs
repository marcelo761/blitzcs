using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ManagedObjectLifetimeTests
{
    [Fact]
    public async Task DeletedAliasKeepsItsPositionAcrossCollectionChurnAndHybridCalls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-managed-lifetime-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "lifetime.bb");
            var assembly = Path.Combine(directory, "lifetime.dll");
            await File.WriteAllTextAsync(source, """
                Type Node
                    Field value%
                End Type
                a.Node=New Node : a\value=1
                alias.Node=New Node
                c.Node=New Node : c\value=3
                Delete alias
                For i=1 To 100000
                    temporary.Node=New Node
                    Delete temporary
                Next
                temporary=Null
                Print CS::Collector.Collect()
                Insert c Before a
                Print After alias=Null
                previous.Node=Before alias
                Print previous\value
                <?cs System.GC.Collect(); ?>
                Insert c After a
                Print After alias=Null
                previous=Before alias
                Print previous\value
                Delete alias
                Delete Each Node
                Print First Node=Null
                <?cs module
                public static class Collector
                {
                    public static int Collect()
                    {
                        System.GC.Collect();
                        System.GC.WaitForPendingFinalizers();
                        System.GC.Collect();
                        return BB.answer();
                    }
                }
                ?>
                Function Answer%()
                    Return 42
                End Function
                """);
            var errors = new StringWriter();
            Assert.True(CliApplication.Run(["build", source, "-o", assembly], new StringWriter(), errors) == 0, errors.ToString());
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(assembly);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, await error);
            Assert.Equal(string.Join(Environment.NewLine, "42", "1", "1", "1", "3", "1", ""), await output);
        }
        finally { Directory.Delete(directory, true); }
    }
}
