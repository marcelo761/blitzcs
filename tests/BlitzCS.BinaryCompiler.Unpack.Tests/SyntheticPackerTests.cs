using System.Security.Cryptography;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Unpack.Tests;

[Collection("GuestRuntime")]
public sealed class SyntheticPackerTests
{
    private static PeImage Read(byte[] b) => PeReader.Read(b);

    [Fact]
    public void The_packed_fixture_really_is_packed_and_cannot_be_translated_directly()
    {
        var packed = Read(SyntheticPacker.Build());
        Assert.Equal(PackedVerdict.Likely, PackedImageAnalyzer.Analyze(packed).Verdict);
        // the file contains no plain copy of the real code
        Assert.False(packed.Bytes.AsSpan().IndexOf(new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08 }) >= 0);
        var art = X64Translator.Translate(packed, new BinaryCompilerOptions(), "k");
        Assert.Contains(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "BC_ROOT_NOT_FILE_BACKED");
    }

    [Fact]
    public void Unpacks_to_a_derived_image_that_translates_loads_and_runs()
    {
        var original = SyntheticPacker.Build();
        var before = SHA256.HashData(original);
        var packed = Read(original);
        var result = ControlledUnpacker.TryUnpack(packed);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(before, SHA256.HashData(original)); // the input is never touched
        Assert.Equal(SyntheticPacker.OepRva, result.Report.OepRva);
        Assert.Equal(OepConfidence.High, result.Report.Confidence);
        Assert.True(result.Report.Instructions > SyntheticPacker.PayloadLength * 6, "the decode loop must have been executed, not skipped");
        Assert.Equal(packed.Sha256, result.Lineage!.DerivedFromSha256);
        Assert.Equal(ControlledUnpacker.Version, result.Lineage.UnpackerVersion);
        Assert.NotEqual(packed.Sha256, result.Lineage.DerivedSha256);

        var derived = Read(result.DerivedPe!);
        Assert.DoesNotContain(derived.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(SyntheticPacker.OepRva, derived.EntryPointRva);
        Assert.Equal(2, derived.Exports.Count);
        var import = Assert.Single(derived.Imports); // only the real program's import: the stub's kernel32 table is gone
        Assert.Equal(("msvcrt.dll", "abs", SyntheticPacker.AbsSlotRva), (import.Dll, import.Name, import.IatRva));
        Assert.DoesNotContain(derived.Sections, s => s.IsExecutable && s.IsWritable); // W^X for the loader
        Assert.Empty(derived.Relocations);
        Assert.Contains(result.Report.ResolvedImports, i => i.Dll == "msvcrt.dll" && i.Name == "abs" && i.ViaGetProcAddress && !i.Called);

        var artifact = X64Translator.Translate(derived, new BinaryCompilerOptions(), CacheKey.Compute(derived, new BinaryCompilerOptions()));
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var resolver = new DictionaryGuestImportResolver().Add("msvcrt.dll", "abs", GuestImportBuilder.Cdecl(x => (uint)Math.Abs((int)x)));
        using var module = GuestModule.Load(artifact, derived, resolver, new GuestModuleOptions { RunEntryPoint = true });
        Assert.Equal(5u, module.Bind("_add2@8", GuestCallingConvention.Stdcall, 2).Call(2, 3));
        Assert.Equal(3u, module.Bind("_callabs@4", GuestCallingConvention.Stdcall, 1).Call(unchecked((uint)-3)));

        // runtime hook: the stub address of the bound import is the value in its IAT slot
        var stub = Assert.NotNull(module.GetImportStubAddress("MSVCRT.DLL", "abs"));
        Assert.Equal(stub, module.Memory.ReadUInt32(derived.ImageBase + SyntheticPacker.AbsSlotRva));
        Assert.Contains(module.BoundImports, b => b.Dll == "msvcrt.dll" && b.Name == "abs" && b.StubAddress == stub);
    }

    [Fact]
    public void The_run_is_deterministic()
    {
        var a = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build()));
        var b = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build()));
        Assert.Equal(a.DerivedPe, b.DerivedPe);
        Assert.Equal(a.Report.Instructions, b.Report.Instructions);
    }

    [Theory]
    [InlineData(StubVariant.AntiDebugPeb, "UNPACK_ANTI_DEBUG")]
    [InlineData(StubVariant.Rdtsc, "UNPACK_ANTI_DEBUG")]
    [InlineData(StubVariant.CallsUnimplementedImport, "UNPACK_UNIMPLEMENTED_IMPORT_CALLED")]
    [InlineData(StubVariant.UsesFpu, "UNPACK_UNSUPPORTED_INSTRUCTION")]
    [InlineData(StubVariant.ReturnsWithoutOep, "UNPACK_NO_OEP")]
    public void Refuses_with_an_explicit_diagnostic(StubVariant variant, string code)
    {
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build(variant)));
        Assert.False(result.Success);
        Assert.Null(result.DerivedPe);
        Assert.Equal(code, result.Report.RefusalCode);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == code);
    }

    [Fact]
    public void An_unimplemented_import_that_is_called_names_dll_and_function()
    {
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build(StubVariant.CallsUnimplementedImport)));
        Assert.Contains("user32.dll!MessageBoxA", result.Diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error).Message);
    }

    [Fact]
    public void The_instruction_budget_bounds_the_run()
    {
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build(StubVariant.SpinsForever)), new UnpackOptions { MaxInstructions = 10_000 });
        Assert.Equal("UNPACK_BUDGET_EXCEEDED", result.Report.RefusalCode);
        Assert.Equal(10_000, result.Report.Instructions);
    }

    [Fact]
    public void Too_little_written_evidence_is_a_low_confidence_refusal()
    {
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build()), new UnpackOptions { MinWrittenBytes = 100_000 });
        Assert.Equal("UNPACK_LOW_CONFIDENCE_OEP", result.Report.RefusalCode);
        Assert.Equal(OepConfidence.Low, result.Report.Confidence);
        Assert.True(ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build()), new UnpackOptions { MinWrittenBytes = 100_000, AcceptLowConfidence = true }).Success);
    }

    [Fact]
    public void A_non_packed_image_is_not_applicable_to_the_stub_policy_but_still_refused_without_an_oep()
    {
        // a stub that never generates code is not an unpacker target: running a normal DLL's entry returns to the sentinel
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.Build(StubVariant.ReturnsWithoutOep)));
        Assert.Equal("UNPACK_NO_OEP", result.Report.RefusalCode);
    }

    [Fact]
    public void Build_pipeline_records_lineage_reuses_the_derived_image_and_keys_the_cache_by_lineage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blitzcs-unpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, "packed.dll");
            var original = SyntheticPacker.Build();
            File.WriteAllBytes(input, original);
            var cache = Path.Combine(dir, "cache");
            var first = UnpackBuild.TranslateWithUnpack(input, new BinaryCompilerOptions(), cache);
            Assert.False(first.UnpackCacheHit);
            Assert.False(first.Build.CacheHit);
            Assert.Equal(BinaryBuild.Sha256(original), first.Lineage.DerivedFromSha256);
            Assert.Equal(first.DerivedImage.Sha256, first.Build.Artifact.SourceSha256);
            Assert.Equal(original, File.ReadAllBytes(input));
            Assert.True(File.Exists(Path.ChangeExtension(first.DerivedPath, ".lineage.json")));

            var second = UnpackBuild.TranslateWithUnpack(input, new BinaryCompilerOptions(), cache);
            Assert.True(second.UnpackCacheHit);
            Assert.True(second.Build.CacheHit);
            Assert.Equal(first.Build.Artifact.CacheKey, second.Build.Artifact.CacheKey);

            // different unpack options -> different lineage -> different derived file and cache key
            var third = UnpackBuild.TranslateWithUnpack(input, new BinaryCompilerOptions(), cache, new UnpackOptions { MinWrittenBytes = 16 });
            Assert.NotEqual(first.DerivedPath, third.DerivedPath);
            Assert.NotEqual(first.Build.Artifact.CacheKey, third.Build.Artifact.CacheKey);

            // the opt-in hook behind IBinaryPreprocessor
            IBinaryPreprocessor hook = new UnpackPreprocessor();
            var pre = hook.Preprocess(input, cache);
            Assert.Equal(first.DerivedPath, pre.DerivedPath);
            Assert.StartsWith("unpack:" + first.Lineage.DerivedFromSha256, pre.LineageToken);

            // a failing stub never yields a derived file
            File.WriteAllBytes(input, SyntheticPacker.Build(StubVariant.Rdtsc));
            var ex = Assert.Throws<UnpackFailedException>(() => UnpackBuild.TranslateWithUnpack(input, new BinaryCompilerOptions(), cache));
            Assert.Equal("UNPACK_ANTI_DEBUG", ex.Result.Report.RefusalCode);
        }
        finally { Directory.Delete(dir, true); }
    }
}
