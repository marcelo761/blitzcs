using System.Security.Cryptography;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Target selection (linux-sysv | windows-x64): options, cross-translation, artifact serialization and the loader's refusal of a foreign artifact.</summary>
[Collection("GuestRuntime")]
public sealed class TargetOsTests
{
    private static byte[] Body() => Asm(a => { a.mov(eax, __dword_ptr[esp + 4]); a.add(eax, eax); a.ret(); });
    // These tests translate for either target on any host: the fixture sits at the low base (0x10000000), the one the pinned hashes and the windows-x64 base diagnostic are about.

    private static Built For(string targetOs, OptimizationProfile profile = OptimizationProfile.Release, Action<PeFixtureBuilder>? configure = null)
    {
        var fx = new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode(Body()).Export("_f", 0);
        configure?.Invoke(fx);
        var image = PeReader.Read(fx.Build());
        return new Built(image, X64Translator.Translate(image, new BinaryCompilerOptions(profile, TargetOs: targetOs), "k"));
    }

    private static string Other => OperatingSystem.IsWindows() ? BinaryTargets.LinuxSysV : BinaryTargets.WindowsX64;

    [Fact]
    public void Default_target_is_the_os_of_the_running_process()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "windows-x64" : "linux-sysv", new BinaryCompilerOptions().TargetOs);
        Assert.Equal("windows-x64", (new BinaryCompilerOptions() with { TargetOs = "windows-x64" }).TargetOs);
    }

    [Theory]
    [InlineData("linux-x64", "linux-sysv"), InlineData("linux-sysv", "linux-sysv"), InlineData("windows-x64", "windows-x64"), InlineData("win-x64", "windows-x64"), InlineData("WINDOWS-X64", "windows-x64")]
    public void Aliases_normalize(string given, string canonical) => Assert.Equal(canonical, BinaryTargets.Normalize(given));

    [Fact]
    public void Unknown_target_is_an_explicit_error()
    {
        var error = Assert.Throws<ArgumentException>(() => For("macos-arm64"));
        Assert.Contains("macos-arm64", error.Message);
        Assert.Contains("windows-x64", error.Message);
        Assert.Null(BinaryTargets.TryNormalize("plan9"));
    }

    [Fact]
    public void Artifacts_record_their_target_and_the_cache_key_depends_on_it()
    {
        Assert.Equal("linux-sysv", For("linux-sysv").Artifact.TargetOs);
        Assert.Equal("windows-x64", For("windows-x64").Artifact.TargetOs);
        var image = PeReader.Read(new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode(Body()).Export("_f", 0).Build());
        Assert.NotEqual(CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "linux-sysv")), CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "windows-x64")));
    }

    [Fact]
    public void Serializer_round_trips_the_target_and_has_a_new_version()
    {
        Assert.True(ArtifactSerializer.CurrentVersion >= 3);
        var s = new ArtifactSerializer();
        foreach (var target in new[] { "linux-sysv", "windows-x64" })
            Assert.Equal(target, s.Deserialize(s.Serialize(For(target).Artifact)).TargetOs);
    }

    [Theory]
    [InlineData(OptimizationProfile.Debug), InlineData(OptimizationProfile.Release)]
    public void Linux_sysv_output_is_pinned_byte_for_byte(OptimizationProfile profile)
    {
        // Pinned from the translator before the Win64 ABI existed: the sysv runtime routines and guest code must never change by accident.
        // Re-pinned once, deliberately, for cross-module dispatch (format 5): the only difference to the previous pin is the miss routine at host offset 0 (it now tries the module registry before the kind 1 trap) and the artifact has no other change.
        // Re-pinned again, deliberately, for guest SEH (format 6): every import stub records EBX/EBP/ESI/EDI in the call context, the extended-return tail gained the register-image
        // redirect, and two runtime stubs follow the imports (ImportStubLayout); the guest code is unchanged.
        var art = For("linux-sysv", profile).Artifact;
        var hash = Convert.ToHexString(SHA256.HashData(art.HostImage));
        Assert.Equal(profile == OptimizationProfile.Debug ? PinnedDebug : PinnedRelease, hash);
    }

    private const string PinnedDebug = "28E754D2CA2DA9B2216988A13F62B7BBDD6DC1CF69D35B3F64DDAE53581F703B", PinnedRelease = "092AE70CE0389C9E00A0AE01BFB203BBEE8B74F326282D1D32B5D890D030D227";


    private const string PinnedWinDebug = "EE0C51CECB5C1416AA83C4D4B2599F7F757AAC0B8DDC60D687E3F7A1FB7CDE34", PinnedWinRelease = "4617AF058F70192459037F75AB6DFBD7879936E56FEBED3A2AB387BF1279B54D";

    [Theory]
    [InlineData(OptimizationProfile.Debug), InlineData(OptimizationProfile.Release)]
    public void Windows_x64_output_without_a_stub_pool_is_pinned_byte_for_byte(OptimizationProfile profile)
    {
        // Pinned from the translator at commit 49eaa1b, before the dynamic stub pool existed (DynamicStubs = 0 must change nothing: same code, same layout); the linux-sysv pins above are the same check.
        // Re-pinned deliberately for guest SEH (format 6), the same change as the linux-sysv pins.
        var hash = Convert.ToHexString(SHA256.HashData(For("windows-x64", profile).Artifact.HostImage));
        Assert.Equal(profile == OptimizationProfile.Debug ? PinnedWinDebug : PinnedWinRelease, hash);
    }

    [Theory]
    [InlineData("linux-sysv"), InlineData("windows-x64")]
    public void A_stub_pool_adds_import_style_stubs_after_the_real_imports_and_nothing_else_moves(string target)
    {
        void Configure(PeFixtureBuilder fx) => fx.Import("host.dll", "hook");
        var fx = new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode(Body()).Export("_f", 0);
        Configure(fx);
        var image = PeReader.Read(fx.Build());
        var plain = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: target), "k");
        var pooled = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: target) { DynamicStubs = 5 }, "k");
        Assert.Equal(0, plain.DynamicStubs);
        Assert.Equal(5, pooled.DynamicStubs);
        Assert.Equal(plain.ImportStubBaseRva, pooled.ImportStubBaseRva);
        Assert.Equal(plain.ExitGuestRva, pooled.ExitGuestRva);
        uint Entry(TranslationArtifact a, uint rva) => BitConverter.ToUInt32(a.HostImage, (int)(a.DispatchTableOffset + 4 * rva));
        var first = pooled.ImportStubBaseRva;
        const uint R = ImportStubLayout.RuntimeStubCount;                                         // runtime stubs (guest SEH) always follow the pool
        for (var k = 0u; k < 1 + 5 + R; k++) Assert.NotEqual(0u, Entry(pooled, first + 16 * k));  // 1 real import + 5 pool stubs + the runtime stubs
        Assert.Equal(0u, Entry(pooled, first + 16 * (6 + R)));                                    // beyond them: still the trap
        Assert.NotEqual(0u, Entry(plain, first + 16 * 1));
        Assert.Equal(0u, Entry(plain, first + 16 * (1 + R)));
        var distinct = Enumerable.Range(0, 6 + (int)R).Select(k => Entry(pooled, first + 16u * (uint)k)).Distinct().Count();
        Assert.Equal(6 + (int)R, distinct);                                                       // each stub has its own body (own thunk slot)
        Assert.True(pooled.DispatchTableOffset >= pooled.ImportThunkTableOffset + 8u * (6 + R));  // the thunk table has a slot per stub
        // the guest code itself is unchanged: same translated bytes for the export, relative to the (moved) layout
        var f = pooled.Exports.Single(e => e.Name == "_f").HostOffset - plain.Exports.Single(e => e.Name == "_f").HostOffset;
        Assert.True(f > 0);                                                                       // the pool bodies precede the guest code
        Assert.Equal(plain.HostImage.AsSpan((int)plain.Exports.Single(e => e.Name == "_f").HostOffset, 8).ToArray(),
            pooled.HostImage.AsSpan((int)pooled.Exports.Single(e => e.Name == "_f").HostOffset, 8).ToArray());
    }

    [Fact]
    public void The_stub_pool_is_part_of_the_cache_key_and_the_serialized_artifact()
    {
        var image = PeReader.Read(new PeFixtureBuilder().WithImageBase(PeFixtureBuilder.LowBase).WithCode(Body()).Export("_f", 0).Build());
        var none = CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "linux-sysv"));
        Assert.Equal(none, CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = 0 }));
        Assert.NotEqual(none, CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = 512 }));
        Assert.NotEqual(CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = 8 }), CacheKey.Compute(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = 512 }));
        var s = new ArtifactSerializer();
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = 7 }, "k");
        Assert.Equal(7, s.Deserialize(s.Serialize(art)).DynamicStubs);
        Assert.Equal(0, s.Deserialize(s.Serialize(For("linux-sysv").Artifact)).DynamicStubs);
        Assert.True(ArtifactSerializer.CurrentVersion >= 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = BinaryCompilerOptions.MaxDynamicStubs + 1 }, "k"));
        Assert.Throws<ArgumentOutOfRangeException>(() => X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: "linux-sysv") { DynamicStubs = -1 }, "k"));
    }

    [Fact]
    public void Win64_differs_from_sysv_only_in_the_runtime_routines()
    {
        var sysv = For("linux-sysv", OptimizationProfile.Debug).Artifact;
        var win = For("windows-x64", OptimizationProfile.Debug).Artifact;
        var fSysv = sysv.Exports.Single(e => e.Name == "_f").HostOffset;
        var fWin = win.Exports.Single(e => e.Name == "_f").HostOffset;
        Assert.NotEqual(fSysv, fWin);                                      // the Win64 prologue/stubs have another size
        // mov eax,[esp+4]; add eax,eax lowers to identical host code
        Assert.Equal(sysv.HostImage.AsSpan((int)fSysv, 8).ToArray(), win.HostImage.AsSpan((int)fWin, 8).ToArray());
        Assert.Equal(sysv.GuestImageBase, win.GuestImageBase);
        Assert.Equal(sysv.ExitGuestRva, win.ExitGuestRva);
    }

    private static List<Instruction> Decode(TranslationArtifact a, uint from, int count)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(a.HostImage, (int)from, a.CodeSize - (int)from));
        decoder.IP = from;
        var list = new List<Instruction>();
        while (list.Count < count) list.Add(decoder.Decode());
        return list;
    }

    [Fact]
    public void Win64_enter_guest_follows_the_Win64_convention()
    {
        var enter = Decode(For("windows-x64").Artifact, For("windows-x64").Artifact.EnterGuestOffset, 24);
        var asm = string.Join("\n", enter.Select(i => i.ToString()));
        // rcx = hostTarget, edx = guestEsp, r8 = call context
        Assert.Contains("mov r14,r8", asm);
        Assert.DoesNotContain("mov r14,rdx", asm);
        // callee-saved GPRs that sysv does not save (guest ESI/EDI live in RSI/RDI) and the callee-saved XMM set translated code can write
        Assert.Contains("push rdi", asm); Assert.Contains("push rsi", asm);
        Assert.Contains("movups [rsp],xmm6", asm); Assert.Contains("movups [rsp+10h],xmm7", asm); Assert.Contains("movups [rsp+20h],xmm15", asm);
        var sysvAsm = string.Join("\n", Decode(For("linux-sysv").Artifact, For("linux-sysv").Artifact.EnterGuestOffset, 12).Select(i => i.ToString()));
        Assert.Contains("mov r14,rdx", sysvAsm);
        Assert.DoesNotContain("xmm6", sysvAsm);
    }

    [Fact]
    public void Win64_import_stub_has_shadow_space_and_does_not_spill_callee_saved_registers()
    {
        void Configure(PeFixtureBuilder fx) => fx.Import("host.dll", "hook");
        foreach (var (target, shadow) in new[] { ("windows-x64", true), ("linux-sysv", false) })
        {
            var b = For(target, OptimizationProfile.Debug, Configure);
            var art = b.Artifact;
            var stubRva = art.ImportStubBaseRva;
            var host = BitConverter.ToUInt32(art.HostImage, (int)(art.DispatchTableOffset + 4 * stubRva));
            var body = string.Join("\n", Decode(art, host, 14).Select(i => i.ToString()));
            if (shadow)
            {
                Assert.Contains("sub rsp,20h", body);
                Assert.Contains("mov ecx,r8d", body);
                Assert.DoesNotContain("push rdi", body);
                Assert.DoesNotContain("push rsi", body);
            }
            else
            {
                Assert.Contains("push rsi", body); Assert.Contains("mov edi,r8d", body);
                Assert.DoesNotContain("sub rsp,20h", body);
            }
            Assert.Contains("call qword ptr", body);
        }
    }

    [Fact]
    public void Loader_refuses_an_artifact_for_another_host_naming_both_targets()
    {
        var foreign = For(Other);
        var error = Assert.Throws<GuestLoadException>(() =>
            GuestModule.Load(foreign.Artifact, foreign.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains(Other, error.Message);
        Assert.Contains(BinaryTargets.Host, error.Message);
        Assert.Contains("re-translate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cross_translation_is_pure_computation_on_any_host()
    {
        var art = For(Other).Artifact;
        Assert.Equal(Other, art.TargetOs);
        Assert.NotEmpty(art.HostImage);
    }

    [Fact]
    public void Windows_target_flags_a_base_below_0x20000000()
    {
        var art = For("windows-x64").Artifact;   // the fixture prefers 0x10000000
        Assert.Contains(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "BC_BASE_UNUSABLE_ON_TARGET");
        Assert.DoesNotContain(For("linux-sysv").Artifact.Diagnostics, d => d.Code == "BC_BASE_UNUSABLE_ON_TARGET");
    }

    [Fact]
    public void Cli_rejects_an_unknown_target_with_a_usage_error()
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(2, CLI.CliApplication.Run(["build", "x.bb", "-o", "y.dll", "--target", "plan9"], output, error));
        Assert.Contains("linux-x64", error.ToString());
        Assert.Contains("windows-x64", error.ToString());
    }
}
