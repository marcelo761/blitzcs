using System.Runtime.Intrinsics.X86;
using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>The kernel32 start-up set of an MSVC executable (docs/binary-win32.md, "Start-up set").</summary>
public sealed class Kernel32StartupTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitz-startup-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static string W(World w, uint p) => w.Mem.ReadWideString(p);

    [Fact]
    public void WideCommandLineModuleNameAndHandles()
    {
        using var w = new World(new Win32Options { CommandLine = "\"C:\\p.exe\" x", MainModulePath = "C:\\p.exe", MainModuleBase = 0x50000000 });
        Assert.Equal("\"C:\\p.exe\" x", W(w, w.Kernel("GetCommandLineW")));
        Assert.Equal(w.Kernel("GetCommandLineW"), w.Kernel("GetCommandLineW"));
        var buf = w.Buf(64);
        Assert.Equal(8u, w.Kernel("GetModuleFileNameW", 0, buf, 32));
        Assert.Equal("C:\\p.exe", W(w, buf));
        Assert.Equal(0x50000000u, w.Kernel("GetModuleHandleW", 0));
        Assert.NotEqual(0u, w.Kernel("GetModuleHandleW", w.Wide("KERNEL32.dll")));
        Assert.Equal(0u, w.Kernel("GetModuleHandleW", w.Wide("nothere.dll")));
        Assert.Equal(Win32Error.ModNotFound, w.LastError);
        // too small: truncated, terminated, ERROR_INSUFFICIENT_BUFFER
        Assert.Equal(4u, w.Kernel("GetModuleFileNameW", 0, buf, 4));
        Assert.Equal("C:\\", W(w, buf));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        var si = w.Buf(68);
        w.Kernel("GetStartupInfoW", si);
        Assert.Equal(68u, w.Mem.ReadUInt32(si));
    }

    [Fact]
    public void GetModuleHandleExWFlags()
    {
        using var w = new World(new Win32Options { MainModuleBase = 0x50000000 });
        var out_ = w.Buf(4);
        Assert.Equal(1u, w.Kernel("GetModuleHandleExW", 2, 0, out_));
        Assert.Equal(0x50000000u, w.Mem.ReadUInt32(out_));
        Assert.Equal(1u, w.Kernel("GetModuleHandleExW", 0, w.Wide("kernel32.dll"), out_));
        Assert.NotEqual(0u, w.Mem.ReadUInt32(out_));
        Assert.Equal(0u, w.Kernel("GetModuleHandleExW", 3, 0, out_));               // PIN with UNCHANGED_REFCOUNT
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("GetModuleHandleExW", 0, w.Wide("nothere.dll"), out_));
        Assert.Equal(0u, w.Mem.ReadUInt32(out_));
        Assert.Equal(0u, w.Kernel("GetModuleHandleExW", 4, 0x1234, out_));          // not an image page in this memory
    }

    [Fact]
    public void TimeAndTickCounters()
    {
        using var w = new World();
        var ft = w.Buf(8);
        w.Kernel("GetSystemTimeAsFileTime", ft);
        var t = DateTime.FromFileTimeUtc(BitConverter.ToInt64(w.Bytes(ft, 8)));
        Assert.InRange((DateTime.UtcNow - t).TotalSeconds, -1, 5);
        var low = w.Kernel("GetTickCount64");
        Assert.Equal(0u, w.Ctx().HighResult);
        Assert.InRange(low, 0u, 60_000u);
        Thread.Sleep(30);
        Assert.True(w.Kernel("GetTickCount64") >= low + 20);
    }

    [Fact]
    public void ProcessorFeaturesComeFromTheHostCpu()
    {
        using var w = new World();
        Assert.Equal(Sse2.IsSupported ? 1u : 0u, w.Kernel("IsProcessorFeaturePresent", 10));
        Assert.Equal(Sse.IsSupported ? 1u : 0u, w.Kernel("IsProcessorFeaturePresent", 6));
        Assert.Equal(Avx2.IsSupported ? 1u : 0u, w.Kernel("IsProcessorFeaturePresent", 40));
        Assert.Equal(0u, w.Kernel("IsProcessorFeaturePresent", 23));  // PF_FASTFAIL_AVAILABLE: the runtime has no int 29h trap
        Assert.Equal(0u, w.Kernel("IsProcessorFeaturePresent", 999)); // unknown
        Assert.Equal(0u, w.Kernel("IsDebuggerPresent"));
    }

    [Fact]
    public void UnhandledExceptionFilterContinuesSearch() { using var w = new World(); Assert.Equal(0u, w.Kernel("UnhandledExceptionFilter", 0)); }

    [Fact]
    public void PointersEncodeReversiblyAndDiffer()
    {
        using var w = new World();
        foreach (var p in new uint[] { 0x401000, 0, 0xFFFFFFFF })
        {
            var e = w.Kernel("EncodePointer", p);
            Assert.NotEqual(p, e);
            Assert.Equal(p, w.Kernel("DecodePointer", e));
        }
    }

    [Fact]
    public void SListPushPopFlushAndDepth()
    {
        using var w = new World();
        var head = w.Buf(16); var a = w.Buf(16); var b = w.Buf(16);
        w.Kernel("InitializeSListHead", head);
        Assert.Equal(0u, w.Kernel("InterlockedPopEntrySList", head));
        Assert.Equal(0u, w.Kernel("InterlockedPushEntrySList", head, a));
        Assert.Equal(a, w.Kernel("InterlockedPushEntrySList", head, b));
        Assert.Equal(2u, w.Kernel("QueryDepthSList", head));
        Assert.Equal(b, w.Kernel("InterlockedPopEntrySList", head));
        Assert.Equal(1u, w.Kernel("QueryDepthSList", head));
        Assert.Equal(a, w.Kernel("InterlockedFlushSList", head));
        Assert.Equal(0u, w.Kernel("QueryDepthSList", head));
        Assert.Equal(0u, w.Kernel("InterlockedPopEntrySList", head));
        Assert.Throws<Win32UsageException>(() => w.Kernel("InitializeSListHead", head + 4));
    }

    [Fact]
    public void SListIsAtomicUnderContention()
    {
        using var w = new World();
        var head = w.Buf(16); w.Kernel("InitializeSListHead", head);
        var entries = Enumerable.Range(0, 200).Select(_ => w.Buf(16)).ToArray();
        Parallel.ForEach(entries, e => w.Kernel("InterlockedPushEntrySList", head, e));
        Assert.Equal(200u, w.Kernel("QueryDepthSList", head));
        var seen = new HashSet<uint>();
        for (var p = w.Kernel("InterlockedFlushSList", head); p != 0; p = w.Mem.ReadUInt32(p)) Assert.True(seen.Add(p));
        Assert.Equal(200, seen.Count);
    }

    [Fact]
    public void FlsSlotsAreAllocatedPerThreadAndFreed()
    {
        using var w = new World();
        var i = w.Kernel("FlsAlloc", 0);
        Assert.NotEqual(0xFFFFFFFFu, i);
        Assert.Equal(0u, w.Kernel("FlsGetValue", i));
        Assert.Equal(0u, w.LastError);
        Assert.Equal(1u, w.Kernel("FlsSetValue", i, 77));
        Assert.Equal(77u, w.Kernel("FlsGetValue", i));
        uint other = 1; var t = new Thread(() => other = w.Kernel("FlsGetValue", i)); t.Start(); t.Join();
        Assert.Equal(0u, other);
        Assert.Equal(1u, w.Kernel("FlsFree", i));
        Assert.Equal(0u, w.Kernel("FlsGetValue", i));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("FlsFree", i));
    }

    [Fact]
    public void HeapSetInformationAcceptsDocumentedClassesOnly()
    {
        using var w = new World();
        var heap = w.Kernel("GetProcessHeap");
        Assert.Equal(1u, w.Kernel("HeapSetInformation", 0, 1, 0, 0));        // termination on corruption, process-wide
        var lfh = w.Buf(4); w.Mem.WriteUInt32(lfh, 2);
        var h2 = w.Kernel("HeapCreate", 0, 0x1000, 0);
        Assert.Equal(1u, w.Kernel("HeapSetInformation", h2, 0, lfh, 4));
        Assert.Equal(0u, w.Kernel("HeapSetInformation", heap, 77, 0, 0));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("HeapSetInformation", 0xDEAD0, 1, 0, 0));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void CriticalSectionExRejectsUnknownFlags()
    {
        using var w = new World();
        var cs = w.Buf(32);
        Assert.Equal(1u, w.Kernel("InitializeCriticalSectionEx", cs, 0, 0x01000000));
        w.Kernel("EnterCriticalSection", cs); w.Kernel("LeaveCriticalSection", cs);
        Assert.Equal(0u, w.Kernel("InitializeCriticalSectionEx", cs, 0, 1));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void LoadLibraryWUsesTheModuleTable()
    {
        using var w = new World();
        Assert.NotEqual(0u, w.Kernel("LoadLibraryW", w.Wide("kernel32.dll")));
        Assert.Equal(0u, w.Kernel("LoadLibraryW", w.Wide("bin\\linker.dll")));
        Assert.Equal(Win32Error.ModNotFound, w.LastError);
        Assert.Equal(0u, w.Kernel("LoadLibraryExW", w.Wide("kernel32.dll"), 0, 2));   // LOAD_LIBRARY_AS_DATAFILE
        Assert.Equal(Win32Error.NotSupported, w.LastError);
        Assert.NotEqual(0u, w.Kernel("LoadLibraryExW", w.Wide("kernel32.dll"), 0, 0x800));
    }

    // ---- files, console, environment, locale ----

    private World Sandbox(Win32Options? o = null) => new(new Win32Options { SandboxRoot = root, Stdout = new MemoryStream(), Stderr = new MemoryStream(), Stdin = new MemoryStream() });

    [Fact]
    public void WideFileApis()
    {
        File.WriteAllBytes(Path.Combine(root, "a.bin"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        using var w = Sandbox();
        var h = w.Kernel("CreateFileW", w.Wide("C:\\a.bin"), 0x80000000, 3, 0, 3, 0, 0);
        Assert.NotEqual(0xFFFFFFFFu, h);
        var size = w.Buf(8);
        Assert.Equal(1u, w.Kernel("GetFileSizeEx", h, size));
        Assert.Equal(10L, BitConverter.ToInt64(w.Bytes(size, 8)));
        var pos = w.Buf(8);
        Assert.Equal(1u, w.Kernel("SetFilePointerEx", h, 4, 0, pos, 0));
        Assert.Equal(4L, BitConverter.ToInt64(w.Bytes(pos, 8)));
        Assert.Equal(1u, w.Kernel("SetFilePointerEx", h, 0xFFFFFFFE, 0xFFFFFFFF, pos, 2)); // -2 from end
        Assert.Equal(8L, BitConverter.ToInt64(w.Bytes(pos, 8)));
        Assert.Equal(0u, w.Kernel("SetFilePointerEx", h, 0xFFFFFFF0, 0xFFFFFFFF, pos, 0)); // negative
        Assert.Equal(Win32Error.NegativeSeek, w.LastError);
        Assert.Equal(0xFFFFFFFFu, w.Kernel("CreateFileW", w.Wide("C:\\missing.bin"), 0x80000000, 3, 0, 3, 0, 0));
        Assert.Equal(Win32Error.FileNotFound, w.LastError);
    }

    [Fact]
    public void FindFirstFileExWReturnsWideFindData()
    {
        File.WriteAllText(Path.Combine(root, "one.txt"), "x"); File.WriteAllText(Path.Combine(root, "two.txt"), "yy");
        using var w = Sandbox();
        var data = w.Buf(592);
        var h = w.Kernel("FindFirstFileExW", w.Wide("C:\\*.txt"), 1, data, 0, 0, 0);
        Assert.NotEqual(0xFFFFFFFFu, h);
        Assert.Equal("one.txt", W(w, data + 44));
        Assert.Equal(1u, w.Kernel("FindNextFileW", h, data));
        Assert.Equal("two.txt", W(w, data + 44));
        Assert.Equal(2u, w.Mem.ReadUInt32(data + 32));
        Assert.Equal(0u, w.Kernel("FindNextFileW", h, data));
        Assert.Equal(Win32Error.NoMoreFiles, w.LastError);
        Assert.Equal(0xFFFFFFFFu, w.Kernel("FindFirstFileExW", w.Wide("C:\\*.txt"), 5, data, 0, 0, 0));
    }

    [Fact]
    public void RedirectedStdioIsNotAConsole()
    {
        using var w = Sandbox();
        var stdout = w.Kernel("GetStdHandle", 0xFFFFFFF5);
        var mode = w.Buf(4);
        Assert.Equal(0u, w.Kernel("GetConsoleMode", stdout, mode));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(0u, w.Kernel("WriteConsoleW", stdout, w.Wide("hi"), 2, w.Buf(4), 0));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(437u, w.Kernel("GetConsoleOutputCP"));
        Assert.Equal(437u, w.Kernel("GetConsoleCP"));
    }

    [Fact]
    public void ConsoleHandlesWriteAndReadLines()
    {
        var o = new MemoryStream(); var i = new MemoryStream(Encoding.UTF8.GetBytes("abc\ndef"));
        using var w = new World(new Win32Options { Stdout = o, Stderr = new MemoryStream(), Stdin = i, StdoutIsConsole = true, StdinIsConsole = true });
        var stdout = w.Kernel("GetStdHandle", 0xFFFFFFF5); var stdin = w.Kernel("GetStdHandle", 0xFFFFFFF6);
        var mode = w.Buf(4);
        Assert.Equal(1u, w.Kernel("GetConsoleMode", stdout, mode));
        Assert.Equal(3u, w.Mem.ReadUInt32(mode));
        var n = w.Buf(4);
        Assert.Equal(1u, w.Kernel("WriteConsoleW", stdout, w.Wide("h\u00e9llo"), 5, n, 0));
        Assert.Equal(5u, w.Mem.ReadUInt32(n));
        Assert.Equal(1u, w.Kernel("WriteConsoleA", stdout, w.Str("!\n"), 2, n, 0));
        Assert.Equal("h\u00e9llo!\n", Encoding.UTF8.GetString(o.ToArray()));
        var buf = w.Buf(64);
        Assert.Equal(1u, w.Kernel("ReadConsoleW", stdin, buf, 32, n));
        Assert.Equal(5u, w.Mem.ReadUInt32(n));
        Assert.Equal("abc\r\n", W(w, buf)[..5]);
        Assert.Equal(1u, w.Kernel("ReadConsoleW", stdin, buf, 32, n));
        Assert.Equal("def\r\n", Nls_(w, buf, w.Mem.ReadUInt32(n)));
    }

    private static string Nls_(World w, uint p, uint chars) => Encoding.Unicode.GetString(w.Bytes(p, (int)chars * 2));

    [Fact]
    public void EnvironmentVariablesCanBeSetAndDeleted()
    {
        using var w = new World(new Win32Options { Environment = new Dictionary<string, string> { ["Keep"] = "1" } });
        var buf = w.Buf(64);
        Assert.Equal(1u, w.Kernel("GetEnvironmentVariableW", w.Wide("keep"), buf, 32));
        Assert.Equal(1u, w.Kernel("SetEnvironmentVariableW", w.Wide("New"), w.Wide("v2")));
        Assert.Equal(2u, w.Kernel("GetEnvironmentVariableW", w.Wide("NEW"), buf, 32));
        Assert.Equal("v2", W(w, buf));
        Assert.Contains("New=v2", Nls_(w, w.Kernel("GetEnvironmentStringsW"), 15));
        Assert.Equal(1u, w.Kernel("SetEnvironmentVariableW", w.Wide("New"), 0));
        Assert.Equal(0u, w.Kernel("GetEnvironmentVariableW", w.Wide("New"), buf, 32));
        Assert.Equal(Win32Error.EnvVarNotFound, w.LastError);
        Assert.Equal(0u, w.Kernel("SetEnvironmentVariableW", w.Wide("New"), 0));
        Assert.Equal(0u, w.Kernel("SetEnvironmentVariableW", w.Wide("a=b"), w.Wide("x")));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void CurrentDirectoryAndFullPathNames()
    {
        Directory.CreateDirectory(Path.Combine(root, "sub")); File.WriteAllText(Path.Combine(root, "sub", "f.txt"), "z");
        using var w = Sandbox();
        var buf = w.Buf(128); var part = w.Buf(4);
        Assert.Equal(1u, w.Kernel("SetCurrentDirectoryA", w.Str("sub")));
        Assert.Equal(6u, w.Kernel("GetCurrentDirectoryA", 64, buf));
        Assert.Equal("C:\\sub", w.Cstr(buf));
        Assert.Equal(10u, w.Kernel("GetFullPathNameA", w.Str("..\\x\\.\\y.txt"), 128, buf, part));
        Assert.Equal("C:\\x\\y.txt", w.Cstr(buf));
        Assert.Equal(buf + 5, w.Mem.ReadUInt32(part));
        Assert.NotEqual(0xFFFFFFFFu, w.Kernel("CreateFileW", w.Wide("f.txt"), 0x80000000, 3, 0, 3, 0, 0)); // relative to the new directory
        Assert.Equal(0u, w.Kernel("SetCurrentDirectoryA", w.Str("nothere")));
        Assert.Equal(Win32Error.PathNotFound, w.LastError);
        Assert.Equal(11u, w.Kernel("GetFullPathNameA", w.Str("C:\\a\\b.txt"), 4, buf, 0)); // needs 10 + NUL
    }

    [Fact]
    public void LocaleFunctionsDescribeAnEnUsMachine()
    {
        using var w = new World();
        Assert.Equal(0x409u, w.Kernel("GetUserDefaultLCID"));
        Assert.Equal(1u, w.Kernel("IsValidLocale", 0x409, 1));
        Assert.Equal(0u, w.Kernel("IsValidLocale", 0x407, 1));
        var buf = w.Buf(64);
        Assert.Equal(6u, w.Kernel("GetLocaleInfoW", 0x409, 0x5C, buf, 32));
        Assert.Equal("en-US", W(w, buf));
        Assert.Equal(2u, w.Kernel("GetLocaleInfoW", 0x409, 0x1004 | 0x20000000, buf, 4));
        Assert.Equal(1252u, w.Mem.ReadUInt32(buf));
        Assert.Equal(0u, w.Kernel("GetLocaleInfoW", 0x409, 0x5C, buf, 3));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        Assert.Throws<Win32UsageException>(() => w.Kernel("GetLocaleInfoW", 0x409, 0x1234, buf, 32));
        Assert.Equal(1u, w.Kernel("IsValidCodePage", 1252));
        Assert.Equal(0u, w.Kernel("IsValidCodePage", 99999));
    }

    [Fact]
    public void CompareStringWOrdersAndIgnoresCaseOnRequest()
    {
        using var w = new World();
        uint Cmp(string a, string b, uint flags = 0) => w.Kernel("CompareStringW", 0x409, flags, w.Wide(a), 0xFFFFFFFF, w.Wide(b), 0xFFFFFFFF);
        Assert.Equal(1u, Cmp("abc", "abd"));
        Assert.Equal(2u, Cmp("abc", "abc"));
        Assert.Equal(3u, Cmp("b", "a"));
        Assert.Equal(1u, Cmp("ABC", "abc"));
        Assert.Equal(2u, Cmp("ABC", "abc", 1));
        Assert.Throws<Win32UsageException>(() => Cmp("a", "b", 0x10000));
        Assert.Equal(2u, w.Kernel("CompareStringW", 0x409, 0, w.Wide("abcdef"), 3, w.Wide("abcxyz"), 3));
    }

    [Fact]
    public void LcMapStringExMapsCaseForTheEnglishLocaleOnly()
    {
        using var w = new World();
        var dst = w.Buf(32);
        Assert.Equal(4u, w.Kernel("LCMapStringEx", w.Wide("en-US"), 0x200 /* LCMAP_UPPERCASE */, w.Wide("abc"), 0xFFFFFFFF, dst, 16, 0, 0, 0));
        Assert.Equal("ABC", W(w, dst));
        Assert.Equal(0u, w.Kernel("LCMapStringEx", w.Wide("de-DE"), 0x200, w.Wide("abc"), 0xFFFFFFFF, dst, 16, 0, 0, 0));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }
}
