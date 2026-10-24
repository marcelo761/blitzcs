using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class RebaseTests
{
    /// <summary>One function-pointer table (relocated) and one function that dispatches through it, a jump table, an absolute data load.</summary>
    private static byte[] Fixture(bool relocs)
    {
        var a = new Asm32();
        // f0: return table[1](arg) via an absolute function-pointer table at data+0x10 (entries are absolute VAs -> relocated)
        a.Raw(0x8B, 0x44, 0x24, 0x04);                       // mov eax,[esp+4]
        a.Raw(0x83, 0xE0, 0x01);                             // and eax,1
        a.Raw(0xFF, 0x14, 0x85).Abs(PeFixtureBuilder.DataRva + 0x10);   // call [table + eax*4]   (abs disp32)
        a.Ret();
        var f1 = a.Func(); a.MovEaxImm(111).Ret();
        var f2 = a.Func(); a.MovEaxImm(222).Ret();
        // jump table: switch (arg & 1) via absolute table of code addresses at data+0x20
        var f3 = a.Func();
        a.Raw(0x8B, 0x44, 0x24, 0x04, 0x83, 0xE0, 0x01);
        a.Raw(0xFF, 0x24, 0x85).Abs(PeFixtureBuilder.DataRva + 0x20);   // jmp [table + eax*4]
        var j0 = a.Func(); a.MovEaxImm(1000).Ret();
        var j1 = a.Func(); a.MovEaxImm(2000).Ret();
        var f4 = a.Func(); a.LoadEaxAbs(PeFixtureBuilder.DataRva + 0x40).Ret();   // returns data dword (absolute load)
        var data = new byte[0x100];
        void Put(int o, uint v) => BitConverter.GetBytes(v).CopyTo(data, o);
        Put(0x10, PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + f1); Put(0x14, PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + f2);
        Put(0x20, PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + j0); Put(0x24, PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + j1);
        Put(0x40, 0xCAFE0001);
        var fx = new PeFixtureBuilder().WithCode(a.Bytes).WithData(data).Export("viatable", 0).Export("viajump", f3).Export("readdata", f4);
        if (relocs)
        {
            foreach (var r in a.Relocs) fx.Relocate(r);
            foreach (var o in new[] { 0x10u, 0x14u, 0x20u, 0x24u }) fx.Relocate(PeFixtureBuilder.DataRva + o);
        }
        return fx.Build();
    }

    [Fact]
    public void Rebase_PatchesBytesButNeverTheOriginal_AndKeepsLineage()
    {
        var image = PeReader.Read(Fixture(true));
        var before = (byte[])image.Bytes.Clone();
        var moved = image.Rebase(0x30000000);
        Assert.Equal(before, image.Bytes);                       // original untouched
        Assert.Equal(0x30000000u, moved.ImageBase);
        Assert.Equal(image.Sha256, moved.Sha256);                // still the ORIGINAL hash
        Assert.Equal(0x10000000u, moved.RebasedFrom!.OriginalImageBase);
        Assert.Null(image.RebasedFrom);
        Assert.Equal(image.Relocations.Count, moved.Relocations.Count);
        Assert.Equal(image.Exports.Select(e => (e.Name, e.Rva)), moved.Exports.Select(e => (e.Name, e.Rva)));
        // every relocation site moved by exactly the delta in the mapped image, nothing else changed
        var a = image.MapImage(); var b = moved.MapImage();
        var sites = image.Relocations.Select(r => (int)r.Rva).ToHashSet();
        for (var i = 0x1000; i < a.Length; i++)   // below the first section RVA the mapper copies raw file bytes (headers plus file-aligned section data); that area is compared via the sites below
        {
            if (sites.Any(s => i >= s && i < s + 4)) continue;
            Assert.True(a[i] == b[i], $"byte 0x{i:X} changed outside relocation sites");
        }
        foreach (var r in image.Relocations)
            Assert.Equal(BitConverter.ToUInt32(a, (int)r.Rva) + 0x20000000u, BitConverter.ToUInt32(b, (int)r.Rva));
        Assert.Same(moved, moved.Rebase(0x30000000));            // already there
        Assert.Equal(0x10000000u, moved.Rebase(0x50000000).RebasedFrom!.OriginalImageBase); // lineage keeps the earliest original
    }

    [Fact]
    public void Rebase_RejectsImagesWithoutRelocationsAndBadBases()
    {
        var stripped = PeReader.Read(Fixture(false));
        Assert.Equal("PE_REBASE_NO_RELOCS", Assert.Throws<PeRebaseException>(() => stripped.Rebase(0x30000000)).Code);
        Assert.Same(stripped, stripped.Rebase(stripped.ImageBase));
        var image = PeReader.Read(Fixture(true));
        Assert.Equal("PE_REBASE_BAD_BASE", Assert.Throws<PeRebaseException>(() => image.Rebase(0x30001000)).Code);
        Assert.Equal("PE_REBASE_BAD_BASE", Assert.Throws<PeRebaseException>(() => image.Rebase(0)).Code);
        // translator-level request fails the same explicit way
        Assert.Throws<PeRebaseException>(() => X64Translator.Translate(stripped, new BinaryCompilerOptions(LoadBase: 0x30000000), "k"));
    }

    [Fact]
    public void CacheKeyAndArtifactRecordTheRebase_AndPreferredBaseKeysAreUnchanged()
    {
        var image = PeReader.Read(Fixture(true));
        var plain = new BinaryCompilerOptions();
        var kPlain = CacheKey.Compute(image, plain);
        Assert.Equal(kPlain, CacheKey.Compute(image, plain with { LoadBase = image.ImageBase }));   // explicit preferred base == default
        var k30 = CacheKey.Compute(image, plain with { LoadBase = 0x30000000 });
        var k50 = CacheKey.Compute(image, plain with { LoadBase = 0x50000000 });
        Assert.NotEqual(kPlain, k30); Assert.NotEqual(k30, k50);
        Assert.Equal(k30, CacheKey.Compute(image.Rebase(0x30000000), plain));                         // original+LoadBase == already-rebased image

        var art = X64Translator.Translate(image, plain with { LoadBase = 0x30000000 }, k30);
        Assert.Equal(0x30000000u, art.GuestImageBase);
        Assert.Equal(0x10000000u, art.RebasedFromImageBase);
        Assert.Equal(image.Sha256, art.SourceSha256);
        Assert.Contains(art.Diagnostics, d => d.Code == "BC_FIXED_BASE" && d.Message.Contains("rebased from 0x10000000"));
        Assert.Null(X64Translator.Translate(image, plain, kPlain).RebasedFromImageBase);
        var s = new ArtifactSerializer();
        Assert.Equal(0x10000000u, s.Deserialize(s.Serialize(art)).RebasedFromImageBase);
    }

    [Fact]
    public void BuildCacheKeepsOneEntryPerBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bcs-rebase-" + Guid.NewGuid().ToString("N"));
        var dll = Path.Combine(dir, "lib.dll");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(dll, Fixture(true));
            var a = BinaryBuild.Translate(dll, new BinaryCompilerOptions(), dir);
            var b = BinaryBuild.Translate(dll, new BinaryCompilerOptions(LoadBase: 0x30000000), dir);
            Assert.False(a.CacheHit); Assert.False(b.CacheHit);
            Assert.NotEqual(a.Artifact.CacheKey, b.Artifact.CacheKey);
            Assert.True(BinaryBuild.Translate(dll, new BinaryCompilerOptions(LoadBase: 0x30000000), dir).CacheHit);
            Assert.True(BinaryBuild.Translate(dll, new BinaryCompilerOptions(), dir).CacheHit);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RebasedCodeHasTheSameShape_AndRunsIdenticallyAtBothBases()
    {
        var image = PeReader.Read(Fixture(true));
        TranslationArtifact At(uint? b) => X64Translator.Translate(image, new BinaryCompilerOptions(LoadBase: b), "k");
        var a = At(null); var c = At(0x30000000);
        Assert.Equal(a.CodeSize, c.CodeSize);
        Assert.Equal(a.AddressMap.Select(m => m.HostOffset), c.AddressMap.Select(m => m.HostOffset));   // identical code layout
        Assert.Equal(a.HostImage.AsSpan(0, a.CodeSize).Length, c.HostImage.AsSpan(0, c.CodeSize).Length);

        foreach (var (art, img) in new[] { (a, image), (c, image.Rebase(0x30000000)) })
        {
            using var m = GuestModule.Load(art, img, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            Assert.Equal(img.ImageBase, m.ImageBase);
            Assert.Equal(111u, m.Bind("viatable", GuestCallingConvention.Cdecl, 1).Call(0));
            Assert.Equal(222u, m.Bind("viatable", GuestCallingConvention.Cdecl, 1).Call(1));
            Assert.Equal(1000u, m.Bind("viajump", GuestCallingConvention.Cdecl, 1).Call(0));
            Assert.Equal(2000u, m.Bind("viajump", GuestCallingConvention.Cdecl, 1).Call(1));
            Assert.Equal(0xCAFE0001u, m.Bind("readdata", GuestCallingConvention.Cdecl, 0).Call());
            var entry = m.Memory.ReadUInt32(img.ImageBase + PeFixtureBuilder.DataRva + 0x10);   // the stored pointer itself moved with the image
            Assert.InRange(entry, img.ImageBase + PeFixtureBuilder.TextRva, img.ImageBase + PeFixtureBuilder.TextRva + 0x100);
        }
    }

    [Fact]
    public void LoaderRejectsMismatchedLineage_AndNamesTheOccupant()
    {
        var image = PeReader.Read(Fixture(true));
        var moved = X64Translator.Translate(image, new BinaryCompilerOptions(LoadBase: 0x30000000), "k");
        var ex = Assert.Throws<GuestLoadException>(() => GuestModule.Load(moved, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains("PeImage.Rebase", ex.Message);

        var art = X64Translator.Translate(image, new BinaryCompilerOptions(), "k");
        using var first = GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        var clash = Assert.Throws<GuestAddressSpaceException>(() => GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains("occupied by 0x10000000-", clash.Message);   // names the existing mapping from /proc/self/maps
        Assert.Equal(1u * 111, first.Bind("viatable", GuestCallingConvention.Cdecl, 1).Call(0)); // the first module is intact
    }

    [Fact]
    public void BaseAllocatorIsDeterministicAlignedAndNonOverlapping()
    {
        PeImage Make(bool relocs) => PeReader.Read(Fixture(relocs));
        var mods = new List<(string, PeImage)> { ("ogg", Make(true)), ("vorbis", Make(true)), ("vorbisFile", Make(true)), ("eax", Make(true)) };
        var plan = ModuleBaseAllocator.Allocate(mods);
        Assert.Equal(plan.Select(p => p.Base), ModuleBaseAllocator.Allocate(mods).Select(p => p.Base));
        Assert.Equal(0x10000000u, plan[0].Base); Assert.False(plan[0].Moved);
        Assert.All(plan.Skip(1), p => Assert.True(p.Moved));
        Assert.All(plan, p => Assert.Equal(0u, p.Base % 0x10000));
        foreach (var x in plan) foreach (var y in plan.Where(y => y != x))
            Assert.True((ulong)x.Base + x.Footprint <= y.Base || (ulong)y.Base + y.Footprint <= x.Base, $"{x.Name} overlaps {y.Name}");
        Assert.All(plan, p => Assert.False(p.Base < 0x80000000 && p.Base + p.Footprint > 0x40000000));   // never in the MAP_32BIT zone (stacks/TEB)

        // images that cannot move keep their base; two of them at one base is an explicit error
        var fixedA = Make(false);
        var mixed = ModuleBaseAllocator.Allocate([("movable", Make(true)), ("fixed", fixedA)]);
        Assert.Equal(0x10000000u, mixed[1].Base); Assert.NotEqual(0x10000000u, mixed[0].Base);
        Assert.Equal("PE_REBASE_NO_RELOCS", Assert.Throws<PeRebaseException>(() => ModuleBaseAllocator.Allocate([("a", fixedA), ("b", Make(false))])).Code);
    }
}
