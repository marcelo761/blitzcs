using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class MsvcrtRuntimeHostTests
{
    [Fact]
    public void RandFollowsTheDocumentedGenerator()
    {
        using var w = new World();
        // MSVCRT: seed = seed * 214013 + 2531011; result = (seed >> 16) & 0x7FFF; first value after srand(1) is 41
        w.Crt("srand", 1);
        Assert.Equal(41u, w.Crt("rand"));
        Assert.Equal(18467u, w.Crt("rand"));
        w.Crt("srand", 1);
        Assert.Equal(41u, w.Crt("rand"));
    }

    [Fact]
    public void RandSeedIsPerThread()
    {
        using var w = new World();
        w.Crt("srand", 1); w.Crt("rand");
        uint other = 0;
        var t = new Thread(() => other = w.Crt("rand")); // a fresh thread starts from seed 1
        t.Start(); t.Join();
        Assert.Equal(41u, other);
    }

    [Fact]
    public void ErrnoIsPerThreadAndStable()
    {
        using var w = new World();
        var p = w.Crt("_errno");
        Assert.Equal(p, w.Crt("_errno"));
        Assert.Equal(0u, w.Mem.ReadUInt32(p));
        uint q = 0;
        var t = new Thread(() => q = w.Crt("_errno")); t.Start(); t.Join();
        Assert.NotEqual(p, q);
    }

    [Fact]
    public void IsctypeUsesTheCLocaleTable()
    {
        using var w = new World();
        Assert.Equal(0x100u, w.Crt("_isctype", 'A', 0x100));
        Assert.Equal(0x84u, w.Crt("_isctype", '7', 0xFFFF));            // digit | hex
        Assert.Equal(0x182u, w.Crt("_isctype", 'f', 0xFFFF));           // lower | hex | alpha
        Assert.Equal(0u, w.Crt("_isctype", 'a', 0x1));                  // lowercase is not upper
        Assert.Equal(0x48u, w.Crt("_isctype", ' ', 0xFFFF));            // space | blank
        Assert.Equal(0u, w.Crt("_isctype", 200, 0xFFFF));               // no high-ASCII classes in the C locale
        Assert.Equal(0u, w.Crt("_isctype", 0xFFFFFFFF, 0xFFFF));        // EOF
        Assert.Equal(0u, w.Crt("_isctype", 300, 0xFFFF));
    }

    [Fact]
    public void VsnprintfReadsTheVaListAndTruncatesLikeSnprintf()
    {
        using var w = new World();
        var list = w.Buf(16);
        w.Mem.WriteUInt32(list, 42); w.Mem.WriteUInt32(list + 4, w.Str("ab"));
        var buf = w.Buf(32);
        Assert.Equal(5u, w.Crt("_vsnprintf", buf, 32, w.Str("%d-%s"), list));
        Assert.Equal("42-ab", w.Cstr(buf));
        Assert.Equal(0xFFFFFFFFu, w.Crt("_vsnprintf", buf, 3, w.Str("%d-%s"), list)); // does not fit: -1, no terminator
        Assert.Equal(5u, w.Crt("_vsnprintf", buf, 5, w.Str("%d-%s"), list));          // exactly fits without terminator
    }

    [Fact]
    public void WcstombsConvertsLatin1AndRejectsWiderCodeUnits()
    {
        using var w = new World();
        var dest = w.Buf(16);
        Assert.Equal(2u, w.Crt("wcstombs", dest, w.Wide("H\u00E9"), 16));
        Assert.Equal([(byte)'H', 0xE9, 0], w.Bytes(dest, 3));
        Assert.Equal(2u, w.Crt("wcstombs", 0, w.Wide("H\u00E9"), 0)); // length query
        Assert.Equal(0xFFFFFFFFu, w.Crt("wcstombs", dest, w.Wide("a\u0100"), 16));
        Assert.Equal(42u, w.Mem.ReadUInt32(w.Crt("_errno"))); // EILSEQ
    }

    [Fact]
    public void OperatorNewAndDeleteUseTheCrtHeap()
    {
        using var w = new World();
        var p = w.Crt("??2@YAPAXI@Z", 24);
        Assert.NotEqual(0u, p);
        w.Mem.WriteUInt32(p, 7);
        Assert.Equal(0u, w.Crt("??3@YAXPAX@Z", p));
        Assert.Throws<Win32UsageException>(() => w.Crt("??3@YAXPAX@Z", p));
        Assert.Equal(0u, w.Crt("??3@YAXPAX@Z", 0)); // delete NULL is a no-op
    }

    [Fact]
    public void ClockCountsMillisecondsSinceProcessStart()
    {
        using var w = new World();
        var a = w.Crt("clock");
        Thread.Sleep(30);
        var b = w.Crt("clock");
        Assert.InRange(b - a, 25u, 2000u);
    }

    [Fact]
    public void StdioBinaryFilesReadSeekAndClose()
    {
        var dir = Directory.CreateTempSubdirectory("blitz-crt").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
            using var w = new World(new Win32Options { SandboxRoot = dir });
            var f = w.Crt("fopen", w.Str("C:\\d.bin"), w.Str("rb"));
            Assert.NotEqual(0u, f);
            var buf = w.Buf(16);
            Assert.Equal(3u, w.Crt("fread", buf, 2, 3, f));                 // 3 items of 2 bytes
            Assert.Equal([1, 2, 3, 4, 5, 6], w.Bytes(buf, 6));
            Assert.Equal(6u, w.Crt("ftell", f));
            Assert.Equal(0u, w.Crt("fseek", f, 0xFFFFFFFE, 2));               // 2 bytes before the end
            Assert.Equal(8u, w.Crt("ftell", f));
            Assert.Equal(2u, w.Crt("fread", buf, 1, 16, f));                 // short read at end of file
            Assert.Equal(0xFFFFFFFFu, w.Crt("fseek", f, 0xFFFFFFF0, 0));    // negative absolute position
            Assert.Equal(22u, w.Mem.ReadUInt32(w.Crt("_errno")));
            Assert.Equal(0u, w.Crt("fclose", f));
            Assert.Throws<Win32UsageException>(() => w.Crt("fclose", f));    // closed FILE
            Assert.Throws<Win32UsageException>(() => w.Crt("ftell", 0x1234));

            Assert.Equal(0u, w.Crt("fopen", w.Str("C:\\missing.bin"), w.Str("rb")));
            Assert.Equal(2u, w.Mem.ReadUInt32(w.Crt("_errno")));            // ENOENT
            Assert.Equal(0u, w.Crt("fopen", w.Str("C:\\d.bin"), w.Str("r"))); // text mode is not translated: refused
            Assert.Equal(22u, w.Mem.ReadUInt32(w.Crt("_errno")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StdioWithoutSandboxFails()
    {
        using var w = new World();
        Assert.Equal(0u, w.Crt("fopen", w.Str("C:\\x"), w.Str("rb")));
        Assert.Equal(13u, w.Mem.ReadUInt32(w.Crt("_errno"))); // EACCES
    }

    [Fact]
    public void GuestCallbackApisFailClearlyWithoutAGuestModule()
    {
        using var w = new World();
        var table = w.Buf(8); w.Mem.WriteUInt32(table, 0x1000);
        Assert.Equal(0u, w.Crt("_initterm", table + 4, table + 4)); // empty range: nothing to call
        Assert.Throws<Win32UsageException>(() => w.Crt("_initterm", table, table + 4));
        var ex = Assert.Throws<Win32UsageException>(() => w.Crt("_ftol"));
        Assert.Contains("guest module", ex.Message);
    }
}

public sealed class WinmmTests
{
    private const string W = "WINMM.dll";

    [Fact]
    public void ThereAreNoDevices()
    {
        using var w = new World();
        Assert.Equal(0u, w.Call(W, "waveOutGetNumDevs"));
        Assert.Equal(0u, w.Call(W, "waveInGetNumDevs"));
        Assert.Equal(0u, w.Call(W, "mixerGetNumDevs"));
    }

    [Fact]
    public void DeviceIdCallsFailWithBadDeviceIdOrNoDriverForTheMapper()
    {
        using var w = new World();
        var p = w.Buf(64);
        Assert.Equal(2u, w.Call(W, "waveOutGetDevCapsA", 0, p, 52));
        Assert.Equal(6u, w.Call(W, "waveOutGetDevCapsA", 0xFFFFFFFF, p, 52));
        Assert.Equal(2u, w.Call(W, "waveInGetDevCapsA", 3, p, 44));
        Assert.Equal(6u, w.Call(W, "waveOutOpen", p, 0xFFFFFFFF, 0, 0, 0, 0));
        Assert.Equal(2u, w.Call(W, "waveOutOpen", p, 0, 0, 0, 0, 1)); // WAVE_FORMAT_QUERY
        Assert.Equal(2u, w.Call(W, "waveInOpen", p, 0, 0, 0, 0, 0));
        Assert.Equal(2u, w.Call(W, "mixerOpen", p, 0, 0, 0, 0));
        Assert.Equal(0u, w.Mem.ReadUInt32(p)); // no handle was produced
    }

    [Theory]
    [InlineData("waveOutClose", 1), InlineData("waveOutReset", 1), InlineData("waveOutPrepareHeader", 3), InlineData("waveOutUnprepareHeader", 3),
     InlineData("waveOutWrite", 3), InlineData("waveOutGetPosition", 3), InlineData("waveInClose", 1), InlineData("waveInReset", 1), InlineData("waveInStart", 1),
     InlineData("waveInAddBuffer", 3), InlineData("waveInPrepareHeader", 3), InlineData("waveInUnprepareHeader", 3), InlineData("mixerClose", 1)]
    public void HandleCallsFailWithInvalidHandle(string api, int argc)
    {
        using var w = new World();
        var args = Enumerable.Repeat(0x1234u, argc).ToArray();
        Assert.Equal(5u, w.Call(W, api, args));
    }

    [Theory]
    [InlineData("mixerGetLineInfoA"), InlineData("mixerGetLineControlsA"), InlineData("mixerGetControlDetailsA"), InlineData("mixerSetControlDetails")]
    public void MixerObjectTypeSelectsTheError(string api)
    {
        using var w = new World();
        Assert.Equal(5u, w.Call(W, api, 1, 0, 0x80000000));   // MIXER_OBJECTF_HANDLE
        Assert.Equal(2u, w.Call(W, api, 0, 0, 0));            // MIXER_OBJECTF_MIXER (device id)
        Assert.Equal(2u, w.Call(W, api, 0, 0, 0x10000000));   // WAVEOUT id
    }

    [Fact]
    public void TimeGetTimeIsMonotonicMilliseconds()
    {
        using var w = new World();
        var a = w.Call(W, "timeGetTime");
        Thread.Sleep(25);
        Assert.InRange(w.Call(W, "timeGetTime") - a, 20u, 2000u);
    }

    [Fact]
    public void KillingAnUnknownTimerIsInvalidParam()
    {
        using var w = new World();
        Assert.Equal(11u, w.Call(W, "timeKillEvent", 77));
    }

    [Fact]
    public void TimerRequiresAGuestAndRejectsEventModes()
    {
        using var w = new World();
        Assert.Throws<Win32UsageException>(() => w.Call(W, "timeSetEvent", 10, 1, 0x1000, 0, 0));
    }

    [Fact]
    public void MciIsNotProvided() => Assert.DoesNotContain(Win32ImportResolver.Coverage.Implemented, a => a.Dll == W && a.Name.StartsWith("mci"));
}

public sealed class WinsockCoreTests
{
    [Fact]
    public void ByteOrderHelpers()
    {
        using var w = new World();
        Assert.Equal(0x3412u, w.Call("WSOCK32.dll", "htons", 0x1234));
        Assert.Equal(0x3412u, w.Call("WS2_32.dll", "ntohs", 0x1234));
        Assert.Equal(0x78563412u, w.Call("WS2_32.dll", "htonl", 0x12345678));
        Assert.Equal(0x12345678u, w.Call("WS2_32.dll", "ntohl", 0x78563412));
        Assert.Equal(0x3412u, w.Call("WS2_32.dll", "htons", 0xFFFF1234)); // only the low 16 bits count
    }

    [Fact]
    public void ByOrdinalWsock32MatchesTheWindowsExportTable()
    {
        var r = Win32ImportResolver.Coverage.Implemented;
        foreach (var (ord, name) in new (ushort, string)[] { (8, "htonl"), (9, "htons"), (14, "ntohl"), (15, "ntohs"), (111, "WSAGetLastError"), (115, "WSAStartup"), (116, "WSACleanup"), (151, "__WSAFDIsSet") })
            Assert.Contains(r, a => a.Dll == "WSOCK32.dll" && a.Name == name && a.Ordinal == ord);
    }

    [Fact]
    public void StartupNegotiatesAndCleanupMatchesReferenceCount()
    {
        using var w = new World();
        var d = w.Buf(400);
        Assert.Equal(0u, w.Call("WS2_32.dll", "WSAStartup", 0x0202, d));
        Assert.Equal((ushort)0x0202, w.Mem.ReadUInt16(d));
        Assert.Equal((ushort)0x0202, w.Mem.ReadUInt16(d + 2));
        Assert.StartsWith("WinSock 2.0", w.Cstr(d + 4));
        Assert.Equal("Running", w.Cstr(d + 261));
        Assert.Equal(0u, w.Call("WS2_32.dll", "WSAStartup", 0x0101, d));
        Assert.Equal((ushort)0x0101, w.Mem.ReadUInt16(d));
        Assert.Equal(10092u, w.Call("WS2_32.dll", "WSAStartup", 0x0009, d)); // major 9 > 2: refused
        Assert.Equal(0u, w.Call("WS2_32.dll", "WSACleanup"));
        Assert.Equal(0u, w.Call("WS2_32.dll", "WSACleanup"));
        Assert.Equal(0xFFFFFFFFu, w.Call("WS2_32.dll", "WSACleanup"));
        Assert.Equal(10093u, w.Call("WS2_32.dll", "WSAGetLastError"));
    }

    [Fact]
    public void StartupRejectsVersionZeroAndReportsTheHighestVersion()
    {
        using var w = new World();
        var d = w.Buf(400);
        Assert.Equal(10092u, w.Call("WS2_32.dll", "WSAStartup", 0x0000, d));
        Assert.Equal((ushort)0x0202, w.Mem.ReadUInt16(d + 2));
        Assert.Equal(10092u, w.Call("WS2_32.dll", "WSAStartup", 0x0003, d));
    }

    [Fact]
    public void FdIsSetScansTheSet()
    {
        using var w = new World();
        var set = w.Buf(260);
        w.Mem.WriteUInt32(set, 2); w.Mem.WriteUInt32(set + 4, 17); w.Mem.WriteUInt32(set + 8, 33);
        Assert.Equal(1u, w.Call("WSOCK32.dll", "__WSAFDIsSet", 33, set));
        Assert.Equal(0u, w.Call("WSOCK32.dll", "__WSAFDIsSet", 99, set));
        w.Mem.WriteUInt32(set, 1);
        Assert.Equal(0u, w.Call("WSOCK32.dll", "__WSAFDIsSet", 33, set)); // beyond fd_count
    }

    [Fact]
    public void SocketsThemselvesAreNotProvided()
    {
        var names = Win32ImportResolver.Coverage.Implemented.Where(a => a.Dll.StartsWith("WSOCK32", StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).ToHashSet();
        Assert.DoesNotContain("socket", names);
        Assert.DoesNotContain("connect", names);
        Assert.DoesNotContain("send", names);
    }
}

public sealed class Kernel32ExtraTests
{
    private const string K = "KERNEL32.dll";

    [Fact]
    public void SystemAndWindowsDirectories()
    {
        using var w = new World();
        var b = w.Buf(64);
        Assert.Equal(19u, w.Call(K, "GetSystemDirectoryA", b, 64));
        Assert.Equal("C:\\WINDOWS\\system32", w.Cstr(b));
        Assert.Equal(10u, w.Call(K, "GetWindowsDirectoryA", b, 64));
        Assert.Equal("C:\\WINDOWS", w.Cstr(b));
        Assert.Equal(11u, w.Call(K, "GetWindowsDirectoryA", b, 10)); // too small: required size with the terminator
    }

    [Fact]
    public void DriveTypes()
    {
        using var w = new World();
        Assert.Equal(3u, w.Call(K, "GetDriveTypeA", w.Str("C:\\")));
        Assert.Equal(3u, w.Call(K, "GetDriveTypeA", w.Str("c:\\")));
        Assert.Equal(1u, w.Call(K, "GetDriveTypeA", w.Str("D:\\")));  // no such drive
        Assert.Equal(0u, w.Call(K, "GetDriveTypeA", w.Str("\\\\srv\\share")));
        Assert.DoesNotContain(Enumerable.Range('A', 26), l => l != 'C' && w.Call(K, "GetDriveTypeA", w.Str((char)l + ":\\")) == 5); // never CD-ROM
    }

    [Fact]
    public void ThreadPriorityValidationAndStorage()
    {
        using var w = new World();
        Assert.Equal(0u, w.Call(K, "GetThreadPriority", 0xFFFFFFFE));
        Assert.Equal(1u, w.Call(K, "SetThreadPriority", 0xFFFFFFFE, 0xFFFFFFFE)); // -2
        Assert.Equal(0xFFFFFFFEu, w.Call(K, "GetThreadPriority", 0xFFFFFFFE));
        Assert.Equal(0u, w.Call(K, "SetThreadPriority", 0xFFFFFFFE, 7));
        Assert.Equal(87u, w.LastError);
        Assert.Equal(0u, w.Call(K, "SetThreadPriority", 0x1234, 0));
        Assert.Equal(6u, w.LastError);
    }

    [Fact]
    public void DisableThreadLibraryCallsNeedsAModule()
    {
        using var w = new World();
        Assert.Equal(0u, w.Call(K, "DisableThreadLibraryCalls", 0x12345678));
        Assert.Equal(6u, w.LastError);
        Assert.Equal(1u, w.Call(K, "DisableThreadLibraryCalls", 0x00400000)); // the main module of the options
    }

    [Fact]
    public void OutputDebugStringGoesToTheHostSinkOnly()
    {
        var seen = new List<string>();
        using var w = new World(new Win32Options { DebugOutput = seen.Add });
        w.Call(K, "OutputDebugStringA", w.Str("hello"));
        Assert.Equal(["hello"], seen);
        using var quiet = new World();
        quiet.Call(K, "OutputDebugStringA", quiet.Str("dropped")); // no debugger: nothing happens, no throw
    }

    [Fact]
    public void CreateThreadNeedsAGuest() => Assert.Throws<Win32UsageException>(() => new World().Call(K, "CreateThread", 0, 0, 0x1000, 0, 0, 0));
}
