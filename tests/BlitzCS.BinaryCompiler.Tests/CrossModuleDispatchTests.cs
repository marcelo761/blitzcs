using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// Guest code of one module calling, jumping and returning into the code of another module of the same set (docs/binary-multimodule.md, "Cross-module dispatch").
/// P is a provider: a vtable-like table of function pointers in its data, a function that calls a pointer it is given, and small leaf functions.
/// Q imports P and calls through the table, hands its own code address back to P (callback), and checks flags around the foreign dispatch.
/// Z only calls pointers it is given (and can block inside a host import).
/// </summary>
[Collection("GuestRuntime")]
public sealed class CrossModuleDispatchTests
{
    private const uint Data = PeFixtureBuilder.DataRva;
    private static uint Va(uint rva) => PeFixtureBuilder.ImageBase + rva;

    // P: vtable at Data+0: [add1, dbl, flag, self, inner].
    private static PeImage LibP()
    {
        var a = new Asm32();
        a.Func(); a.Raw(0x90).Ret();                                                                    // padding function (Q and Z get others), so that no two modules line up RVA for RVA
        var add1 = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x40).Ret();                               // eax = x + 1
        var dbl = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x01, 0xC0).Ret();                          // eax = 2x
        var flag = a.Func(); a.Raw(0xBA, 5, 0, 0, 0).Ret();                                            // edx = 5, touches no flag
        var self = a.Func(); a.Raw(0x64, 0xA1, 0x18, 0, 0, 0).Ret();                                   // eax = fs:[18h] (the TEB address)
        var inner = a.Func(); a.Raw(0xA1).Abs(Data).Raw(0xFF, 0x74, 0x24, 0x04, 0xFF, 0xD0, 0x83, 0xC4, 0x04).Ret();   // eax = vt[0](x): an indirect call that stays inside P and is dispatched through P's own table
        var getvt = a.Func(); a.Raw(0xB8).Abs(Data).Ret();
        var callcb = a.Func(); a.Raw(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x54, 0x24, 0x08, 0x83, 0xC4, 0x04).Ret();   // push x; call cb; add esp,4
        var useflag = a.Func(); a.Raw(0x83, 0xD0, 0x00).Ret();                                         // adc eax,0: reads CF, so every dispatch of this image must preserve flags
        var tlsSet = a.Func(); a.Raw(0x64, 0xC7, 0x05, 0x10, 0x0E, 0, 0, 0x78, 0x56, 0x34, 0x12).Ret();   // fs:[E10h] = 0x12345678 (TLS slot 0)
        var tlsGet = a.Func(); a.Raw(0x64, 0xA1, 0x10, 0x0E, 0, 0).Ret();
        var main = a.Func(); a.MovEaxImm(1).Ret(12);
        var data = new byte[0x100];
        void W(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v);
        W(0, Va(PeFixtureBuilder.TextRva + add1)); W(4, Va(PeFixtureBuilder.TextRva + dbl)); W(8, Va(PeFixtureBuilder.TextRva + flag)); W(12, Va(PeFixtureBuilder.TextRva + self)); W(16, Va(PeFixtureBuilder.TextRva + inner));
        var fx = new PeFixtureBuilder().WithCode(a.Bytes).WithData(data).EntryPoint(main)
            .Export("getvt", getvt).Export("callcb", callcb).Export("useflag", useflag).Export("p_tls_set", tlsSet).Export("p_tls_get", tlsGet);
        foreach (var r in a.Relocs) fx.Relocate(r);
        for (var i = 0u; i < 5; i++) fx.Relocate(Data + 4 * i);
        return PeReader.Read(fx.Build());
    }

    private static PeImage LibQ()
    {
        var a = new Asm32();
        var fx = new PeFixtureBuilder().WithData(new byte[0x100]);
        var getvt = fx.Import("p.dll", "getvt"); var callcb = fx.Import("p.dll", "callcb");
        var init = a.Func(); a.CallIat(getvt).Raw(0x8B, 0x40, 0x08).StoreEaxAbs(Data).Ret();           // [Data] = vtable[2] (the flag-neutral function)
        var flags = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x3B, 0x44, 0x24, 0x08, 0xFF, 0x15).Abs(Data).Raw(0x9C, 0x58, 0x25, 0xD5, 0x08, 0, 0).Ret();
        var flagsRef = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x3B, 0x44, 0x24, 0x08, 0x9C, 0x58, 0x25, 0xD5, 0x08, 0, 0).Ret();
        var callvt = a.Func(); a.CallIat(getvt).Raw(0x8B, 0x4C, 0x24, 0x04, 0xFF, 0x74, 0x24, 0x08, 0xFF, 0x14, 0x88, 0x83, 0xC4, 0x04).Ret();   // vt[i](x)
        var cb = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x05, 0x64, 0, 0, 0).Ret();                    // eax = x + 100
        var callcbQ = a.Func(); a.Raw(0xFF, 0x74, 0x24, 0x04, 0x68).Abs(PeFixtureBuilder.TextRva + cb).CallIat(callcb).AddEsp(8).Ret();   // P.callcb(&cb, x)
        var callptr = a.Func(); a.Raw(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x54, 0x24, 0x08, 0x83, 0xC4, 0x04).Ret();
        var selfQ = a.Func(); a.Raw(0x64, 0xA1, 0x18, 0, 0, 0).Ret();
        var tlsGetQ = a.Func(); a.Raw(0x64, 0xA1, 0x10, 0x0E, 0, 0).Ret();
        var main = a.Func(); a.MovEaxImm(1).Ret(12);
        fx.WithCode(a.Bytes).EntryPoint(main)
            .Export("q_init", init).Export("q_flags", flags).Export("q_flags_ref", flagsRef).Export("q_callvt", callvt).Export("q_callcb", callcbQ)
            .Export("q_callptr", callptr).Export("q_self", selfQ).Export("q_tls_get", tlsGetQ);
        foreach (var r in a.Relocs) fx.Relocate(r);
        return PeReader.Read(fx.Build());
    }

    private static PeImage LibZ(bool withBlock)
    {
        var a = new Asm32();
        var fx = new PeFixtureBuilder().WithData(new byte[0x100]);
        a.Func(); a.Raw(0x40).Ret();                                 // same-sized pad as P's: Z.callptr then sits at the RVA of P.add1 with different code, so a stale dispatch table (r13) of Z would jump into the wrong code instead of missing
        var callptr = a.Func(); a.Raw(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x54, 0x24, 0x08, 0x83, 0xC4, 0x04).Ret();
        uint block = 0;
        if (withBlock) { var slot = fx.Import("host.dll", "block"); block = a.Func(); a.CallIat(slot).Ret(); }
        var main = a.Func(); a.MovEaxImm(1).Ret(12);
        fx.WithCode(a.Bytes).EntryPoint(main).Export("z_callptr", callptr);
        if (withBlock) fx.Export("z_block", block);
        foreach (var r in a.Relocs) fx.Relocate(r);
        return PeReader.Read(fx.Build());
    }

    private sealed record Built(PeImage Image, TranslationArtifact Artifact, uint Base);

    private static List<Built> Translate(OptimizationProfile profile, params (string Name, PeImage Image)[] mods)
    {
        var plan = ModuleBaseAllocator.Allocate(mods.Select(m => (m.Name, m.Image)).ToList(), HostPlatform.AllocatorReserved());
        var list = new List<Built>();
        for (var i = 0; i < mods.Length; i++)
        {
            var img = mods[i].Image.Rebase(plan[i].Base);
            var art = X64Translator.Translate(mods[i].Image, new BinaryCompilerOptions(profile, LoadBase: plan[i].Base), "x" + i);
            Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            list.Add(new Built(img, art, plan[i].Base));
        }
        return list;
    }

    private static GuestModuleSet NewSet(OptimizationProfile profile, out List<Built> built, IGuestImportResolver? host = null, bool block = false)
    {
        built = Translate(profile, ("p", LibP()), ("q", LibQ()), ("z", LibZ(block)));
        var set = new GuestModuleSet(host);
        set.Add("p.dll", built[0].Artifact, built[0].Image, ExportConventions.AllCdecl)
           .Add("q.dll", built[1].Artifact, built[1].Image, ExportConventions.AllCdecl)
           .Add("z.dll", built[2].Artifact, built[2].Image, ExportConventions.AllCdecl);
        set.LoadAll();
        return set;
    }

    private static GuestFunction Fn(GuestModuleSet set, string module, string export, int args) => set[module].Bind(export, GuestCallingConvention.Cdecl, args);

    public static IEnumerable<object[]> Profiles() => [[OptimizationProfile.Debug], [OptimizationProfile.Release]];

    [Theory, MemberData(nameof(Profiles))]
    public void CallThroughAFunctionPointerTableInAnotherModule(OptimizationProfile profile)
    {
        using var set = NewSet(profile, out var built);
        var callvt = Fn(set, "q", "q_callvt", 2);
        Assert.Equal(42u, callvt.Call(0, 41));          // P.add1 reached by `call [eax+ecx*4]` in Q
        Assert.Equal(42u, callvt.Call(1, 21));          // P.dbl
        // the returned pointer really lies in P, not in Q
        var vt = Fn(set, "p", "getvt", 0).Call();
        Assert.True(vt >= built[0].Base && vt < built[0].Base + built[0].Image.SizeOfImage);
        Assert.Equal(7u, Fn(set, "z", "z_callptr", 2).Call(ReadVt(set, "p", 0), 6));   // a third module jumps into P too
        // Entered by a jump from Z, P's own indirect call and return must use P's code base and dispatch table (not Z's, which the jump left in r12/r13).
        Assert.Equal(10u, Fn(set, "z", "z_callptr", 2).Call(ReadVt(set, "p", 4), 9));
    }

    private static uint ReadVt(GuestModuleSet set, string module, int index) => set[module].Memory.ReadUInt32(Fn(set, module, "getvt", 0).Call() + 4u * (uint)index);

    [Theory, MemberData(nameof(Profiles))]
    public void CallbackIntoTheCallersModuleReturnsToTheCallee(OptimizationProfile profile)
    {
        using var set = NewSet(profile, out _);
        // Q hands the address of its own cb to P.callcb, which calls it (P -> Q jump) and cb's ret goes back into P (Q -> P return).
        Assert.Equal(107u, Fn(set, "q", "q_callcb", 1).Call(7));
        Assert.Equal(100u, Fn(set, "q", "q_callcb", 1).Call(0));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void FunctionPointersPassedBackAndForthUnderTwoGuestThreads(OptimizationProfile profile)
    {
        using var set = NewSet(profile, out _);
        var callvt = Fn(set, "q", "q_callvt", 2); var callcb = Fn(set, "q", "q_callcb", 1);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var threads = Enumerable.Range(0, 2).Select(t => new Thread(() =>
        {
            try
            {
                for (var i = 0u; i < 4000; i++)
                {
                    if (callvt.Call(i & 1, i) != ((i & 1) == 0 ? i + 1 : 2 * i)) failures.Add($"t{t} vt {i}");
                    if (callcb.Call(i) != i + 100) failures.Add($"t{t} cb {i}");
                }
            }
            catch (GuestException e) { failures.Add($"t{t}: {e.Message}"); }
        })).ToList();
        threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
        Assert.Empty(failures);
    }

    [Theory, MemberData(nameof(Profiles))]
    public void FlagsAreIdenticalAcrossTheForeignCallAndReturn(OptimizationProfile profile)
    {
        using var set = NewSet(profile, out _);
        Fn(set, "q", "q_init", 0).Call();
        var flags = Fn(set, "q", "q_flags", 2); var reference = Fn(set, "q", "q_flags_ref", 2);
        var seen = new HashSet<uint>();
        foreach (var (x, y) in new (uint, uint)[] { (1, 2), (2, 1), (5, 5), (0, 0xFFFFFFFF), (0x80000000, 1), (0x7FFFFFFF, 0xFFFFFFFF), (0x10, 0x11), (3, 0x100) })
        {
            var expected = reference.Call(x, y);
            seen.Add(expected);
            Assert.Equal(expected, flags.Call(x, y));       // cmp; call into P (and its ret back to Q); pushfd -> the same CF/PF/AF/ZF/SF/OF
        }
        Assert.True(seen.Count >= 4);                       // the inputs really exercise different flag combinations
    }

    [Theory, MemberData(nameof(Profiles))]
    public void AnUnknownForeignTargetStillTrapsWithKindOne(OptimizationProfile profile)
    {
        using var set = NewSet(profile, out var built);
        var callptr = Fn(set, "z", "z_callptr", 2);
        var outside = Assert.Throws<GuestTrapException>(() => callptr.Call(0x7FFF1234, 1));
        Assert.Contains("0x7FFF1234", outside.Message); Assert.Contains("untranslated code", outside.Message);
        var data = Fn(set, "p", "getvt", 0).Call();                                    // inside P's range but not translated code
        var inData = Assert.Throws<GuestTrapException>(() => callptr.Call(data, 1));
        Assert.Contains($"0x{data:X8}", inData.Message); Assert.Contains("untranslated code", inData.Message);
        Assert.Equal(42u, Fn(set, "q", "q_callvt", 2).Call(0, 41));                    // the set is still usable afterwards
    }

    [Fact]
    public void AModuleOutsideTheSetIsNotReachable_MutationWitness()
    {
        // Without registry membership the very same call traps: this is what proves the positive tests exercise the registry and not some accident.
        var built = Translate(OptimizationProfile.Release, ("p", LibP()), ("z", LibZ(false)), ("p2", LibP()));
        using var set = new GuestModuleSet();
        set.Add("p.dll", built[0].Artifact, built[0].Image, ExportConventions.AllCdecl).Add("z.dll", built[1].Artifact, built[1].Image, ExportConventions.AllCdecl);
        set.LoadAll();
        using var lonely = GuestModule.Load(built[2].Artifact, built[2].Image, new DictionaryGuestImportResolver());
        var pointer = lonely.Memory.ReadUInt32(lonely.Bind("getvt", GuestCallingConvention.Cdecl, 0).Call());   // P2.add1
        var ex = Assert.Throws<GuestTrapException>(() => Fn(set, "z", "z_callptr", 2).Call(pointer, 1));
        Assert.Contains("untranslated code", ex.Message);
    }

    [Fact]
    public void LateRegistryTargetIsReachableAfterEarlierAndRetiredRecords()
    {
        const int fillers = 15;
        var modules = Enumerable.Range(0, fillers).Select(i => ($"p{i:D2}", LibP())).ToList();
        modules.Add(("target", LibP()));
        modules.Add(("target2", LibP()));
        modules.Add(("z", LibZ(false)));
        var built = Translate(OptimizationProfile.Release, [.. modules]);
        using var set = new GuestModuleSet();
        for (var i = 0; i < fillers; i++)
            set.Add($"p{i:D2}.dll", built[i].Artifact, built[i].Image, ExportConventions.AllCdecl);
        set.Add("target.dll", built[fillers].Artifact, built[fillers].Image, ExportConventions.AllCdecl);
        set.Add("target2.dll", built[fillers + 1].Artifact, built[fillers + 1].Image, ExportConventions.AllCdecl);
        set.Add("z.dll", built[fillers + 2].Artifact, built[fillers + 2].Image, ExportConventions.AllCdecl);
        set.LoadAll();

        var callptr = Fn(set, "z", "z_callptr", 2);
        var context = set["z"].HostImageBase + (nint)built[fillers + 2].Artifact.ContextOffset;
        var cachedRecord = () => Marshal.ReadIntPtr(context + (nint)ModuleRegistryLayout.CachedRecordCellOffset);
        var retiredTarget = set["p00"].Bind("getvt", GuestCallingConvention.Cdecl, 0).Call();
        var retiredPointer = set["p00"].Memory.ReadUInt32(retiredTarget);
        var target = set["target"].Bind("getvt", GuestCallingConvention.Cdecl, 0).Call();
        var pointer = set["target"].Memory.ReadUInt32(target);
        var target2 = set["target2"].Bind("getvt", GuestCallingConvention.Cdecl, 0).Call();
        var pointer2 = set["target2"].Memory.ReadUInt32(target2);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var callers = new[] { retiredPointer, pointer, pointer2 }.Select(address => new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 512; i++)
                    if (callptr.Call(address, (uint)i) != (uint)i + 1) failures.Add($"0x{address:X8} call {i}");
            }
            catch (Exception e) { failures.Add(e.ToString()); }
        })).ToArray();
        foreach (var thread in callers) thread.Start();
        foreach (var thread in callers) thread.Join();
        Assert.Empty(failures);

        // Alternate two live records at the end of the registry. Each target change misses the per-module MRU cell,
        // so both paths must continue resolving correctly through the registry fallback.
        for (uint i = 0; i < 128; i++)
            Assert.Equal(i + 1, callptr.Call((i & 1) == 0 ? pointer : pointer2, i));

        Assert.Equal(42u, callptr.Call(retiredPointer, 41)); // leave p00 in the cache before retiring it
        var beforeRetirement = cachedRecord();
        Assert.NotEqual(nint.Zero, beforeRetirement);

        set.Unload("p00"); // its stable registry slot remains before the live target
        var retired = Assert.Throws<GuestTrapException>(() => callptr.Call(retiredPointer, 42));
        Assert.Contains("'p00.dll'", retired.Message);
        Assert.Contains("unloaded", retired.Message);
        Assert.Equal(beforeRetirement, cachedRecord());

        Assert.Equal(43u, callptr.Call(pointer, 42));
        Assert.NotEqual(beforeRetirement, cachedRecord()); // stale cache was revalidated and replaced by the late live record
        Assert.Equal(44u, callptr.Call(pointer2, 43));

        var data = set["target"].Bind("getvt", GuestCallingConvention.Cdecl, 0).Call();
        var ex = Assert.Throws<GuestTrapException>(() => callptr.Call(data, 1));
        Assert.Contains($"0x{data:X8}", ex.Message);
        Assert.Contains("untranslated code", ex.Message);

        // Removing and adding the same artifact leaves the retired record inert and publishes a fresh live record.
        // The old guest address must resolve to the retranslated module rather than the earlier retired slot.
        set.Add("p00.dll", built[0].Artifact, built[0].Image, ExportConventions.AllCdecl);
        set.LoadAll();
        var retranslatedTarget = set["p00"].Bind("getvt", GuestCallingConvention.Cdecl, 0).Call();
        var retranslatedPointer = set["p00"].Memory.ReadUInt32(retranslatedTarget);
        Assert.Equal(retiredPointer, retranslatedPointer);
        Assert.Equal(45u, callptr.Call(retranslatedPointer, 44));
    }

    [Theory, MemberData(nameof(Profiles))]
    public void CallingIntoAnUnloadedModuleFailsWithTheModulesName(OptimizationProfile profile)
    {
        using var set = NewSet(profile, out _);
        var pointer = ReadVt(set, "p", 0);
        Assert.Equal(6u, Fn(set, "z", "z_callptr", 2).Call(pointer, 5));
        set.Unload("q");
        set.Unload("p");                                                                // idle: allowed
        var ex = Assert.Throws<GuestTrapException>(() => Fn(set, "z", "z_callptr", 2).Call(pointer, 5));
        Assert.Contains("'p.dll'", ex.Message); Assert.Contains("unloaded", ex.Message);
    }

    [Fact]
    public void AModuleIsNotUnloadedWhileAnotherThreadRunsGuestCode()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var host = new DictionaryGuestImportResolver().Add("host.dll", "block", GuestImportBuilder.Cdecl(() => { entered.Set(); release.Wait(); return 0; }));
        using var set = NewSet(OptimizationProfile.Release, out _, host, block: true);
        var worker = new Thread(() => Fn(set, "z", "z_block", 0).Call());
        worker.Start();
        try
        {
            Assert.True(entered.Wait(10000));
            var ex = Assert.Throws<InvalidOperationException>(() => set.Unload("q"));   // any module: the busy thread may be inside it without call accounting
            Assert.Contains("other guest thread", ex.Message);
            Assert.Contains("other guest thread", Assert.Throws<InvalidOperationException>(() => set.Unload("z")).Message);
            Assert.False(set["q"].IsDisposed);
        }
        finally { release.Set(); worker.Join(); }
        set.Unload("q"); set.Unload("p");                                               // idle again
        Assert.True(set.Contains("z"));
    }

    [Fact]
    public void ModulesOfASetShareOneTebPerThread()
    {
        using var set = NewSet(OptimizationProfile.Release, out _);
        var teb = Fn(set, "q", "q_self", 0).Call();
        Assert.Equal(teb, set["q"].CurrentThreadTeb); Assert.Equal(teb, set["p"].CurrentThreadTeb); Assert.Equal(teb, set["z"].CurrentThreadTeb);
        Assert.Equal(teb, ReadAndCall(set, 3));                      // code of P reads the very same fs:[18h] when entered by a jump from Q
        Fn(set, "p", "p_tls_set", 0).Call();                          // P writes TLS slot 0 ...
        Assert.Equal(0x12345678u, Fn(set, "q", "q_tls_get", 0).Call());   // ... Q reads it
        uint other = 0;
        var t = new Thread(() => other = Fn(set, "q", "q_self", 0).Call()); t.Start(); t.Join();
        Assert.NotEqual(teb, other);                                 // a different thread has a different TEB
        var t2 = new Thread(() => other = Fn(set, "q", "q_tls_get", 0).Call()); t2.Start(); t2.Join();
        Assert.Equal(0u, other);
    }

    private static uint ReadAndCall(GuestModuleSet set, int vtIndex) => Fn(set, "z", "z_callptr", 2).Call(ReadVt(set, "p", vtIndex), 0);

    [Fact]
    public void ModulesTranslatedBeforeCrossModuleDispatchAreRejectedWithAClearMessage()
    {
        var built = Translate(OptimizationProfile.Release, ("p", LibP()));
        var old = built[0].Artifact with { FormatVersion = 4 };
        var ex = Assert.Throws<GuestLoadException>(() => GuestModule.Load(old, built[0].Image, new DictionaryGuestImportResolver()));
        Assert.Contains("format version 4", ex.Message); Assert.Contains("Re-translate", ex.Message);
        var bytes = new ArtifactSerializer().Serialize(built[0].Artifact);
        BitConverter.GetBytes(4).CopyTo(bytes, 4);                  // forge the on-disk version (checksum no longer matches, so re-seal it)
        var body = bytes[..^32];
        System.Security.Cryptography.SHA256.HashData(body).CopyTo(bytes, bytes.Length - 32);
        Assert.Contains("cross-module dispatch", Assert.Throws<ArtifactFormatException>(() => new ArtifactSerializer().Deserialize(bytes)).Message);
    }
}
