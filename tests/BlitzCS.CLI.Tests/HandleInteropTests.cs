using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class HandleInteropTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-handle-interop-" + Guid.NewGuid());
    public HandleInteropTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task HandlesCrossThePrimitiveBridgeAcrossIncludesWithoutExposingObjects()
    {
        File.WriteAllText(Path.Combine(directory, "objects.bb"), """
            Type Node
              Field value%
            End Type
            Global shared.Node=New Node
            Function MakeHandle%()
              Return Handle(shared)
            End Function
            Function ReadHandle%(number%)
              n.Node=Object.Node(number)
              If n=Null Then Return -1
              Return n\value
            End Function
            Function DeleteHandle%(number%)
              n.Node=Object.Node(number)
              Delete n
              Return 0
            End Function
            """);
        File.WriteAllText(Path.Combine(directory, "bridge.bb"), """
            <?cs module
            public static class HandleBridge {
                public static int Read(int number) => BB.readhandle(number);
                public static int Delete(int number) => BB.deletehandle(number);
                public static int Make() => BB.makehandle();
            }
            ?>
            """);
        var result = await Execute("""
            Include "objects.bb"
            Include "bridge.bb"
            shared\value=42
            number%=CS::HandleBridge.Make()
            Print CS::HandleBridge.Read(number)
            CS::HandleBridge.Delete(number)
            Print CS::HandleBridge.Read(number)
            Print Handle(shared)
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("42\n-1\n0\n", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task HandleAndObjectOperandsExecuteOnceAndDeleteEachInvalidatesAllHandles()
    {
        var result = await Execute("""
            Type Node
            End Type
            Global calls%, stored.Node=New Node
            Function Ref.Node()
              calls=calls+1
              Return stored
            End Function
            Function Number%()
              calls=calls+1
              Return Handle(stored)
            End Function
            h%=Handle(Ref())
            Print calls
            n.Node=Object.Node(Number())
            Print calls
            Print n=stored
            Delete Each Node
            Print Object.Node(h)=Null
            Print Handle(stored)
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1\n2\n1\n1\n0\n", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> Execute(string source)
    {
        var input = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "main.dll");
        File.WriteAllText(input, source);
        using var error = new StringWriter();
        Assert.True(CliApplication.Run(["build", input, "-o", output], TextWriter.Null, error) == 0, error.ToString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, (await stdout).Replace("\r\n", "\n"), await stderr);
    }
    public void Dispose() => Directory.Delete(directory, true);
}
