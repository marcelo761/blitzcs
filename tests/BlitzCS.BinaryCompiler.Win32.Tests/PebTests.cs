using System.Text;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>The modelled PEB fields (docs/binary-teb-contract.md): ProcessParameters, Ldr, OS version, ProcessHeap.</summary>
[Collection("GuestRuntime")]
public sealed class PebTests
{
    private static GuestModule Load(Win32Options options, out Win32ImportResolver resolver)
    {
        var image = PeReader.Read(new PeFixtureBuilder().WithCode([0xB8, 1, 0, 0, 0, 0xC2, 0x0C, 0]).EntryPoint(0).Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "peb-test");
        resolver = new Win32ImportResolver(options);
        return GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { Process = Win32ProcessInfo.From(options) });
    }

    private static string Unicode(GuestModule m, uint at)
    {
        var length = m.Memory.ReadUInt16(at); var buffer = m.Memory.ReadUInt32(at + 4);
        var b = new byte[length]; m.Memory.Read(buffer, b);
        return Encoding.Unicode.GetString(b);
    }

    private static readonly Win32Options Opts = new()
    {
        CommandLine = "\"C:\\prog.exe\" -x", MainModulePath = "C:\\prog.exe", Version = Win32ProcessInfo.VersionForImage(6, 0),
        Environment = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" },
    };

    [Fact]
    public void ProcessParametersCarryImagePathCommandLineAndEnvironment()
    {
        using var m = Load(Opts, out var r); using var _ = r;
        var peb = m.PebAddress;
        var pp = m.Memory.ReadUInt32(peb + 0x10);
        Assert.NotEqual(0u, pp);
        Assert.Equal(1u, m.Memory.ReadUInt32(pp + 8));                   // RTL_USER_PROC_PARAMS_NORMALIZED
        Assert.Equal("C:\\prog.exe", Unicode(m, pp + 0x38));
        Assert.Equal("\"C:\\prog.exe\" -x", Unicode(m, pp + 0x40));
        Assert.Equal("C:\\", Unicode(m, pp + 0x24));
        var env = m.Memory.ReadUInt32(pp + 0x48);
        var bytes = new byte[2 * "A=1\0B=2\0\0".Length]; m.Memory.Read(env, bytes);
        Assert.Equal("A=1\0B=2\0\0", Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void LdrListsAreEmptyAndCircular()
    {
        using var m = Load(Opts, out var r); using var _ = r;
        var ldr = m.Memory.ReadUInt32(m.PebAddress + 0x0C);
        Assert.NotEqual(0u, ldr);
        foreach (var head in new uint[] { 0x0C, 0x14, 0x1C })
        {
            Assert.Equal(ldr + head, m.Memory.ReadUInt32(ldr + head));
            Assert.Equal(ldr + head, m.Memory.ReadUInt32(ldr + head + 4));
        }
    }

    [Fact]
    public void OsVersionNtGlobalFlagAndProcessHeapFollowThePolicy()
    {
        using var m = Load(Opts, out var r); using var _ = r;
        var peb = m.PebAddress;
        Assert.Equal(6u, m.Memory.ReadUInt32(peb + 0xA4));
        Assert.Equal(0u, m.Memory.ReadUInt32(peb + 0xA8));
        Assert.Equal(6002, m.Memory.ReadUInt16(peb + 0xAC));
        Assert.Equal(0u, m.Memory.ReadUInt32(peb + 0x68));
        Assert.Equal(0u, m.Memory.ReadUInt32(peb + 0x18));     // no heap yet
        m.SetProcessHeap(0x1234);
        Assert.Equal(0x1234u, m.Memory.ReadUInt32(peb + 0x18));
        Assert.Equal(m.ImageBase, m.Memory.ReadUInt32(peb + 0x08));
        Assert.Equal(0, m.Memory.ReadByte(peb + 0x02));        // BeingDebugged
    }

    [Fact]
    public void VersionPolicyNeverGoesBelowTheDefaultAndFollowsTheImage()
    {
        Assert.Equal(new Win32Version(), Win32ProcessInfo.VersionForImage(4, 0));
        Assert.Equal(6u, Win32ProcessInfo.VersionForImage(6, 0).Major);
        Assert.Equal(7601u, Win32ProcessInfo.VersionForImage(6, 1).Build);
        Assert.Equal(new Win32Version(), Win32ProcessInfo.VersionForImage(5, 1));
    }
}
