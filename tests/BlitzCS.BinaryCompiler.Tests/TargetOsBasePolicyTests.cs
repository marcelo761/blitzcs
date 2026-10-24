using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>windows-x64 base policy: images live in [0x20000000, 0x70000000), 64 KiB aligned, deterministic; low preferred bases are rebased automatically.</summary>
public sealed class TargetOsBasePolicyTests
{
    private const string Win = BinaryTargets.WindowsX64;

    private static PeImage Relocatable() =>
        PeReader.Read(new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode([0xC3]).WithData(new byte[0x100]).Export("f", 0).Relocate(PeFixtureBuilder.DataRva).Build());

    private static PeImage Stripped() => PeReader.Read(new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode([0xC3]).Export("f", 0).Build());

    [Fact]
    public void Low_preferred_bases_are_moved_into_the_windows_image_window()
    {
        var modules = Enumerable.Range(0, 3).Select(i => ($"m{i}.dll", Relocatable())).ToArray();   // all prefer 0x10000000
        var plan = ModuleBaseAllocator.Allocate(modules, null, Win);
        Assert.All(plan, m =>
        {
            Assert.True(m.Base >= 0x20000000 && (ulong)m.Base + m.Footprint <= 0x70000000, $"0x{m.Base:X8}");
            Assert.Equal(0u, m.Base % 0x10000);
            Assert.True(m.Moved);
        });
        Assert.Equal(3, plan.Select(m => m.Base).Distinct().Count());
        Assert.Equal(ModuleBaseAllocator.WindowsPreferredStart, plan[0].Base);   // the quiet upper half of the window first
        var ordered = plan.OrderBy(m => m.Base).ToArray();
        for (var i = 1; i < ordered.Length; i++) Assert.True(ordered[i].Base >= ordered[i - 1].Base + ordered[i - 1].Footprint);
    }

    [Fact]
    public void The_lower_half_of_the_window_is_the_fallback_when_the_upper_half_is_reserved()
    {
        var plan = ModuleBaseAllocator.Allocate([("a.dll", Relocatable())], [(ModuleBaseAllocator.WindowsPreferredStart, ModuleBaseAllocator.WindowsImageWindowEnd)], Win);
        Assert.InRange(plan[0].Base, ModuleBaseAllocator.WindowsMinimumBase, ModuleBaseAllocator.WindowsPreferredStart - 1);
    }

    [Fact]
    public void Allocation_is_deterministic()
    {
        var modules = new[] { ("a.dll", Relocatable()), ("b.dll", Relocatable()) };
        Assert.Equal(ModuleBaseAllocator.Allocate(modules, null, Win), ModuleBaseAllocator.Allocate(modules, null, Win));
    }

    [Fact]
    public void A_usable_preferred_base_is_kept()
    {
        var high = Relocatable().Rebase(0x30000000);
        Assert.Null(ModuleBaseAllocator.RequiredBase("h.dll", high, Win));
        var plan = ModuleBaseAllocator.Allocate([("h.dll", high)], null, Win);
        Assert.Equal(0x30000000u, plan[0].Base);
        Assert.False(plan[0].Moved);
    }

    [Fact]
    public void RequiredBase_gives_the_single_library_base()
    {
        Assert.Equal(ModuleBaseAllocator.WindowsPreferredStart, ModuleBaseAllocator.RequiredBase("one.dll", Relocatable(), Win));
        Assert.Null(ModuleBaseAllocator.RequiredBase("one.dll", Relocatable(), BinaryTargets.LinuxSysV));
    }

    [Fact]
    public void Stripped_low_image_fails_with_a_windows_specific_hint()
    {
        var error = Assert.Throws<PeRebaseException>(() => ModuleBaseAllocator.Allocate([("old.dll", Stripped())], null, Win));
        Assert.Equal("PE_REBASE_NO_RELOCS", error.Code);
        Assert.Contains("old.dll", error.Message);
        Assert.Contains("windows-x64", error.Message);
        Assert.Contains("0x20000000", error.Message);
        // the same image is fine on Linux
        Assert.Equal(PeFixtureBuilder.LowBase, ModuleBaseAllocator.Allocate([("old.dll", Stripped())], null, BinaryTargets.LinuxSysV)[0].Base);
    }

    [Fact]
    public void Linux_policy_is_unchanged()
    {
        var plan = ModuleBaseAllocator.Allocate([("a.dll", Relocatable()), ("b.dll", Relocatable())], null, BinaryTargets.LinuxSysV);
        Assert.Equal(PeFixtureBuilder.LowBase, plan[0].Base);
        Assert.NotEqual(plan[0].Base, plan[1].Base);
        Assert.All(plan, m => Assert.True(m.Base < 0x40000000 || m.Base >= 0x80000000));
    }

    [Fact]
    public void Rebased_image_translates_for_windows_without_the_base_diagnostic()
    {
        var image = Relocatable().Rebase(0x20000000);
        var art = CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: Win), "k");
        Assert.DoesNotContain(art.Diagnostics, d => d.Code == "BC_BASE_UNUSABLE_ON_TARGET");
        Assert.Equal(0x20000000u, art.GuestImageBase);
    }
}
