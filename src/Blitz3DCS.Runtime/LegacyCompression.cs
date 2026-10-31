using System.Buffers.Binary;
using System.IO.Compression;
using BlitzCS.LegacyRuntime;

namespace Blitz3DCS.Runtime;

/// <summary>Typed adapters for the zlib functions used by Blitz_File_ZipApi.bb.</summary>
/// <remarks>Bank handles stay managed tokens; no native pointers cross the runtime boundary.</remarks>
public static class LegacyCompression
{
    private const int Ok = 0;
    private const int StreamError = -2;
    private const int DataError = -3;
    private const int MemoryError = -4;
    private const int BufferError = -5;
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    /// <summary>Updates a zlib CRC-32 value with the first sourceLength bytes of a bank.</summary>
    public static int ZlibWapi_Crc32(int crcValue, int sourceBuffer, int sourceLength)
    {
        if (sourceLength < 0) return 0;
        try
        {
            var crc = unchecked((uint)crcValue) ^ uint.MaxValue;
            LegacyBanks.WithBytes(sourceBuffer, 0, sourceLength, bytes =>
            {
                foreach (var value in bytes)
                    crc = Crc32Table[(crc ^ value) & 0xff] ^ (crc >> 8);
                return 0;
            });
            return unchecked((int)(crc ^ uint.MaxValue));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>Updates a zlib Adler-32 value with the first sourceLength bytes of a bank.</summary>
    public static int ZlibWapi_Adler32(int adler, int sourceBuffer, int sourceLength)
    {
        if (sourceLength < 0) return 0;
        try
        {
            var value = unchecked((uint)adler);
            if (value == 0) value = 1;
            var low = value & 0xffff;
            var high = value >> 16;
            LegacyBanks.WithBytes(sourceBuffer, 0, sourceLength, bytes =>
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var block = Math.Min(bytes.Length - offset, 5552);
                    var end = offset + block;
                    for (; offset < end; offset++)
                    {
                        low += bytes[offset];
                        high += low;
                    }
                    low %= 65521;
                    high %= 65521;
                }
                return 0;
            });
            return unchecked((int)((high << 16) | low));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>Returns zlib's compressBound value for a nonnegative source size.</summary>
    public static int ZlibWapi_CompressBound(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        return checked(sourceLength + (sourceLength >> 12) + (sourceLength >> 14) + (sourceLength >> 25) + 13);
    }

    /// <summary>Compresses one bank using the RFC 1950 zlib wrapper and zlib level -1 through 9.</summary>
    public static int ZlibWapi_Compress2(int destBank, int destSizeBank, int sourceBank, int sourceSize, int compressionLevel)
    {
        if (sourceSize < 0 || compressionLevel is < -1 or > 9 ||
            destBank == sourceBank || destBank == destSizeBank || sourceBank == destSizeBank)
            return StreamError;

        try
        {
            var capacity = ReadCapacity(destSizeBank);
            if (capacity < 0 || capacity > LegacyBanks.BankSize(destBank)) return StreamError;
            var input = LegacyBanks.WithBytes(sourceBank, 0, sourceSize, bytes => bytes.ToArray());
            var options = new ZLibCompressionOptions { CompressionLevel = compressionLevel };
            using var output = new MemoryStream();
            using (var compressor = new ZLibStream(output, options, leaveOpen: true))
                compressor.Write(input);
            var compressed = output.ToArray();
            if (compressed.Length > capacity)
            {
                WriteBytes(destBank, capacity, compressed.AsSpan(0, capacity));
                WriteCapacity(destSizeBank, capacity);
                return BufferError;
            }

            WriteBytes(destBank, capacity, compressed);
            WriteCapacity(destSizeBank, compressed.Length);
            return Ok;
        }
        catch (OutOfMemoryException) { return MemoryError; }
        catch (ArgumentOutOfRangeException) { return StreamError; }
        catch (ArgumentException) { return StreamError; }
        catch (InvalidOperationException) { return StreamError; }
        catch (KeyNotFoundException) { return StreamError; }
    }

    /// <summary>Inflates one RFC 1950 bank into the supplied capacity, retaining zlib's partial output on Z_BUF_ERROR.</summary>
    public static int ZlibWapi_UnCompress(int destBank, int destSizeBank, int sourceBank, int sourceSize)
    {
        if (sourceSize < 0 || destBank == sourceBank || destBank == destSizeBank || sourceBank == destSizeBank)
            return StreamError;

        var capacity = 0;
        MemoryStream? output = null;
        try
        {
            capacity = ReadCapacity(destSizeBank);
            if (capacity < 0 || capacity > LegacyBanks.BankSize(destBank)) return StreamError;
            var input = LegacyBanks.WithBytes(sourceBank, 0, sourceSize, bytes => bytes.ToArray());
            using var source = new MemoryStream(input, writable: false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            output = new MemoryStream(Math.Min(capacity, 64 * 1024));
            var buffer = new byte[8192];
            var result = Ok;
            while (true)
            {
                var remaining = capacity - checked((int)output.Length);
                var read = inflater.Read(buffer, 0, Math.Min(buffer.Length, remaining == 0 ? 1 : remaining + (remaining < buffer.Length ? 1 : 0)));
                if (read == 0) break;
                var writable = Math.Min(read, remaining);
                output.Write(buffer, 0, writable);
                if (writable != read)
                {
                    result = BufferError;
                    break;
                }
            }

            var bytesWritten = checked((int)output.Length);
            WriteBytes(destBank, capacity, output.GetBuffer().AsSpan(0, bytesWritten));
            WriteCapacity(destSizeBank, bytesWritten);
            return result;
        }
        catch (InvalidDataException)
        {
            // ZLibStream can throw after reading ahead, before returning bytes already decoded.
            // Re-read the raw deflate payload to preserve zlib's partial-output contract.
            var (partial, overflow) = ReadPartialDeflate(sourceBank, sourceSize, capacity);
            if (partial.Length > 0)
            {
                WriteBytes(destBank, capacity, partial);
                WriteCapacity(destSizeBank, partial.Length);
            }
            return overflow ? BufferError : DataError;
        }
        catch (OutOfMemoryException) { return MemoryError; }
        catch (ArgumentOutOfRangeException) { return StreamError; }
        catch (ArgumentException) { return StreamError; }
        catch (InvalidOperationException) { return StreamError; }
        catch (KeyNotFoundException) { return StreamError; }
        finally { output?.Dispose(); }
    }

    private static (byte[] Bytes, bool Overflow) ReadPartialDeflate(int sourceBank, int sourceSize, int capacity)
    {
        try
        {
            var input = LegacyBanks.WithBytes(sourceBank, 0, sourceSize, bytes => bytes.ToArray());
            if (input.Length < 6 || (input[0] & 0x0f) != 8 || (input[0] >> 4) > 7 ||
                ((input[0] << 8) | input[1]) % 31 != 0 || (input[1] & 0x20) != 0)
                return ([], false);

            using var source = new MemoryStream(input, 2, input.Length - 6, writable: false);
            using var inflater = new DeflateStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Min(capacity, 64 * 1024));
            try
            {
                var buffer = new byte[8192];
                var overflow = false;
                while (true)
                {
                    var remaining = capacity - checked((int)output.Length);
                    var read = inflater.Read(buffer, 0, Math.Min(buffer.Length, remaining == 0 ? 1 : remaining + (remaining < buffer.Length ? 1 : 0)));
                    if (read == 0) break;
                    var writable = Math.Min(read, remaining);
                    output.Write(buffer, 0, writable);
                    if (writable != read)
                    {
                        overflow = true;
                        break;
                    }
                }
                return (output.ToArray(), overflow);
            }
            catch (InvalidDataException) { return (output.ToArray(), false); }
        }
        catch (InvalidDataException) { return ([], false); }
    }

    private static int ReadCapacity(int sizeBank) => LegacyBanks.WithBytes(sizeBank, 0, 4,
        bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static void WriteCapacity(int sizeBank, int value) => LegacyBanks.WithBytes(sizeBank, 0, 4, bytes =>
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return 0;
    });

    private static void WriteBytes(int bank, int capacity, ReadOnlySpan<byte> output)
    {
        var snapshot = output.ToArray();
        LegacyBanks.WithBytes(bank, 0, Math.Min(capacity, snapshot.Length), bytes =>
        {
            snapshot.CopyTo(bytes);
            return 0;
        });
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var value = n;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[n] = value;
        }
        return table;
    }
}
