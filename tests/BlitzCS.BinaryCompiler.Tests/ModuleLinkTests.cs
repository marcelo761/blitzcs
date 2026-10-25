using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// Cross-module linking on controlled hand-assembled libraries: B (callee: cdecl/stdcall/64-bit/x87 exports, a DllMain counter), A (imports B),
/// C (imports A). All three prefer 0x10000000; the allocator moves two of them.
/// </summary>
[Collection("GuestRuntime")]
public sealed class ModuleLinkTests
{
    private const uint Data = PeFixtureBuilder.DataRva;

    private static PeImage LibB()
    {
        var a = new Asm32();
        var add3 = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0x03, 0x44, 0x24, 0x0C).Ret();
        var mul64 = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0xF7, 0x64, 0x24, 0x08).Ret();                 // edx:eax = a*b
        var fdiv = a.Func(); a.Raw(0xDB, 0x44, 0x24, 0x04, 0xDA, 0x74, 0x24, 0x08).Ret();                  // st0 = (double)a / b
        var sub = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x2B, 0x44, 0x24, 0x08).Ret(8);                  // stdcall(8): a-b
        var twice = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x01, 0xC0).Ret(4);                            // stdcall(4): 2a (undecorated name)
        var cnt = a.Func(); a.LoadEaxAbs(Data).Raw(0x40).StoreEaxAbs(Data).Ret();                            // ++counter (own data)
        var sum6 = a.Func(); a.Raw(0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0x03, 0x44, 0x24, 0x0C, 0x03, 0x44, 0x24, 0x10, 0x03, 0x44, 0x24, 0x14, 0x03, 0x44, 0x24, 0x18).Ret();
        var plain = a.Func(); a.MovEaxImm(7).Ret();
        var main = a.Func(); a.IncAbs(Data).MovEaxImm(1).Ret(12);                                            // DllMain: counter = 1
        var fx = new PeFixtureBuilder().WithCode(a.Bytes).WithData(new byte[0x100]).EntryPoint(main)
            .Export("add3", add3).Export("mul64", mul64).Export("fdiv", fdiv).Export("_sub_std@8", sub).Export("twice", twice)
            .Export("cnt", cnt).Export("sum6", sum6).Export("plain", plain);
        foreach (var r in a.Relocs) fx.Relocate(r);
        return PeReader.Read(fx.Build());
    }

    private static readonly string[] AImports = ["add3", "mul64", "fdiv", "_sub_std@8", "twice", "cnt", "sum6"];

    private static PeImage LibA(string target = "b.dll", string[]? imports = null)
    {
        imports ??= AImports;
        var a = new Asm32();
        var fx = new PeFixtureBuilder().WithData(new byte[0x100]);
        var slot = imports.ToDictionary(n => n, n => fx.Import(target, n));
        var f = new Dictionary<string, uint>();
        void Fwd(string name, string import, int nargs, int cleanup)
        {
            if (!slot.TryGetValue(import, out var rva)) return;
            f[name] = a.Func(); a.ForwardArgs(nargs).CallIat(rva); if (cleanup > 0) a.AddEsp((byte)cleanup); a.Ret();
        }
        Fwd("a_add3", "add3", 3, 12); Fwd("a_mul64", "mul64", 2, 8); Fwd("a_fdiv", "fdiv", 2, 8); Fwd("a_sub", "_sub_std@8", 2, 0);
        Fwd("a_twice", "twice", 1, 0); Fwd("a_cnt", "cnt", 0, 0); Fwd("a_sum6", "sum6", 6, 24); Fwd("a_plain", "plain", 0, 0); Fwd("a_nope", "nope", 0, 0);
        f["a_saw"] = a.Func(); a.LoadEaxAbs(Data).Ret();
        var main = a.Func();
        if (slot.TryGetValue("cnt", out var cntSlot)) a.CallIat(cntSlot).StoreEaxAbs(Data);
        a.MovEaxImm(1).Ret(12);                                                                              // DllMain: saw = B.cnt()
        fx.WithCode(a.Bytes).EntryPoint(main);
        foreach (var (n, o) in f) fx.Export(n, o);
        foreach (var r in a.Relocs) fx.Relocate(r);
        return PeReader.Read(fx.Build());
    }

    private static PeImage LibC()
    {
        var a = new Asm32();
        var fx = new PeFixtureBuilder().WithData(new byte[0x100]);
        var slot = fx.Import("a.dll", "a_add3");
        var f = a.Func(); a.ForwardArgs(3).CallIat(slot).AddEsp(12).Raw(0x05, 0xE8, 0x03, 0x00, 0x00).Ret();   // add3 + 1000
        fx.WithCode(a.Bytes).Export("c_f", f);
        foreach (var r in a.Relocs) fx.Relocate(r);
        return PeReader.Read(fx.Build());
    }

    private sealed record Built(PeImage Image, TranslationArtifact Artifact, uint Base);

    private static List<Built> Translate(params (string Name, PeImage Image)[] mods)
    {
        var plan = ModuleBaseAllocator.Allocate(mods.Select(m => (m.Name, m.Image)).ToList(), HostPlatform.AllocatorReserved());
        var list = new List<Built>();
        for (var i = 0; i < mods.Length; i++)
        {
            var img = mods[i].Image.Rebase(plan[i].Base);
            var art = X64Translator.Translate(mods[i].Image, new BinaryCompilerOptions(OptimizationProfile.Release, LoadBase: plan[i].Base), "t" + i);
            Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            list.Add(new Built(img, art, plan[i].Base));
        }
        return list;
    }

    private static readonly ExportConventions BConv = new() { Default = GuestCallingConvention.Cdecl, Exports = new Dictionary<string, ExportSignature> { ["twice"] = ExportSignature.Stdcall(1) } };

    private static GuestModuleSet Set(out List<Built> built, ModuleLinkOptions? link = null)
    {
        built = Translate(("b", LibB()), ("a", LibA()), ("c", LibC()));
        var set = new GuestModuleSet(options: link);
        set.Add("b.dll", built[0].Artifact, built[0].Image, BConv)
           .Add("a.dll", built[1].Artifact, built[1].Image, ExportConventions.AllCdecl)
           .Add("c.dll", built[2].Artifact, built[2].Image);
        return set;
    }

    [Fact]
    public void ThreeModulesAtDifferentBasesCallEachOther_WithEdxAndSt0Preserved()
    {
        using var set = Set(out var built);
        set.LoadAll();
        Assert.Equal(3, built.Select(b => b.Base).Distinct().Count());
        Assert.All(built, b => Assert.Equal(b.Base, set.LoadedModules.First(m => m.ImageBase == b.Base).ImageBase));
        var a = set["a"]; var c = set["c"];
        Assert.Equal(60u, a.Bind("a_add3", GuestCallingConvention.Cdecl, 3).Call(10, 20, 30));
        Assert.Equal(1060u, c.Bind("c_f", GuestCallingConvention.Cdecl, 3).Call(10, 20, 30));          // C -> A -> B
        Assert.Equal(0xFFFF_FFFFUL * 0xFFFF_FFFEUL, a.Bind("a_mul64", GuestCallingConvention.Cdecl, 2).CallUInt64(0xFFFFFFFF, 0xFFFFFFFE));   // EDX:EAX through two modules
        Assert.Equal(22.0 / 7.0, a.Bind("a_fdiv", GuestCallingConvention.Cdecl, 2).CallDouble(22, 7));    // ST0 through two modules
        Assert.Equal(-3.5, a.Bind("a_fdiv", GuestCallingConvention.Cdecl, 2).CallDouble(unchecked((uint)-7), 2));
        Assert.Equal(5u, a.Bind("a_sub", GuestCallingConvention.Cdecl, 2).Call(12, 7));               // stdcall decorated: callee pops 8
        Assert.Equal(84u, a.Bind("a_twice", GuestCallingConvention.Cdecl, 1).Call(42));               // stdcall declared in the map
        Assert.Equal(21u, a.Bind("a_sum6", GuestCallingConvention.Cdecl, 6).Call(1, 2, 3, 4, 5, 6));  // 6 args fit the window
    }

    [Fact]
    public void DependencyDllMainRunsFirst_AndEachModuleHasItsOwnState()
    {
        using var set = Set(out _);
        set.LoadAll();
        Assert.Equal(2u, set["a"].Bind("a_saw", GuestCallingConvention.Cdecl, 0).Call());              // B.DllMain set counter=1, A's DllMain called cnt() -> 2
        Assert.Equal(3u, set["a"].Bind("a_cnt", GuestCallingConvention.Cdecl, 0).Call());
        Assert.Equal(4u, set["b"].Bind("cnt", GuestCallingConvention.Cdecl, 0).Call());                // the same counter, in B's own address space
        Assert.NotEqual(set["a"].PebAddress, set["b"].PebAddress);                                    // own PEB per module
    }

    [Fact]
    public void WindowIsClippedToMappedMemory_AndWideCdeclCallsNeedAWiderWindow()
    {
        using (var set = Set(out _, new ModuleLinkOptions { CdeclWindowDwords = 256 }))
        {
            set.LoadAll();
            // 256 dwords (1 KiB) reach past the top of the 1 MiB-stack frame region only partially; the bridge copies fewer instead of faulting
            Assert.Equal(21u, set["a"].Bind("a_sum6", GuestCallingConvention.Cdecl, 6).Call(1, 2, 3, 4, 5, 6));
        }
        using (var set = Set(out _, new ModuleLinkOptions { CdeclWindowDwords = 4 }))
        {
            set.LoadAll();
            // A window of 4 words cannot carry 6 arguments: documented limitation, the callee sees the caller's stack above the window as zero/garbage
            Assert.NotEqual(21u, set["a"].Bind("a_sum6", GuestCallingConvention.Cdecl, 6).Call(1, 2, 3, 4, 5, 6));
        }
    }

    [Fact]
    public void ConcurrentNestedCallsOnManyThreads()
    {
        using var set = Set(out _);
        set.LoadAll();
        var addC = set["c"].Bind("c_f", GuestCallingConvention.Cdecl, 3);
        var mul = set["a"].Bind("a_mul64", GuestCallingConvention.Cdecl, 2);
        var div = set["a"].Bind("a_fdiv", GuestCallingConvention.Cdecl, 2);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            var rnd = new Random(t);
            try
            {
            for (var i = 0; i < 3000; i++)
            {
                uint x = (uint)rnd.Next(), y = (uint)rnd.Next(), z = (uint)rnd.Next();
                if (addC.Call(x, y, z) != unchecked(x + y + z + 1000)) failures.Add($"t{t} add");
                if (mul.CallUInt64(x, y) != (ulong)x * y) failures.Add($"t{t} mul");
                var d = (int)(y % 1000) + 1;
                if (div.CallDouble(x % 100000, (uint)d) != (double)(x % 100000) / d) failures.Add($"t{t} div");
            }
            }
            catch (GuestException e) { failures.Add($"t{t}: {e.Message}"); }
        })).ToList();
        threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
        Assert.Empty(failures);
    }

    [Fact]
    public void LifetimeDisposeOrderAndLiveDependents()
    {
        var set = Set(out _);
        set.LoadAll();
        var b = set["b"]; var a = set["a"];
        var ex = Assert.Throws<InvalidOperationException>(() => b.Dispose());
        Assert.Contains("live dependent", ex.Message);
        Assert.Throws<InvalidOperationException>(() => set.Unload("b"));
        Assert.Throws<InvalidOperationException>(() => set.Unload("a"));    // C depends on A
        set.Unload("c"); set.Unload("a"); Assert.True(a.IsDisposed); Assert.False(b.IsDisposed);
        set.Dispose();
        Assert.True(b.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => set.LoadAll());
    }

    [Fact]
    public void BadLinksAreExplicitErrors()
    {
        // convention not declared for an undecorated export
        var built = Translate(("b", LibB()), ("a2", LibA("b.dll", ["add3"])));
        using (var set = new GuestModuleSet())
        {
            set.Add("b.dll", built[0].Artifact, built[0].Image).Add("a.dll", built[1].Artifact, built[1].Image);
            var e = Assert.Throws<GuestImportException>(() => set.LoadAll());
            Assert.Contains("not declared", e.Message); Assert.Contains("add3", e.Message);
        }
        // missing export
        var missing = Translate(("b", LibB()), ("a", LibA("b.dll", ["nope"])));
        using (var set = new GuestModuleSet())
        {
            set.Add("b.dll", missing[0].Artifact, missing[0].Image, ExportConventions.AllCdecl).Add("a.dll", missing[1].Artifact, missing[1].Image);
            Assert.Contains("has no such export", Assert.Throws<GuestImportException>(() => set.LoadAll()).Message);
        }
        // contradicting declaration for a decorated name
        var conflict = Translate(("b", LibB()), ("a", LibA("b.dll", ["_sub_std@8"])));
        using (var set = new GuestModuleSet())
        {
            set.Add("b.dll", conflict[0].Artifact, conflict[0].Image, new ExportConventions { Exports = new Dictionary<string, ExportSignature> { ["_sub_std@8"] = ExportSignature.Cdecl } })
               .Add("a.dll", conflict[1].Artifact, conflict[1].Image);
            Assert.Contains("contradicts", Assert.Throws<GuestImportException>(() => set.LoadAll()).Message);
        }
        // non-sibling imports still need the host resolver; unbound => explicit error naming the import, and the earlier module is unloaded again
        var noHost = Translate(("a", LibA("kernel32.dll", ["add3"])));
        using (var set = new GuestModuleSet())
        {
            set.Add("a.dll", noHost[0].Artifact, noHost[0].Image);
            Assert.Contains("kernel32.dll!add3", Assert.Throws<GuestImportException>(() => set.LoadAll()).Message);
        }
        // cycle: A imports B and B imports A
        var cycA = Translate(("a", LibA("b.dll", ["add3"])), ("b", LibA("a.dll", ["add3"])));
        using (var set = new GuestModuleSet())
        {
            set.Add("a.dll", cycA[0].Artifact, cycA[0].Image, ExportConventions.AllCdecl).Add("b.dll", cycA[1].Artifact, cycA[1].Image, ExportConventions.AllCdecl);
            Assert.Contains("Circular", Assert.Throws<GuestImportException>(() => set.LoadAll()).Message);
        }
    }

    [Fact]
    public void ForwardedExportsAreRejectedExplicitly()
    {
        var bytes = (byte[])LibB().Bytes.Clone();
        var img = PeReader.Read(bytes);
        // Point the "plain" export at a string inside the export directory (the PE definition of a forwarder).
        var dir = img.Sections.First(s => s.Name == ".edata");
        var plain = img.Exports.First(e => e.Name == "plain");
        var tableOffset = (int)dir.RawOffset + 40 + 4 * (plain.Ordinal - 1);
        BitConverter.GetBytes(dir.VirtualAddress + 40u + 4u * 8 + 4u * 8 + 2u * 8 + 1).CopyTo(bytes, tableOffset);   // inside the directory (name strings)
        var forwarder = PeReader.Read(bytes);
        Assert.NotNull(forwarder.Exports.First(e => e.Name == "plain").ForwarderTarget);

        var built = Translate(("b", forwarder), ("a", LibA("b.dll", ["plain"])));
        using var set = new GuestModuleSet();
        set.Add("b.dll", built[0].Artifact, built[0].Image, ExportConventions.AllCdecl).Add("a.dll", built[1].Artifact, built[1].Image);
        Assert.Contains("forwards to", Assert.Throws<GuestImportException>(() => set.LoadAll()).Message);
    }

    [Fact]
    public void HostResolverIsConsultedFirst_AndNeedsNoSiblingKnowledge()
    {
        var built = Translate(("b", LibB()), ("a", LibA()));
        var host = new DictionaryGuestImportResolver().Add("b.dll", "cnt", GuestImportBuilder.Cdecl(() => 5000));   // the host shadows one sibling symbol
        using var set = new GuestModuleSet(host);
        set.Add("b.dll", built[0].Artifact, built[0].Image, BConv).Add("a.dll", built[1].Artifact, built[1].Image, ExportConventions.AllCdecl);
        set.LoadAll();
        Assert.Equal(5000u, set["a"].Bind("a_saw", GuestCallingConvention.Cdecl, 0).Call());          // DllMain of A got the host's value
        Assert.Equal(60u, set["a"].Bind("a_add3", GuestCallingConvention.Cdecl, 3).Call(10, 20, 30));
    }

    [Fact]
    public void HostImportsCanReturnEdxAndSt0()
    {
        // the extended import return protocol is available to every managed import, not only the cross-module bridge
        var a = new Asm32();
        var fx = new PeFixtureBuilder().WithData(new byte[0x100]);
        var s64 = fx.Import("host.dll", "get64"); var sf = fx.Import("host.dll", "getf");
        var f64 = a.Func(); a.CallIat(s64).Ret();
        var ff = a.Func(); a.CallIat(sf).Ret();
        fx.WithCode(a.Bytes).Export("f64", f64).Export("ff", ff);
        foreach (var r in a.Relocs) fx.Relocate(r);
        var image = PeReader.Read(fx.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(), "k");
        var resolver = new DictionaryGuestImportResolver()
            .Add("host.dll", "get64", GuestImportBuilder.CdeclCall(call => { call.SetReturnEdx(0xAABBCCDD); return 0x11223344; }))
            .Add("host.dll", "getf", GuestImportBuilder.CdeclCall(call =>
            {
                var raw = new byte[10]; BitConverter.GetBytes(0x8000000000000000UL).CopyTo(raw, 0); BitConverter.GetBytes((ushort)0x3FFF).CopyTo(raw, 8);   // 1.0 in extended precision
                call.SetReturnSt0(raw); return 0;
            }));
        using var m = GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        Assert.Equal(0xAABBCCDD_11223344UL, m.Bind("f64", GuestCallingConvention.Cdecl, 0).CallUInt64());
        Assert.Equal(1.0, m.Bind("ff", GuestCallingConvention.Cdecl, 0).CallDouble());
    }
}
