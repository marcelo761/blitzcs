using System.Buffers.Binary;

namespace Blitz3DCS.Engine.Windows;

internal static class DdsDecoder
{
    private const int HeaderSize = 128;
    private const int MaxDimension = 16_384;
    private const int MaxDecodedBytes = 256 * 1024 * 1024;
    private const uint FourCcFlag = 0x4;

    internal static bool HasSignature(ReadOnlySpan<byte> signature) =>
        signature.Length >= 4 && signature[0] == (byte)'D' && signature[1] == (byte)'D' &&
        signature[2] == (byte)'S' && signature[3] == (byte)' ';

    internal static (int Width, int Height, byte[] Rgba8) Decode(Stream stream, ReadOnlySpan<byte> signature) =>
        Decode(stream, signature, TextureLoadOptions.Disabled);

    internal static (int Width, int Height, byte[] Rgba8) Decode(Stream stream, ReadOnlySpan<byte> signature, bool legacy)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        signature.CopyTo(header);
        ReadExactly(stream, header[signature.Length..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 124 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[76..]) != 32)
            throw new InvalidDataException("DDS header has an invalid size.");

        var height = ReadDimension(header[12..]);
        var width = ReadDimension(header[16..]);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(header[80..]) & FourCcFlag) == 0)
            throw new InvalidDataException("DDS format is not a supported block-compressed format.");

        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        var blockBytes = fourCc switch
        {
            0x31545844 => 8,  // DXT1
            0x33545844 => 16, // DXT3
            0x35545844 => 16, // DXT5
            _ => throw new InvalidDataException("DDS FourCC is not supported.")
        };

        var pixelBytes = checked(width * height * 4);
        if (pixelBytes > MaxDecodedBytes)
            throw new InvalidDataException("DDS image exceeds the decoded size limit.");
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var compressedBytes = checked(checked(blocksWide * blocksHigh) * blockBytes);
        var rgba = new byte[pixelBytes];
        if (legacy)
        {
            var compressed = new byte[compressedBytes];
            ReadExactly(stream, compressed);
            var offset = 0;
            for (var by = 0; by < blocksHigh; by++)
            for (var bx = 0; bx < blocksWide; bx++)
            {
                var block = compressed.AsSpan(offset, blockBytes);
                offset += blockBytes;
                DecodeBlock(block, fourCc, rgba, width, height, bx * 4, by * 4);
            }
            return (width, height, rgba);
        }

        // Stream one block row at a time through a pooled buffer instead of materializing the whole payload.
        var rowBytes = checked(blocksWide * blockBytes);
        var rowBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (var by = 0; by < blocksHigh; by++)
            {
                ReadExactly(stream, rowBuffer.AsSpan(0, rowBytes));
                for (var bx = 0; bx < blocksWide; bx++)
                    DecodeBlock(rowBuffer.AsSpan(bx * blockBytes, blockBytes), fourCc, rgba, width, height, bx * 4, by * 4);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rowBuffer); }
        return (width, height, rgba);
    }

    private static int ReadDimension(ReadOnlySpan<byte> value)
    {
        var dimension = BinaryPrimitives.ReadUInt32LittleEndian(value);
        if (dimension is 0 or > MaxDimension)
            throw new InvalidDataException("DDS dimensions are outside the supported range.");
        return (int)dimension;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = stream.Read(destination);
            if (read == 0) throw new InvalidDataException("DDS data ended unexpectedly.");
            destination = destination[read..];
        }
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, uint fourCc, byte[] output,
        int width, int height, int originX, int originY)
    {
        var colorOffset = fourCc == 0x31545844 ? 0 : 8;
        Span<byte> palette = stackalloc byte[16];
        DecodeColors(block[colorOffset..], palette, fourCc == 0x31545844);
        var colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block[(colorOffset + 4)..]);

        ulong alphaBits = 0;
        byte alpha0 = 255, alpha1 = 255;
        if (fourCc == 0x33545844)
        {
            for (var i = 0; i < 8; i++) alphaBits |= (ulong)block[i] << (i * 8);
        }
        else if (fourCc == 0x35545844)
        {
            alpha0 = block[0]; alpha1 = block[1];
            for (var i = 0; i < 6; i++) alphaBits |= (ulong)block[i + 2] << (i * 8);
        }

        Span<byte> alphaPalette = stackalloc byte[8];
        if (fourCc == 0x35545844) DecodeAlphaPalette(alpha0, alpha1, alphaPalette);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var x = originX + pixel % 4;
            var y = originY + pixel / 4;
            if (x >= width || y >= height) continue;
            var destination = (y * width + x) * 4;
            var index = (int)((colorIndices >> (pixel * 2)) & 3);
            output[destination] = palette[index * 4];
            output[destination + 1] = palette[index * 4 + 1];
            output[destination + 2] = palette[index * 4 + 2];
            output[destination + 3] = fourCc switch
            {
                0x33545844 => (byte)(((alphaBits >> (pixel * 4)) & 0xf) * 17),
                0x35545844 => alphaPalette[(int)((alphaBits >> (pixel * 3)) & 7)],
                _ => palette[index * 4 + 3]
            };
        }
    }

    private static void DecodeColors(ReadOnlySpan<byte> block, Span<byte> palette, bool dxt1)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Decode565(color0, palette);
        Decode565(color1, palette[4..]);
        if (color0 > color1 || !dxt1)
        {
            for (var c = 0; c < 3; c++)
            {
                palette[8 + c] = (byte)((2 * palette[c] + palette[4 + c]) / 3);
                palette[12 + c] = (byte)((palette[c] + 2 * palette[4 + c]) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (var c = 0; c < 3; c++) palette[8 + c] = (byte)((palette[c] + palette[4 + c]) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = palette[15] = 0;
        }
    }

    private static void Decode565(ushort color, Span<byte> rgba)
    {
        var r = (color >> 11) & 31;
        var g = (color >> 5) & 63;
        var b = color & 31;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }

    private static void DecodeAlphaPalette(byte alpha0, byte alpha1, Span<byte> palette)
    {
        palette[0] = alpha0; palette[1] = alpha1;
        if (alpha0 > alpha1)
            for (var i = 1; i <= 6; i++) palette[i + 1] = (byte)(((7 - i) * alpha0 + i * alpha1) / 7);
        else
        {
            for (var i = 1; i <= 4; i++) palette[i + 1] = (byte)(((5 - i) * alpha0 + i * alpha1) / 5);
            palette[6] = 0; palette[7] = 255;
        }
    }
}
