using System.Text;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.Linking.E2E.Tests;

/// <summary>
/// A plain .bb program calls exports of the genuine libogg / libvorbis (BLITZCS_REAL_PE32_DIR; skipped when unset; never copied into the repository).
/// Expected output comes from an independent C# model of the Ogg bit-packing and page/CRC definitions.
/// </summary>
[Collection("LinkingE2E")]
public sealed class RealOggTests : E2EBase
{
    private static readonly uint[] CrcTable = BuildTable();
    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { var r = i << 24; for (var k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1; t[i] = r; }
        return t;
    }
    private static uint Crc(byte[] data) { uint c = 0; foreach (var x in data) c = (c << 8) ^ CrcTable[((c >> 24) & 0xFF) ^ x]; return c; }

    /// <summary>Copies the named libraries into the test's scratch folder. The callers are [RealLibraryFact]s, skipped at discovery when the files are absent.</summary>
    private void Stage(params string[] files)
    {
        foreach (var f in files) File.Copy(RealLibraries.PathOf(f), Path.Combine(Root, f));
    }

    private static string ExpectedOgg()
    {
        var sb = new StringBuilder();
        // bit packer: LSB-first fields (5:3)(1000:11)(123456:20)(1:1) = 35 bits
        var bytes = new byte[8]; var pos = 0;
        foreach (var (v, bits) in new (uint, int)[] { (5, 3), (1000, 11), (123456, 20), (1, 1) })
            for (var b = 0; b < bits; b++, pos++) if (((v >> b) & 1) != 0) bytes[pos >> 3] |= (byte)(1 << (pos & 7));
        var n = (pos + 7) >> 3;
        sb.Append(n).Append('\n');
        for (var i = 0; i < n; i++) sb.Append(bytes[i]).Append('\n');
        sb.Append("5\n1000\n123456\n1\n");
        // page: one 8-byte packet, bos+eos (libogg 1.3 leaves this page's granule field 0; confirmed against the native libogg.so), serial 0x1234ABCD, pageno 0
        var body = Enumerable.Range(0, 8).Select(i => (byte)(i * 10 + 3)).ToArray();
        var header = new byte[28];
        "OggS"u8.CopyTo(header); header[5] = 0x06;
        BitConverter.GetBytes(0x1234ABCD).CopyTo(header, 14);
        header[26] = 1; header[27] = 8;
        var crc = Crc(header.Concat(body).ToArray());
        sb.Append("28\n8\n6\n").Append(0x1234ABCD).Append("\n0\n").Append(unchecked((int)crc)).Append('\n');
        foreach (var b in body) sb.Append(b).Append('\n');
        return sb.ToString();
    }

    [RealLibraryFact("ogg.dll")]
    public void BasicProgramDrivesRealLibogg()
    {
        Stage("ogg.dll");
        var catalog = File.ReadAllText(RepoFile("samples/BinaryLinking/Real/ogg.json"));
        var source = File.ReadAllText(RepoFile("samples/BinaryLinking/Real/ogg.bb"));
        var stdout = Build(source, catalog, out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        Assert.Equal(ExpectedOgg(), Run());
    }

    [RealLibraryFact("ogg.dll", "vorbis.dll")]
    public void VorbisAndOggLoadAsOneRebasedSetAndVorbisCallsIntoOgg()
    {
        Stage("ogg.dll", "vorbis.dll");
        const string I = "Integer";
        var catalog = "{\"binaryLibraries\":[{\"path\":\"ogg.dll\",\"defaultConvention\":\"cdecl\"},{\"path\":\"vorbis.dll\",\"defaultConvention\":\"cdecl\"}],\"functions\":["
            + string.Join(",", new[]
            {
                Entry("GuestAlloc", [I], I, "ogg.dll", "x").Replace("\"export\":\"x\"", "\"intrinsic\":\"alloc\""),
                Entry("GuestPeek8", [I], I, "vorbis.dll", "x").Replace("\"export\":\"x\"", "\"intrinsic\":\"peek8\""),
                Entry("GuestPeek32", [I], I, "vorbis.dll", "x").Replace("\"export\":\"x\"", "\"intrinsic\":\"peek32\""),
                Entry("CommentInit", [I], "Void", "vorbis.dll", "vorbis_comment_init", "cdecl"),
                Entry("CommentHeaderOut", [I, I], I, "vorbis.dll", "vorbis_commentheader_out", "cdecl"),
            }) + "]}";
        var source = "vc = GuestAlloc(16)\nop = GuestAlloc(32)\nCommentInit vc\nPrint CommentHeaderOut(vc, op)\np = GuestPeek32(op)\nn = GuestPeek32(op + 4)\nPrint n\n"
            + "For i = 0 To 6\nPrint GuestPeek8(p + i)\nNext\nPrint GuestPeek32(p + 7)\nPrint GuestPeek8(p + n - 1)\n";
        var stdout = Build(source, catalog, out var exit, out var err);
        Assert.True(exit == 0, err + stdout);
        var lines = Run().TrimEnd('\n').Split('\n');
        Assert.Equal(11, lines.Length);
        var vendorLength = int.Parse(lines[9]);
        Assert.Equal("0", lines[0]);
        Assert.Equal(vendorLength + 7 + 4 + 4 + 1, int.Parse(lines[1]));     // type+"vorbis", vendor length, vendor, comment count, framing bit
        Assert.Equal(new[] { "3", "118", "111", "114", "98", "105", "115" }, lines[2..9]); // 0x03 "vorbis"
        Assert.Equal("1", lines[10]);                                          // framing bit
    }
}
