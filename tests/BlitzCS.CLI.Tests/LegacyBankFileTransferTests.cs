using Blitz3DCS.Runtime;
using BlitzCS.LegacyRuntime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyBankFileTransferTests
{
    [Fact]
    public void ReadBytes_copies_available_file_bytes_at_bank_offset_and_reports_partial_eof()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitz-read-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [10, 20, 30, 40, 50]);
        var bank = LegacyBanks.CreateBank(8);
        var file = LegacyStreams.ReadFile(path);
        try
        {
            Assert.NotEqual(0, file);
            Assert.Equal(4, LegacyStreams.ReadBytes(bank, file, 2, 4));
            Assert.Equal(new byte[] { 0, 0, 10, 20, 30, 40, 0, 0 }, ReadBank(bank));
            Assert.Equal(1, LegacyStreams.ReadBytes(bank, file, 0, 8));
            Assert.Equal(new byte[] { 50, 0, 10, 20, 30, 40, 0, 0 }, ReadBank(bank));
            Assert.Equal(0, LegacyStreams.ReadBytes(bank, file, 0, 8));
        }
        finally
        {
            LegacyStreams.CloseFile(file);
            LegacyBanks.FreeBank(bank);
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteBytes_uses_bank_range_clamps_to_bank_end_and_returns_written_count()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitz-write-{Guid.NewGuid():N}.bin");
        var bank = LegacyBanks.CreateBank(5);
        var file = LegacyStreams.WriteFile(path);
        try
        {
            LegacyBanks.WithBytes(bank, 0, 5, bytes => { new byte[] { 3, 5, 7, 9, 11 }.CopyTo(bytes); return 0; });
            Assert.Equal(4, LegacyStreams.WriteBytes(bank, file, 1, 10));
            Assert.Equal(0, LegacyStreams.WriteBytes(bank, file, 5, 1));
            Assert.Equal(0, LegacyStreams.WriteBytes(bank, file, -1, 2));
            LegacyStreams.CloseFile(file);
            file = 0;
            Assert.Equal(new byte[] { 5, 7, 9, 11 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (file != 0) LegacyStreams.CloseFile(file);
            LegacyBanks.FreeBank(bank);
            File.Delete(path);
        }
    }

    private static byte[] ReadBank(int bank) => LegacyBanks.WithBytes(bank, 0, LegacyBanks.BankSize(bank), bytes => bytes.ToArray());
}
