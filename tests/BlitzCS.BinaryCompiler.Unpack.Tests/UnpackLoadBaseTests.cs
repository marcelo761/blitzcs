using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Unpack.Tests;

/// <summary>
/// UnpackOptions.LoadBase: the stub runs with the image mapped at a chosen base. The relocatable synthetic packer (SyntheticPacker.BuildRelocatable) behaves like a real packer:
/// its stub reads its own runtime base, adds (runtime - preferred) to every location of a relocation list it carries, and the packed file has a .reloc for the stub's own absolute operands.
/// </summary>
[Collection("GuestRuntime")]
public sealed class UnpackLoadBaseTests
{
    private const uint Preferred = 0x10000000;
    private static PeImage Read(byte[] b) => PeReader.Read(b);
    private static uint U32(byte[] image, uint rva) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)rva));

    private static UnpackResult Unpack(uint? loadBase)
    {
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.BuildRelocatable(Preferred)), new UnpackOptions { LoadBase = loadBase });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        return result;
    }

    private static uint[] PayloadLocations()
    {
        SyntheticPacker.RelocatableCode(Preferred, out var locations);
        return locations;
    }

    [Fact]
    public void The_relocatable_fixture_is_a_packed_image_with_its_own_relocations_and_absolute_addresses_in_the_unpacked_code()
    {
        var packed = Read(SyntheticPacker.BuildRelocatable(Preferred));
        Assert.Equal(PackedVerdict.Likely, PackedImageAnalyzer.Analyze(packed).Verdict);
        Assert.True(packed.Relocations.Count >= 5, "the stub's absolute operands are in the packed file's .reloc");
        Assert.All(packed.Relocations, r => Assert.InRange(r.Rva, SyntheticPacker.Pk1, SyntheticPacker.Pk1 + 0x1FF));
        Assert.Equal(4, PayloadLocations().Length);
        // at the preferred base the packer needs no fix-up: the unpacked pointer table holds the preferred-base addresses
        var mapped = Read(Unpack(null).DerivedPe!).MapImage();
        Assert.Equal(Preferred + SyntheticPacker.AddRva, U32(mapped, SyntheticPacker.TableRva));
        Assert.Equal(Preferred, U32(mapped, SyntheticPacker.HinstSlotRva));
    }

    [Fact]
    public void Unpacking_at_two_bases_gives_images_that_each_work_at_their_own_base_and_differ_exactly_by_the_base_delta()
    {
        var baseA = HostPlatform.FreeBase(0x30000000, 0x40000);
        var baseB = HostPlatform.FreeBase(0x50000000, 0x40000);
        Assert.NotEqual(baseA, baseB);
        var delta = unchecked(baseB - baseA);
        var packed = Read(SyntheticPacker.BuildRelocatable(Preferred));
        var images = new Dictionary<uint, byte[]>();

        foreach (var x in new[] { baseA, baseB })
        {
            var result = Unpack(x);
            var derived = Read(result.DerivedPe!);
            Assert.Equal(x, derived.ImageBase);                       // the derived image is valid for, and fixed at, the chosen base
            Assert.Empty(derived.Relocations);                       // no table: it cannot be moved again
            Assert.DoesNotContain(derived.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var mapped = derived.MapImage();
            images[x] = mapped;
            // the stub's relocation processing: absolute pointers in the unpacked data and code are those of THIS base
            Assert.Equal(x + SyntheticPacker.AddRva, U32(mapped, SyntheticPacker.TableRva));
            Assert.Equal(x + SyntheticPacker.CallAbsRva, U32(mapped, SyntheticPacker.TableRva + 4));
            Assert.Equal(x + SyntheticPacker.TableRva, U32(mapped, SyntheticPacker.GetPtrRva + 7));
            Assert.Equal(x + SyntheticPacker.AbsSlotRva, U32(mapped, SyntheticPacker.CallAbsRva + 6));
            // DllMain's hinstDLL is the chosen base
            Assert.Equal(x, U32(mapped, SyntheticPacker.HinstSlotRva));

            var artifact = X64Translator.Translate(derived, new BinaryCompilerOptions(), CacheKey.Compute(derived, new BinaryCompilerOptions()));
            Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var resolver = new DictionaryGuestImportResolver().Add("msvcrt.dll", "abs", GuestImportBuilder.Cdecl(v => (uint)Math.Abs((int)v)));
            using var module = GuestModule.Load(artifact, derived, resolver, new GuestModuleOptions { RunEntryPoint = true });
            Assert.Equal(x, module.ImageBase);
            Assert.Equal(5u, module.Bind("_add2@8", GuestCallingConvention.Stdcall, 2).Call(2, 3));
            Assert.Equal(3u, module.Bind("_callabs@4", GuestCallingConvention.Stdcall, 1).Call(unchecked((uint)-3)));   // the absolute [AbsSlot] operand is right
            var getPtr = module.Bind("_getptr@4", GuestCallingConvention.Stdcall, 1);                                      // absolute table operand, then a pointer table lookup
            Assert.Equal(x + SyntheticPacker.AddRva, getPtr.Call(0));
            Assert.Equal(x + SyntheticPacker.CallAbsRva, getPtr.Call(1));
        }

        // Image A and image B differ ONLY where a base-dependent address lives, and there by exactly B - A: the packer's relocation list, the stub's own operands
        // (the packed file's .reloc, applied by the loader), and the stored module handle. Everything else, including every opcode and string, is identical.
        var a = images[baseA]; var b = images[baseB];
        Assert.Equal(a.Length, b.Length);
        var relocated = PayloadLocations().Concat(packed.Relocations.Select(r => r.Rva)).Append(SyntheticPacker.HinstSlotRva).ToHashSet();
        foreach (var rva in relocated) Assert.Equal(U32(a, rva) + delta, U32(b, rva));
        var covered = new bool[a.Length];
        foreach (var rva in relocated) for (var k = 0u; k < 4; k++) covered[rva + k] = true;
        var firstSection = (int)Read(SyntheticPacker.BuildRelocatable(Preferred)).Sections.Min(s => s.VirtualAddress);
        for (var i = firstSection; i < a.Length; i++)
            if (!covered[i]) Assert.True(a[i] == b[i], $"byte at RVA 0x{i:X} differs between the two bases but is not a relocated address");
    }

    [Fact]
    public void An_explicit_base_equal_to_the_preferred_base_is_the_default()
    {
        Assert.Equal(Unpack(null).DerivedPe, Unpack(Preferred).DerivedPe);
    }

    [Fact]
    public void The_load_base_is_part_of_the_options_digest_and_the_lineage()
    {
        Assert.NotEqual(new UnpackOptions().Digest(), new UnpackOptions { LoadBase = 0x30000000 }.Digest());
        Assert.NotEqual(new UnpackOptions { LoadBase = 0x30000000 }.Digest(), new UnpackOptions { LoadBase = 0x50000000 }.Digest());
        Assert.Equal(new UnpackOptions { LoadBase = 0x30000000 }.Digest(), new UnpackOptions { LoadBase = 0x30000000 }.Digest());
        var a = Unpack(0x30000000); var b = Unpack(0x50000000);
        Assert.NotEqual(a.Lineage!.OptionsDigest, b.Lineage!.OptionsDigest);
        Assert.NotEqual(a.Lineage.DerivedSha256, b.Lineage.DerivedSha256);
        Assert.NotEqual(a.Lineage.Token, b.Lineage.Token);
    }

    [Fact]
    public void The_build_pipeline_keeps_a_derived_image_and_a_translation_per_base()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blitzcs-unpack-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, "packed.dll");
            File.WriteAllBytes(input, SyntheticPacker.BuildRelocatable(Preferred));
            var cache = Path.Combine(dir, "cache");
            UnpackBuildResult Build(uint? loadBase) => UnpackBuild.TranslateWithUnpack(input, new BinaryCompilerOptions(), cache, new UnpackOptions { LoadBase = loadBase });
            var baseA = HostPlatform.FreeBase(0x30000000, 0x40000); var baseB = HostPlatform.FreeBase(0x50000000, 0x40000);
            var a = Build(baseA); var b = Build(baseB);
            Assert.False(a.UnpackCacheHit); Assert.False(b.UnpackCacheHit);
            Assert.NotEqual(a.DerivedPath, b.DerivedPath);
            Assert.NotEqual(a.Build.Artifact.CacheKey, b.Build.Artifact.CacheKey);
            Assert.Equal(baseA, a.Build.Artifact.GuestImageBase); Assert.Equal(baseB, b.Build.Artifact.GuestImageBase);
            Assert.Equal(baseA, a.DerivedImage.ImageBase); Assert.Equal(baseB, b.DerivedImage.ImageBase);
            var again = Build(baseA);
            Assert.True(again.UnpackCacheHit); Assert.True(again.Build.CacheHit);
            Assert.Equal(a.Build.Artifact.CacheKey, again.Build.Artifact.CacheKey);
            // the opt-in hook behind IBinaryPreprocessor takes the base chosen by the build policy
            IBinaryPreprocessor hook = new UnpackPreprocessor();
            var pre = hook.Preprocess(input, cache, baseB);
            Assert.Equal(b.DerivedPath, pre.DerivedPath);
            Assert.Equal(b.Lineage.Token, pre.LineageToken);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0x30001000u, "UNPACK_BAD_LOAD_BASE")]      // not 64 KiB aligned
    [InlineData(0u, "UNPACK_BAD_LOAD_BASE")]
    [InlineData(0x40000000u, "UNPACK_ADDRESS_CONFLICT")]   // the sandbox heap starts here
    [InlineData(0x7C000000u, "UNPACK_ADDRESS_CONFLICT")]   // inside the sandbox's loader zone
    public void An_unusable_load_base_is_refused_with_its_own_code(uint loadBase, string code)
    {
        var result = ControlledUnpacker.TryUnpack(Read(SyntheticPacker.BuildRelocatable(Preferred)), new UnpackOptions { LoadBase = loadBase });
        Assert.False(result.Success);
        Assert.Equal(code, result.Report.RefusalCode);
    }
}

