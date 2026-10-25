using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// End-to-end scenarios that run unchanged on every supported host (Linux SysV natively, windows-x64 natively or under Wine): load, call, import, nested call,
/// guest memory, TEB, threads, reload. The fixture carries relocations and is moved into the host's image window first (ModuleBaseAllocator.RequiredBase),
/// which is a no-op on Linux and the 0x10000000 -> 0x20000000 rebase on Windows.
/// </summary>
[Collection("GuestRuntime")]
public sealed class HostScenarioTests
{
    private sealed record Fixture(PeImage Image, TranslationArtifact Artifact, uint FAdd, uint FImport, uint FNested, uint FRead, uint FTeb, uint FXmm);

    private static Fixture Build(OptimizationProfile profile)
    {
        var fx = new PeFixtureBuilder();
        var hook = fx.Import("host.dll", "hook");
        var nested = fx.Import("host.dll", "nested");
        var a = new Asm32();
        var add = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08).Ret();                          // mov eax,[esp+4]; add eax,[esp+8]; ret
        var import = a.Func(); a.PushArgSlot(4).CallIat(hook).AddEsp(0).Raw(0x40).Ret();                          // push [esp+4]; call [hook] (stdcall pops); inc eax; ret
        var nest = a.Func(); a.PushArgSlot(4).CallIat(nested).Ret();                                               // push [esp+4]; call [nested]; ret
        var read = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x8B, 0x00).Ret();                                      // mov eax,[esp+4]; mov eax,[eax]; ret
        var teb = a.Func(); a.Raw(0x64, 0xA1, 0x18, 0, 0, 0).Ret();                                                // mov eax, fs:[18h]; ret
        var xmm = a.Func(); a.Raw(0x0F, 0x57, 0xF6, 0x0F, 0x57, 0xFF, 0x0F, 0x28, 0xF7, 0xB8, 0x2A, 0, 0, 0).Ret(); // xorps xmm6,xmm6; xorps xmm7,xmm7; movaps xmm6,xmm7; mov eax,42; ret
        fx.WithCode(a.Bytes).WithData(new byte[0x100]).Export("add", add).Export("callhook", import).Export("callnested", nest).Export("readptr", read).Export("teb", teb).Export("xmm", xmm);
        foreach (var r in a.Relocs) fx.Relocate(r);
        var image = PeReader.Read(fx.Build());
        if (ModuleBaseAllocator.RequiredBase("scenario.dll", image, null) is { } moved) image = image.Rebase(moved);
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "scenario");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var text = image.ImageBase + PeFixtureBuilder.TextRva;
        return new Fixture(image, artifact, text + add, text + import, text + nest, text + read, text + teb, text + xmm);
    }

    private static DictionaryGuestImportResolver Imports() => new DictionaryGuestImportResolver()
        .Add("HOST.DLL", "hook", GuestImportBuilder.Stdcall(x => x * 2))
        .Add("HOST.DLL", "nested", GuestImportBuilder.Stdcall(x => GuestModule.Current!.CallAddress(CurrentFixture!.FAdd, x, 1000)));

    [ThreadStatic] private static Fixture? CurrentFixture;

    private static GuestModule Load(Fixture f)
    {
        CurrentFixture = f;
        return GuestModule.Load(f.Artifact, f.Image, Imports(), new GuestModuleOptions { RunEntryPoint = false });
    }

    public static TheoryData<OptimizationProfile> Profiles => new() { OptimizationProfile.Debug, OptimizationProfile.Release };

    [Theory, MemberData(nameof(Profiles))]
    public void Load_and_call_an_export(OptimizationProfile profile)
    {
        var f = Build(profile);
        Assert.Equal(BinaryTargets.Host, f.Artifact.TargetOs);
        Assert.True(f.Image.ImageBase >= PlatformMemory.Current.MinimumImageBase);
        using var m = Load(f);
        Assert.Equal(f.Image.ImageBase, m.ImageBase);
        Assert.Equal(7u, m.Bind("add", GuestCallingConvention.Cdecl, 2).Call(3, 4));
        Assert.Equal(0u, m.Bind("add", GuestCallingConvention.Cdecl, 2).Call(0xFFFFFFFF, 1));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Import_stub_reaches_managed_code_and_returns(OptimizationProfile profile)
    {
        using var m = Load(Build(profile));
        var call = m.Bind("callhook", GuestCallingConvention.Cdecl, 1);
        for (uint i = 0; i < 50; i++) Assert.Equal(i * 2 + 1, call.Call(i));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Import_can_call_back_into_the_guest_nested(OptimizationProfile profile)
    {
        using var m = Load(Build(profile));
        Assert.Equal(1005u, m.Bind("callnested", GuestCallingConvention.Cdecl, 1).Call(5));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Guest_reads_scratch_memory_allocated_in_the_low_4GiB(OptimizationProfile profile)
    {
        using var m = Load(Build(profile));
        using var buffer = m.Memory.Allocate(64);
        Assert.True((ulong)buffer.Address + 64 <= uint.MaxValue);
        m.Memory.WriteUInt32(buffer.Address, 0xCAFEF00D);
        Assert.Equal(0xCAFEF00Du, m.Bind("readptr", GuestCallingConvention.Cdecl, 1).Call(buffer.Address));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Teb_is_per_thread_and_reachable_through_fs(OptimizationProfile profile)
    {
        using var m = Load(Build(profile));
        var teb = m.Bind("teb", GuestCallingConvention.Cdecl, 0);
        var mine = teb.Call();
        Assert.Equal(m.CurrentThreadTeb, mine);
        uint other = 0;
        var t = new Thread(() => other = teb.Call());
        t.Start(); t.Join();
        Assert.NotEqual(0u, other);
        Assert.NotEqual(mine, other);
        Assert.True(m.Memory.IsMapped(mine, 0x1000));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Several_threads_run_the_guest_concurrently(OptimizationProfile profile)
    {
        var f = Build(profile);
        using var m = Load(f);
        var failures = new List<string>();
        var threads = Enumerable.Range(0, 6).Select(t => new Thread(() =>
        {
            CurrentFixture = f;
            try
            {
                var call = m.Bind("callhook", GuestCallingConvention.Cdecl, 1);
                var nest = m.Bind("callnested", GuestCallingConvention.Cdecl, 1);
                for (uint i = 0; i < 300; i++)
                {
                    if (call.Call(i + (uint)t) != (i + (uint)t) * 2 + 1) throw new InvalidOperationException("import result");
                    if (nest.Call(i) != i + 1000) throw new InvalidOperationException("nested result");
                }
            }
            catch (Exception e) { lock (failures) failures.Add(e.ToString()); }
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        Assert.Empty(failures);
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Guest_xmm6_xmm7_writes_do_not_corrupt_the_hosts_live_floating_point_values(OptimizationProfile profile)
    {
        using var m = Load(Build(profile));
        var call = m.Bind("xmm", GuestCallingConvention.Cdecl, 0);
        // The JIT keeps these in callee-saved XMM registers on Win64 across the guest call; the Win64 EnterGuest must restore XMM6/XMM7/XMM15.
        double a = 1.25, b = 2.5, c = 3.75, d = 5.0, e = 6.25, g = 7.5, h = 8.75, i = 10.0, j = 11.25;
        var sum = 0.0;
        for (var n = 0; n < 200; n++)
        {
            Assert.Equal(42u, call.Call());
            sum += a * b + c * d + e * g + h * i + j;
            a += 0.5; b -= 0.25; c += 1; d -= 0.5; e += 0.125; g -= 0.125; h += 2; i -= 1; j += 0.25;
        }
        var ea = 1.25; var eb = 2.5; var ec = 3.75; var ed = 5.0; var ee = 6.25; var eg = 7.5; var eh = 8.75; var ei = 10.0; var ej = 11.25; var expected = 0.0;
        for (var n = 0; n < 200; n++)
        {
            expected += ea * eb + ec * ed + ee * eg + eh * ei + ej;
            ea += 0.5; eb -= 0.25; ec += 1; ed -= 0.5; ee += 0.125; eg -= 0.125; eh += 2; ei -= 1; ej += 0.25;
        }
        Assert.Equal(expected, sum);
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Dispose_frees_the_range_so_the_same_base_loads_again(OptimizationProfile profile)
    {
        var f = Build(profile);
        for (var round = 0; round < 3; round++)
        {
            using var m = Load(f);
            Assert.Equal(3u, m.Bind("add", GuestCallingConvention.Cdecl, 2).Call(1, 2));
        }
    }

    [Theory, MemberData(nameof(Profiles))]
    public void A_second_load_at_the_same_base_is_refused_and_names_the_occupant(OptimizationProfile profile)
    {
        var f = Build(profile);
        using var first = Load(f);
        var error = Assert.Throws<GuestAddressSpaceException>(() => Load(f));
        Assert.Contains($"0x{f.Image.ImageBase:X8}", error.Message);
        Assert.Contains("occupied by", error.Message);
        Assert.Contains("0x", error.Message[(error.Message.IndexOf("occupied by", StringComparison.Ordinal))..]);
        Assert.Equal(3u, first.Bind("add", GuestCallingConvention.Cdecl, 2).Call(1, 2)); // the first module is untouched
    }

    [WindowsOnlyFact("a base below the host minimum is refused only on Windows; Linux accepts any page-aligned base")]
    public void A_base_below_the_hosts_minimum_is_refused_before_any_mapping()
    {
        var image = PeReader.Read(new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode([0xC3]).WithData(new byte[16]).Export("f", 0).Relocate(PeFixtureBuilder.DataRva).Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: BinaryTargets.WindowsX64), "low") with { Diagnostics = [] };
        var error = Assert.Throws<GuestAddressSpaceException>(() => GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains("0x20000000", error.Message);
    }
}
