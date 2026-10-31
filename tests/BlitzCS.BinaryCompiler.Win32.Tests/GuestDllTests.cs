using System.Buffers.Binary;
using BlitzCS.BcRun;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// A guest EXE loads a guest DLL through LoadLibraryA and calls it through the address GetProcAddress returns (hand-assembled PE32s): the REAL address (module base
/// + export RVA), reached through cross-module dispatch with no calling-convention knowledge. The EXE writes an array of dwords (what it observed) to stdout; the test decodes it.
/// </summary>
[Collection("GuestRuntime")]
public sealed class GuestDllTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("guestdll-").FullName;
    public void Dispose() => Directory.Delete(dir, true);

    private static void P32(List<byte> c, uint v) => c.AddRange(BitConverter.GetBytes(v));

    // Export code offsets inside the DLL's .text.
    private const uint AddcAt = 0x00, AddsAt = 0x10, ThunkAt = 0x20, MixedAt = 0x40, MainAt = 0x60, DvalAt = 0x1010; // DvalAt is relative to .text: it lies in .data (RVA 0x2010)

    /// <summary>
    /// addc: cdecl (plain ret); adds: stdcall (ret 8); thunk: jmp [GetTickCount's IAT slot] (no ret of its own); mixed: ret on one path, ret 8 on another;
    /// dval: a data export (0xDEADBEEF); DllMain: returns <paramref name="dllMainResult"/>. Exports sorted by name get ordinals addc=1, adds=2, dval=3, mixed=4, thunk=5.
    /// </summary>
    private static byte[] BuildDll(uint dllMainResult = 1)
    {
        var b = new PeFixtureBuilder();
        var iat = b.Import("KERNEL32.dll", "GetTickCount");
        var code = new byte[0x80];
        void Put(uint at, params byte[] bytes) => bytes.CopyTo(code, at);
        Put(AddcAt, 0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0xC3);                       // mov eax,[esp+4]; add eax,[esp+8]; ret
        Put(AddsAt, 0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0xC2, 0x08, 0x00);           // ...; ret 8
        Put(ThunkAt, [0xFF, 0x25, .. BitConverter.GetBytes(PeFixtureBuilder.ImageBase + iat)]);   // jmp [iat]
        b.Relocate(PeFixtureBuilder.TextRva + ThunkAt + 2);
        Put(MixedAt, 0x83, 0x7C, 0x24, 0x04, 0x00, 0x74, 0x01, 0xC3, 0xC2, 0x08, 0x00);          // cmp dword [esp+4],0; je +1; ret; ret 8
        Put(MainAt, [0xB8, .. BitConverter.GetBytes(dllMainResult), 0xC2, 0x0C, 0x00]);           // mov eax,result; ret 12
        var data = new byte[0x40];
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(data, 0x10);
        return b.WithCode(code).WithData(data).Export("addc", AddcAt).Export("adds", AddsAt).Export("thunk", ThunkAt).Export("mixed", MixedAt).Export("dval", DvalAt).EntryPoint(MainAt).Build();
    }

    /// <summary>Tiny assembler for the EXE: every absolute address gets a relocation, so the loader can move the image.</summary>
    private sealed class Exe
    {
        public readonly PeFixtureBuilder B = new PeFixtureBuilder().AsExecutable();
        public readonly List<byte> C = [];
        public readonly byte[] Data = new byte[0x400];
        public const uint Results = 0x000, LibName = 0x100, Addc = 0x110, Adds = 0x118, Thunk = 0x120, Mixed = 0x128, Missing = 0x130, Dval = 0x300, Written = 0x140, Buf = 0x150;
        private readonly Dictionary<string, uint> iat = [];

        public Exe() { Str(LibName, "lib.dll"); Str(Addc, "addc"); Str(Adds, "adds"); Str(Thunk, "thunk"); Str(Mixed, "mixed"); Str(Missing, "missing.dll"); Str(Dval, "dval"); }
        public void Str(uint at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(Data, at);
        private uint Slot(string name) => iat.TryGetValue(name, out var s) ? s : iat[name] = B.Import("KERNEL32.dll", name);
        private static uint DataVa(uint off) => PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva + off;
        private void Abs(uint va) { B.Relocate(PeFixtureBuilder.TextRva + (uint)C.Count); P32(C, va); }
        public uint Here => (uint)C.Count;

        public void Call(string api) { C.AddRange([0xFF, 0x15]); Abs(PeFixtureBuilder.ImageBase + Slot(api)); }
        public void PushData(uint off) { C.Add(0x68); Abs(DataVa(off)); }
        public void PushMem(uint off) { C.AddRange([0xFF, 0x35]); Abs(DataVa(off)); }
        public void PushImm8(sbyte v) => C.AddRange([0x6A, (byte)v]);
        public void PushImm32(uint v) { C.Add(0x68); P32(C, v); }
        public void PushCode(uint codeOffset) { C.Add(0x68); Abs(PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + codeOffset); }
        public void Store(uint off) { C.Add(0xA3); Abs(DataVa(off)); }              // mov [data+off], eax
        public void Load(uint off) { C.Add(0xA1); Abs(DataVa(off)); }              // mov eax,[data+off]
        public void CallMem(uint off) { C.AddRange([0xFF, 0x15]); Abs(DataVa(off)); } // call [data+off]
        public void StoreEsp(uint off) { C.AddRange([0x89, 0x25]); Abs(DataVa(off)); }  // mov [data+off], esp
        public void DerefEax() => C.AddRange([0x8B, 0x00]);                                // mov eax,[eax]
        public void Raw(params byte[] bytes) => C.AddRange(bytes);

        /// <summary>WriteFile(stdout, results, count*4); ExitProcess(0).</summary>
        public void FinishWith(int dwords)
        {
            PushImm8(-11); Call("GetStdHandle"); Raw(0x89, 0xC3);                       // mov ebx,eax
            PushImm8(0); PushData(Written); PushImm8((sbyte)(dwords * 4)); PushData(Results); Raw(0x53); Call("WriteFile");
            PushImm8(0); Call("ExitProcess"); Raw(0xCC);
        }

        public byte[] Build(uint entry) => B.WithCode([.. C]).WithData(Data).EntryPoint(entry).Build();
    }

    private (uint[] Words, BcRunResult Result, string Err) Run(byte[] exe, string? cache = null)
    {
        var path = Path.Combine(dir, "app.exe");
        File.WriteAllBytes(path, exe);
        var stdout = new MemoryStream();
        var log = new List<string>();
        var r = BcRunner.Run(new BcRunOptions { ExePath = path, Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream(), CacheDirectory = cache, Log = log.Add });
        var bytes = stdout.ToArray();
        var words = Enumerable.Range(0, bytes.Length / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4))).ToArray();
        return (words, r, string.Join("\n", log));
    }

    private byte[] ProbeExe(string lib)
    {
        var e = new Exe();
        e.Str(Exe.LibName, lib);
        e.PushData(Exe.LibName); e.Call("LoadLibraryA"); e.Store(Exe.Results);
        e.Call("GetLastError"); e.Store(Exe.Results + 4);
        e.FinishWith(2);
        return e.Build(0);
    }

    [Fact]
    public void GetProcAddressReturnsTheRealAddressAndCallsThroughItWork()
    {
        File.WriteAllBytes(Path.Combine(dir, "lib.dll"), BuildDll());
        var e = new Exe();
        // hmod = LoadLibraryA("lib.dll")
        e.PushData(Exe.LibName); e.Call("LoadLibraryA"); e.Store(0);
        void Gpa(uint name, uint into) { e.PushData(name); e.PushMem(0); e.Call("GetProcAddress"); e.Store(into); }
        Gpa(Exe.Addc, 0x80); Gpa(Exe.Adds, 0x84);
        e.PushImm8(12); e.PushImm8(30); e.CallMem(0x80); e.Raw(0x83, 0xC4, 0x08); e.Store(4);   // addc(30,12): cdecl, caller pops
        e.PushImm8(13); e.PushImm8(30); e.CallMem(0x84); e.Store(8);                              // adds(30,13): stdcall, the callee pops
        Gpa(Exe.Thunk, 12); e.CallMem(12); e.Store(16);                                           // thunk: a bare jmp [iat] to GetTickCount, no ret of its own
        Gpa(Exe.Mixed, 0x60); e.StoreEsp(20); e.PushImm8(0); e.PushImm8(0); e.CallMem(0x60); e.StoreEsp(24); // mixed(0,0): ret 8 path, the callee pops
        Gpa(Exe.Dval, 80); e.Load(80); e.DerefEax(); e.Store(84);                                // data export: the address of the variable
        e.PushImm8(3); e.PushMem(0); e.Call("GetProcAddress"); e.Store(88);                       // ... also by ordinal
        e.PushImm8(2); e.PushMem(0); e.Call("GetProcAddress"); e.Store(28);                       // by ordinal 2 = adds
        e.PushData(Exe.LibName); e.Call("LoadLibraryA"); e.Store(32);                             // second load: same HMODULE
        e.PushData(Exe.Missing); e.Call("LoadLibraryA"); e.Store(36); e.Call("GetLastError"); e.Store(40);
        e.PushImm32(260); e.PushData(Exe.Buf); e.PushMem(0); e.Call("GetModuleFileNameA"); e.Store(44);
        e.PushData(Exe.LibName); e.Call("GetModuleHandleA"); e.Store(48);
        e.PushMem(0); e.Call("FreeLibrary"); e.Store(52);                                         // refcount 2 -> 1
        e.PushMem(0); e.Call("FreeLibrary"); e.Store(56);                                         // 1 -> 0: DLL_PROCESS_DETACH, unloaded
        e.PushMem(0); e.Call("FreeLibrary"); e.Store(60); e.Call("GetLastError"); e.Store(64);    // already unloaded
        e.PushData(Exe.LibName); e.Call("GetModuleHandleA"); e.Store(68); e.Call("GetLastError"); e.Store(72);
        e.Load(0x84); e.Store(76);                                                                // the by-name address of adds
        e.FinishWith(23);

        var (w, r, log) = Run(e.Build(0));
        Assert.True(r.Failure is null, r.Failure + "\n" + log);
        Assert.NotEqual(0u, w[0]);                       // HMODULE = the module's guest base
        Assert.Equal(42u, w[1]);                         // addc(30,12)
        Assert.Equal(43u, w[2]);                         // adds(30,13): stdcall: "ret 8" popped the arguments
        Assert.Equal(w[0] + PeFixtureBuilder.TextRva + ThunkAt, w[3]); // the address equals module base + export RVA from the PE export table
        // w[4]: the bare "jmp [iat]" thunk reached GetTickCount and returned (the run continued with a balanced stack; the tick value itself is not asserted)
        Assert.Equal(w[5], w[6]);                        // mixed: stack balanced after the call
        Assert.Equal(w[0] + PeFixtureBuilder.TextRva + AddsAt, w[7]);
        Assert.Equal(w[19], w[7]);                       // same pointer as the by-name lookup of adds
        Assert.Equal(w[0] + PeFixtureBuilder.TextRva + AddsAt, w[19]);
        Assert.Equal(w[0] + PeFixtureBuilder.DataRva + 0x10, w[20]); // data export: address of the data
        Assert.Equal(0xDEADBEEFu, w[21]);                // and the guest can read it through that address
        Assert.Equal(w[20], w[22]);
        Assert.Equal(w[0], w[8]);                        // loading twice returns the same HMODULE
        Assert.Equal(0u, w[9]); Assert.Equal(126u, w[10]); // ERROR_MOD_NOT_FOUND
        Assert.Equal((uint)"C:\\lib.dll".Length, w[11]); // GetModuleFileName(hmod)
        Assert.Equal(w[0], w[12]);                       // GetModuleHandle("lib.dll")
        Assert.Equal(1u, w[13]); Assert.Equal(1u, w[14]);
        Assert.Equal(0u, w[15]); Assert.Equal(6u, w[16]); // FreeLibrary of an unloaded handle: ERROR_INVALID_HANDLE
        Assert.Equal(0u, w[17]); Assert.Equal(126u, w[18]); // GetModuleHandle after unload
    }

    [Fact]
    public void DllMainReturningFalseFailsTheLoadWithDllInitFailed()
    {
        File.WriteAllBytes(Path.Combine(dir, "lib.dll"), BuildDll(dllMainResult: 0));
        var (w, r, log) = Run(ProbeExe("lib.dll"));
        Assert.True(r.Failure is null, r.Failure + "\n" + log);
        Assert.Equal(0u, w[0]);
        Assert.Equal(1114u, w[1]); // ERROR_DLL_INIT_FAILED
    }

    [Fact]
    public void MissingFileIs126AndGarbageIsBadExeFormat193()
    {
        var (w, r, log) = Run(ProbeExe("nothere.dll"));
        Assert.True(r.Failure is null, r.Failure + "\n" + log);
        Assert.Equal([0u, 126u], w);
        File.WriteAllBytes(Path.Combine(dir, "bad.dll"), "this is not a portable executable"u8.ToArray());
        (w, r, log) = Run(ProbeExe("bad.dll"));
        Assert.True(r.Failure is null, r.Failure + "\n" + log);
        Assert.Equal([0u, 193u], w);
    }

    [Fact]
    public void GuestDllsCanBeDisabled()
    {
        File.WriteAllBytes(Path.Combine(dir, "lib.dll"), BuildDll());
        var path = Path.Combine(dir, "app.exe");
        File.WriteAllBytes(path, ProbeExe("lib.dll"));
        var stdout = new MemoryStream();
        var r = BcRunner.Run(new BcRunOptions { ExePath = path, GuestDlls = false, Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream() });
        Assert.Null(r.Failure);
        var bytes = stdout.ToArray();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(126u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void SecondRunHitsTheTranslationCache()
    {
        File.WriteAllBytes(Path.Combine(dir, "lib.dll"), BuildDll());
        var cache = Path.Combine(dir, "cache");
        var exe = ProbeExe("lib.dll");
        var (_, r1, log1) = Run(exe, cache);
        Assert.True(r1.Failure is null, r1.Failure + "\n" + log1);
        Assert.Contains("lib: translated in", log1);
        var (w, r2, log2) = Run(exe, cache);
        Assert.True(r2.Failure is null, r2.Failure + "\n" + log2);
        Assert.Contains("lib: translation cache hit", log2);
        Assert.NotEqual(0u, w[0]);
    }

    [Fact]
    public void ExportsOfOneLoadedDllAreCalledFromTwoGuestThreads()
    {
        File.WriteAllBytes(Path.Combine(dir, "lib.dll"), BuildDll());
        var e = new Exe();
        // Thread routine at code offset 0: for (i = 0; i < 1000; i++) acc = addc(acc, 1); return acc.
        e.Raw(0x8B, 0x74, 0x24, 0x04, 0x31, 0xFF, 0xBB, 0xE8, 0x03, 0x00, 0x00,
              0x6A, 0x01, 0x57, 0xFF, 0xD6, 0x83, 0xC4, 0x08, 0x89, 0xC7, 0x4B, 0x75, 0xF3,
              0x89, 0xF8, 0xC2, 0x04, 0x00);
        var entry = e.Here;
        e.PushData(Exe.LibName); e.Call("LoadLibraryA"); e.Store(0x40);
        e.PushData(Exe.Addc); e.PushMem(0x40); e.Call("GetProcAddress"); e.Store(0x80);
        void Spawn(uint handleAt)
        {
            e.PushData(0x90); e.PushImm8(0); e.PushMem(0x80); e.PushCode(0); e.PushImm8(0); e.PushImm8(0); e.Call("CreateThread"); e.Store(handleAt);
        }
        Spawn(0x88); Spawn(0x8C);
        foreach (var (handleAt, result) in new[] { (0x88u, 0u), (0x8Cu, 4u) })
        {
            e.PushImm8(-1); e.PushMem(handleAt); e.Call("WaitForSingleObject");
            e.PushData(result); e.PushMem(handleAt); e.Call("GetExitCodeThread");
        }
        e.FinishWith(2);
        var (w, r, log) = Run(e.Build(entry));
        Assert.True(r.Failure is null, r.Failure + "\n" + log);
        Assert.Equal([1000u, 1000u], w);
    }
}
