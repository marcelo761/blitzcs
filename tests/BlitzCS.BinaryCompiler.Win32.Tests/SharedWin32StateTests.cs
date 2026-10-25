using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// Two hand-assembled DLLs (D and E) in one GuestModuleSet over ONE Win32 state (<see cref="Win32ImportResolver.SharedFactory"/>): the process heap, the CRT heap, the handle table,
/// the TLS index space, critical sections, events and the last error are process-wide, as on Windows (docs/binary-win32.md, "One process, one Win32 state").
/// </summary>
[Collection("GuestRuntime")]
public sealed class SharedWin32StateTests
{
    /// <summary>Tiny hand assembler; every absolute operand gets a relocation so the allocator may move the image.</summary>
    private sealed class Asm(Win32PeBuilder b)
    {
        public readonly List<byte> Bytes = [];
        public uint Here => (uint)Bytes.Count;
        public Asm Raw(params byte[] x) { Bytes.AddRange(x); return this; }
        public Asm Call(uint iatRva) { Raw(0xFF, 0x15); return Abs(iatRva); }
        public Asm Abs(uint rva) { b.Relocate(Win32PeBuilder.TextRva + Here); Bytes.AddRange(BitConverter.GetBytes(Win32PeBuilder.ImageBase + rva)); return this; }
    }

    private static PeImage LibD()
    {
        var b = new Win32PeBuilder().WithData(new byte[0x100]);
        var getHeap = b.Import("kernel32.dll", "GetProcessHeap"); var heapAlloc = b.Import("kernel32.dll", "HeapAlloc"); var tlsAlloc = b.Import("kernel32.dll", "TlsAlloc");
        var setLast = b.Import("kernel32.dll", "SetLastError"); var createEvent = b.Import("kernel32.dll", "CreateEventA"); var csInit = b.Import("kernel32.dll", "InitializeCriticalSection");
        var malloc = b.Import("msvcrt.dll", "malloc");
        var a = new Asm(b);
        var halloc = a.Here; a.Call(getHeap).Raw(0x6A, 0x40, 0x6A, 0x00, 0x50).Call(heapAlloc).Raw(0xC3);
        var cmalloc = a.Here; a.Raw(0x6A, 0x40).Call(malloc).Raw(0x83, 0xC4, 0x04, 0xC3);
        var tls = a.Here; a.Call(tlsAlloc).Raw(0xC3);
        var last = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(setLast).Raw(0xC3);
        var ev = a.Here; a.Raw(0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x01, 0x6A, 0x00).Call(createEvent).Raw(0xC3);
        var csp = a.Here; a.Raw(0xB8).Abs(Win32PeBuilder.DataRva + 0x40).Raw(0xC3);
        var csi = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(csInit).Raw(0xC3);
        var main = a.Here; a.Raw(0xB8, 1, 0, 0, 0, 0xC2, 0x0C, 0x00);
        b.WithCode([.. a.Bytes]).EntryPoint(main).Export("d_halloc", halloc).Export("d_malloc", cmalloc).Export("d_tlsalloc", tls).Export("d_setlast", last)
         .Export("d_event", ev).Export("d_csptr", csp).Export("d_csinit", csi);
        return PeReader.Read(b.Build());
    }

