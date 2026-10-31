using System.Security.Cryptography;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// Code that the guest generates at run time (docs/binary-dynamic-code.md). A hand-assembled guest allocates executable memory with VirtualAlloc, copies code bytes
/// there with its own `rep movsb`, applies fix-ups with its own stores (before the first execution, as a loader or the Blitz runtime does), and calls the code.
/// The expected values follow from the IA-32 semantics of the few instructions in each blob (they are what a native run computes).
/// </summary>
[Collection("GuestRuntime")]
public sealed class DynamicCodeTests : IDisposable
{
    private const string WindowsOnly = "translating code generated at run time needs the windows-x64 write guard (vectored exception handler)";
    private const uint PageExecuteReadWrite = 0x40, PageReadWrite = 0x04;
    private static readonly uint Base = Win32PeBuilder.ImageBase;

    private readonly Win32ImportResolver resolver = new();
    private readonly GuestModule module;
    private readonly Dictionary<string, uint> labels = [];

    public DynamicCodeTests() : this(true) { }

    private DynamicCodeTests(bool dynamicCode)
    {
        var pe = new Win32PeBuilder();
        uint valloc = pe.Import("KERNEL32.dll", "VirtualAlloc"), vfree = pe.Import("KERNEL32.dll", "VirtualFree"), vprotect = pe.Import("KERNEL32.dll", "VirtualProtect");
        var code = new List<byte>();
        void Label(string n) => labels[n] = (uint)code.Count;
        void B(params byte[] b) => code.AddRange(b);
        void Call(uint slot) { B(0xFF, 0x15); code.AddRange(BitConverter.GetBytes(Base + slot)); }
        Label("alloc"); B(0xFF, 0x74, 0x24, 0x08, 0x68, 0x00, 0x30, 0x00, 0x00, 0xFF, 0x74, 0x24, 0x0C, 0x6A, 0x00); Call(valloc); B(0xC3);         // VirtualAlloc(0, size, COMMIT|RESERVE, protect)
        Label("free"); B(0x68, 0x00, 0x80, 0x00, 0x00, 0x6A, 0x00, 0xFF, 0x74, 0x24, 0x0C); Call(vfree); B(0xC3);                                  // VirtualFree(p, 0, MEM_RELEASE)
        Label("vp"); B(0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10); Call(vprotect); B(0xC3);
        Label("copy"); B(0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x8B, 0x74, 0x24, 0x10, 0x8B, 0x4C, 0x24, 0x14, 0xF3, 0xA4, 0x5F, 0x5E, 0xC3);           // rep movsb (dst, src, n)
        Label("poke"); B(0x8B, 0x44, 0x24, 0x04, 0x8B, 0x4C, 0x24, 0x08, 0x89, 0x08, 0xC3);                                                        // *(uint*)a = v
        Label("callp"); B(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x54, 0x24, 0x08, 0x83, 0xC4, 0x04, 0xC3);                                                // return p(arg)
        Label("plus100"); B(0x8B, 0x44, 0x24, 0x04, 0x05, 0x64, 0x00, 0x00, 0x00, 0xC3);                                                          // x + 100
        pe.WithCode([.. code]).WithData(new byte[0x100]);
        foreach (var (name, off) in labels) pe.Export(name, off);
        var image = PeReader.Read(pe.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "dyn-guest");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        module = GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { RunEntryPoint = false, DynamicCode = dynamicCode });
    }

    public void Dispose() { module.Dispose(); resolver.Dispose(); }

    private uint Fn(string name) => Base + Win32PeBuilder.TextRva + labels[name];
    private uint G(string name, params uint[] args) => module.Bind(name, GuestCallingConvention.Cdecl, args.Length).Call(args);

    /// <summary>Allocates guest memory with <paramref name="protect"/> and lets the guest copy <paramref name="blob"/> into it.</summary>
    private uint Place(byte[] blob, uint protect = PageExecuteReadWrite)
    {
        var p = G("alloc", 4096, protect);
        Assert.NotEqual(0u, p);
        using var src = module.Memory.Allocate(blob.Length);
        src.Write(0, blob);
        G("copy", p, src.Address, (uint)blob.Length);
        return p;
    }

    private static readonly byte[] SquarePlus7 = [0x8B, 0x44, 0x24, 0x04, 0x0F, 0xAF, 0xC0, 0x83, 0xC0, 0x07, 0xC3];   // mov eax,[esp+4]; imul eax,eax; add eax,7; ret

    [RequiresWindowsFact(WindowsOnly)]
    public void Position_independent_code_copied_at_run_time_is_translated_and_runs()
    {
        var p = Place(SquarePlus7);
        Assert.Equal(32u, G("callp", p, 5));
        Assert.Equal(0x20008u, G("callp", p, 0x10001));   // 0x10001 squared wraps to 0x00020001 in 32 bits
        var t = Assert.Single(module.DynamicTranslations);
        Assert.Equal((p, p, p + 4096), (t.EntryAddress, t.RegionStart, t.RegionEnd));
        var page = new byte[4096];
        module.Memory.Read(p, page);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(page)).ToLowerInvariant(), t.SnapshotSha256);   // the bytes at translation time are the identity
        Assert.True(module.Memory.TryQueryPage(p, out var info));
        Assert.Equal(GuestPageKind.DynamicCode, info.Kind);
        Assert.True(info.Writable);                                                                       // what the guest asked for; the host page is read-only
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void Code_relocated_by_the_guest_calls_into_the_image_and_returns()
    {
        // mov eax,[esp+4]; push eax; call rel32 plus100; add esp,4; mov ecx,imm32 plus100; push eax; call ecx; add esp,4; ret  =>  x + 200
        byte[] blob = [0x8B, 0x44, 0x24, 0x04, 0x50, 0xE8, 0, 0, 0, 0, 0x83, 0xC4, 0x04, 0xB9, 0, 0, 0, 0, 0x50, 0xFF, 0xD1, 0x83, 0xC4, 0x04, 0xC3];
        var p = Place(blob);
        G("poke", p + 6, Fn("plus100") - (p + 10));   // rel32 fix-up (relative to the copy's own address)
        G("poke", p + 14, Fn("plus100"));             // absolute fix-up
        Assert.Equal(1234u + 200u, G("callp", p, 1234));
        Assert.Single(module.DynamicTranslations);
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void Data_on_the_code_page_stays_writable_for_the_guest_and_the_host()
    {
        // inc dword [p+0x100]; mov eax,[p+0x100]; ret
        byte[] blob = [0xFF, 0x05, 0, 0, 0, 0, 0xA1, 0, 0, 0, 0, 0xC3];
        var p = Place(blob);
        G("poke", p + 2, p + 0x100);
        G("poke", p + 7, p + 0x100);
        var before = GuestModule.DynamicCodeGuardCounters.DataWrites;
        Assert.Equal(1u, G("callp", p, 0));
        Assert.Equal(2u, G("callp", p, 0));                    // the guest store went through the write guard (single-stepped)
        Assert.True(GuestModule.DynamicCodeGuardCounters.DataWrites >= before + 2);
        G("poke", p + 0x100, 41);                              // a guest store from image code into the data word
        Assert.Equal(42u, G("callp", p, 0));
        module.Memory.WriteUInt32(p + 0x100, 99);              // a managed write to data next to the code
        Assert.Equal(100u, G("callp", p, 0));
        Assert.Single(module.DynamicTranslations);
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void A_store_into_translated_instruction_bytes_is_refused_by_name()
    {
        // mov byte [p+7], 0x90; ret   (p+7 is the ret: an instruction byte of this very translation)
        byte[] blob = [0xC6, 0x05, 0, 0, 0, 0, 0x90, 0xC3];
        var p = Place(blob);
        G("poke", p + 2, p + 7);
        var e = Assert.Throws<GuestSelfModifyingCodeException>(() => G("callp", p, 0));
        Assert.Equal((p + 7, p, p + 4096), (e.Address, e.RegionStart, e.RegionEnd));
        Assert.Equal(0xC3, module.Memory.ReadByte(p + 7));     // nothing was written
        var m = Assert.Throws<GuestSelfModifyingCodeException>(() => module.Memory.WriteByte(p + 1, 0x90));
        Assert.Equal(p + 1, m.Address);
        Assert.Equal(32u, G("callp", Place(SquarePlus7), 5));    // the process goes on
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void Making_translated_dynamic_code_writable_is_refused_by_name()
    {
        var p = Place(SquarePlus7);
        Assert.Equal(32u, G("callp", p, 5));
        using var old = module.Memory.Allocate(4);
        var e = Assert.Throws<GuestImportException>(() => G("vp", p, 4096, PageReadWrite, old.Address));
        Assert.IsType<Win32SelfModifyingCodeException>(e.InnerException);
        Assert.Contains("generated at run time", e.InnerException!.Message);
        Assert.Equal(32u, G("callp", p, 5));
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void Freeing_the_memory_drops_the_translation_and_new_code_there_is_translated_again()
    {
        var p = Place(SquarePlus7);
        Assert.Equal(32u, G("callp", p, 5));
        Assert.Equal(1u, G("free", p));
        // mov eax,[esp+4]; neg eax; ret
        var q = Place([0x8B, 0x44, 0x24, 0x04, 0xF7, 0xD8, 0xC3]);
        Assert.Equal(unchecked((uint)-5), G("callp", q, 5));
        Assert.Equal(2, module.DynamicTranslations.Count);
        if (q != p)
        {
            var e = Assert.Throws<GuestTrapException>(() => G("callp", p, 5));   // the old address is a miss again, and nothing is mapped there
            Assert.Contains($"0x{p:X8}", e.Message);
        }
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void A_second_entry_into_the_same_region_retranslates_it_with_both_entries()
    {
        // f at +0: mov eax,[esp+4]; add eax,1; ret     g at +0x40: mov eax,[esp+4]; add eax,2; ret
        var blob = new byte[0x48];
        new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x83, 0xC0, 0x01, 0xC3 }.CopyTo(blob, 0);
        new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x83, 0xC0, 0x02, 0xC3 }.CopyTo(blob, 0x40);
        var p = Place(blob);
        Assert.Equal(11u, G("callp", p, 10));
        Assert.Equal(12u, G("callp", p + 0x40, 10));
        Assert.Equal(11u, G("callp", p, 10));
        var log = module.DynamicTranslations;
        Assert.Equal(2, log.Count);
        Assert.Equal((p + 0x40, 2), (log[1].EntryAddress, log[1].Entries));
    }

    [RequiresWindowsFact(WindowsOnly)]
    public void Bytes_that_do_not_decode_fault_by_name_with_the_address()
    {
        var p = Place([0x90, 0xFF, 0xFF, 0xC3]);   // nop; (FF /7: no instruction)
        var e = Assert.Throws<GuestTrapException>(() => G("callp", p, 0));
        Assert.Contains($"0x{p + 1:X8}", e.Message);
        Assert.Equal(32u, G("callp", Place(SquarePlus7), 5));
    }

    [Fact]
    public void Jumping_into_memory_that_was_never_made_executable_traps_by_name()
    {
        var p = Place(SquarePlus7, PageReadWrite);
        var e = Assert.Throws<GuestTrapException>(() => G("callp", p, 5));
        Assert.Contains($"0x{p:X8}", e.Message);
        Assert.Contains("never made executable", e.Message);
        Assert.Empty(module.DynamicTranslations);
    }

    [Fact]
    public void The_option_disables_translation_and_other_hosts_name_the_missing_guard()
    {
        if (!OperatingSystem.IsWindows())
        {
            var q = Place(SquarePlus7);
            var f = Assert.Throws<GuestTrapException>(() => G("callp", q, 5));
            Assert.Contains("only the windows-x64 runtime has", f.Message);
        }
        module.Dispose();   // the second fixture needs the same fixed base
        using var off = new DynamicCodeTests(dynamicCode: false);
        var p = off.Place(SquarePlus7);
        var e = Assert.Throws<GuestTrapException>(() => off.G("callp", p, 5));
        Assert.Contains("disabled (GuestModuleOptions.DynamicCode = false)", e.Message);
        Assert.Empty(off.module.DynamicTranslations);
    }

    [Fact]
    public void GuestProcessEnvironment_registry_capacity_supports_large_dynamic_allocation_counts()
    {
        Assert.True(GuestProcessEnvironment.RegistryCapacity >= 65536);
        using var env = new GuestProcessEnvironment(ownedBySet: false);
        // Registering more than the old limit of 1024 modules does not exhaust the registry.
        var slots = new List<int>();
        for (var i = 0; i < 1100; i++)
        {
            var slot = env.Publish((uint)(0x20000000 + i * 0x1000), 0x1000, 0x10000, 0x20000);
            slots.Add(slot);
        }
        Assert.Equal(1100, slots.Count);
        Assert.Equal(1100, slots.Distinct().Count());
    }
}
