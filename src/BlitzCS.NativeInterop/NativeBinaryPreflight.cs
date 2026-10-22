using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace BlitzCS.NativeInterop;

internal static class NativeBinaryPreflight
{
    internal static void Validate(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[4];
        if (stream.Read(prefix) < prefix.Length)
            throw Error(path, "binary header is truncated");

        if (prefix[0] == 0x7f && prefix[1] == (byte)'E' && prefix[2] == (byte)'L' && prefix[3] == (byte)'F')
        {
            if (!OperatingSystem.IsLinux()) throw Error(path, "ELF libraries require Linux, but this process is running on another operating system");
            ValidateElf(path, stream);
            return;
        }

        if (prefix[0] == (byte)'M' && prefix[1] == (byte)'Z')
        {
            if (!OperatingSystem.IsWindows()) throw Error(path, "PE libraries require Windows, but this process is running on another operating system");
            ValidatePe(path, stream);
            return;
        }

        throw Error(path, "unsupported native library format; expected an ELF or PE binary");
    }

    private static void ValidateElf(string path, Stream stream)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[20];
        if (stream.Read(header) < header.Length) throw Error(path, "ELF binary header is truncated");
        var expectedClass = Environment.Is64BitProcess ? 2 : 1;
        if (header[4] != expectedClass) throw Error(path, $"ELF class {header[4]} does not match this {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process");
        if (header[5] != 1) throw Error(path, "ELF binary uses unsupported byte order; little-endian is required");
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]);
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (ushort)62,
            Architecture.X86 => (ushort)3,
            Architecture.Arm => (ushort)40,
            Architecture.Arm64 => (ushort)183,
            _ => (ushort)0
        };
        if (expected == 0 || machine != expected)
            throw Error(path, $"ELF machine {machine} does not match process architecture {RuntimeInformation.ProcessArchitecture}");
    }

    private static void ValidatePe(string path, Stream stream)
    {
        stream.Position = 0;
        Span<byte> dos = stackalloc byte[64];
        if (stream.Read(dos) < dos.Length) throw Error(path, "PE DOS header is truncated");
        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos[60..64]);
        if (peOffset > stream.Length - 6) throw Error(path, "PE header offset is outside the file or truncated");
        stream.Position = peOffset;
        Span<byte> header = stackalloc byte[6];
        if (stream.Read(header) < header.Length || !header[..4].SequenceEqual("PE\0\0"u8)) throw Error(path, "PE signature or machine field is missing");
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]);
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (ushort)0x8664,
            Architecture.X86 => (ushort)0x014c,
            Architecture.Arm => (ushort)0x01c4,
            Architecture.Arm64 => (ushort)0xaa64,
            _ => (ushort)0
        };
        if (expected == 0 || machine != expected)
            throw Error(path, $"PE machine 0x{machine:x4} does not match process architecture {RuntimeInformation.ProcessArchitecture}");
    }

    private static NativeLibraryLoadException Error(string path, string detail) => new(path, $"Cannot load native library '{path}': {detail}.");
}
