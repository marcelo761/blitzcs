using System.Text;
using BlitzCS.BcRun;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>BcRun: a hand-assembled PE32 EXE runs as a guest process (entry path, ExitProcess exit code, return from the entry, stdio, command line).</summary>
[Collection("GuestRuntime")]
public sealed class BcRunTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("bcrun-").FullName;
    public void Dispose() => Directory.Delete(dir, true);

    private static void P32(List<byte> c, uint v) => c.AddRange(BitConverter.GetBytes(v));

    /// <summary>
    /// start: call GetCommandLineA; mov esi,eax; push -11; call GetStdHandle; mov ebx,eax; push 0; push &amp;written; push 5; push esi; push ebx; call WriteFile;
    /// push 7; call ExitProcess. Writes the first five characters of the command line to stdout and ends the process with code 7.
    /// </summary>
    private string WriteCommandLineExe(uint exitCode = 7)
    {
        var b = new PeFixtureBuilder().AsExecutable();
        var iatCmd = b.Import("KERNEL32.dll", "GetCommandLineA");
        var iatStd = b.Import("KERNEL32.dll", "GetStdHandle");
        var iatWrite = b.Import("KERNEL32.dll", "WriteFile");
        var iatExit = b.Import("KERNEL32.dll", "ExitProcess");
        var baseVa = PeFixtureBuilder.ImageBase;
        var code = new List<byte>();
        void Call(uint iat) { code.AddRange([0xFF, 0x15]); b.Relocate(PeFixtureBuilder.TextRva + (uint)code.Count); P32(code, baseVa + iat); }
        void PushWritten() { code.Add(0x68); b.Relocate(PeFixtureBuilder.TextRva + (uint)code.Count); P32(code, baseVa + PeFixtureBuilder.DataRva + 0x10); }
        Call(iatCmd);
        code.AddRange([0x89, 0xC6]);              // mov esi, eax
        code.AddRange([0x6A, 0xF5]);              // push -11 (STD_OUTPUT_HANDLE)
        Call(iatStd);
        code.AddRange([0x89, 0xC3]);              // mov ebx, eax
        code.AddRange([0x6A, 0x00]); PushWritten(); code.AddRange([0x6A, 0x05, 0x56, 0x53]); // push 0; push &written; push 5; push esi; push ebx
        Call(iatWrite);
        code.Add(0x68); P32(code, exitCode);     // push exitCode
        Call(iatExit);
        code.Add(0xCC);
        var path = Path.Combine(dir, "cmd.exe");
        File.WriteAllBytes(path, b.WithCode([.. code]).EntryPoint(0).Build());
        return path;
    }

    private static (BcRunResult Result, string Stdout) Run(string exe, params string[] args)
    {
        var stdout = new MemoryStream();
        var r = BcRunner.Run(new BcRunOptions { ExePath = exe, Args = args, Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream() });
        return (r, Encoding.Latin1.GetString(stdout.ToArray()));
    }

    [Fact]
    public void ExitProcessCodeAndStdoutAreCaptured()
    {
        var (r, text) = Run(WriteCommandLineExe(7), "a b");
        Assert.Null(r.Failure);
        Assert.True(r.CalledExit);
        Assert.Equal(7u, r.ExitCode);
        Assert.Equal("\"C:\\c", text); // the command line is the quoted C:\cmd.exe path
    }

    [Fact]
    public void EntryPointReturningYieldsEaxAsExitCode()
    {
        var b = new PeFixtureBuilder().AsExecutable().WithCode([0xB8, 0x09, 0x00, 0x00, 0x00, 0xC3]).EntryPoint(0); // mov eax,9; ret
        var path = Path.Combine(dir, "ret.exe");
        File.WriteAllBytes(path, b.Build());
        var (r, _) = Run(path);
        Assert.Null(r.Failure);
        Assert.False(r.CalledExit);
        Assert.Equal(9u, r.ExitCode);
    }

    [Fact]
    public void DllsAreRefused()
    {
        var path = Path.Combine(dir, "x.dll");
        File.WriteAllBytes(path, new PeFixtureBuilder().WithCode([0xB8, 1, 0, 0, 0, 0xC2, 0x0C, 0]).EntryPoint(0).Build());
        var (r, _) = Run(path);
        Assert.NotNull(r.Failure);
        Assert.Contains("DLL", r.Failure);
    }

    [Fact]
    public void CommandLineQuotingFollowsCommandLineToArgvW()
    {
        Assert.Equal("\"C:\\p.exe\" a \"b c\" \"d\\\"e\" \"\"", BcRunner.BuildCommandLine("C:\\p.exe", ["a", "b c", "d\"e", ""]));
        Assert.Equal("\"C:\\p.exe\" \"x y\\\\\"", BcRunner.BuildCommandLine("C:\\p.exe", ["x y\\"]));
    }

    [Fact]
    public void UnknownTargetIsReportedNotThrown()
    {
        var r = BcRunner.Run(new BcRunOptions { ExePath = WriteCommandLineExe(), TargetOs = "plan9" });
        Assert.Contains("Unknown target", r.Failure);
    }

    /// <summary>Observed with the original blitzcc.exe (SDK 1.118) when run with blitzpath=C:\ and guest DLL loading disabled: the program starts, parses, tries to load bin/linker.dll and exits with -1.</summary>
    [Blitz3DSdkFact]
    public void OriginalBlitzccStartsRunsItsMainAndExits()
    {
        var sdk = Blitz3DSdkFactAttribute.Dir!;
        var stdout = new MemoryStream();
        var r = BcRunner.Run(new BcRunOptions
        {
            ExePath = Path.Combine(sdk, "bin", "blitzcc.exe"), SandboxRoot = sdk, AllowTranslationErrors = true, GuestDlls = false,
            Environment = new Dictionary<string, string> { ["blitzpath"] = "C:\\" },
            Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream(),
        });
        Assert.Null(r.Failure);
        Assert.True(r.CalledExit);
        Assert.Equal(0xFFFFFFFFu, r.ExitCode);
        Assert.StartsWith("Unable to open linker.dll", Encoding.Latin1.GetString(stdout.ToArray()));
    }

    [Blitz3DSdkFact]
    public void OriginalBlitzccWithoutBlitzpathSaysSo()
    {
        var sdk = Blitz3DSdkFactAttribute.Dir!;
        var stdout = new MemoryStream();
        var r = BcRunner.Run(new BcRunOptions
        {
            ExePath = Path.Combine(sdk, "bin", "blitzcc.exe"), SandboxRoot = sdk, AllowTranslationErrors = true,
            Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream(),
        });
        Assert.Equal(0xFFFFFFFFu, r.ExitCode);
        Assert.StartsWith("Can't find blitzpath environment variable", Encoding.Latin1.GetString(stdout.ToArray()));
    }

    /// <summary>
    /// With guest DLL loading, blitzcc loads bin/linker.dll and bin/runtime.dll, resolves linkerGetLinker/runtimeGetRuntime through GetProcAddress (cdecl, inferred from the
    /// exports' ret instructions) and then calls a virtual method of the returned C++ object: the vtable points into linker.dll, which the EXE's dispatch table does not know.
    /// That cross-module function-pointer call is the documented next blocker (docs/binary-multimodule.md, Limits).
    /// </summary>
    [Blitz3DSdkFact]
    public void OriginalBlitzccLoadsItsDllsAndStopsAtTheCrossModuleVirtualCall()
    {
        var sdk = Blitz3DSdkFactAttribute.Dir!;
        var r = BcRunner.Run(new BcRunOptions
        {
            ExePath = Path.Combine(sdk, "bin", "blitzcc.exe"), SandboxRoot = sdk, AllowTranslationErrors = true,
            Environment = new Dictionary<string, string> { ["blitzpath"] = "C:\\" },
            Stdout = new MemoryStream(), Stderr = new MemoryStream(), Stdin = new MemoryStream(),
        });
        Assert.NotNull(r.Failure);
        Assert.Contains("indirect target not discovered at build time", r.Failure);
    }
}
