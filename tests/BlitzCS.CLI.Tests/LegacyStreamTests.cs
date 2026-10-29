using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyStreamTests
{
    [Fact]
    public void Binary_layout_matches_little_endian_CP1252_and_CRLF_contract()
    {
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "Values.dat");
        var handle = 0;
        try
        {
            handle = LegacyStreams.WriteFile(path);
            Assert.True(handle > 0);
            LegacyStreams.WriteByte(handle, 511);
            LegacyStreams.WriteShort(handle, 0xfedc);
            LegacyStreams.WriteInt(handle, -123);
            LegacyStreams.WriteFloat(handle, 1.25f);
            LegacyStreams.WriteString(handle, "€\0A");
            LegacyStreams.WriteLine(handle, "a\rb");
            LegacyStreams.CloseFile(handle);
            handle = 0;
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xff, 0xdc, 0xfe, 0x85, 0xff, 0xff, 0xff, 0, 0, 0xa0, 0x3f,
                3, 0, 0, 0, 0x80, 0, 0x41, 0x61, 0x0d, 0x62, 0x0d, 0x0a }, bytes);

            handle = LegacyStreams.ReadFile(Path.Combine(directory, "values.DAT"));
            Assert.True(handle > 0);
            Assert.Equal(bytes.Length, LegacyStreams.ReadAvail(handle));
            Assert.Equal(255, LegacyStreams.ReadByte(handle));
            Assert.Equal(0xfedc, LegacyStreams.ReadShort(handle));
            Assert.Equal(-123, LegacyStreams.ReadInt(handle));
            Assert.Equal(1.25f, LegacyStreams.ReadFloat(handle));
            Assert.Equal("€\0A", LegacyStreams.ReadString(handle));
            Assert.Equal("ab", LegacyStreams.ReadLine(handle));
            Assert.Equal(1, LegacyStreams.Eof(handle));
            Assert.Equal(bytes.Length, LegacyStreams.FilePos(handle));
            Assert.Equal(0, LegacyStreams.ReadByte(handle));
            Assert.Equal(0, LegacyStreams.ReadAvail(handle));
            Assert.Equal(0, LegacyStreams.SeekFile(handle, 0));
            Assert.Equal(0, LegacyStreams.Eof(handle));
        }
        finally { if (handle != 0) LegacyStreams.CloseFile(handle); Directory.Delete(directory, true); }
    }

    [Fact]
    public void Partial_values_zero_fill_and_open_file_never_truncates_or_creates()
    {
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "tail.dat");
        var handle = 0;
        try
        {
            Assert.Equal(0, LegacyStreams.OpenFile(path));
            Assert.False(File.Exists(path));
            File.WriteAllBytes(path, [0xfe, 0x80]);
            handle = LegacyStreams.OpenFile(path);
            Assert.Equal(0x80fe, LegacyStreams.ReadInt(handle));
            Assert.Equal(0f, LegacyStreams.ReadFloat(handle));
            Assert.Equal(-1, LegacyStreams.SeekFile(handle, -1));
            Assert.Equal(2, LegacyStreams.FilePos(handle));
            LegacyStreams.SeekFile(handle, 0);
            LegacyStreams.WriteByte(handle, 7);
            LegacyStreams.CloseFile(handle);
            var closed = handle;
            handle = 0;
            Assert.Equal(new byte[] { 7, 0x80 }, File.ReadAllBytes(path));
            Assert.Throws<ArgumentException>(() => LegacyStreams.ReadByte(closed));
        }
        finally { if (handle != 0) LegacyStreams.CloseFile(handle); Directory.Delete(directory, true); }
    }

    [Fact]
    public void Directories_keep_dot_entries_and_nonrecursive_delete_semantics()
    {
        var directory = TemporaryDirectory();
        var child = Path.Combine(directory, "child");
        var handle = 0;
        try
        {
            LegacyStreams.CreateDir(child);
            File.WriteAllText(Path.Combine(child, "entry.txt"), "data");
            handle = LegacyStreams.ReadDir(child);
            Assert.True(handle > 0);
            var names = new List<string>();
            for (var name = LegacyStreams.NextFile(handle); name.Length != 0; name = LegacyStreams.NextFile(handle)) names.Add(name);
            Assert.Contains(".", names);
            Assert.Contains("..", names);
            Assert.Contains("entry.txt", names);
            LegacyStreams.CloseDir(handle);
            handle = 0;
            LegacyStreams.DeleteDir(child);
            Assert.True(Directory.Exists(child));
            LegacyStreams.DeleteFile(Path.Combine(child, "ENTRY.TXT"));
            LegacyStreams.DeleteDir(child);
            Assert.False(Directory.Exists(child));
            LegacyStreams.CreateDir(Path.Combine(directory, "missing", "nested"));
            Assert.False(Directory.Exists(Path.Combine(directory, "missing")));
        }
        finally { if (handle != 0) LegacyStreams.CloseDir(handle); Directory.Delete(directory, true); }
    }

    [Fact]
    public void Invalid_length_prefixed_data_fails_without_unbounded_allocation()
    {
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "bad.dat");
        var handle = 0;
        try
        {
            File.WriteAllBytes(path, [0xff, 0xff, 0xff, 0x7f]);
            handle = LegacyStreams.ReadFile(path);
            Assert.Throws<InvalidDataException>(() => LegacyStreams.ReadString(handle));
        }
        finally { if (handle != 0) LegacyStreams.CloseFile(handle); Directory.Delete(directory, true); }
    }

    private static string TemporaryDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "blitz-stream-tests-" + Guid.NewGuid().ToString("N"))).FullName;
}
