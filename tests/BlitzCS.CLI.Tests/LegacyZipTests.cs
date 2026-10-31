using System.Text;
using Blitz3DCS.Runtime;
using BlitzCS.LegacyRuntime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyZipTests
{
    [Fact]
    public void Unz_reader_opens_external_zip_iterates_metadata_and_reads_entry_bytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-zipapi-fixture.zip");
        var handle = LegacyZip.ZlibWapi_UnzOpen(path);
        Assert.True(handle > 0);
        var info = LegacyBanks.CreateBank(80);
        var name = LegacyBanks.CreateBank(64);
        var output = LegacyBanks.CreateBank(64);
        try
        {
            Assert.Equal(0, LegacyZip.ZlibWapi_UnzGetGlobalInfo(handle, info));
            Assert.Equal(2, LegacyBanks.PeekInt(info, 0));
            Assert.Equal(11, LegacyBanks.PeekInt(info, 4));
            var comment = LegacyBanks.CreateBank(12);
            try
            {
                Assert.Equal(11, LegacyZip.ZlibWapi_UnzGetGlobalComment(handle, comment, 11));
                Assert.Equal("zip comment", LegacyBanks.WithBytes(comment, 0, 11, bytes => Encoding.ASCII.GetString(bytes)));
            }
            finally { LegacyBanks.FreeBank(comment); }

            Assert.Equal(0, LegacyZip.ZlibWapi_UnzGoToFirstFile(handle));
            Assert.Equal(0, LegacyZip.ZlibWapi_UnzGetCurrentFileInfo(handle, info, name, 64, 0, 0, 0, 0));
            Assert.Equal("first.txt", ReadCString(name));
            Assert.Equal(unchecked((int)0x78f8b5f6), LegacyBanks.PeekInt(info, 20));
            Assert.Equal(12, LegacyBanks.PeekInt(info, 28));
            Assert.Equal(0, LegacyZip.ZlibWapi_UnzOpenCurrentFile(handle));
            Assert.Equal(12, LegacyZip.ZlibWapi_UnzReadCurrentFile(handle, output, 64));
            Assert.Equal(Encoding.ASCII.GetBytes("first entry\0"), LegacyBanks.WithBytes(output, 0, 12, bytes => bytes.ToArray()));
            Assert.Equal(0, LegacyZip.ZlibWapi_UnzCloseCurrentFile(handle));

            Assert.Equal(0, LegacyZip.ZlibWapi_UnzGoToNextFile(handle));
            Assert.Equal(0, LegacyZip.ZlibWapi_UnzGetCurrentFileInfo(handle, info, name, 64, 0, 0, 0, 0));
            Assert.Equal("Maps/room é.dat", ReadCString(name));
            Assert.Equal(-100, LegacyZip.ZlibWapi_UnzGoToNextFile(handle));
            Assert.Equal(-100, LegacyZip.ZlibWapi_UnzLocateFile(handle, "missing.txt", 0));
            Assert.Equal(0, LegacyZip.ZlibWapi_UnzLocateFile(handle, "maps/ROOM é.dat", 0));
        }
        finally
        {
            LegacyZip.ZlibWapi_UnzClose(handle);
            LegacyBanks.FreeBank(output);
            LegacyBanks.FreeBank(name);
            LegacyBanks.FreeBank(info);
        }
    }

    [Fact]
    public void Invalid_archives_and_handles_return_minizip_style_errors()
    {
        Assert.Equal(0, LegacyZip.ZlibWapi_UnzOpen(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.Equal(-102, LegacyZip.ZlibWapi_UnzGoToFirstFile(123456789));
        Assert.Equal(-102, LegacyZip.ZlibWapi_UnzClose(123456789));
    }

    private static string ReadCString(int bank)
    {
        var size = LegacyBanks.BankSize(bank);
        var bytes = LegacyBanks.WithBytes(bank, 0, size, value => value.ToArray());
        var nul = Array.IndexOf(bytes, (byte)0);
        return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes, 0, nul < 0 ? bytes.Length : nul);
    }
}