    private static PeImage LibE()
    {
        var b = new Win32PeBuilder().WithData(new byte[0x100]);
        var getHeap = b.Import("kernel32.dll", "GetProcessHeap"); var heapFree = b.Import("kernel32.dll", "HeapFree"); var tlsSet = b.Import("kernel32.dll", "TlsSetValue");
        var tlsGet = b.Import("kernel32.dll", "TlsGetValue"); var getLast = b.Import("kernel32.dll", "GetLastError"); var setEvent = b.Import("kernel32.dll", "SetEvent");
        var wait = b.Import("kernel32.dll", "WaitForSingleObject"); var enter = b.Import("kernel32.dll", "EnterCriticalSection"); var leave = b.Import("kernel32.dll", "LeaveCriticalSection");
        var free = b.Import("msvcrt.dll", "free");
        var a = new Asm(b);
        var hfree = a.Here; a.Call(getHeap).Raw(0xFF, 0x74, 0x24, 0x04, 0x6A, 0x00, 0x50).Call(heapFree).Raw(0xC3);
        var cfree = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(free).Raw(0x83, 0xC4, 0x04, 0xC3);
        var tset = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08).Call(tlsSet).Raw(0xC3);
        var tget = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(tlsGet).Raw(0xC3);
        var last = a.Here; a.Call(getLast).Raw(0xC3);
        var sev = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(setEvent).Raw(0xC3);
        var wev = a.Here; a.Raw(0x6A, 0x00, 0xFF, 0x74, 0x24, 0x08).Call(wait).Raw(0xC3);
        var center = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(enter).Raw(0xC3);
        var cleave = a.Here; a.Raw(0xFF, 0x74, 0x24, 0x04).Call(leave).Raw(0xC3);
        var main = a.Here; a.Raw(0xB8, 1, 0, 0, 0, 0xC2, 0x0C, 0x00);
        b.WithCode([.. a.Bytes]).EntryPoint(main).Export("e_hfree", hfree).Export("e_free", cfree).Export("e_tlsset", tset).Export("e_tlsget", tget).Export("e_getlast", last)
         .Export("e_setevent", sev).Export("e_wait", wev).Export("e_csenter", center).Export("e_csleave", cleave);
        return PeReader.Read(b.Build());
    }

    private static GuestModuleSet Open(bool shared)
    {
        var images = new[] { ("d", LibD()), ("e", LibE()) };
        var plan = ModuleBaseAllocator.Allocate(images);
        var options = new Win32Options();
        var set = shared ? new GuestModuleSet(Win32ImportResolver.SharedFactory(options)) : new GuestModuleSet(_ => new Win32ImportResolver(options));
        for (var i = 0; i < images.Length; i++)
        {
            var art = X64Translator.Translate(images[i].Item2, new BinaryCompilerOptions(OptimizationProfile.Release, LoadBase: plan[i].Base), "sw" + i);
            Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            set.Add(images[i].Item1, art, images[i].Item2.Rebase(plan[i].Base), ExportConventions.AllCdecl);
        }
        set.LoadAll();
        return set;
    }

    private static uint Call(GuestModuleSet set, string module, string export, params uint[] args) => set[module].Bind(export, GuestCallingConvention.Cdecl, args.Length).Call(args);

    [Fact]
    public void HeapsHandlesTlsCriticalSectionsAndLastErrorAreSharedByTheModulesOfOneProcess()
    {
        using var set = Open(shared: true);
        // process heap: allocated in D, freed in E
        var block = Call(set, "d", "d_halloc");
        Assert.NotEqual(0u, block);
        Assert.Equal(1u, Call(set, "e", "e_hfree", block));
        // CRT heap: malloc in D, free in E (a pointer the CRT of another module never saw would be refused by name)
        Call(set, "e", "e_free", Call(set, "d", "d_malloc"));
        // one TLS index space; the value is per thread
        var index = Call(set, "d", "d_tlsalloc");
        Assert.Equal(1u, Call(set, "e", "e_tlsset", index, 0xBEEF));
        Assert.Equal(0xBEEFu, Call(set, "e", "e_tlsget", index));
        Assert.NotEqual(index, Call(set, "d", "d_tlsalloc"));
        uint onOtherThread = 1;
        var t = new Thread(() => onOtherThread = Call(set, "e", "e_tlsget", index)); t.Start(); t.Join();
        Assert.Equal(0u, onOtherThread);
        // last error set in D, read in E (same thread)
        Call(set, "d", "d_setlast", 1234);
        Assert.Equal(1234u, Call(set, "e", "e_getlast"));
        // an event created in D is a handle E can signal and wait for
        var ev = Call(set, "d", "d_event");
        Assert.Equal(1u, Call(set, "e", "e_setevent", ev));
        Assert.Equal(0u, Call(set, "e", "e_wait", ev));                      // WAIT_OBJECT_0
        // a critical section initialised by D is entered and left by E
        var cs = Call(set, "d", "d_csptr");
        Call(set, "d", "d_csinit", cs);
        Call(set, "e", "e_csenter", cs); Call(set, "e", "e_csleave", cs);
        // the module that owns the shared allocations cannot be pulled out from under the other
        Assert.Throws<InvalidOperationException>(() => set.Unload("d"));
        set.Unload("e");
    }

    [Fact]
    public void WithOneResolverPerModuleTheStateIsSeparate_MutationWitness()
    {
        // The same scenario over per-module Win32 state (the previous behaviour, still available): the pieces do not meet.
        using var set = Open(shared: false);
        var block = Call(set, "d", "d_halloc");
        uint freed;
        try { freed = Call(set, "e", "e_hfree", block); } catch (GuestException) { freed = 0; }
        Assert.Equal(0u, freed);                                              // E's process heap never saw D's block
        var index = Call(set, "d", "d_tlsalloc");
        Assert.Equal(0u, Call(set, "e", "e_tlsset", index, 0xBEEF));            // E's TLS index space has not allocated D's index
        Call(set, "d", "d_setlast", 1234);
        Assert.NotEqual(1234u, Call(set, "e", "e_getlast"));
    }
}
