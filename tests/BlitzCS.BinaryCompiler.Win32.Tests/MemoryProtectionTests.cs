using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// kernel32!VirtualProtect / VirtualQuery with truthful semantics, driven by a hand-assembled guest. A guest write into a read-only page is a hardware fault that would kill the
/// process, so read-only transitions are verified through VirtualQuery and through the runtime's managed access checks, never by letting guest code write.
/// </summary>
[Collection("GuestRuntime")]
public sealed class MemoryProtectionTests : IDisposable
{
    private static readonly uint Base = Win32PeBuilder.ImageBase;
    private static uint Text(uint offset) => Base + Win32PeBuilder.TextRva + offset;
    private static uint Data(uint offset) => Base + Win32PeBuilder.DataRva + offset;

    private readonly Win32ImportResolver resolver = new();
    private readonly GuestModule module;
    private readonly Dictionary<string, uint> labels = [];

    public MemoryProtectionTests()
    {
        var pe = new Win32PeBuilder();
        uint vprotect = pe.Import("KERNEL32.dll", "VirtualProtect"), vquery = pe.Import("KERNEL32.dll", "VirtualQuery"), valloc = pe.Import("KERNEL32.dll", "VirtualAlloc");
        var code = new List<byte>();
        void Label(string n) => labels[n] = (uint)code.Count;
        void B(params byte[] b) => code.AddRange(b);
        void Call(uint slot) { B(0xFF, 0x15); code.AddRange(BitConverter.GetBytes(Base + slot)); }
        Label("vp"); B(0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10); Call(vprotect); B(0xC3);
        Label("vq"); B(0xFF, 0x74, 0x24, 0x0C, 0xFF, 0x74, 0x24, 0x0C, 0xFF, 0x74, 0x24, 0x0C); Call(vquery); B(0xC3);
        Label("valloc"); B(0xFF, 0x74, 0x24, 0x08, 0x68, 0x00, 0x30, 0x00, 0x00, 0xFF, 0x74, 0x24, 0x0C, 0x6A, 0x00); Call(valloc); B(0xC3);
        Label("callp"); B(0xFF, 0x54, 0x24, 0x04, 0xC3);
        Label("nop"); B(0xC3);
        pe.WithCode([.. code]).WithData(new byte[0x100]);
        foreach (var (name, off) in labels) pe.Export(name, off);
        var image = PeReader.Read(pe.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "x1-guest");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.All(resolver.Analyze(image), v => Assert.Equal(Win32ImportStatus.Implemented, v.Status));
        module = GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
    }

    public void Dispose() { module.Dispose(); resolver.Dispose(); }

    private uint Vp(uint address, uint size, uint protect, uint oldPtr) => module.Bind("vp", GuestCallingConvention.Cdecl, 4).Call(address, size, protect, oldPtr);
    private uint Vq(uint address, uint buffer, uint length = 28) => module.Bind("vq", GuestCallingConvention.Cdecl, 3).Call(address, buffer, length);
    private uint Alloc(uint size, uint protect) => module.Bind("valloc", GuestCallingConvention.Cdecl, 2).Call(size, protect);
    private uint LastError => resolver.Context.LastError;

    private readonly record struct Info(uint BaseAddress, uint AllocationBase, uint AllocationProtect, uint RegionSize, uint State, uint Protect, uint Type);

    private Info Query(uint address)
    {
        using var scratch = module.Memory.Allocate(64);
        Assert.Equal(28u, Vq(address, scratch.Address));
        var b = scratch.ToArray();
        uint W(int i) => BitConverter.ToUInt32(b, i * 4);
        return new Info(W(0), W(1), W(2), W(3), W(4), W(5), W(6));
    }

    private (uint Result, uint Old) Protect(uint address, uint size, uint protect)
    {
        using var scratch = module.Memory.Allocate(16);
        scratch.Write(0, BitConverter.GetBytes(0xDEADBEEF));
        var r = Vp(address, size, protect, scratch.Address);
        return (r, BitConverter.ToUInt32(scratch.ToArray(), 0));
    }

