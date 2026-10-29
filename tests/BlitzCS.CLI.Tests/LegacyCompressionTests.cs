using BlitzCS.LegacyRuntime;
using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyCompressionTests
{
    [Fact]
    public void Crc32_matches_zlib_vectors_and_supports_incremental_bank_updates()
    {
        var input = System.Text.Encoding.ASCII.GetBytes("123456789");
        var first = LegacyBanks.CreateBank(4);
        var second = LegacyBanks.CreateBank(input.Length - 4);
        var whole = LegacyBanks.CreateBank(input.Length);
        var empty = LegacyBanks.CreateBank(0);
        try
        {
            LegacyBanks.WithBytes(first, 0, 4, bytes => { input.AsSpan(0, 4).CopyTo(bytes); return 0; });
            LegacyBanks.WithBytes(second, 0, input.Length - 4, bytes => { input.AsSpan(4).CopyTo(bytes); return 0; });
            LegacyBanks.WithBytes(whole, 0, input.Length, bytes => { input.CopyTo(bytes); return 0; });
            Assert.Equal(0, LegacyCompression.ZlibWapi_Crc32(0, empty, 0));
            var crc = LegacyCompression.ZlibWapi_Crc32(0, first, 4);
            Assert.Equal(unchecked((int)0xcbf43926), LegacyCompression.ZlibWapi_Crc32(crc, second, input.Length - 4));
            Assert.Equal(unchecked((int)0xcbf43926), LegacyCompression.ZlibWapi_Crc32(0, whole, input.Length));
        }
        finally { Free(empty, whole, second, first); }
    }

    [Fact]
    public void Adler32_matches_zlib_vectors_and_uses_one_for_empty_seed()
    {
        var input = System.Text.Encoding.ASCII.GetBytes("Wikipedia");
        var first = LegacyBanks.CreateBank(4);
        var second = LegacyBanks.CreateBank(input.Length - 4);
        var whole = LegacyBanks.CreateBank(input.Length);
        var empty = LegacyBanks.CreateBank(0);
        try
        {
            LegacyBanks.WithBytes(first, 0, 4, bytes => { input.AsSpan(0, 4).CopyTo(bytes); return 0; });
            LegacyBanks.WithBytes(second, 0, input.Length - 4, bytes => { input.AsSpan(4).CopyTo(bytes); return 0; });
            LegacyBanks.WithBytes(whole, 0, input.Length, bytes => { input.CopyTo(bytes); return 0; });
            Assert.Equal(1, LegacyCompression.ZlibWapi_Adler32(0, empty, 0));
            var adler = LegacyCompression.ZlibWapi_Adler32(0, first, 4);
            Assert.Equal(unchecked((int)0x11e60398), LegacyCompression.ZlibWapi_Adler32(adler, second, input.Length - 4));
            Assert.Equal(unchecked((int)0x11e60398), LegacyCompression.ZlibWapi_Adler32(0, whole, input.Length));
        }
        finally { Free(empty, whole, second, first); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void Compress2_writes_a_zlib_stream_and_round_trips_bank_bytes(int level)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(
            "Containment breach; maintain radio silence. ", 40)));
        var source = LegacyBanks.CreateBank(input.Length);
        var compressed = LegacyBanks.CreateBank(LegacyCompression.ZlibWapi_CompressBound(input.Length));
        var compressedSize = LegacyBanks.CreateBank(4);
        var output = LegacyBanks.CreateBank(input.Length);
        var outputSize = LegacyBanks.CreateBank(4);
        try
        {
            LegacyBanks.WithBytes(source, 0, input.Length, bytes => { input.CopyTo(bytes); return 0; });
            LegacyBanks.PokeInt(compressedSize, 0, LegacyBanks.BankSize(compressed));

            Assert.Equal(0, LegacyCompression.ZlibWapi_Compress2(compressed, compressedSize, source, input.Length, level));

            var actualCompressedSize = LegacyBanks.PeekInt(compressedSize, 0);
            var compressedBytes = LegacyBanks.WithBytes(compressed, 0, actualCompressedSize, bytes => bytes.ToArray());
            Assert.Equal(0x78, compressedBytes[0]);
            Assert.Equal(0, ((compressedBytes[0] << 8) | compressedBytes[1]) % 31);
            LegacyBanks.PokeInt(outputSize, 0, input.Length);

            Assert.Equal(0, LegacyCompression.ZlibWapi_UnCompress(output, outputSize, compressed, actualCompressedSize));
            Assert.Equal(input.Length, LegacyBanks.PeekInt(outputSize, 0));
            Assert.Equal(input, LegacyBanks.WithBytes(output, 0, input.Length, bytes => bytes.ToArray()));
        }
        finally
        {
            Free(outputSize, output, compressedSize, compressed, source);
        }
    }

    [Fact]
    public void CompressBound_matches_the_zlib_bound_formula_and_accepts_empty_data()
    {
        Assert.Equal(13, LegacyCompression.ZlibWapi_CompressBound(0));
        Assert.Equal(1024 + (1024 >> 12) + (1024 >> 14) + (1024 >> 25) + 13,
            LegacyCompression.ZlibWapi_CompressBound(1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyCompression.ZlibWapi_CompressBound(-1));
    }

    [Fact]
    public void UnCompress_reads_a_zlib_fixture_created_by_python_zlib()
    {
        // Python zlib.compress("Blitz3D zlib fixture" + NUL + " with binary data").
        byte[] compressed = Convert.FromHexString("789c73cac92ca9327651a8cac94c5248cbac28292d4a655028cf2cc95048cacc4b2caa5448492c49040008290db0");
        byte[] expected = System.Text.Encoding.UTF8.GetBytes("Blitz3D zlib fixture\0 with binary data");
        var source = LegacyBanks.CreateBank(compressed.Length);
        var output = LegacyBanks.CreateBank(expected.Length);
        var outputSize = LegacyBanks.CreateBank(4);
        try
        {
            LegacyBanks.WithBytes(source, 0, compressed.Length, bytes => { compressed.CopyTo(bytes); return 0; });
            LegacyBanks.PokeInt(outputSize, 0, expected.Length);
            Assert.Equal(0, LegacyCompression.ZlibWapi_UnCompress(output, outputSize, source, compressed.Length));
            Assert.Equal(expected, LegacyBanks.WithBytes(output, 0, expected.Length, bytes => bytes.ToArray()));
        }
        finally { Free(outputSize, output, source); }
    }

    [Fact]
    public void UnCompress_reports_and_writes_partial_output_when_capacity_is_too_small()
    {
        byte[] compressed = Convert.FromHexString("789c73cac92ca9327651a8cac94c5248cbac28292d4a655028cf2cc95048cacc4b2caa5448492c49040008290db0");
        byte[] expected = System.Text.Encoding.UTF8.GetBytes("Blitz3D zlib fixture\0 with binary data");
        const int capacity = 9;
        var source = LegacyBanks.CreateBank(compressed.Length);
        var output = LegacyBanks.CreateBank(capacity);
        var outputSize = LegacyBanks.CreateBank(4);
        try
        {
            LegacyBanks.WithBytes(source, 0, compressed.Length, bytes => { compressed.CopyTo(bytes); return 0; });
            LegacyBanks.PokeInt(outputSize, 0, capacity);
            Assert.Equal(-5, LegacyCompression.ZlibWapi_UnCompress(output, outputSize, source, compressed.Length));
            Assert.Equal(capacity, LegacyBanks.PeekInt(outputSize, 0));
            Assert.Equal(expected.AsSpan(0, capacity).ToArray(),
                LegacyBanks.WithBytes(output, 0, capacity, bytes => bytes.ToArray()));
        }
        finally { Free(outputSize, output, source); }
    }

    [Fact]
    public void UnCompress_retains_inflated_bytes_when_the_zlib_checksum_is_invalid()
    {
        byte[] compressed = Convert.FromHexString("789c73cac92ca9327651a8cac94c5248cbac28292d4a655028cf2cc95048cacc4b2caa5448492c49040008290db0");
        byte[] expected = System.Text.Encoding.UTF8.GetBytes("Blitz3D zlib fixture\0 with binary data");
        compressed[^1] ^= 1;
        var source = LegacyBanks.CreateBank(compressed.Length);
        var output = LegacyBanks.CreateBank(expected.Length);
        var outputSize = LegacyBanks.CreateBank(4);
        try
        {
            LegacyBanks.WithBytes(source, 0, compressed.Length, bytes => { compressed.CopyTo(bytes); return 0; });
            LegacyBanks.PokeInt(outputSize, 0, expected.Length);
            Assert.Equal(-3, LegacyCompression.ZlibWapi_UnCompress(output, outputSize, source, compressed.Length));
            Assert.Equal(expected.Length, LegacyBanks.PeekInt(outputSize, 0));
            Assert.Equal(expected, LegacyBanks.WithBytes(output, 0, expected.Length, bytes => bytes.ToArray()));
        }
        finally { Free(outputSize, output, source); }
    }

    [Fact]
    public void Compression_reports_zlib_errors_without_exposing_bank_addresses()
    {
        var source = LegacyBanks.CreateBank(3);
        var destination = LegacyBanks.CreateBank(1);
        var destinationSize = LegacyBanks.CreateBank(4);
        var malformed = LegacyBanks.CreateBank(3);
        var uncompressed = LegacyBanks.CreateBank(8);
        var uncompressedSize = LegacyBanks.CreateBank(4);
        try
        {
            LegacyBanks.PokeInt(destinationSize, 0, 1);
            Assert.Equal(-5, LegacyCompression.ZlibWapi_Compress2(destination, destinationSize, source, 3, 6));
            Assert.Equal(1, LegacyBanks.PeekInt(destinationSize, 0));
            Assert.Equal(-2, LegacyCompression.ZlibWapi_Compress2(destination, destinationSize, source, 3, 10));
            Assert.Equal(-2, LegacyCompression.ZlibWapi_Compress2(destination, destinationSize, int.MaxValue, 3, 6));

            LegacyBanks.WithBytes(malformed, 0, 3, bytes => { bytes.Fill(0xff); return 0; });
            LegacyBanks.PokeInt(uncompressedSize, 0, 8);
            Assert.Equal(-3, LegacyCompression.ZlibWapi_UnCompress(uncompressed, uncompressedSize, malformed, 3));
            Assert.Equal(8, LegacyBanks.PeekInt(uncompressedSize, 0));
        }
        finally
        {
            Free(uncompressedSize, uncompressed, malformed, destinationSize, destination, source);
        }
    }

    private static void Free(params int[] banks)
    {
        foreach (var bank in banks) LegacyBanks.FreeBank(bank);
    }
}
