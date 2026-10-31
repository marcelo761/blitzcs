using System.Security.Cryptography;
using BlitzCS.BinaryCompiler.CodeGen;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>X64Translator.TranslateDynamic: the translator entry for code that exists only at run time (docs/binary-dynamic-code.md).</summary>
public sealed class DynamicTranslationTests
{
    private const uint Base = 0x781A0000;

    private static byte[] Page(params byte[] code) { var p = new byte[4096]; code.CopyTo(p, 0); return p; }

    [Theory]
    [InlineData("linux-sysv"), InlineData("windows-x64")]
    public void The_snapshot_is_the_identity_and_targets_outside_it_are_dispatch_misses(string target)
    {
        // +0: push 1; call rel32 0x10001000 (outside); add esp,4; ret
        var code = Page(0x6A, 0x01, 0xE8, 0, 0, 0, 0, 0x83, 0xC4, 0x04, 0xC3);
        BitConverter.GetBytes(unchecked(0x10001000u - (Base + 7))).CopyTo(code, 3);
        var t = X64Translator.TranslateDynamic(code, Base, [Base], new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: target));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(code)).ToLowerInvariant(), t.SnapshotSha256);
        Assert.Equal(t.SnapshotSha256, t.Artifact.SourceSha256);
        Assert.DoesNotContain(t.Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(t.Artifact.Diagnostics, d => d.Code is "BC_FIXED_BASE" or "BC_BASE_UNUSABLE_ON_TARGET");
        Assert.Contains(t.Artifact.Diagnostics, d => d.Code == "BC_UNTRANSLATED_TARGET");   // the call leaves the snapshot: reached through the module registry
        Assert.Equal(4, t.InstructionCount);
        Assert.Equal(11, t.InstructionBytes.Count(b => b));
        Assert.True(t.InstructionBytes[..11].All(b => b));
        Assert.Equal(Base, t.Artifact.GuestImageBase);
        Assert.Contains(t.Artifact.AddressMap, e => e.GuestRva == 7);                     // the return site after the external call is a dispatch target
    }

    [Fact]
    public void Inputs_outside_the_contract_are_rejected()
    {
        var o = new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: "linux-sysv");
        Assert.Throws<ArgumentException>(() => X64Translator.TranslateDynamic(new byte[100], Base, [Base], o));
        Assert.Throws<ArgumentOutOfRangeException>(() => X64Translator.TranslateDynamic(Page(0xC3), Base + 1, [Base + 1], o));
        Assert.Throws<ArgumentOutOfRangeException>(() => X64Translator.TranslateDynamic(Page(0xC3), Base, [Base + 4096], o));
        Assert.Throws<ArgumentException>(() => X64Translator.TranslateDynamic(Page(0xC3), Base, [], o));
    }

    [Fact]
    public void Each_entry_roots_the_graph()
    {
        var code = Page(0xC3);
        code[0x80] = 0x40; code[0x81] = 0xC3;   // inc eax; ret (only reachable as a second entry)
        var o = new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: "linux-sysv");
        Assert.DoesNotContain(X64Translator.TranslateDynamic(code, Base, [Base], o).Artifact.AddressMap, e => e.GuestRva == 0x80);
        Assert.Contains(X64Translator.TranslateDynamic(code, Base, [Base, Base + 0x80], o).Artifact.AddressMap, e => e.GuestRva == 0x80);
    }
}
