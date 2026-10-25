using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class TrapReportingTests
{
    [Fact]
    public void UntranslatedIndirectTargetRaisesInsteadOfReturningZero()
    {
        // jmp through a register to an address no root, branch or table reaches.
        var code = Asm(a => { a.mov(eax, __dword_ptr[esp + 4]); a.jmp(eax); });
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile);
            using var m = GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            var fn = m.Bind("_f", GuestCallingConvention.Cdecl, 1);
            var error = Assert.Throws<GuestTrapException>(() => fn.Call(CodeVa + 0x80));
            Assert.Contains("untranslated", error.Message);
            Assert.Contains((CodeVa + 0x80).ToString("X8"), error.Message);
            // the module stays usable: the trap record was cleared
            Assert.Throws<GuestTrapException>(() => fn.Call(CodeVa + 0x90));
        }
    }

    [Fact]
    public void ExportIntoSectionWithoutFileDataIsAnErrorNotTranslatedZeros()
    {
        // A packed image: the export lives in an executable section that has VirtualSize but no raw data.
        var bytes = new PeFixtureBuilder().WithCode([0xC3]).Export("_f", 0).Build();
        var image = PeReader.Read(bytes);
        var unbacked = image.Sections.Select(x => x.Name == ".text" ? x with { RawSize = 0 } : x).ToArray();
        var packed = new PeImage
        {
            Bytes = image.Bytes, Sha256 = image.Sha256, Machine = image.Machine, IsDll = image.IsDll, ImageBase = image.ImageBase,
            SizeOfImage = image.SizeOfImage, EntryPointRva = 0, Sections = unbacked, Imports = image.Imports, Exports = image.Exports,
            Relocations = image.Relocations, Tls = image.Tls, Diagnostics = image.Diagnostics,
        };
        var artifact = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(packed, new BinaryCompilerOptions(), "k");
        var error = Assert.Single(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("BC_ROOT_NOT_FILE_BACKED", error.Code);
        Assert.Contains("packed or self-modifying", error.Message);
        Assert.Throws<GuestLoadException>(() => GuestModule.Load(artifact, packed, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
    }

    [Fact]
    public void ExportOfDataInANonExecutableSectionIsNotAnError()
    {
        // vorbis.dll-style data exports: a table exported from .data must not be treated as a code root.
        var bytes = new PeFixtureBuilder().WithCode([0xC3]).Export("_f", 0).Build();
        var image = PeReader.Read(bytes);
        var data = image.Sections.Single(x => x.Name == ".data");
        var withDataExport = new PeImage
        {
            Bytes = image.Bytes, Sha256 = image.Sha256, Machine = image.Machine, IsDll = image.IsDll, ImageBase = image.ImageBase,
            SizeOfImage = image.SizeOfImage, EntryPointRva = 0, Sections = image.Sections, Imports = image.Imports,
            Exports = [.. image.Exports, new PeExport("_table", 2, data.VirtualAddress + 8, null)],
            Relocations = image.Relocations, Tls = image.Tls, Diagnostics = image.Diagnostics,
        };
        var artifact = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(withDataExport, new BinaryCompilerOptions(), "k");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
