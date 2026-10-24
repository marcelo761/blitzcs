using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>Implicit TLS: per-thread data copy, TLS index, ThreadLocalStoragePointer at fs:[0x2C] and TLS callbacks.</summary>
[Collection("GuestRuntime")]
public sealed class TebTlsTests
{
    private const uint Data = PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva;
    private const uint Log = PeFixtureBuilder.ImageBase + PeFixtureBuilder.IdataRva + 0x400;
    private const uint TemplateOff = 0x100, IndexOff = 0x20, CallbacksOff = 0x30;

    private sealed record Fixture(byte[] Pe, int ReadOffset, int IncOffset);

    /// <summary>
    /// _read(off): value of the thread's TLS data at byte offset off (the MSVC access sequence: [tls_index] -> fs:[2Ch][index*4] -> +off).
    /// _inc(off): adds 1 to that dword and returns the new value. A TLS callback logs its reason through the host import.
    /// </summary>
    private static Fixture Build(bool callbacks = true, uint indexAddress = Data + IndexOff, uint zeroFill = 0x20, bool withTls = true)
    {
        var read = Asm(a =>
        {
            a.mov(eax, __dword_ptr[Data + IndexOff]); a.mov(ecx, __dword_ptr.fs[0x2C]); a.mov(ecx, __dword_ptr[ecx + eax * 4]);
            a.mov(eax, __dword_ptr[esp + 4]); a.mov(eax, __dword_ptr[ecx + eax]); a.ret();
        });
        var inc = Asm(a =>
        {
            a.mov(eax, __dword_ptr[Data + IndexOff]); a.mov(ecx, __dword_ptr.fs[0x2C]); a.mov(ecx, __dword_ptr[ecx + eax * 4]);
            a.mov(eax, __dword_ptr[esp + 4]); a.add(ecx, eax); a.inc(__dword_ptr[ecx]); a.mov(eax, __dword_ptr[ecx]); a.ret();
        });
        var callback = Asm(a => { a.push(__dword_ptr[esp + 8]); a.call(__dword_ptr[Log]); a.add(esp, 4); a.ret(12); });
        var code = new List<byte>(read);
        var incAt = code.Count; code.AddRange(inc);
        var cbAt = code.Count; code.AddRange(callback);

        var data = new byte[0x400];
        void W(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v);
        const uint templateLength = 16;
        W(0x00, Data + TemplateOff); W(0x04, Data + TemplateOff + templateLength); W(0x08, indexAddress);
        W(0x0C, callbacks ? Data + CallbacksOff : 0); W(0x10, zeroFill); W(0x14, 0);
        W((int)IndexOff, 0x55);                                   // the loader must overwrite this with the real index
        W((int)CallbacksOff, PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + (uint)cbAt); W((int)CallbacksOff + 4, 0);
        for (var i = 0; i < 4; i++) W((int)TemplateOff + 4 * i, 0x11111111u * (uint)(i + 1));

        var fx = new PeFixtureBuilder().WithCode([.. code]).WithData(data).Export("_read", 0).Export("_inc", (uint)incAt);
        fx.Import("HOST.DLL", "log");
        var pe = fx.Build();
        if (withTls)
        {
            // data directory 9 (TLS) -> the directory at the start of .data
            var opt = 0x40 + 4 + 20;
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 96 + 9 * 8), PeFixtureBuilder.DataRva);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 96 + 9 * 8 + 4), 24);
        }
        return new Fixture(pe, 0, incAt);
    }

    private static (GuestModule Module, List<uint> Log) Load(Fixture f, OptimizationProfile profile, bool runEntry = true)
    {
        var image = PeReader.Read(f.Pe);
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "tls-test");
        Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(art.Diagnostics, d => d.Code == "TLS_HANDLED");
        var log = new List<uint>();
        var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "log", GuestImportBuilder.Cdecl(reason => { lock (log) log.Add(reason); return 0; }));
        return (GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = runEntry }), log);
    }

    [Fact]
    public void ThreadLocalDataIsACopyOfTheTemplateFollowedByZeroFill()
    {
        foreach (var profile in Profiles)
        {
            var (m, _) = Load(Build(), profile);
            using (m)
            {
                var read = m.Bind("_read", GuestCallingConvention.Cdecl, 1);
                for (var i = 0u; i < 4; i++) Assert.Equal(0x11111111u * (i + 1), read.Call(4 * i));
                for (var off = 16u; off < 16 + 0x20; off += 4) Assert.Equal(0u, read.Call(off));            // SizeOfZeroFill
            }
        }
    }

    [Fact]
    public void TheLoaderWritesTheTlsIndexAndPublishesThePointerAtFs2C()
    {
        var (m, _) = Load(Build(), OptimizationProfile.Release);
        using (m)
        {
            Assert.Equal(0u, m.Memory.ReadUInt32(Data + IndexOff));                        // 0x55 was replaced by the real index
            m.Bind("_read", GuestCallingConvention.Cdecl, 1).Call(0);
            var teb = m.CurrentThreadTeb;
            var array = m.Memory.ReadUInt32(teb + 0x2C);
            Assert.NotEqual(0u, array);
            var data = m.Memory.ReadUInt32(array);                                         // slot [index]
            Assert.Equal(0x11111111u, m.Memory.ReadUInt32(data));
            Assert.Equal(0x44444444u, m.Memory.ReadUInt32(data + 12));
        }
    }

    [Fact]
    public void EachThreadHasItsOwnTlsDataThatPersistsAcrossCalls()
    {
        foreach (var profile in Profiles)
        {
            var (m, _) = Load(Build(), profile);
            using (m)
            {
                var inc = m.Bind("_inc", GuestCallingConvention.Cdecl, 1);
                Assert.Equal(0x11111112u, inc.Call(0));
                Assert.Equal(0x11111113u, inc.Call(0));
                uint other = 0, otherAgain = 0;
                var t = new Thread(() => { other = inc.Call(0); otherAgain = inc.Call(0); }); t.Start(); t.Join();
                Assert.Equal(0x11111112u, other);                                          // a new thread starts from the template
                Assert.Equal(0x11111113u, otherAgain);
                Assert.Equal(0x11111114u, inc.Call(0));                                     // this thread's copy was not disturbed
            }
        }
    }

    [Fact]
    public void ParallelThreadsKeepIndependentTlsCounters()
    {
        var (m, _) = Load(Build(), OptimizationProfile.Release);
        using (m)
        {
            var inc = m.Bind("_inc", GuestCallingConvention.Cdecl, 1);
            var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                for (uint k = 1; k <= 2000; k++) { var v = inc.Call(4); if (v != 0x22222222u + k) { bad.Add($"thread {t}: {v:X} at {k}"); return; } }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            Assert.Empty(bad);
        }
    }

    [Fact]
    public void TlsCallbacksReceiveProcessAndThreadNotifications()
    {
        foreach (var profile in Profiles)
        {
            var (m, log) = Load(Build(), profile);
            Assert.Equal([1u], log);                                                        // DLL_PROCESS_ATTACH at load
            var read = m.Bind("_read", GuestCallingConvention.Cdecl, 1);
            read.Call(0);
            Assert.Equal([1u], log);                                                        // the loading thread is not a new thread
            var t = new Thread(() => read.Call(0)); t.Start(); t.Join();
            Assert.Equal([1u, 2u], log);                                                    // DLL_THREAD_ATTACH on first use by a new thread
            var t2 = new Thread(() => { read.Call(0); read.Call(0); }); t2.Start(); t2.Join();
            Assert.Equal([1u, 2u, 2u], log);                                                // once per thread
            m.Dispose();
            Assert.Equal([1u, 2u, 2u, 3u, 0u], log);                                        // THREAD_DETACH (disposing thread) then PROCESS_DETACH
            Assert.Null(m.DetachError);
        }
    }

    [Fact]
    public void WithoutRunEntryPointNoCallbacksRunButTlsStillWorks()
    {
        var (m, log) = Load(Build(), OptimizationProfile.Release, runEntry: false);
        using (m)
        {
            Assert.Equal(0x33333333u, m.Bind("_read", GuestCallingConvention.Cdecl, 1).Call(8));
            Assert.Empty(log);
        }
    }

    [Fact]
    public void ImageWithoutCallbacksOrWithoutTlsKeepsFs2CAccordingToTheContract()
    {
        var (m, log) = Load(Build(callbacks: false), OptimizationProfile.Release);
        using (m) { Assert.Equal(0x11111111u, m.Bind("_read", GuestCallingConvention.Cdecl, 1).Call(0)); Assert.Empty(log); }
        var plain = Translate(Asm(a => { a.mov(eax, __dword_ptr.fs[0x2C]); a.ret(); }), OptimizationProfile.Release);
        Assert.DoesNotContain(plain.Artifact.Diagnostics, d => d.Code == "TLS_HANDLED");
        using var pm = GuestModule.Load(plain.Artifact, plain.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        Assert.Equal(0u, pm.Bind("_f", GuestCallingConvention.Cdecl, 0).Call());
    }

    [Fact]
    public void UnsupportedTlsFormsAreExplicitTranslationErrors()
    {
        foreach (var f in new[] { Build(indexAddress: 0x10), Build(indexAddress: Data + IndexOff + 1) })
        {
            var image = PeReader.Read(f.Pe);
            var art = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "tls-bad");
            var d = Assert.Single(art.Diagnostics, x => x.Code == "BC_TLS_UNSUPPORTED");
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Contains("TLS index", d.Message);
            Assert.Throws<GuestLoadException>(() => GuestModule.Load(art, image, new DictionaryGuestImportResolver().Add("HOST.DLL", "log", GuestImportBuilder.Cdecl(_ => 0u)), new GuestModuleOptions()));
        }
        var huge = Build(zeroFill: 0x200000);
        var art2 = X64Translator.Translate(PeReader.Read(huge.Pe), new BinaryCompilerOptions(OptimizationProfile.Release), "tls-big");
        Assert.Contains(art2.Diagnostics, x => x.Code == "BC_TLS_UNSUPPORTED" && x.Severity == DiagnosticSeverity.Error);
    }
}
