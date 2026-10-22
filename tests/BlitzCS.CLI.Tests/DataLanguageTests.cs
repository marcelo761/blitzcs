using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class DataLanguageTests
{
    [Fact]
    public async Task DataReadAndRestoreShareOneTypedProgramStreamAcrossIncludesAndFunctions()
    {
        var result = await BuildAndRun("""
            Include "values.bb"
            Function ReadAtStart%()
                Restore start
                Local value%
                Read value
                Return value
            End Function
            Local whole%, negative%, fraction#, prefix%, numericText$, exact$
            Read whole, negative, fraction, prefix, numericText, exact
            Print whole
            Print negative
            Print fraction
            Print prefix
            Print numericText
            Print exact
            Print ReadAtStart()
            Restore
            Read whole
            Print whole
            """, [("values.bb", """
                Data 1.9,-1.9,"12.75tail","4.75",12.75,"stored"
                .start
                Data 9
                """)]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("1", "-1", "12.75", "4", "12.75", "stored", "9", "1"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task ReadWritesArrayElementsAndPrimitiveObjectFields()
    {
        var result = await BuildAndRun("""
            Type Record
                Field amount#
            End Type
            Dim values%(1)
            Local record.Record=New Record
            Data 12,2.5
            Read values(1),record\amount
            Print values(1)
            Print record\amount
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("12", "2.5"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task ReadingPastTheProgramDataReportsOutOfData()
    {
        var result = await BuildAndRun("Data 1\nRead item%,other%");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Out of data", result.Stderr);
    }

    [Fact]
    public async Task TwoWordElseIfBranchesWithinOneBlock()
    {
        var result = await BuildAndRun("""
            For index%=1 To 3
                If index%=1 Then
                    Print 1
                Else If index%=2 Then
                    Print 2
                Else
                    Print 3
                End If
            Next
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("1", "2", "3"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task BareRestoreBeforeElseStopsAtTheBranchBoundary()
    {
        var result = await BuildAndRun("""
            Data 7
            Local value%
            If 1 Then Restore Else Read value
            Read value
            Print value
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("7"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task InlineCSharpAndBasicReadUseTheSameDataCursor()
    {
        var result = await BuildAndRun("""
            Data 10,20
            Function NextValue%()
                Local value%
                Read value
                Return value
            End Function
            Local left%, right%
            left=CS::DataBridge.Consume()
            Read right
            Print left
            Print right
            <?cs module internal static class DataBridge
            {
                public static int Consume() => BB.nextvalue();
            } ?>
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("10", "20"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> BuildAndRun(
        string source, IReadOnlyList<(string Name, string Source)>? includes = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-data-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            if (includes is not null)
                foreach (var (name, text) in includes)
                    await File.WriteAllTextAsync(Path.Combine(directory, name), text);
            var input = Path.Combine(directory, "main.bb");
            var output = Path.Combine(directory, "main.dll");
            await File.WriteAllTextAsync(input, source);
            using var errors = new StringWriter();
            var build = CliApplication.Run(["build", input, "-o", output], TextWriter.Null, errors);
            Assert.True(build == 0, errors.ToString());

            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(output);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
