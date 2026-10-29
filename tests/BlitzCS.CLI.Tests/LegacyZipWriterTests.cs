using System.IO.Compression;
using System.Text;
using Blitz3DCS.Runtime;
using BlitzCS.LegacyRuntime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyZipWriterTests
{
    [Fact]
    public void Create_writes_stored_and_deflated_entries_with_exact_levels_and_dates()
    {
        var retainedPath = Environment.GetEnvironmentVariable("BLITZCS_ZIP_INTEROP_OUTPUT");
        var path = retainedPath ?? Path.Combine(Path.GetTempPath(), $"blitz-create-{Guid.NewGuid():N}.zip");
        var handle = LegacyZip.ZlibWapi_ZipOpen(path, 0);
        var date = DateBank(2024, 4, 3, 12, 34, 56);
        var empty = LegacyBanks.CreateBank(0);
        var data = LegacyBanks.CreateBank(8192);
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ZIP exact compression level ", 250)));
        try
        {
            Assert.True(handle > 0);
            LegacyBanks.WithBytes(data, 0, payload.Length, bytes => { payload.CopyTo(bytes); return 0; });
            AssertEntry(handle, "stored.txt", date, empty, data, payload.Length, method: 0, level: 0);
            AssertEntry(handle, "fast.txt", date, empty, data, payload.Length, method: 8, level: 0);
            AssertEntry(handle, "small.txt", date, empty, data, payload.Length, method: 8, level: 9);
            Assert.Equal(0, LegacyZip.ZlibWapi_ZipClose(handle, "created by test"));
            handle = 0;

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal("created by test", ReadComment(path));
            Assert.Equal(payload, ReadEntry(archive.GetEntry("stored.txt")!));
            Assert.Equal(0, CompressionMethod(path, "stored.txt"));
            Assert.Equal(8, CompressionMethod(path, "fast.txt"));
            Assert.Equal(8, CompressionMethod(path, "small.txt"));
            Assert.Equal(payload, ReadEntry(archive.GetEntry("fast.txt")!));
            Assert.Equal(payload, ReadEntry(archive.GetEntry("small.txt")!));
            Assert.Equal(new DateTime(2024, 5, 3, 12, 34, 56), archive.GetEntry("small.txt")!.LastWriteTime.DateTime);
            Assert.True(archive.GetEntry("fast.txt")!.CompressedLength > archive.GetEntry("small.txt")!.CompressedLength);
        }
        finally
        {
            if (handle != 0) LegacyZip.ZlibWapi_ZipClose(handle, string.Empty);
            LegacyBanks.FreeBank(data);
            LegacyBanks.FreeBank(empty);
            LegacyBanks.FreeBank(date);
            if (retainedPath is null) File.Delete(path);
        }
    }

    [Fact]
    public void AddInZip_preserves_external_python_archive_entries_and_adds_new_entry()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-zipapi-fixture.zip");
        var path = Path.Combine(Path.GetTempPath(), $"blitz-append-{Guid.NewGuid():N}.zip");
        File.Copy(fixture, path);
        DateTime originalTime;
        using (var original = ZipFile.OpenRead(path)) originalTime = original.GetEntry("first.txt")!.LastWriteTime.DateTime;
        var handle = LegacyZip.ZlibWapi_ZipOpen(path, 2);
        var date = DateBank(2025, 0, 2, 0, 0, 0);
        var empty = LegacyBanks.CreateBank(0);
        var data = LegacyBanks.CreateBank(5);
        try
        {
            Assert.True(handle > 0);
            LegacyBanks.WithBytes(data, 0, 5, bytes => { Encoding.ASCII.GetBytes("added").CopyTo(bytes); return 0; });
            AssertEntry(handle, "added.txt", date, empty, data, 5, method: 8, level: 6);
            Assert.Equal(0, LegacyZip.ZlibWapi_ZipClose(handle, string.Empty));
            handle = 0;

            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "first.txt");
            Assert.Contains(archive.Entries, entry => entry.FullName == "Maps/room é.dat");
            Assert.Equal("added", Encoding.ASCII.GetString(ReadEntry(archive.GetEntry("added.txt")!)));
            Assert.Equal(12, ReadEntry(archive.GetEntry("first.txt")!).Length);
            Assert.Equal(32, ReadEntry(archive.GetEntry("Maps/room é.dat")!).Length);
            Assert.Equal(originalTime, archive.GetEntry("first.txt")!.LastWriteTime.DateTime);
        }
        finally
        {
            if (handle != 0) LegacyZip.ZlibWapi_ZipClose(handle, string.Empty);
            LegacyBanks.FreeBank(data);
            LegacyBanks.FreeBank(empty);
            LegacyBanks.FreeBank(date);
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateAfter_keeps_existing_prefix_and_writes_a_readable_archive_after_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitz-after-{Guid.NewGuid():N}.zip");
        var prefix = Encoding.ASCII.GetBytes("existing executable prefix\0");
        File.WriteAllBytes(path, prefix);
        var handle = LegacyZip.ZlibWapi_ZipOpen(path, 1);
        var date = DateBank(2025, 0, 2, 0, 0, 0);
        var empty = LegacyBanks.CreateBank(0);
        var data = LegacyBanks.CreateBank(4);
        try
        {
            Assert.True(handle > 0);
            LegacyBanks.WithBytes(data, 0, 4, bytes => { Encoding.ASCII.GetBytes("tail").CopyTo(bytes); return 0; });
            AssertEntry(handle, "after.txt", date, empty, data, 4, method: 8, level: 6);
            Assert.Equal(0, LegacyZip.ZlibWapi_ZipClose(handle, string.Empty));
            handle = 0;

            var written = File.ReadAllBytes(path);
            Assert.Equal(prefix, written.AsSpan(0, prefix.Length).ToArray());
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal("tail", Encoding.ASCII.GetString(ReadEntry(archive.GetEntry("after.txt")!)));
        }
        finally
        {
            if (handle != 0) LegacyZip.ZlibWapi_ZipClose(handle, string.Empty);
            LegacyBanks.FreeBank(data);
            LegacyBanks.FreeBank(empty);
            LegacyBanks.FreeBank(date);
            File.Delete(path);
        }
    }

    [Fact]
    public void Password_writer_fails_explicitly_instead_of_creating_unencrypted_data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitz-password-{Guid.NewGuid():N}.zip");
        var handle = LegacyZip.ZlibWapi_ZipOpen(path, 0);
        var date = DateBank(2025, 0, 2, 0, 0, 0);
        var empty = LegacyBanks.CreateBank(0);
        try
        {
            Assert.Equal(-102, LegacyZip.ZlibWapi_ZipOpenNewFileInZip3(handle, "secret.txt", date,
                empty, 0, empty, 0, string.Empty, 8, 6, 0, -15, 8, 0, "password", 0));
            Assert.Equal(0, LegacyZip.ZlibWapi_ZipClose(handle, string.Empty));
            handle = 0;
            using var archive = ZipFile.OpenRead(path);
            Assert.Empty(archive.Entries);
        }
        finally
        {
            if (handle != 0) LegacyZip.ZlibWapi_ZipClose(handle, string.Empty);
            LegacyBanks.FreeBank(empty);
            LegacyBanks.FreeBank(date);
            File.Delete(path);
        }
    }

    private static void AssertEntry(int handle, string name, int date, int empty, int data, int length, int method, int level)
    {
        Assert.Equal(0, LegacyZip.ZlibWapi_ZipOpenNewFileInZip(handle, name, date,
            empty, 0, empty, 0, string.Empty, method, level));
        Assert.Equal(0, LegacyZip.ZlibWapi_ZipWriteFileInZip(handle, data, length));
        Assert.Equal(0, LegacyZip.ZlibWapi_ZipCloseFileInZip(handle));
    }

    private static int DateBank(int year, int month, int day, int hour, int minute, int second)
    {
        var bank = LegacyBanks.CreateBank(36);
        LegacyBanks.PokeInt(bank, 0, second);
        LegacyBanks.PokeInt(bank, 4, minute);
        LegacyBanks.PokeInt(bank, 8, hour);
        LegacyBanks.PokeInt(bank, 12, day);
        LegacyBanks.PokeInt(bank, 16, month);
        LegacyBanks.PokeInt(bank, 20, year - 1980);
        return bank;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string ReadComment(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 22 - ushort.MaxValue); i--)
            if (BitConverter.ToUInt32(bytes, i) == 0x06054b50)
            {
                var length = BitConverter.ToUInt16(bytes, i + 20);
                return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes, i + 22, length);
            }
        return string.Empty;
    }

    private static int CompressionMethod(string path, string wantedName)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 22 - ushort.MaxValue); i--)
        {
            if (i < 0 || BitConverter.ToUInt32(bytes, i) != 0x06054b50) continue;
            var cursor = checked((int)BitConverter.ToUInt32(bytes, i + 16));
            var count = BitConverter.ToUInt16(bytes, i + 10);
            for (var entry = 0; entry < count; entry++)
            {
                var method = BitConverter.ToUInt16(bytes, cursor + 10);
                var nameLength = BitConverter.ToUInt16(bytes, cursor + 28);
                var extraLength = BitConverter.ToUInt16(bytes, cursor + 30);
                var commentLength = BitConverter.ToUInt16(bytes, cursor + 32);
                var name = Encoding.UTF8.GetString(bytes, cursor + 46, nameLength);
                if (name == wantedName) return method;
                cursor = checked(cursor + 46 + nameLength + extraLength + commentLength);
            }
        }
        return -1;
    }
}
