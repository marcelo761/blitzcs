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

    private static Built For(string targetOs, OptimizationProfile profile = OptimizationProfile.Release, Action<PeFixtureBuilder>? configure = null)
    {
        var fx = new PeFixtureBuilder().WithCode(Body()).Export("_f", 0);
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
        var image = PeReader.Read(new PeFixtureBuilder().WithCode(Body()).Export("_f", 0).Build());
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
        var art = For("linux-sysv", profile).Artifact;
        var hash = Convert.ToHexString(SHA256.HashData(art.HostImage));
        Assert.Equal(profile == OptimizationProfile.Debug ? PinnedDebug : PinnedRelease, hash);
    }

    private const string PinnedDebug = "8590EF11744340294A0AA3C2EA2623C5EACE0DEDCC48EE108A5D02CCD7468BD1", PinnedRelease = "10FC2ADBF32D2B8C1714E2379E5A580A96E3EFDE2230F24F757BA8970475E432";

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