    [Fact]
    public void Own_data_pages_change_protection_and_report_the_old_one()
    {
        var p = Alloc(8192, 4);
        Assert.NotEqual(0u, p);
        var all = Query(p);
        Assert.Equal((p, p, 4u, 8192u, 0x1000u, 4u, 0x20000u), (all.BaseAddress, all.AllocationBase, all.AllocationProtect, all.RegionSize, all.State, all.Protect, all.Type));

        Assert.Equal((1u, 4u), Protect(p + 4096, 4096, 2));                 // READONLY on the second page; old = READWRITE
        Assert.Equal((p, 4096u, 4u), (Query(p).BaseAddress, Query(p).RegionSize, Query(p).Protect));
        Assert.Equal((4096u, 2u), (Query(p + 4096).RegionSize, Query(p + 4096).Protect));
        module.Memory.WriteUInt32(p, 1);                                     // first page untouched
        Assert.Equal(0u, module.Memory.ReadUInt32(p + 4096));                // reads still work
        Assert.Throws<GuestMemoryException>(() => module.Memory.WriteUInt32(p + 4096, 1));   // the runtime refuses the write instead of a hardware fault

        Assert.Equal((1u, 2u), Protect(p + 4096, 1, 1));                     // NOACCESS: size 1 covers the whole page
        Assert.Throws<GuestMemoryException>(() => module.Memory.ReadUInt32(p + 4096));
        Assert.Equal(1u, Query(p + 4096).Protect);

        Assert.Equal((1u, 4u), Protect(p, 8192, 0x40));                      // EXECUTE_READWRITE (old = the first page): allowed, recorded, never executable on the host
        Assert.Equal(0x40u, Query(p + 4096).Protect);
        module.Memory.WriteUInt32(p + 4096, 7);
        Assert.Equal((1u, 0x40u), Protect(p, 8192, 8));                      // WRITECOPY: private memory, behaves as READWRITE
        module.Memory.WriteUInt32(p + 4096, 8);
        Assert.Equal(8u, Query(p).Protect);
        Assert.Equal((1u, 8u), Protect(p, 8192, 0x20 | 0x200));              // EXECUTE_READ + NOCACHE accepted
        Assert.Equal(0x220u, Query(p).Protect);
    }

    [Fact]
    public void Writable_protection_on_translated_code_is_refused_by_name()
    {
        var code = Text(0);
        foreach (var writable in new uint[] { 4, 8, 0x40, 0x80 })
        {
            var e = Assert.Throws<GuestImportException>(() => Protect(code, 16, writable));
            var smc = Assert.IsType<Win32SelfModifyingCodeException>(e.InnerException);
            Assert.IsAssignableFrom<Win32UsageException>(smc);
            Assert.Contains("self-modifying code", smc.Message);
            Assert.Contains("ahead-of-time translation cannot honour", smc.Message);
            Assert.Contains("translated module at base 0x" + Base.ToString("X8"), smc.Message);
            Assert.Contains($"0x{code & ~0xFFFu:X8}", smc.Message);
            Assert.Contains($"0x{writable:X}", smc.Message);
            Assert.Equal(code & ~0xFFFu, smc.Start);
        }
        // the refusal changed nothing, and the module is still usable
        Assert.Equal(0x20u, Query(code).Protect);
        Assert.Equal(0x1000000u, Query(code).Type);
        // a range that merely includes one code page is refused too, and applies nothing to its data page
        Assert.Throws<GuestImportException>(() => Protect(Text(0), 0x2000, 4));
        Assert.Equal(4u, Query(Data(0)).Protect);   // .data is the image's own RW section: untouched
        // behaviour-neutral requests are accepted as no-ops and report EXECUTE_READ
        Assert.Equal((1u, 0x20u), Protect(code, 16, 0x20));
        Assert.Equal((1u, 0x20u), Protect(code, 16, 2));
        Assert.Equal((1u, 0x20u), Protect(code, 16, 0x10));
        Assert.Equal(0x20u, Query(code).Protect);
        var none = Assert.Throws<GuestImportException>(() => Protect(code, 16, 1));
        Assert.IsType<Win32UsageException>(none.InnerException);
        module.Bind("nop", GuestCallingConvention.Cdecl, 0).Call();   // the translated code still runs
    }

    [Fact]
    public void Image_data_sections_keep_their_protection_rules()
    {
        // .data is RW in the guest and stays RW for the host (W^X): it can be made read-only and back, and an executable tag is recorded without host execute permission.
        var data = Data(0);
        Assert.Equal(4u, Query(data).Protect);
        Assert.Equal((1u, 4u), Protect(data, 16, 2));
        Assert.Throws<GuestMemoryException>(() => module.Memory.WriteUInt32(data, 1));
        Assert.Equal((1u, 2u), Protect(data, 16, 4));
        module.Memory.WriteUInt32(data, 1);
        Assert.Equal((1u, 4u), Protect(data, 16, 0x40));
        Assert.Equal(0x40u, Query(data).Protect);
        Assert.Equal((1u, 0x40u), Protect(data, 16, 4));
    }

