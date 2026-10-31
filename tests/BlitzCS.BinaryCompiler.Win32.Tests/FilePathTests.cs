using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class FilePathTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitz-win32-fp-").FullName;
    private const uint Invalid = 0xFFFFFFFF;
    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // a test may leave read-only files
        Directory.Delete(root, true);
    }
    private World New() => new(new Win32Options { SandboxRoot = root, CaseInsensitiveFileNames = true });
    private string P(string rel) => Path.Combine(root, rel);

    [Fact]
    public void CopyFileFailIfExistsAndOverwrite()
    {
        using var w = New();
        File.WriteAllText(P("a.txt"), "one");
        File.WriteAllText(P("b.txt"), "two");
        Assert.Equal(0u, w.Kernel("CopyFileA", w.Str("C:\\A.TXT"), w.Str("C:\\b.txt"), 1));
        Assert.Equal(80u, w.LastError);
        Assert.Equal("two", File.ReadAllText(P("b.txt")));
        Assert.Equal(1u, w.Kernel("CopyFileA", w.Str("a.txt"), w.Str("b.txt"), 0));
        Assert.Equal("one", File.ReadAllText(P("b.txt")));
        Assert.Equal(1u, w.Kernel("CopyFileW", w.Wide("C:\\a.txt"), w.Wide("C:\\c.txt"), 1));
        Assert.Equal("one", File.ReadAllText(P("c.txt")));
        Assert.Equal(0u, w.Kernel("CopyFileA", w.Str("nope.txt"), w.Str("d.txt"), 0));
        Assert.Equal(2u, w.LastError);
        Assert.Equal(0u, w.Kernel("CopyFileA", w.Str("a.txt"), w.Str("nodir\\d.txt"), 0));
        Assert.Equal(3u, w.LastError);
        Assert.Equal(1u, w.Kernel("CopyFileA", w.Str("a.txt"), w.Str("..\\..\\..\\escape.txt"), 0)); // ".." clamps at the drive root: lands inside the sandbox
        Assert.True(File.Exists(P("escape.txt")));
        Assert.False(File.Exists(Path.Combine(root, "..", "escape.txt")));
    }

    [Fact]
    public void CopyPreservesWriteTimeAndReadOnly()
    {
        using var w = New();
        File.WriteAllText(P("a.txt"), "x");
        var stamp = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(P("a.txt"), stamp);
        w.Kernel("SetFileAttributesA", w.Str("a.txt"), 1);
        Assert.Equal(1u, w.Kernel("GetFileAttributesA", w.Str("a.txt")));
        Assert.Equal(1u, w.Kernel("CopyFileA", w.Str("a.txt"), w.Str("b.txt"), 1));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(P("b.txt")));
        Assert.Equal(1u, w.Kernel("GetFileAttributesA", w.Str("b.txt")));
        Assert.Equal(0u, w.Kernel("DeleteFileA", w.Str("b.txt")));
        Assert.Equal(5u, w.LastError);
        Assert.Equal(1u, w.Kernel("SetFileAttributesA", w.Str("b.txt"), 0x80));
        Assert.Equal(0x80u, w.Kernel("GetFileAttributesW", w.Wide("b.txt")));
        Assert.Equal(1u, w.Kernel("DeleteFileW", w.Wide("b.txt")));
        Assert.False(File.Exists(P("b.txt")));
    }

    [Fact]
    public void DeleteAndDirectories()
    {
        using var w = New();
        Assert.Equal(1u, w.Kernel("CreateDirectoryA", w.Str("d"), 0));
        Assert.Equal(0u, w.Kernel("CreateDirectoryA", w.Str("D"), 0));
        Assert.Equal(183u, w.LastError);
        Assert.Equal(0u, w.Kernel("CreateDirectoryA", w.Str("x\\y"), 0));
        Assert.Equal(3u, w.LastError);
        File.WriteAllText(P("d/f"), "1");
        Assert.Equal(0u, w.Kernel("RemoveDirectoryA", w.Str("d")));
        Assert.Equal(145u, w.LastError);
        Assert.Equal(0u, w.Kernel("DeleteFileA", w.Str("d")));
        Assert.Equal(5u, w.LastError);
        Assert.Equal(1u, w.Kernel("DeleteFileA", w.Str("d\\F")));
        Assert.Equal(1u, w.Kernel("RemoveDirectoryW", w.Wide("d")));
        Assert.Equal(0u, w.Kernel("DeleteFileA", w.Str("d\\F")));
        Assert.Equal(3u, w.LastError);
        Assert.Equal(0u, w.Kernel("DeleteFileA", w.Str("gone")));
        Assert.Equal(2u, w.LastError);
    }

    [Fact]
    public void MoveFileRules()
    {
        using var w = New();
        File.WriteAllText(P("a"), "A"); File.WriteAllText(P("b"), "B"); Directory.CreateDirectory(P("sub"));
        Assert.Equal(0u, w.Kernel("MoveFileA", w.Str("a"), w.Str("b")));
        Assert.Equal(183u, w.LastError);
        Assert.Equal(1u, w.Kernel("MoveFileExA", w.Str("a"), w.Str("b"), 1));
        Assert.Equal("A", File.ReadAllText(P("b")));
        Assert.False(File.Exists(P("a")));
        Assert.Equal(1u, w.Kernel("MoveFileW", w.Wide("b"), w.Wide("sub\\c")));
        Assert.True(File.Exists(P("sub/c")));
        Assert.Equal(1u, w.Kernel("MoveFileA", w.Str("sub"), w.Str("sub2")));
        Assert.True(File.Exists(P("sub2/c")));
        Assert.Equal(0u, w.Kernel("MoveFileExA", w.Str("sub2\\c"), w.Str("z"), 4));
        Assert.Equal(50u, w.LastError);
        Assert.Equal(0u, w.Kernel("MoveFileA", w.Str("missing"), w.Str("q")));
        Assert.Equal(2u, w.LastError);
    }

    [Fact]
    public void AttributesAndExData()
    {
        using var w = New();
        File.WriteAllBytes(P("a.bin"), new byte[300]); Directory.CreateDirectory(P("dd"));
        Assert.Equal(0x80u, w.Kernel("GetFileAttributesA", w.Str("A.BIN")));
        Assert.Equal(0x10u, w.Kernel("GetFileAttributesA", w.Str("dd")));
        Assert.Equal(Invalid, w.Kernel("GetFileAttributesA", w.Str("zz")));
        Assert.Equal(2u, w.LastError);
        Assert.Equal(Invalid, w.Kernel("GetFileAttributesA", w.Str("zz\\q")));
        Assert.Equal(3u, w.LastError);
        var d = w.Buf(36);
        Assert.Equal(1u, w.Kernel("GetFileAttributesExA", w.Str("a.bin"), 0, d));
        Assert.Equal(0x80u, w.Mem.ReadUInt32(d));
        Assert.Equal(300u, w.Mem.ReadUInt32(d + 32));
        Assert.Equal(1u, w.Kernel("GetFileAttributesExW", w.Wide("dd"), 0, d));
        Assert.Equal(0x10u, w.Mem.ReadUInt32(d));
        Assert.Equal(0u, w.Kernel("GetFileAttributesExA", w.Str("a.bin"), 1, d));
        Assert.Equal(87u, w.LastError);
    }

    [Fact]
    public void FileTimes()
    {
        using var w = New();
        File.WriteAllText(P("t"), "x");
        var h = w.Kernel("CreateFileA", w.Str("t"), 0xC0000000, 3, 0, 3, 0, 0);
        var ft = w.Buf(8);
        var stamp = new DateTime(2011, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        w.Mem.Write(ft, BitConverter.GetBytes(stamp.ToFileTimeUtc()));
        Assert.Equal(1u, w.Kernel("SetFileTime", h, 0, 0, ft));
        var o = w.Buf(8);
        Assert.Equal(1u, w.Kernel("GetFileTime", h, 0, 0, o));
        Assert.Equal(stamp.ToFileTimeUtc(), BitConverter.ToInt64(w.Bytes(o, 8)));
        Assert.Equal(0u, w.Kernel("GetFileTime", 0x7777, 0, 0, o));
        Assert.Equal(6u, w.LastError);
    }

    [Fact]
    public void PathQueries()
    {
        using var w = New();
        Directory.CreateDirectory(P("Some Dir")); File.WriteAllText(P("Some Dir/File.TXT"), "x"); File.WriteAllText(P("tool.exe"), "x");
        var o = w.Buf(260);
        var n = w.Kernel("GetLongPathNameA", w.Str("c:\\some dir\\file.txt"), o, 260);
        Assert.Equal("C:\\Some Dir\\File.TXT", w.Cstr(o));
        Assert.Equal((uint)"C:\\Some Dir\\File.TXT".Length, n);
        Assert.Equal(21u, w.Kernel("GetLongPathNameA", w.Str("c:\\some dir\\file.txt"), o, 3)); // too small: required size with terminator
        Assert.Equal(0u, w.Kernel("GetLongPathNameA", w.Str("nope"), o, 260));
        Assert.Equal(2u, w.LastError);
        Assert.Equal(8u, w.Kernel("GetTempPathA", 260, o)); Assert.Equal("C:\\Temp\\", w.Cstr(o));
        var part = w.Buf(4);
        n = w.Kernel("SearchPathA", 0, w.Str("tool"), w.Str(".exe"), 260, o, part);
        Assert.Equal("C:\\tool.exe", w.Cstr(o)); Assert.Equal(11u, n);
        Assert.Equal("tool.exe", w.Cstr(w.Mem.ReadUInt32(part)));
        Assert.Equal(0u, w.Kernel("SearchPathA", w.Str("C:\\Some Dir"), w.Str("tool.exe"), 0, 260, o, 0));
        Assert.Equal(2u, w.LastError);
        Assert.Equal(1u, w.Kernel("SearchPathA", w.Str("C:\\Some Dir"), w.Str("file.txt"), 0, 260, o, 0) > 0 ? 1u : 0u);
    }

    [Fact]
    public void TempFileNameCreatesFileOrUsesGivenNumber()
    {
        using var w = New();
        Directory.CreateDirectory(P("Temp"));
        var o = w.Buf(260);
        Assert.Equal(0xABu, w.Kernel("GetTempFileNameA", w.Str("C:\\Temp"), w.Str("blitzcc"), 0xAB, o));
        Assert.Equal("C:\\Temp\\bli00ab.tmp".Replace("00ab", "ab"), w.Cstr(o));
        Assert.False(File.Exists(P("Temp/bliab.tmp"))); // a given number does not create the file
        var u = w.Kernel("GetTempFileNameW", w.Wide("C:\\Temp\\"), w.Wide("xyz"), 0, o);
        Assert.NotEqual(0u, u);
        Assert.True(File.Exists(P("Temp/xyz" + u.ToString("x") + ".tmp")));
        Assert.Equal(0u, w.Kernel("GetTempFileNameA", w.Str("C:\\Missing"), w.Str("a"), 0, o));
        Assert.Equal(3u, w.LastError);
    }
}
