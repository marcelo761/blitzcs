using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// Delay-load imports. Each delay IAT slot is modelled as an ordinary import (PeImport.IsDelay): the translator allocates a stub for it and the loader
/// writes the stub address into the slot, so the per-function load stub that the linker left in the slot (which would run __delayLoadHelper2) is never
/// reached. The fixture is a hand-built PE with a real ImgDelayDescr directory and hand-assembled MSVC-style thunks (jmp [iat]); the load stubs are marked
/// (they return 0xDEAD0000), so a call that still reached the helper path would be visible. NOT covered: a real __delayLoadHelper2 / MSVC-generated thunks.
/// </summary>
[Collection("GuestRuntime")]
public sealed class DelayImportTests
{
    private const uint Marker = 0xDEAD0000;

    private static byte[] Code(uint iat0Va, uint iat1Va)
    {
        var c = new byte[0x40];
        Array.Fill(c, (byte)0xCC);
        void Put(int at, params byte[] bytes) => bytes.CopyTo(c, at);
        Put(0x00, 0xFF, 0x74, 0x24, 0x04);                 // push dword [esp+4]
        Put(0x04, 0xE8, 0x07, 0x00, 0x00, 0x00);           // call thunk0 (0x10)
        Put(0x09, 0x83, 0xC4, 0x04, 0xC3);                 // add esp,4 ; ret
        Put(0x10, 0xFF, 0x25); BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(0x12), iat0Va);   // thunk0: jmp [iat0]
        Put(0x18, 0xB8, 0x00, 0x00, 0xAD, 0xDE, 0xC3);     // load stub 0: mov eax, Marker ; ret
        Put(0x20, 0xFF, 0x25); BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(0x22), iat1Va);   // thunk1: jmp [iat1]
        Put(0x28, 0xB8, 0x00, 0x00, 0xAD, 0xDE, 0xC3);     // load stub 1
        Put(0x30, 0xE8, 0xEB, 0xFF, 0xFF, 0xFF, 0xC3);     // call thunk1 (0x20) ; ret
        return c;
    }

    private static PeFixtureBuilder Fixture(out uint iat0, out uint iat1)
    {
        var f = new PeFixtureBuilder().WithData(new byte[0x100]);
        iat0 = f.DelayImport("delaylib.dll", "Twice", 0x18);
        iat1 = f.DelayImport("delaylib.dll", "Seven", 0x28);
        f.WithCode(Code(PeFixtureBuilder.ImageBase + iat0, PeFixtureBuilder.ImageBase + iat1)).Export("_call_twice", 0).Export("_call_seven", 0x30)
            .Relocate(PeFixtureBuilder.TextRva + 0x12).Relocate(PeFixtureBuilder.TextRva + 0x22);
        return f;
    }

    private static DictionaryGuestImportResolver Resolver() => new DictionaryGuestImportResolver()
        .Add("delaylib.dll", "Twice", GuestImportBuilder.Cdecl(a => a * 2 + 1))
        .Add("delaylib.dll", "Seven", GuestImportBuilder.Cdecl(() => 7u));

    [Fact]
    public void ParsesDelayDescriptorsIntoFlaggedImports()
    {
        var f = Fixture(out var iat0, out var iat1);
        var image = PeReader.Read(f.Build());
        Assert.Empty(image.Diagnostics);
        Assert.Equal([new PeImport("delaylib.dll", "Twice", null, iat0, true), new PeImport("delaylib.dll", "Seven", null, iat1, true)], image.Imports);
        Assert.All(image.Imports, i => Assert.True(i.IsDelay));
        Assert.Equal(PeFixtureBuilder.DelayRva + 0x500, iat0);
    }

    [Fact]
    public void DelayAndEagerImportsCoexist()
    {
        var f = Fixture(out _, out _);
        f.Import("kernel32.dll", "Sleep");
        var image = PeReader.Read(f.Build());
        Assert.Empty(image.Diagnostics);
        Assert.Equal([false, true, true], image.Imports.OrderBy(i => i.IsDelay).Select(i => i.IsDelay).Order());
        Assert.Single(image.Imports, i => !i.IsDelay);
    }

    [Theory]
    [InlineData(OptimizationProfile.Debug)]
    [InlineData(OptimizationProfile.Release)]
    public void DelayImportCallsReachTheBoundHostFunctionNotTheLoadStub(OptimizationProfile profile)
    {
        var f = Fixture(out var iat0, out var iat1);
        var image = PeReader.Read(f.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "delay");
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        using var m = GuestModule.Load(art, image, Resolver(), new GuestModuleOptions { RunEntryPoint = false });
        // the loader replaced the linker's initial value (the load stub) with an import stub
        Assert.NotEqual(PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + 0x18, m.Memory.ReadUInt32(PeFixtureBuilder.ImageBase + iat0));
        Assert.NotEqual(PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + 0x28, m.Memory.ReadUInt32(PeFixtureBuilder.ImageBase + iat1));
        var twice = m.Bind("_call_twice", GuestCallingConvention.Cdecl, 1);
        var seven = m.Bind("_call_seven", GuestCallingConvention.Cdecl, 0);
        foreach (var x in new uint[] { 0, 1, 20, 0x7FFFFFFF }) Assert.Equal(unchecked(x * 2 + 1), twice.Call(x));
        Assert.Equal(7u, seven.Call());
        Assert.NotEqual(Marker, twice.Call(1));
    }

    [Fact]
    public void UnboundDelayImportsFollowAllowUnboundImportsSemantics()
    {
        var f = Fixture(out _, out _);
        var image = PeReader.Read(f.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "delay");
        var load = Assert.Throws<GuestImportException>(() => GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains("delaylib.dll!Twice", load.Message);
        Assert.Contains("delaylib.dll!Seven", load.Message);

        using var m = GuestModule.Load(art, image, new DictionaryGuestImportResolver().Add("delaylib.dll", "Seven", GuestImportBuilder.Cdecl(() => 7u)),
            new GuestModuleOptions { RunEntryPoint = false, AllowUnboundImports = true });
        Assert.Equal(7u, m.Bind("_call_seven", GuestCallingConvention.Cdecl, 0).Call());
        var ex = Assert.Throws<GuestImportException>(() => m.Bind("_call_twice", GuestCallingConvention.Cdecl, 1).Call(3));
        Assert.Contains("delaylib.dll!Twice", ex.Message + ex.InnerException?.Message);
    }

    [Fact]
    public void VirtualAddressFormDescriptorsAreAnExplicitError()
    {
        var f = Fixture(out _, out _).DelayAttributes(0);
        var image = PeReader.Read(f.Build());
        Assert.Contains(image.Diagnostics, d => d.Code == "PE052" && d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(image.Imports);
        Assert.DoesNotContain(image.Diagnostics, d => d.Code == "PE050");
    }

    [Fact]
    public void MalformedDelayDirectoriesNeverCrashTheReader()
    {
        var good = Fixture(out _, out _).Build();
        var directory = 0x58 + 96 + 13 * 8;
        // directory outside the image
        var b = (byte[])good.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(directory), 0x7FFF0000);
        Assert.Contains(PeReader.Read(b).Diagnostics, d => d.Code == "PE050");
        // every truncation either parses or reports an error; never an exception other than the documented format one
        for (var cut = 0; cut < good.Length; cut += 61)
        {
            try { _ = PeReader.Read(good.AsMemory(0, cut)); } catch (PeFormatException) { }
        }
    }
}