/// <summary>Why a fixed, low-base unpacked image cannot be used on windows-x64, independent of any local binary (the real sample runs through LoadBase instead).</summary>
public sealed class UnpackRefusalTests
{
    [Fact]
    public void A_packed_image_without_relocations_cannot_be_mapped_anywhere_else()
    {
        var result = ControlledUnpacker.TryUnpack(PeReader.Read(SyntheticPacker.Build(imageBase: 0x10000000)), new UnpackOptions { LoadBase = 0x30000000 });
        Assert.False(result.Success);
        Assert.Equal("UNPACK_LOAD_BASE_NO_RELOCS", result.Report.RefusalCode);
    }

    [Fact]
    public void A_fixed_low_base_derived_image_is_refused_explicitly_for_windows_x64_and_cannot_be_rebased()
    {
        var packed = PeReader.Read(SyntheticPacker.Build(imageBase: 0x10000000));
        var result = ControlledUnpacker.TryUnpack(packed);     // preferred base: derived image fixed at 0x10000000, no relocation table
        Assert.True(result.Success);
        var derived = PeReader.Read(result.DerivedPe!);
        Assert.Equal(0x10000000u, derived.ImageBase);
        Assert.Empty(derived.Relocations);
        // translation for the windows-x64 target works from any host and reports the base as unusable
        var options = new BinaryCompilerOptions(TargetOs: BinaryTargets.WindowsX64);
        var artifact = X64Translator.Translate(derived, options, CacheKey.Compute(derived, options));
        Assert.Contains(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "BC_BASE_UNUSABLE_ON_TARGET");
        Assert.Equal("PE_REBASE_NO_RELOCS", Assert.Throws<PeRebaseException>(() => derived.Rebase(0x30000000)).Code);
        Assert.Equal("PE_REBASE_NO_RELOCS", Assert.Throws<PeRebaseException>(() => ModuleBaseAllocator.RequiredBase("derived.dll", derived, BinaryTargets.WindowsX64)).Code);
    }
}
