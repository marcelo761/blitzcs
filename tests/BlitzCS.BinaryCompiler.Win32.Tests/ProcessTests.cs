using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class ProcessTests
{
    private static Win32Options Opts() => new()
    {
        CommandLine = "\"C:\\game\\game.exe\" -windowed",
        MainModulePath = "C:\\game\\game.exe",
        Environment = new Dictionary<string, string> { ["PATH"] = "C:\\bin", ["Temp"] = "C:\\tmp" },
    };

    [Fact]
    public void CallTraceReportsEveryApiThatRuns()
    {
        var seen = new List<string>();
        using var w = new World(new Win32Options { CallTrace = seen.Add });
        w.Kernel("SetLastError", 7);
        w.Kernel("GetLastError");
        Assert.Equal(2, seen.Count);
        Assert.EndsWith("KERNEL32.dll!SetLastError", seen[0]);
        Assert.EndsWith("KERNEL32.dll!GetLastError", seen[1]);
        using var silent = new World(); // no hook: nothing is observed and nothing breaks
        silent.Kernel("GetLastError");
    }

    [Fact]
    public void AbnormalGuestThreadTerminationRefusesFurtherWin32CallsWithTheCause()
    {
        using var w = new World();
        w.Kernel("GetLastError"); // fine before the fault
        w.R.Context.PoisonProcess(new InvalidOperationException("boom"));
        var error = Assert.Throws<Win32UsageException>(() => w.Kernel("GetLastError"));
        Assert.Contains("InvalidOperationException", error.Message);
        Assert.Contains("boom", error.Message);
        w.R.Context.PoisonProcess(new Exception("second")); // the first cause is kept
        Assert.Contains("boom", Assert.Throws<Win32UsageException>(() => w.Kernel("GetLastError")).Message);
    }

    [Fact]
    public void ErrorsArePerThread()
    {
        using var w = new World();
        w.Kernel("SetLastError", 1234);
        Assert.Equal(1234u, w.LastError);
        uint other = 99;
        var t = new Thread(() => other = w.LastError);
        t.Start(); t.Join();
        Assert.Equal(0u, other);
    }

    [Fact]
    public void ThreadAndProcessIdentity()
    {
        using var w = new World();
        Assert.Equal(0xFFFFFFFFu, w.Kernel("GetCurrentProcess"));
        Assert.Equal(0xFFFFFFFEu, w.Kernel("GetCurrentThread"));
        Assert.NotEqual(0u, w.Kernel("GetCurrentProcessId"));
        var id = w.Kernel("GetCurrentThreadId");
        Assert.Equal(id, w.Kernel("GetCurrentThreadId"));
        uint other = id;
        var t = new Thread(() => other = w.Kernel("GetCurrentThreadId"));
        t.Start(); t.Join();
        Assert.NotEqual(id, other);
    }

    [Fact]
    public void ExitProcessNeverEndsTheHost()
    {
        using var w = new World();
        var e = Assert.Throws<Win32ExitProcessException>(() => w.Kernel("ExitProcess", 7));
        Assert.Equal(7u, e.ExitCode);
        e = Assert.Throws<Win32ExitProcessException>(() => w.Kernel("TerminateProcess", 0xFFFFFFFF, 3));
        Assert.Equal(3u, e.ExitCode);
        Assert.Equal(0u, w.Kernel("TerminateProcess", 0x1234, 3)); // some other process: refused
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void VersionReportsWindowsXp()
    {
        using var w = new World();
        Assert.Equal(0x0A280105u, w.Kernel("GetVersion"));
        var p = w.Buf(200);
        w.Mem.WriteUInt32(p, 148);
        Assert.Equal(1u, w.Kernel("GetVersionExA", p));
        Assert.Equal(5u, w.Mem.ReadUInt32(p + 4)); Assert.Equal(1u, w.Mem.ReadUInt32(p + 8));
        Assert.Equal(2600u, w.Mem.ReadUInt32(p + 12)); Assert.Equal(2u, w.Mem.ReadUInt32(p + 16));
        Assert.Equal("Service Pack 3", w.Cstr(p + 20));
        w.Mem.WriteUInt32(p, 156);
        Assert.Equal(1u, w.Kernel("GetVersionExA", p));
        Assert.Equal(3, w.Mem.ReadUInt16(p + 148));
        w.Mem.WriteUInt32(p, 100);
        Assert.Equal(0u, w.Kernel("GetVersionExA", p));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
    }

    [Fact]
    public void CustomVersionIsHonoured()
    {
        using var w = new World(new Win32Options { Version = new Win32Version(6, 1, 7601, "SP1", 1) });
        Assert.Equal(7601u << 16 | 1u << 8 | 6u, w.Kernel("GetVersion"));
    }

    [Fact]
    public void CommandLineIsStable()
    {
        using var w = new World(Opts());
        var p = w.Kernel("GetCommandLineA");
        Assert.Equal("\"C:\\game\\game.exe\" -windowed", w.Cstr(p));
        Assert.Equal(p, w.Kernel("GetCommandLineA"));
    }

    [Fact]
    public void EnvironmentBlocksAreSortedFreeableAndNeverLeakTheHost()
    {
        using var w = new World(Opts());
        var a = w.Kernel("GetEnvironmentStrings");
        var text = Encoding.Latin1.GetString(w.Bytes(a, "PATH=C:\\bin\0Temp=C:\\tmp\0\0".Length));
        Assert.Equal("PATH=C:\\bin\0Temp=C:\\tmp\0\0", text);
        var wide = w.Kernel("GetEnvironmentStringsW");
        Assert.Equal("PATH=C:\\bin", w.Mem.ReadWideString(wide));
        Assert.Equal(1u, w.Kernel("FreeEnvironmentStringsA", a));
        Assert.Equal(0u, w.Kernel("FreeEnvironmentStringsA", a));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(1u, w.Kernel("FreeEnvironmentStringsW", wide));
        Assert.False(w.Mem.IsMapped(a, 1));
        using var empty = new World();
        var e = empty.Kernel("GetEnvironmentStrings");
        Assert.Equal(new byte[] { 0, 0 }, empty.Bytes(e, 2));
    }

    [Fact]
    public void EnvironmentVariableLookup()
    {
        using var w = new World(Opts());
        var buf = w.Buf(64);
        Assert.Equal(6u, w.Kernel("GetEnvironmentVariableA", w.Str("path"), buf, 64)); // case-insensitive
        Assert.Equal("C:\\bin", w.Cstr(buf));
        Assert.Equal(7u, w.Kernel("GetEnvironmentVariableA", w.Str("PATH"), buf, 6)); // too small: size with terminator
        Assert.Equal(0u, w.Kernel("GetEnvironmentVariableA", w.Str("HOME"), buf, 64));
        Assert.Equal(Win32Error.EnvVarNotFound, w.LastError);
    }

    [Fact]
    public void ModulesProcAddressesAndLoadLibrary()
    {
        using var w = new World(Opts());
        var k32 = w.Kernel("GetModuleHandleA", w.Str("KERNEL32.dll"));
        Assert.NotEqual(0u, k32);
        Assert.Equal(k32, w.Kernel("GetModuleHandleA", w.Str("kernel32")));
        Assert.Equal(k32, w.Kernel("LoadLibraryA", w.Str("Kernel32.DLL")));
        Assert.Equal(0x00400000u, w.Kernel("GetModuleHandleA", 0));
        Assert.Equal(0u, w.Kernel("GetModuleHandleA", w.Str("nothere.dll")));
        Assert.Equal(Win32Error.ModNotFound, w.LastError);
        Assert.Equal(0u, w.Kernel("LoadLibraryA", w.Str("nothere.dll")));
        Assert.Equal(Win32Error.ModNotFound, w.LastError);
        Assert.Equal(0u, w.Kernel("LoadLibraryA", w.Str("")));
        Assert.Equal(1u, w.Kernel("FreeLibrary", k32));
        Assert.Equal(0u, w.Kernel("FreeLibrary", 0x1234));

        w.R.Context.RegisterModule("ogg.dll", 0x10000000, "C:\\game\\ogg.dll", new Dictionary<string, uint> { ["ogg_sync_init"] = 0x10001234, ["#3"] = 0x10005555 });
        var ogg = w.Kernel("LoadLibraryA", w.Str("OGG"));
        Assert.Equal(0x10000000u, ogg);
        Assert.Equal(0x10001234u, w.Kernel("GetProcAddress", ogg, w.Str("ogg_sync_init")));
        Assert.Equal(0x10005555u, w.Kernel("GetProcAddress", ogg, 3));
        Assert.Equal(0u, w.Kernel("GetProcAddress", ogg, w.Str("missing")));
        Assert.Equal(Win32Error.ProcNotFound, w.LastError);
        Assert.Equal(0u, w.Kernel("GetProcAddress", 0x777, w.Str("x")));
        Assert.Equal(Win32Error.ModNotFound, w.LastError);
        // emulated functions are only exposed once the runtime hook exists
        Assert.Equal(0u, w.Kernel("GetProcAddress", k32, w.Str("HeapAlloc")));
        Assert.Equal(Win32Error.ProcNotFound, w.LastError);
        w.R.Context.SetExportStubAddressProvider((dll, name) => dll == "kernel32.dll" && name == "HeapAlloc" ? 0x7000u : null);
        Assert.Equal(0x7000u, w.Kernel("GetProcAddress", k32, w.Str("HeapAlloc")));
        Assert.Equal(0u, w.Kernel("GetProcAddress", k32, w.Str("Nope")));
        Assert.Throws<InvalidOperationException>(() => w.R.Context.RegisterModule("OGG.DLL", 0x20000000, "x"));
    }

    [Fact]
    public void ModuleFileNameTruncatesLikeWindows()
    {
        using var w = new World(Opts());
        var buf = w.Buf(64);
        Assert.Equal(16u, w.Kernel("GetModuleFileNameA", 0, buf, 64));
        Assert.Equal("C:\\game\\game.exe", w.Cstr(buf));
        Assert.Equal(5u, w.Kernel("GetModuleFileNameA", 0, buf, 5));
        Assert.Equal("C:\\g", w.Cstr(buf));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        Assert.Equal(0u, w.Kernel("GetModuleFileNameA", 0x999, buf, 64));
        Assert.Equal(Win32Error.ModNotFound, w.LastError);
        var k32 = w.Kernel("GetModuleHandleA", w.Str("kernel32.dll"));
        w.Kernel("GetModuleFileNameA", k32, buf, 64);
        Assert.Equal("C:\\WINDOWS\\system32\\kernel32.dll", w.Cstr(buf));
    }

    [Fact]
    public void StartupInfoAndTimers()
    {
        using var w = new World();
        var si = w.Buf(68);
        w.Mem.Fill(si, 0xFF, 68);
        w.Kernel("GetStartupInfoA", si);
        Assert.Equal(68u, w.Mem.ReadUInt32(si));
        Assert.All(w.Bytes(si + 4, 64), x => Assert.Equal(0, x));
        var q = w.Buf(8);
        Assert.Equal(1u, w.Kernel("QueryPerformanceFrequency", q));
        Assert.True(w.Mem.ReadUInt32(q) != 0 || w.Mem.ReadUInt32(q + 4) != 0);
        w.Kernel("QueryPerformanceCounter", q);
        var t1 = BitConverter.ToUInt64(w.Bytes(q, 8));
        Thread.Sleep(5);
        w.Kernel("QueryPerformanceCounter", q);
        Assert.True(BitConverter.ToUInt64(w.Bytes(q, 8)) > t1);
        Assert.Equal(0u, w.Kernel("QueryPerformanceCounter", 0));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        var tick = w.Kernel("GetTickCount");
        w.Kernel("Sleep", 20);
        Assert.True(w.Kernel("GetTickCount") - tick >= 15);
        Assert.Throws<Win32UsageException>(() => w.Kernel("Sleep", 0xFFFFFFFF));
    }

    [Fact]
    public void BadPointerProbesAndUnhandledFilter()
    {
        using var w = new World();
        var p = w.Buf(16);
        Assert.Equal(0u, w.Kernel("IsBadReadPtr", p, 16));
        Assert.Equal(1u, w.Kernel("IsBadReadPtr", p, 5000));
        Assert.Equal(1u, w.Kernel("IsBadWritePtr", 0, 4));
        Assert.Equal(0u, w.Kernel("IsBadWritePtr", 0, 0));
        Assert.Equal(0u, w.Kernel("IsBadCodePtr", p));
        Assert.Equal(1u, w.Kernel("IsBadCodePtr", 0));
        Assert.Equal(0u, w.Kernel("SetUnhandledExceptionFilter", 0x1111));
        Assert.Equal(0x1111u, w.Kernel("SetUnhandledExceptionFilter", 0x2222));
    }

    [Fact]
    public void StandardHandlesAndHandleCount()
    {
        using var w = new World();
        Assert.Equal(3u, w.Kernel("GetStdHandle", 0xFFFFFFF6));
        Assert.Equal(7u, w.Kernel("GetStdHandle", 0xFFFFFFF5));
        Assert.Equal(11u, w.Kernel("GetStdHandle", 0xFFFFFFF4));
        Assert.Equal(0xFFFFFFFFu, w.Kernel("GetStdHandle", 5));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(2u, w.Kernel("GetFileType", 7));
        Assert.Equal(0u, w.Kernel("GetFileType", 0x9999));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(32u, w.Kernel("SetHandleCount", 32));
        Assert.Equal(0u, w.Kernel("SetStdHandle", 0xFFFFFFF5, 0x9999));
        Assert.Equal(1u, w.Kernel("SetStdHandle", 0xFFFFFFF5, 11));
        Assert.Equal(11u, w.Kernel("GetStdHandle", 0xFFFFFFF5));
    }
}
