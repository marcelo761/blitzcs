using Blitz3DCS.Runtime;
using BlitzCS.LegacyRuntime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacySystemTests
{
    [Theory]
    [InlineData("https://scpcbgame.com", "https://scpcbgame.com", null)]
    [InlineData("  program.exe  -safe mode  ", "program.exe", "-safe mode")]
    [InlineData("\"program files\\game.exe\"  -windowed  ", "program files\\game.exe", "-windowed")]
    [InlineData("  \"https://example.test/path?a=b\" ", "https://example.test/path?a=b", null)]
    public void ExecFile_splits_shell_command_and_passes_uri_or_executable_without_launching(
        string command, string expectedFile, string? expectedParameters)
    {
        string? actualFile = null;
        string? actualParameters = null;
        LegacySystem.ShellExecuteOverride = (file, parameters) =>
        {
            actualFile = file;
            actualParameters = parameters;
            return 33;
        };
        try
        {
            Assert.Equal(1, LegacySystem.ExecFile(command));
            Assert.Equal(expectedFile, actualFile);
            Assert.Equal(expectedParameters, actualParameters);
        }
        finally { LegacySystem.ShellExecuteOverride = null; }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(2)]
    [InlineData(0)]
    public void ExecFile_returns_false_for_ShellExecute_failure_codes(int nativeResult)
    {
        LegacySystem.ShellExecuteOverride = (_, _) => nativeResult;
        try { Assert.Equal(0, LegacySystem.ExecFile("missing-file.exe")); }
        finally { LegacySystem.ShellExecuteOverride = null; }
    }

    [Fact]
    public void ExecFile_empty_command_returns_false_without_calling_shell()
    {
        var called = false;
        LegacySystem.ShellExecuteOverride = (_, _) => { called = true; return 33; };
        try
        {
            Assert.Equal(0, LegacySystem.ExecFile(""));
            Assert.False(called);
        }
        finally { LegacySystem.ShellExecuteOverride = null; }
    }

    [Fact]
    public void ApiGetModuleFileName_copies_native_bytes_and_respects_both_capacities()
    {
        var bank = LegacyBanks.CreateBank(32);
        var requestedModule = -1;
        LegacySystem.ModuleFileNameOverride = (module, buffer) =>
        {
            requestedModule = module;
            var path = System.Text.Encoding.ASCII.GetBytes("game.exe");
            var copied = Math.Min(path.Length, buffer.Length);
            path.AsSpan(0, copied).CopyTo(buffer);
            if (copied < buffer.Length) buffer[copied] = 0;
            return (uint)copied;
        };
        try
        {
            Assert.Equal(4, LegacySystem.api_GetModuleFileName(0, bank, 4));
            Assert.Equal(0, requestedModule);
            Assert.Equal(new byte[] { (byte)'g', (byte)'a', (byte)'m', (byte)'e' }, LegacyBankStreams.ReadRange(bank, 0, 4));

            Assert.Equal(8, LegacySystem.api_GetModuleFileName(0, bank, 32));
            Assert.Equal(new byte[] { (byte)'g', (byte)'a', (byte)'m', (byte)'e', (byte)'.', (byte)'e', (byte)'x', (byte)'e', 0 },
                LegacyBankStreams.ReadRange(bank, 0, 9));
            Assert.Equal(0, LegacySystem.api_GetModuleFileName(0, bank, 0));
        }
        finally
        {
            LegacySystem.ModuleFileNameOverride = null;
            LegacyBanks.FreeBank(bank);
        }
    }
}