    [Fact]
    public void Bad_ranges_and_arguments_fail_like_windows_and_change_nothing()
    {
        var p = Alloc(8192, 4);
        Assert.Equal(0u, Protect(0x10, 16, 4).Result); Assert.Equal(Win32Error.InvalidAddress, LastError);        // unmapped
        Assert.Equal(0u, Protect(p, 0, 4).Result); Assert.Equal(Win32Error.InvalidParameter, LastError);          // empty range
        Assert.Equal(0u, Protect(p, 8192, 3).Result); Assert.Equal(Win32Error.InvalidParameter, LastError);       // not a protection constant
        Assert.Equal(0u, Protect(p, 8192, 0x1 | 0x100).Result); Assert.Equal(Win32Error.InvalidParameter, LastError);   // NOACCESS|GUARD
        Assert.Equal(0u, Protect(p, 8192, 0x4 | 0x800).Result); Assert.Equal(Win32Error.InvalidParameter, LastError);   // unknown modifier bit
        Assert.Equal(0u, Protect(p, 8192, 0x4 | 0x200 | 0x400).Result); Assert.Equal(Win32Error.InvalidParameter, LastError);
        Assert.Equal(0u, Protect(0xFFFFF000, 0x2000, 4).Result); Assert.Equal(Win32Error.InvalidAddress, LastError);   // wraps past 4 GiB
        Assert.Equal(0u, Protect(p, 3 * 4096, 2).Result); Assert.Equal(Win32Error.InvalidAddress, LastError);     // third page is not part of the allocation: all or nothing
        Assert.Equal(4u, Query(p).Protect);
        Assert.Equal(8192u, Query(p).RegionSize);
        Assert.Equal(0u, Vp(p, 16, 2, 0)); Assert.Equal(Win32Error.NoAccess, LastError);                          // lpflOldProtect == NULL
        var guard = Assert.Throws<GuestImportException>(() => Protect(p, 16, 4 | 0x100));
        Assert.Contains("PAGE_GUARD", guard.InnerException!.Message);
        // a range spanning two different allocations
        var q = Alloc(4096, 4);
        var lo = Math.Min(p, q);
        var span = Math.Max(p, q) + 4096 - lo;
        if (span < 0x100000) { Assert.Equal(0u, Protect(lo, span, 2).Result); Assert.Equal(Win32Error.InvalidAddress, LastError); }
        // unaligned address and size are widened to whole pages
        Assert.Equal((1u, 4u), Protect(p + 4090, 20, 2));
        Assert.Equal(2u, Query(p).Protect); Assert.Equal(8192u, Query(p).RegionSize);
    }

    [Fact]
    public void Query_reports_free_space_and_bad_lengths()
    {
        var free = Query(0x00011234);
        Assert.Equal((0x00011000u, 0u, 0u, 0x10000u, 1u, 0u), (free.BaseAddress, free.AllocationBase, free.AllocationProtect, free.State, free.Protect, free.Type));
        Assert.True(free.RegionSize >= 0x1000);
        using var scratch = module.Memory.Allocate(64);
        Assert.Equal(0u, Vq(Text(0), scratch.Address, 27)); Assert.Equal(24u, LastError);
        Assert.Equal(0u, Vq(0xFFFF0000, scratch.Address)); Assert.Equal(Win32Error.InvalidParameter, LastError);
        var image = Query(Base);
        Assert.Equal((Base, 0x1000000u, 0x1000u), (image.AllocationBase, image.Type, image.State));
    }

    [Fact]
    public void Jumping_into_guest_generated_code_traps_instead_of_running_it()
    {
        var p = Alloc(4096, 0x40);   // PAGE_EXECUTE_READWRITE: the guest generates code here
        Assert.Equal(0x40u, Query(p).Protect);
        module.Memory.Write(p, [0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3]);   // mov eax, 42 ; ret
        var trap = Assert.Throws<GuestTrapException>(() => module.Bind("callp", GuestCallingConvention.Cdecl, 1).Call(p));
        Assert.Contains($"0x{p:X8}", trap.Message);
        Assert.Contains("untranslated code", trap.Message);
        module.Bind("nop", GuestCallingConvention.Cdecl, 0).Call();   // the module survived the trap
    }
}
