using Xunit;
using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Tests.Support;

namespace BlitzCS.BinaryCompiler.Tests;

public class PeReaderTests
{
    private static byte[] Fixture(out uint iat)
    {
        var f = new PeFixtureBuilder().WithCode([0x55, 0x8B, 0xEC, 0xC3]).Export("_foo@8", 0).Export("bar", 1).Export("_baz", 2).EntryPoint(0);
        iat = f.Import("kernel32.dll", "Sleep"); f.Import("kernel32.dll", "ExitProcess");
        return f.Relocate(PeFixtureBuilder.DataRva + 4).Relocate(PeFixtureBuilder.DataRva + 0x10).Build();
    }

    private static PeImage Read(byte[] b) => PeReader.Read(b);
    private static bool Has(PeImage i, string code) => i.Diagnostics.Any(d => d.Code == code && d.Severity == DiagnosticSeverity.Error);

    [Fact]
    public void ParsesValidFixture()
    {
        var bytes = Fixture(out var iat); var copy = (byte[])bytes.Clone();
        var img = Read(bytes);
        Assert.Empty(img.Diagnostics);
        Assert.Equal(copy, bytes);
        Assert.True(img.IsDll); Assert.Equal(PeFixtureBuilder.ImageBase, img.ImageBase); Assert.Equal(0x14C, img.Machine);
        Assert.Equal(PeFixtureBuilder.TextRva, img.EntryPointRva);
        Assert.Equal(64, img.Sha256.Length);
        Assert.Equal(["_baz", "_foo@8", "bar"], img.Exports.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal([(ushort)1, (ushort)2, (ushort)3], img.Exports.Select(e => e.Ordinal));
        Assert.All(img.Exports, e => Assert.Null(e.ForwarderTarget));
        Assert.Equal(2, img.Imports.Count);
        Assert.Equal(new PeImport("kernel32.dll", "Sleep", null, iat), img.Imports[0]);
        Assert.Equal(iat + 4, img.Imports[1].IatRva);
        Assert.Equal([PeFixtureBuilder.DataRva + 4, PeFixtureBuilder.DataRva + 0x10], img.Relocations.Select(r => r.Rva));
        Assert.Equal(img.SizeOfImage, (uint)img.MapImage().Length);
        Assert.Equal(0x55, img.MapImage()[PeFixtureBuilder.TextRva]);
    }

    [Fact]
    public void TruncationsNeverCrash()
    {
        var bytes = Fixture(out _);
        for (var len = 0; len < bytes.Length; len += 7)
        {
            try { var img = Read(bytes[..len]); Assert.NotNull(img.Diagnostics); }
            catch (PeFormatException) { }
        }
    }

    [Fact]
    public void NotPeThrows()
    {
        Assert.Throws<PeFormatException>(() => Read([1, 2, 3]));
        var b = Fixture(out _); b[0x40] = (byte)'X';
        Assert.Throws<PeFormatException>(() => Read(b));
        b = Fixture(out _); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x3C), 0xFFFFFFF0);
        Assert.Throws<PeFormatException>(() => Read(b));
    }

    [Fact] public void RejectsPe32Plus() { var b = Fixture(out _); b[0x58] = 0x0B; b[0x59] = 0x02; Assert.True(Has(Read(b), "PE001")); }
    [Fact] public void RejectsNonX86() { var b = Fixture(out _); b[0x44] = 0x64; b[0x45] = 0x86; Assert.True(Has(Read(b), "PE002")); }

    [Fact]
    public void BadSectionBounds()
    {
        var b = Fixture(out _); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x138 + 20), (uint)b.Length - 4);
        Assert.True(Has(Read(b), "PE003"));
    }

    [Fact]
    public void OverlappingSections()
    {
        var b = Fixture(out _); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x138 + 40 + 12), PeFixtureBuilder.TextRva);
        Assert.True(Has(Read(b), "PE004"));
    }

    [Fact]
    public void SectionTableTruncated() => Assert.True(Has(Read(Fixture(out _)[..0x150]), "PE006"));

    [Fact]
    public void BadRelocationType()
    {
        var bytes = Fixture(out _); var img = Read(bytes);
        var reloc = img.Sections.Single(s => s.Name == ".reloc");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((int)reloc.RawOffset + 8), (ushort)(0xA000 | 4));
        var bad = Read(bytes);
        var d = Assert.Single(bad.Diagnostics, x => x.Code == "PE031");
        Assert.Equal(PeFixtureBuilder.DataRva + 4, d.GuestRva);
    }

    [Fact]
    public void OrdinalImportAndIltFallback()
    {
        var bytes = Fixture(out var iat); var img = Read(bytes);
        var idata = img.Sections.Single(s => s.Name == ".idata");
        // ordinal import in the ILT for slot 0
        var iltOff = (int)idata.RawOffset + 0x400 + 2 * 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(iltOff), 0x80000000 | 42);
        Assert.Equal(new PeImport("kernel32.dll", null, 42, iat), Read(bytes).Imports[0]);
        // zero ILT => IAT is used
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)idata.RawOffset), 0);
        var viaIat = Read(bytes);
        Assert.Empty(viaIat.Diagnostics);
        Assert.Equal("Sleep", viaIat.Imports[0].Name);
    }

    [Fact]
    public void ForwarderExport()
    {
        var bytes = new PeFixtureBuilder().Export("fwd", 0).Build(); var img = Read(bytes);
        var ed = img.Sections.Single(s => s.Name == ".edata");
        var o = (int)ed.RawOffset; var text = "OTHER.Func\0"u8;
        // overwrite the function RVA to point at a string inside the export directory
        var strRva = PeFixtureBuilder.EdataRva + 0x100;
        text.CopyTo(bytes.AsSpan(o + 0x100));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o + 40), strRva);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x58 + 96 + 4), 0x200);
        var e = Assert.Single(Read(bytes).Exports);
        Assert.Equal("OTHER.Func", e.ForwarderTarget);
    }

    [Fact]
    public void ClrImagesAreErrors()
    {
        // Delay-load directories are supported (see DelayImportTests); only the CLR header stays an error.
        var b = Fixture(out _);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x58 + 96 + 14 * 8), 0x1000);
        Assert.True(Has(Read(b), "PE051"));
    }

    [Fact]
    public void ParsesTls()
    {
        var b = Fixture(out _); var img = Read(b); var data = img.Sections.Single(s => s.Name == ".data");
        var o = (int)data.RawOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), 0x11); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), 0x22);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), 0x33); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 12), 0x44);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x58 + 96 + 9 * 8), PeFixtureBuilder.DataRva);
        Assert.Equal(new PeTls(0x11, 0x22, 0x33, 0x44), Read(b).Tls);
    }

    [Fact]
    public void BadSizeOfImageAndEntry()
    {
        var b = Fixture(out _); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x58 + 56), 0);
        Assert.True(Has(Read(b), "PE007"));
        b = Fixture(out _); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x58 + 16), 0xFFFFFF);
        Assert.True(Has(Read(b), "PE008"));
    }

    [Fact]
    public void RelocationApplyAdjustsHighLow()
    {
        var code = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(4), PeFixtureBuilder.ImageBase + 0x2000);
        var img = Read(new PeFixtureBuilder().WithCode(code).Relocate(PeFixtureBuilder.TextRva + 4).Build());
        var mapped = img.MapImage();
        PeRelocator.Apply(img, mapped, 0x20000000);
        Assert.Equal(0x20002000u, BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan((int)PeFixtureBuilder.TextRva + 4)));
        var again = img.MapImage();
        PeRelocator.Apply(img, again, 0x0FFFF000);
        Assert.Equal(0x0FFFF000u + 0x2000, BinaryPrimitives.ReadUInt32LittleEndian(again.AsSpan((int)PeFixtureBuilder.TextRva + 4)));
    }

    [Fact]
    public void RelocationApplyChecksBounds()
    {
        var img = Read(new PeFixtureBuilder().Relocate(PeFixtureBuilder.TextRva).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => PeRelocator.Apply(img, new byte[16], 0x2000));
    }

    [Fact]
    public void ReadsRealWorldDll()
    {
        // Optional: point BLITZCS_REAL_PE32_DIR at a directory of locally available PE32 DLLs; nothing is copied or distributed.
        var dir = Environment.GetEnvironmentVariable("BLITZCS_REAL_PE32_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var p in Directory.EnumerateFiles(dir, "*.dll").Take(20))
        {
            if (!File.Exists(p)) continue;
            var img = Read(File.ReadAllBytes(p));
            Assert.Equal(0x14C, img.Machine);
            Assert.NotEmpty(img.Sections);
            Assert.True(img.Imports.Count > 0 || img.Exports.Count > 0);
            Assert.Equal(img.SizeOfImage, (uint)img.MapImage().Length);
        }
    }

    [Theory]
    [InlineData("_foo@8", GuestCallingConvention.Stdcall, 8)]
    [InlineData("_foo@0", GuestCallingConvention.Stdcall, 0)]
    [InlineData("@foo@12", GuestCallingConvention.Fastcall, 12)]
    [InlineData("_foo", GuestCallingConvention.Cdecl, null)]
    [InlineData("foo", GuestCallingConvention.Unknown, null)]
    [InlineData("foo@8", GuestCallingConvention.Unknown, null)]
    [InlineData("_foo@7", GuestCallingConvention.Unknown, null)]
    [InlineData("_foo@x", GuestCallingConvention.Unknown, null)]
    [InlineData("?Foo@@YAXXZ", GuestCallingConvention.Unknown, null)]
    [InlineData("_", GuestCallingConvention.Unknown, null)]
    [InlineData(null, GuestCallingConvention.Unknown, null)]
    public void AbiHints(string? name, GuestCallingConvention cc, int? bytes) => Assert.Equal((cc, bytes), ExportAbiHints.Infer(name));
}
