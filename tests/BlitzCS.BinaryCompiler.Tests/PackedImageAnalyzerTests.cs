using System.Buffers.Binary;
using System.Text;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Structure-only packed-image heuristics, on controlled synthetic PE32 fixtures and (optionally) real local images.</summary>
public sealed class PackedImageAnalyzerTests
{
    private const uint Exec = 0x60000020, ExecWrite = 0xE0000060, Data = 0xC0000040;
    private static int SectionHeader(byte[] pe, int index) => 0x40 + 4 + 20 + 224 + index * 40;

    private static byte[] Mutate(byte[] pe, int section, string? name = null, uint? rawSize = null, uint? flags = null)
    {
        var b = (byte[])pe.Clone(); var h = SectionHeader(b, section);
        if (name is not null) { Array.Clear(b, h, 8); Encoding.ASCII.GetBytes(name).CopyTo(b, h); }
        if (rawSize is { } r) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(h + 16), r);
        if (flags is { } f) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(h + 36), f);
        return b;
    }

    private static byte[] SetEntry(byte[] pe, uint rva) { var b = (byte[])pe.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x40 + 24 + 16), rva); return b; }
    private static byte[] Noise(int n) { var b = new byte[n]; new Random(7).NextBytes(b); return b; }
    private static byte[] WithImports(params (string Dll, string Name)[] imports)
    {
        var b = new PeFixtureBuilder();
        foreach (var (dll, name) in imports) b.Import(dll, name);
        return b.Build();
    }
    private static byte[] Plain()
    {
        var b = new PeFixtureBuilder().WithCode(Enumerable.Range(0, 0x600).Select(i => (byte)(i % 23 == 0 ? 0xC3 : 0x90 + i % 5)).ToArray()).Export("f", 0);
        b.Import("kernel32.dll", "GetTickCount"); b.Import("kernel32.dll", "Sleep"); b.Import("user32.dll", "MessageBeep");
        return b.Build();
    }

    private static PackedImageReport Run(byte[] pe) => PackedImageAnalyzer.Analyze(pe);
    private static bool Has(PackedImageReport r, string code) => r.Diagnostics.Any(d => d.Code == code);

    [Fact]
    public void A_normal_controlled_dll_is_not_flagged()
    {
        var r = Run(Plain());
        Assert.Equal(PackedVerdict.None, r.Verdict);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code.StartsWith("PACK_") && d.Severity != DiagnosticSeverity.Info);
        Assert.Equal("", r.Summary);
    }

    [Fact]
    public void An_executable_section_without_file_data_is_reported_and_exports_into_it_make_it_likely()
    {
        var r = Run(Mutate(Plain(), 0, rawSize: 0));
        Assert.True(Has(r, "PACK_UNBACKED_EXEC_SECTION"));
        Assert.True(Has(r, "PACK_EXPORT_UNBACKED"));
        Assert.Equal(PackedVerdict.Likely, r.Verdict);
        Assert.Contains("appears to be packed", r.Summary);
        Assert.Contains("created at run time", r.Summary + " " + string.Join(' ', r.Reasons));
    }

    [Fact]
    public void An_unbacked_exec_section_alone_without_exports_is_suspected()
    {
        var pe = new PeFixtureBuilder().Build();
        var r = Run(Mutate(pe, 0, rawSize: 0));
        Assert.True(Has(r, "PACK_UNBACKED_EXEC_SECTION"));
        Assert.Equal(PackedVerdict.Suspected, r.Verdict);
    }

    [Fact]
    public void A_writable_bss_like_section_without_file_data_is_normal()
    {
        var r = Run(Mutate(Plain(), 1, rawSize: 0));
        Assert.Equal(PackedVerdict.None, r.Verdict);
        Assert.False(Has(r, "PACK_UNBACKED_EXEC_SECTION"));
    }

    [Fact]
    public void High_entropy_code_is_suspected_and_reports_the_entropy()
    {
        var r = Run(new PeFixtureBuilder().WithCode(Noise(0x1000)).Build());
        Assert.True(Has(r, "PACK_HIGH_ENTROPY"));
        Assert.True(r.Entropies[0].Entropy > PackedImageAnalyzer.HighEntropy);
        Assert.Equal(PackedVerdict.Suspected, r.Verdict);
    }

    [Fact]
    public void An_entry_point_in_a_high_entropy_section_after_the_first_code_section_is_flagged()
    {
        var pe = new PeFixtureBuilder().WithData(Noise(0x1000)).Build();
        pe = SetEntry(Mutate(pe, 1, flags: Exec), PeFixtureBuilder.DataRva);
        var r = Run(pe);
        Assert.True(Has(r, "PACK_ENTRY_IN_HIGH_ENTROPY"));
        Assert.True(r.Verdict >= PackedVerdict.Suspected);
        // the same entry point into ordinary low-entropy data is not an anomaly
        Assert.False(Has(Run(SetEntry(Mutate(new PeFixtureBuilder().Build(), 1, flags: Exec), PeFixtureBuilder.DataRva)), "PACK_ENTRY_IN_HIGH_ENTROPY"));
    }

    [Fact]
    public void Only_loader_class_imports_are_a_weak_signal_that_needs_company()
    {
        var weak = WithImports(("kernel32.dll", "LoadLibraryA"), ("kernel32.dll", "GetProcAddress"));
        var r = Run(weak);
        Assert.True(Has(r, "PACK_MINIMAL_IMPORTS"));
        Assert.Equal(PackedVerdict.None, r.Verdict);
        var plus = Run(Mutate(weak, 1, flags: ExecWrite));
        Assert.True(Has(plus, "PACK_WX_SECTION"));
        Assert.Equal(PackedVerdict.Suspected, plus.Verdict);
        // a normal import set that merely includes LoadLibraryA is untouched
        Assert.False(Has(Run(WithImports(("kernel32.dll", "LoadLibraryA"), ("kernel32.dll", "GetProcAddress"), ("kernel32.dll", "Sleep"))), "PACK_MINIMAL_IMPORTS"));
    }

    // PeFixtureBuilder does not terminate per-DLL thunk lists, so multi-DLL import tables are modelled on the parsed image instead.
    private static PeImage WithParsedImports(params (string Dll, string Name)[] imports)
    {
        var b = PeReader.Read(Plain());
        return new PeImage
        {
            Bytes = b.Bytes, Sha256 = b.Sha256, Machine = b.Machine, IsDll = b.IsDll, ImageBase = b.ImageBase, SizeOfImage = b.SizeOfImage, EntryPointRva = b.EntryPointRva,
            Sections = b.Sections, Exports = b.Exports, Relocations = b.Relocations, Diagnostics = b.Diagnostics,
            Imports = imports.Select((x, i) => new PeImport(x.Dll, x.Name, null, 0x3400 + (uint)i * 4)).ToList(),
        };
    }

    [Fact]
    public void A_stub_that_keeps_one_function_per_dll_beside_the_loader_pair_is_a_weak_signal()
    {
        var stub = WithParsedImports(("kernel32.dll", "LoadLibraryA"), ("kernel32.dll", "GetProcAddress"), ("advapi32.dll", "RegCloseKey"), ("ole32.dll", "CoInitialize"), ("user32.dll", "ShowWindow"));
        Assert.True(Has(PackedImageAnalyzer.Analyze(stub), "PACK_MINIMAL_IMPORTS"));
        var normal = WithParsedImports(("kernel32.dll", "LoadLibraryA"), ("kernel32.dll", "GetProcAddress"), ("kernel32.dll", "Sleep"), ("user32.dll", "MessageBeep"), ("user32.dll", "ShowWindow"), ("gdi32.dll", "Rectangle"));
        Assert.False(Has(PackedImageAnalyzer.Analyze(normal), "PACK_MINIMAL_IMPORTS"));
    }

    [Fact]
    public void Packer_section_names_are_only_an_info_hint_never_proof()
    {
        var named = Run(Mutate(Plain(), 0, name: "UPX0"));
        Assert.Equal(PackedVerdict.None, named.Verdict);
        Assert.Equal("UPX0", named.NameHint);
        Assert.Contains(named.Diagnostics, d => d.Code == "PACK_NAME_HINT" && d.Severity == DiagnosticSeverity.Info);
        // structurally packed without any telltale name is still detected
        Assert.Equal(PackedVerdict.Likely, Run(Mutate(Plain(), 0, name: ".text", rawSize: 0)).Verdict);
    }

    [RealLibraryFact("ogg.dll", "vorbis.dll", "vorbisFile.dll", "eax.dll")]
    public void Real_unpacked_libraries_are_not_flagged()
    {
        foreach (var name in new[] { "ogg.dll", "vorbis.dll", "vorbisFile.dll", "eax.dll" })
        {
            var r = Run(File.ReadAllBytes(RealLibraries.PathOf(name)));
            Assert.True(r.Verdict == PackedVerdict.None, $"{name} flagged: {string.Join("; ", r.Reasons)}");
        }
    }

    [PackedSampleFact]
    public void The_local_fmod_stub_is_recognised_as_packed()
    {
        var r = Run(File.ReadAllBytes(RealLibraries.PackedSample!));
        Assert.Equal(PackedVerdict.Likely, r.Verdict);
        Assert.True(Has(r, "PACK_UNBACKED_EXEC_SECTION"));
        Assert.True(Has(r, "PACK_EXPORT_UNBACKED"));
        Assert.True(Has(r, "PACK_MINIMAL_IMPORTS"));
        Assert.Equal("UPX0", r.NameHint);
    }
}
