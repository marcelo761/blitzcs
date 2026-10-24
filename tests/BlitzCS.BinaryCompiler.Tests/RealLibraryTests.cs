using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// A genuine third-party PE32 library (libogg, MSVC build) translated and executed. Nothing is copied into the repository:
/// point BLITZCS_REAL_PE32_DIR at a directory containing ogg.dll. Results are compared with an independent C# reference of the
/// Ogg page format and its CRC (not with the translator).
/// </summary>
[Collection("GuestRuntime")]
public sealed class RealLibraryTests
{
    private static string? OggPath()
    {
        var dir = Environment.GetEnvironmentVariable("BLITZCS_REAL_PE32_DIR");
        var path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "ogg.dll");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    private static uint OggCrc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        uint crc = 0;
        foreach (var b in header) crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
        foreach (var b in body) crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
        return crc;
    }

    [Fact]
    public void OggPageFunctionsMatchAnIndependentReference()
    {
        var path = OggPath();
        if (path is null) return; // no local copy of the library: nothing to validate
        var dll = File.ReadAllBytes(path);
        var image = PeReader.Read(dll);
        var options = new BinaryCompilerOptions(OptimizationProfile.Release);
        var artifact = X64Translator.Translate(image, options, "real-ogg");
        // The CRT start-up code (SEH through fs:) is rejected with explicit errors; the functions below never reach it.
        Assert.Contains(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "BC_SEGMENT_OVERRIDE");
        using var module = GuestModule.Load(artifact, image, new DictionaryGuestImportResolver(),
            new GuestModuleOptions { RunEntryPoint = false, AllowTranslationErrors = true, AllowUnboundImports = true });
        GuestFunction Fn(string name) => module.Bind(name, GuestCallingConvention.Cdecl, 1);
        var version = Fn("ogg_page_version"); var continued = Fn("ogg_page_continued"); var bos = Fn("ogg_page_bos"); var eos = Fn("ogg_page_eos");
        var pageno = Fn("ogg_page_pageno"); var serial = Fn("ogg_page_serialno"); var packets = Fn("ogg_page_packets"); var checksum = Fn("ogg_page_checksum_set");

        var seed = 0x2545F491u;
        uint Next() { seed = seed * 1664525u + 1013904223u; return seed >> 8; }
        for (var round = 0; round < 300; round++)
        {
            var segments = (int)(Next() % 40);
            var header = new byte[27 + segments];
            "OggS"u8.CopyTo(header);
            header[4] = (byte)(round % 3 == 0 ? 0 : Next() & 1);
            header[5] = (byte)(Next() & 7);
            for (var i = 6; i < 26; i++) header[i] = (byte)Next();
            header[26] = (byte)segments;
            var bodyLength = 0;
            for (var i = 0; i < segments; i++) { header[27 + i] = (byte)(Next() % 4 == 0 ? 255 : Next() % 255); bodyLength += header[27 + i]; }
            var body = new byte[bodyLength];
            for (var i = 0; i < body.Length; i++) body[i] = (byte)Next();

            using var h = module.Memory.Allocate(header.Length); h.Write(0, header);
            using var bd = module.Memory.Allocate(Math.Max(1, body.Length)); if (body.Length > 0) bd.Write(0, body);
            using var page = module.Memory.Allocate(16);
            page.Write(0, BitConverter.GetBytes(h.Address)); page.Write(4, BitConverter.GetBytes(header.Length));
            page.Write(8, BitConverter.GetBytes(bd.Address)); page.Write(12, BitConverter.GetBytes(body.Length));

            Assert.Equal((uint)header[4], version.Call(page.Address));
            Assert.Equal((uint)(header[5] & 1), continued.Call(page.Address));
            Assert.Equal((uint)(header[5] & 2), bos.Call(page.Address));
            Assert.Equal((uint)(header[5] & 4), eos.Call(page.Address));
            Assert.Equal(BitConverter.ToUInt32(header, 18), pageno.Call(page.Address));
            Assert.Equal(BitConverter.ToUInt32(header, 14), serial.Call(page.Address));
            var expectedPackets = 0u;
            for (var i = 0; i < segments; i++) if (header[27 + i] < 255) expectedPackets++;
            Assert.Equal(expectedPackets, packets.Call(page.Address));

            var expected = (byte[])header.Clone();
            expected[22] = expected[23] = expected[24] = expected[25] = 0;
            var crc = OggCrc(expected, body);
            BitConverter.GetBytes(crc).CopyTo(expected, 22);
            checksum.Call(page.Address);
            Assert.Equal(expected, h.ToArray());
        }
    }
}
