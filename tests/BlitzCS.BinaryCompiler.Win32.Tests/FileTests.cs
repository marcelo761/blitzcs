using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class FileTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitz-win32-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("blitz-win32-out-").FullName;
    private const uint Read = 0x80000000, WriteAccess = 0x40000000, CreateNew = 1, CreateAlways = 2, OpenExisting = 3, OpenAlways = 4, Truncate = 5;
    private const uint Invalid = 0xFFFFFFFF;

    public void Dispose() { Directory.Delete(root, true); Directory.Delete(outside, true); }

    private World New(Stream? stdout = null, Stream? stdin = null) => new(new Win32Options { SandboxRoot = root, Stdout = stdout, Stderr = stdout, Stdin = stdin });
    private static uint Open(World w, string path, uint access, uint disposition, uint flags = 0) => w.Kernel("CreateFileA", w.Str(path), access, 3, 0, disposition, flags, 0);

    private static void WriteAll(World w, uint h, byte[] data)
    {
        var p = w.Buf(Math.Max(1, data.Length)); w.Mem.Write(p, data);
        var n = w.Buf(4);
        Assert.Equal(1u, w.Kernel("WriteFile", h, p, (uint)data.Length, n, 0));
        Assert.Equal((uint)data.Length, w.Mem.ReadUInt32(n));
    }

    private static byte[] ReadSome(World w, uint h, int count)
    {
        var p = w.Buf(Math.Max(1, count)); var n = w.Buf(4);
        Assert.Equal(1u, w.Kernel("ReadFile", h, p, (uint)count, n, 0));
        return w.Bytes(p, (int)w.Mem.ReadUInt32(n));
    }

    [Fact]
    public void WriteSeekReadSizeRoundTrip()
    {
        using var w = New();
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var h = Open(w, "C:\\data\\a.bin", Read | WriteAccess, CreateAlways);
        Assert.NotEqual(Invalid, h);
        Assert.Equal(0u, w.LastError);
        WriteAll(w, h, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        Assert.Equal(100u, w.Kernel("GetFileSize", h, 0));
        var high = w.Buf(4); w.Mem.WriteUInt32(high, 99);
        Assert.Equal(100u, w.Kernel("GetFileSize", h, high));
        Assert.Equal(0u, w.Mem.ReadUInt32(high));
        Assert.Equal(10u, w.Kernel("SetFilePointer", h, 10, 0, 0));
        Assert.Equal(Enumerable.Range(10, 5).Select(i => (byte)i), ReadSome(w, h, 5));
        Assert.Equal(15u, w.Kernel("SetFilePointer", h, 0, 0, 1));
        Assert.Equal(95u, w.Kernel("SetFilePointer", h, unchecked((uint)-5), 0, 2));
        Assert.Equal(5, ReadSome(w, h, 50).Length);
        Assert.Empty(ReadSome(w, h, 10)); // EOF: success with 0 bytes
        Assert.Equal(1u, w.Kernel("SetFilePointer", h, unchecked((uint)-99), 0, 2));
        Assert.Equal(1u, w.Kernel("SetEndOfFile", h));
        Assert.Equal(1u, w.Kernel("GetFileSize", h, 0));
        Assert.Equal(1u, w.Kernel("FlushFileBuffers", h));
        Assert.Equal(1u, w.Kernel("GetFileType", h));
        Assert.Equal(1u, w.Kernel("CloseHandle", h));
        Assert.Equal(1, new FileInfo(Path.Combine(root, "data", "a.bin")).Length);
    }

    [Fact]
    public void LargeTransfersCrossChunkBoundaries()
    {
        using var w = New();
        var h = Open(w, "big.bin", Read | WriteAccess, CreateAlways);
        var data = new byte[200_000]; new Random(1).NextBytes(data);
        WriteAll(w, h, data);
        w.Kernel("SetFilePointer", h, 0, 0, 0);
        Assert.Equal(data, ReadSome(w, h, data.Length));
    }

    [Fact]
    public void DispositionsAndErrors()
    {
        using var w = New();
        Assert.Equal(Invalid, Open(w, "missing.txt", Read, OpenExisting));
        Assert.Equal(Win32Error.FileNotFound, w.LastError);
        Assert.Equal(Invalid, Open(w, "nodir\\missing.txt", Read, OpenExisting));
        Assert.Equal(Win32Error.PathNotFound, w.LastError);
        Assert.Equal(Invalid, Open(w, "nodir\\x.txt", WriteAccess, CreateAlways));
        Assert.Equal(Win32Error.PathNotFound, w.LastError);
        var h = Open(w, "f.txt", WriteAccess, CreateNew);
        Assert.NotEqual(Invalid, h);
        WriteAll(w, h, "abc"u8.ToArray());
        w.Kernel("CloseHandle", h);
        Assert.Equal(Invalid, Open(w, "f.txt", WriteAccess, CreateNew));
        Assert.Equal(Win32Error.FileExists, w.LastError);
        var again = Open(w, "f.txt", Read, OpenAlways);
        Assert.NotEqual(Invalid, again);
        Assert.Equal(Win32Error.AlreadyExists, w.LastError);
        w.Kernel("CloseHandle", again);
        var created = Open(w, "g.txt", Read, OpenAlways);
        Assert.NotEqual(Invalid, created);
        Assert.Equal(Win32Error.Success, w.LastError);
        Assert.Equal(Invalid, Open(w, "f.txt", Read, Truncate)); // needs GENERIC_WRITE
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        var t = Open(w, "f.txt", WriteAccess, Truncate);
        Assert.NotEqual(Invalid, t);
        Assert.Equal(0, new FileInfo(Path.Combine(root, "f.txt")).Length);
        var over = Open(w, "f.txt", Read, CreateAlways);
        Assert.NotEqual(Invalid, over);
        Assert.Equal(Win32Error.AlreadyExists, w.LastError);
        Assert.Equal(Invalid, Open(w, "f.txt", Read, 9));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(Invalid, Open(w, "f.txt", Read, OpenExisting, 0x40000000)); // FILE_FLAG_OVERLAPPED
        Assert.Equal(Win32Error.NotSupported, w.LastError);
        Directory.CreateDirectory(Path.Combine(root, "d"));
        Assert.Equal(Invalid, Open(w, "d", Read, OpenExisting));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
    }

    [Fact]
    public void AccessModesAreEnforcedPerHandle()
    {
        using var w = New();
        File.WriteAllText(Path.Combine(root, "ro.txt"), "hello");
        var ro = Open(w, "ro.txt", Read, OpenExisting);
        var p = w.Buf(8); var n = w.Buf(4);
        Assert.Equal(0u, w.Kernel("WriteFile", ro, p, 1, n, 0));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(0u, w.Kernel("SetEndOfFile", ro));
        var wo = Open(w, "ro.txt", WriteAccess, OpenExisting);
        Assert.Equal(0u, w.Kernel("ReadFile", wo, p, 1, n, 0));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(0u, w.Kernel("ReadFile", ro, p, 1, n, 1)); // overlapped
        Assert.Equal(Win32Error.NotSupported, w.LastError);
        Assert.Equal(0u, w.Kernel("ReadFile", ro, p, 1, 0, 0)); // lpNumberOfBytesRead required
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("ReadFile", 0x9999, p, 1, n, 0));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        Assert.Equal(Invalid, w.Kernel("SetFilePointer", ro, unchecked((uint)-1), 0, 0));
        Assert.Equal(Win32Error.NegativeSeek, w.LastError);
        Assert.Equal(Invalid, w.Kernel("SetFilePointer", ro, 0, 0, 7));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(Invalid, w.Kernel("GetFileSize", 0x9999, 0));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void CloseHandleDetectsDoubleClose()
    {
        using var w = New();
        var h = Open(w, "c.txt", WriteAccess, CreateAlways);
        Assert.Equal(1u, w.Kernel("CloseHandle", h));
        Assert.Equal(0u, w.Kernel("CloseHandle", h));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
        var h2 = Open(w, "c.txt", WriteAccess, OpenExisting);
        Assert.NotEqual(h, h2); // never reissued
    }

    [Fact]
    public void PathTranslationIsSandboxedAndCaseInsensitive()
    {
        using var w = New();
        Directory.CreateDirectory(Path.Combine(root, "Dir"));
        File.WriteAllText(Path.Combine(root, "Dir", "File.TXT"), "x");
        Assert.NotEqual(Invalid, Open(w, "c:\\DIR\\file.txt", Read, OpenExisting));
        Assert.NotEqual(Invalid, Open(w, "d:/dir/./sub/../FILE.txt", Read, OpenExisting));
        Assert.NotEqual(Invalid, Open(w, "\\Dir\\File.TXT", Read, OpenExisting));
        Assert.Equal(Invalid, Open(w, "C:\\..\\..\\..\\etc\\passwd", Read, OpenExisting)); // ".." clamps at the root
        Assert.Equal(Win32Error.PathNotFound, w.LastError);
        Assert.Equal(Invalid, Open(w, "\\\\server\\share\\x", Read, OpenExisting));
        Assert.Equal(Win32Error.BadPathname, w.LastError);
        Assert.Equal(Invalid, Open(w, "C:\\a<b", Read, OpenExisting));
        Assert.Equal(123u, w.LastError);
        using var strict = new World(new Win32Options { SandboxRoot = root, CaseInsensitiveFileNames = false });
        Assert.Equal(Invalid, Open(strict, "dir\\file.txt", Read, OpenExisting));
    }

    [Fact]
    public void SymbolicLinksOutOfTheSandboxAreNeverFollowed()
    {
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "flink.txt"), Path.Combine(outside, "secret.txt"));
        using var w = New();
        Assert.Equal(Invalid, Open(w, "link\\secret.txt", Read, OpenExisting));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(Invalid, Open(w, "flink.txt", Read, OpenExisting));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
    }

    [Fact]
    public void WithoutSandboxEveryPathApiIsDenied()
    {
        using var w = new World();
        Assert.Equal(Invalid, Open(w, "C:\\x.txt", Read, OpenExisting));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(Invalid, w.Kernel("FindFirstFileA", w.Str("*"), w.Buf(320)));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(Invalid, w.Kernel("GetFileAttributesA", w.Str("x")));
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
    }

    [Fact]
    public void FindFilesEnumeratesWithWildcards()
    {
        using var w = New();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "12345");
        File.WriteAllText(Path.Combine(root, "sub", "A.txt"), "1");
        File.WriteAllText(Path.Combine(root, "sub", "c.dat"), "");
        Directory.CreateDirectory(Path.Combine(root, "sub", "inner.txt"));
        var data = w.Buf(320);
        var h = w.Kernel("FindFirstFileA", w.Str("C:\\sub\\*.txt"), data);
        Assert.NotEqual(Invalid, h);
        var names = new List<(string, uint, uint)>();
        do names.Add((w.Cstr(data + 44), w.Mem.ReadUInt32(data), w.Mem.ReadUInt32(data + 32)));
        while (w.Kernel("FindNextFileA", h, data) == 1);
        Assert.Equal(Win32Error.NoMoreFiles, w.LastError);
        Assert.Equal([("A.txt", 0x80u, 1u), ("b.txt", 0x80u, 5u), ("inner.txt", 0x10u, 0u)], names);
        Assert.Equal(1u, w.Kernel("FindClose", h));
        Assert.Equal(0u, w.Kernel("FindClose", h));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);

        h = w.Kernel("FindFirstFileA", w.Str("sub\\*.*"), data);
        Assert.Equal(".", w.Cstr(data + 44));
        w.Kernel("FindNextFileA", h, data); Assert.Equal("..", w.Cstr(data + 44));
        Assert.Equal(Invalid, w.Kernel("FindFirstFileA", w.Str("sub\\*.zzz"), data));
        Assert.Equal(Win32Error.FileNotFound, w.LastError);
        Assert.Equal(Invalid, w.Kernel("FindFirstFileA", w.Str("nodir\\*"), data));
        Assert.Equal(Win32Error.PathNotFound, w.LastError);
        h = w.Kernel("FindFirstFileA", w.Str("*"), data); // root has no "." / ".."
        Assert.Equal("sub", w.Cstr(data + 44));
        Assert.Equal(0u, w.Kernel("FindNextFileA", 0x9999, data));
        Assert.Equal(Win32Error.InvalidHandle, w.LastError);
    }

    [Fact]
    public void FileAttributes()
    {
        using var w = New();
        Directory.CreateDirectory(Path.Combine(root, "d"));
        File.WriteAllText(Path.Combine(root, "d", "f"), "");
        Assert.Equal(0x10u, w.Kernel("GetFileAttributesA", w.Str("d")));
        Assert.Equal(0x80u, w.Kernel("GetFileAttributesA", w.Str("d\\f")));
        Assert.Equal(Invalid, w.Kernel("GetFileAttributesA", w.Str("d\\nope")));
        Assert.Equal(Win32Error.FileNotFound, w.LastError);
        Assert.Equal(Invalid, w.Kernel("GetFileAttributesA", w.Str("nodir\\nope")));
        Assert.Equal(Win32Error.PathNotFound, w.LastError);
    }

    [Fact]
    public void StandardStreamsUseTheConfiguredStreams()
    {
        var stdout = new MemoryStream();
        using var w = New(stdout, new MemoryStream("input"u8.ToArray()));
        var h = w.Kernel("GetStdHandle", 0xFFFFFFF5);
        WriteAll(w, h, "hello"u8.ToArray());
        Assert.Equal("hello"u8.ToArray(), stdout.ToArray());
        Assert.Equal("inp"u8.ToArray(), ReadSome(w, w.Kernel("GetStdHandle", 0xFFFFFFF6), 3));
        var p = w.Buf(4); var n = w.Buf(4);
        Assert.Equal(0u, w.Kernel("WriteFile", 3, p, 1, n, 0)); // writing to stdin
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(0u, w.Kernel("ReadFile", 7, p, 1, n, 0));  // reading from stdout
        Assert.Equal(Win32Error.AccessDenied, w.LastError);
        Assert.Equal(1u, w.Kernel("FlushFileBuffers", 7));
        Assert.Equal(Invalid, w.Kernel("GetFileSize", 7, 0));
        // redirect stdout to a file
        var f = Open(w, "out.txt", WriteAccess, CreateAlways);
        Assert.Equal(1u, w.Kernel("SetStdHandle", 0xFFFFFFF5, f));
        WriteAll(w, w.Kernel("GetStdHandle", 0xFFFFFFF5), "to file"u8.ToArray());
        w.Kernel("CloseHandle", f);
        Assert.Equal("to file", File.ReadAllText(Path.Combine(root, "out.txt")));
    }

    [Fact]
    public void GuestMemoryFaultsSurfaceAsExceptions()
    {
        using var w = New();
        var h = Open(w, "m.txt", Read | WriteAccess, CreateAlways);
        var n = w.Buf(4);
        WriteAll(w, h, [1, 2, 3, 4]);
        w.Kernel("SetFilePointer", h, 0, 0, 0);
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestMemoryException>(() => w.Kernel("WriteFile", h, 0x10, 4, n, 0));
        Assert.Throws<BlitzCS.BinaryCompiler.Runtime.GuestMemoryException>(() => w.Kernel("ReadFile", h, 0x10, 4, n, 0));
    }

    [Fact]
    public void FileStateCacheInvalidationAllowsFindingNewlyCreatedFilesAndDirectories()
    {
        using var w = New();
        var subDir = Path.Combine(root, "SubFolder");
        Directory.CreateDirectory(subDir);

        // Before creating file: opening non-existent file returns Invalid
        var hFail = Open(w, "SubFolder\\newfile.txt", Read, OpenExisting);
        Assert.Equal(Invalid, hFail);
        Assert.Equal(Win32Error.FileNotFound, w.LastError);

        // Create file via CreateFileA with CreateNew
        var hCreate = Open(w, "SubFolder\\newfile.txt", WriteAccess, CreateNew);
        Assert.NotEqual(Invalid, hCreate);
        WriteAll(w, hCreate, "sample data"u8.ToArray());
        w.Kernel("CloseHandle", hCreate);

        // Now case-insensitive open must succeed because cache was invalidated on creation
        var hOpen = Open(w, "SUBFOLDER\\NEWFILE.TXT", Read, OpenExisting);
        Assert.NotEqual(Invalid, hOpen);
        var read = ReadSome(w, hOpen, 11);
        Assert.Equal("sample data"u8.ToArray(), read);
        w.Kernel("CloseHandle", hOpen);

        // Delete file and verify it's no longer found
        Assert.Equal(1u, w.Kernel("DeleteFileA", w.Str("SubFolder\\newfile.txt")));
        var hAfterDelete = Open(w, "SUBFOLDER\\NEWFILE.TXT", Read, OpenExisting);
        Assert.Equal(Invalid, hAfterDelete);

        // Exercise ArrayPool renting in ReadFile with large buffer (> 65536 bytes)
        var large = new byte[70000];
        for (var i = 0; i < large.Length; i++) large[i] = (byte)(i % 251);
        var hLarge = Open(w, "large.bin", Read | WriteAccess, CreateAlways);
        WriteAll(w, hLarge, large);
        w.Kernel("SetFilePointer", hLarge, 0, 0, 0);
        var readLarge = ReadSome(w, hLarge, large.Length);
        Assert.Equal(large, readLarge);
        w.Kernel("CloseHandle", hLarge);
    }
}
