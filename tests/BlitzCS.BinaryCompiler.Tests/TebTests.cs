using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;
using static BlitzCS.BinaryCompiler.Tests.Support.TranslatorHarness;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>FS/TEB model (docs/binary-teb-contract.md): field table, per-thread blocks, bounds checks, diagnostics.</summary>
[Collection("GuestRuntime")]
public sealed class TebTests
{
    private static readonly uint Hook = PeFixtureBuilder.ImageBase + PeFixtureBuilder.IdataRva + 0x400;

    private static GuestModule Load(Built b, IGuestImportResolver? resolver = null, GuestModuleOptions? options = null)
        => GuestModule.Load(b.Artifact, b.Image, resolver ?? new DictionaryGuestImportResolver(), options ?? new GuestModuleOptions { RunEntryPoint = false });

    /// <summary>Runs export _f under both profiles; <paramref name="check"/> sees each result and the TEB of the calling thread (addresses differ per module, so results must not embed them).</summary>
    private static void RunEach(byte[] code, Action<uint, uint, GuestModule> check, uint[]? args = null)
    {
        foreach (var profile in Profiles)
        {
            using var m = Load(Translate(code, profile));
            check(m.Bind("_f", GuestCallingConvention.Cdecl, args?.Length ?? 0).Call(args ?? []), m.CurrentThreadTeb, m);
        }
    }

    private static List<BinaryDiagnostic> Errors(Built b) => b.Artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    [Fact]
    public void InitialFieldValuesFollowTheContract()
    {
        // Returns Self when every field has its documented initial value, else 0.
        var code = Asm(a =>
        {
            var bad = a.CreateLabel();
            a.cmp(__dword_ptr.fs[0], -1); a.jne(bad);
            a.mov(eax, esp); a.cmp(eax, __dword_ptr.fs[4]); a.jae(bad);
            a.cmp(eax, __dword_ptr.fs[8]); a.jbe(bad);
            a.mov(eax, __dword_ptr.fs[0x18]); a.cmp(__dword_ptr.fs[0x2C], 0); a.jne(bad);
            a.cmp(__dword_ptr.fs[0x34], 0); a.jne(bad);
            a.cmp(__dword_ptr.fs[0x20], 0); a.je(bad);
            a.cmp(__dword_ptr.fs[0x24], 0); a.je(bad);
            a.ret();
            a.Label(ref bad); a.xor(eax, eax); a.ret();
        });
        RunEach(code, (r, teb, m) =>
        {
            Assert.Equal(teb, r);
            Assert.NotEqual(0u, teb); Assert.Equal(0u, teb & 0xFFF);
            Assert.NotEqual(0u, m.PebAddress); Assert.Equal(0u, m.PebAddress & 0xFFF);
        });
    }

    [Fact]
    public void ProcessEnvironmentBlockIsReachableAndModelled()
    {
        // eax = peb; return ImageBase from peb+8 ^ BeingDebugged byte
        var code = Asm(a => { a.mov(eax, __dword_ptr.fs[0x30]); a.movzx(ecx, __byte_ptr[eax + 2]); a.mov(eax, __dword_ptr[eax + 8]); a.or(eax, ecx); a.ret(); });
        Assert.Equal(PeFixtureBuilder.ImageBase, Run(code));
    }

    [Fact]
    public void ClientIdFieldsAreTheProcessAndThreadIds()
    {
        var pid = Run(Asm(a => { a.mov(eax, __dword_ptr.fs[0x20]); a.ret(); }));
        Assert.Equal((uint)Environment.ProcessId, pid);
        var tid = Run(Asm(a => { a.mov(eax, __dword_ptr.fs[0x24]); a.ret(); }));
        Assert.Equal((uint)Environment.CurrentManagedThreadId, tid);
    }

    [Fact]
    public void StackBoundsDescribeTheGuestStack()
    {
        var size = Run(Asm(a =>
        {
            var bad = a.CreateLabel();
            a.mov(eax, __dword_ptr.fs[4]); a.sub(eax, __dword_ptr.fs[8]); a.ret();
            a.Label(ref bad); a.ret();
        }));
        Assert.Equal(1024u * 1024u, size);
    }

    [Fact]
    public void SelfPointerEqualsTheTebAndIsConsistentThroughFlatMemory()
    {
        // fs:[0x18] == fs:[0x18 + 0] == lea-style: [self + 0x18]; LastError visible through Self
        var code = Asm(a =>
        {
            a.mov(eax, __dword_ptr.fs[0x18]); a.mov(ecx, __dword_ptr[eax + 0x18]); a.cmp(eax, ecx); var bad = a.CreateLabel(); a.jne(bad);
            a.mov(__dword_ptr.fs[0x34], 0x1234); a.cmp(__dword_ptr[eax + 0x34], 0x1234); a.jne(bad);
            a.mov(ecx, __dword_ptr.fs[0x30]); a.cmp(ecx, __dword_ptr[eax + 0x30]); a.jne(bad);
            a.ret();
            a.Label(ref bad); a.xor(eax, eax); a.ret();
        });
        RunEach(code, (r, teb, _) => Assert.Equal(teb, r));
    }

    [Fact]
    public void ExceptionListChainPushAndPopLikeAnMsvcSehProlog()
    {
        // push handler; push fs:[0]; mov fs:[0], esp ... (chain visible) ... pop fs:[0]; add esp, 4
        var code = Asm(a =>
        {
            var bad = a.CreateLabel();
            a.push(0x12345678); a.push(__dword_ptr.fs[0]); a.mov(__dword_ptr.fs[0], esp);
            a.cmp(__dword_ptr.fs[0], esp); a.jne(bad);
            a.mov(eax, __dword_ptr.fs[0]); a.cmp(__dword_ptr[eax], -1); a.jne(bad);          // prev link
            a.cmp(__dword_ptr[eax + 4], 0x12345678); a.jne(bad);                             // handler
            a.pop(__dword_ptr.fs[0]); a.add(esp, 4);
            a.mov(eax, __dword_ptr.fs[0]); a.ret();
            a.Label(ref bad); a.mov(eax, 0xBAD); a.ret();
        });
        Assert.Equal(0xFFFFFFFFu, Run(code));
    }

    [Fact]
    public void LastErrorAndTlsSlotsRoundTripAndPersistAcrossCalls()
    {
        var set = Asm(a =>
        {
            // _f(v): write v to LastError and v+i to every TLS slot via static offsets
            a.mov(eax, __dword_ptr[esp + 4]); a.mov(__dword_ptr.fs[0x34], eax);
            for (var i = 0; i < 64; i++) { a.lea(ecx, __[eax + i]); a.mov(__dword_ptr.fs[0xE10 + 4 * i], ecx); }
            a.ret();
        });
        var get = Asm(a =>
        {
            a.mov(eax, __dword_ptr.fs[0x34]);
            for (var i = 0; i < 64; i++) { a.mov(ecx, __dword_ptr.fs[0xE10 + 4 * i]); a.sub(ecx, __dword_ptr.fs[0x34]); a.add(ecx, -i); a.or(eax, ecx); a.xor(ecx, ecx); }
            a.ret();
        });
        foreach (var profile in Profiles)
        {
            var fx = new PeFixtureBuilder().WithCode([.. set, .. get]).Export("_set", 0).Export("_get", (uint)set.Length);
            var image = PeReader.Read(fx.Build());
            var art = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(profile), "teb");
            using var m = GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            m.Bind("_set", GuestCallingConvention.Cdecl, 1).Call(1000);
            Assert.Equal(1000u, m.Bind("_get", GuestCallingConvention.Cdecl, 0).Call());
        }
    }

    [Fact]
    public void SubwordAndReadModifyWriteAccessesOnFsWork()
    {
        var code = Asm(a =>
        {
            a.mov(__dword_ptr.fs[0xE10], 0x11223344);
            a.movzx(eax, __byte_ptr.fs[0xE11]);                    // 0x33
            a.movzx(ecx, __word_ptr.fs[0xE12]);                    // 0x1122
            a.shl(ecx, 8); a.add(eax, ecx);                        // 0x112233
            a.add(__dword_ptr.fs[0xE10], 1);                       // RMW
            a.mov(edx, 0x10); a.xchg(__dword_ptr.fs[0xE14], edx);  // slot1 <-> edx
            a.add(eax, __dword_ptr.fs[0xE10]);                     // + 0x11223345
            a.inc(__dword_ptr.fs[0xE14]);
            a.add(eax, __dword_ptr.fs[0xE14]);                     // + 0x11
            a.add(eax, edx);                                       // + 0 (old slot 1)
            a.ret();
        });
        Assert.Equal(0x112233u + 0x11223345u + 0x11u, Run(code));
    }

    [Fact]
    public void LeaWithFsPrefixIgnoresTheSegment()
    {
        // lea eax, fs:[ecx+4]
        var code = Asm(a => { a.mov(ecx, __dword_ptr[esp + 4]); a.db(0x64, 0x8D, 0x41, 0x04); a.ret(); });
        Assert.Equal(0x1004u, Run(code, [0x1000u]));
    }

    [Fact]
    public void PushAndPopMemoryWithFs()
    {
        var code = Asm(a =>
        {
            a.mov(__dword_ptr.fs[0xE20], 0xCAFE);
            a.push(__dword_ptr.fs[0xE20]); a.pop(__dword_ptr.fs[0xE24]);
            a.mov(eax, __dword_ptr.fs[0xE24]); a.ret();
        });
        Assert.Equal(0xCAFEu, Run(code));
    }

    [Fact]
    public void CallThroughAnFsSlotWorks()
    {
        // fs slot 5 holds the address of the function that returns 99
        // Build explicitly: callee at fixed offset 0x40.
        var bytes = new byte[0x50]; Array.Fill(bytes, (byte)0xCC);
        var caller = Asm(a => { a.mov(eax, PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + 0x40); a.mov(__dword_ptr.fs[0xE10 + 20], eax); a.call(__dword_ptr.fs[0xE10 + 20]); a.ret(); });
        caller.CopyTo(bytes, 0);
        bytes[0x40] = 0xB8; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x41), 99); bytes[0x45] = 0xC3;
        Assert.Equal(99u, Run(bytes, configure: f => f.Relocate(PeFixtureBuilder.TextRva + 1)));
    }

    // ---- translation-time diagnostics -------------------------------------------------------------------------

    public static IEnumerable<object[]> UnmodelledStaticOffsets()
    {
        foreach (var off in new uint[] { 0x0C, 0x10, 0x14, 0x1C, 0x28, 0x38, 0xC00, 0xE0C, 0xF10, 0xFFC, 0x1000, 0xFFFFFFFC })
            yield return [off];
    }

    [Theory, MemberData(nameof(UnmodelledStaticOffsets))]
    public void UnmodelledStaticOffsetIsATranslationError(uint offset)
    {
        var code = Asm(a => { a.nop(); a.db(0x64, 0x8B, 0x05, (byte)offset, (byte)(offset >> 8), (byte)(offset >> 16), (byte)(offset >> 24)); a.ret(); });
        foreach (var profile in Profiles)
        {
            var d = Assert.Single(Errors(Translate(code, profile)));
            Assert.Equal("BC_FS_UNSUPPORTED_OFFSET", d.Code);
            Assert.Contains($"0x{offset:X}", d.Message);
            Assert.Contains("mov", d.Message);
            Assert.Equal(PeFixtureBuilder.TextRva + 1, d.GuestRva);
        }
    }

    [Theory]
    [InlineData(0x04), InlineData(0x08), InlineData(0x18), InlineData(0x20), InlineData(0x24), InlineData(0x30)]
    public void WritingAReadOnlyFieldIsATranslationError(int offset)
    {
        var code = Asm(a => { a.mov(__dword_ptr.fs[offset], eax); a.ret(); });
        foreach (var profile in Profiles)
        {
            var d = Assert.Single(Errors(Translate(code, profile)));
            Assert.Equal("BC_FS_UNSUPPORTED_OFFSET", d.Code);
            Assert.Contains("read-only", d.Message);
            Assert.Contains($"0x{offset:X}", d.Message);
        }
    }

    [Fact]
    public void AccessesStraddlingTwoFieldsOrWiderThanTheFieldAreRejected()
    {
        foreach (var bytes in new[] { new byte[] { 0x64, 0xA1, 0x06, 0, 0, 0 }, new byte[] { 0x64, 0x8B, 0x05, 0x02, 0, 0, 0 }, new byte[] { 0x64, 0x66, 0x8B, 0x05, 0x03, 0, 0, 0 } })
        {
            var code = Asm(a => { a.db(bytes); a.ret(); });
            Assert.Equal("BC_FS_UNSUPPORTED_OFFSET", Assert.Single(Errors(Translate(code, OptimizationProfile.Release))).Code);
        }
        // a qword access to a 4-byte field is outside the table too, an 8-byte access inside the slot array is fine
        Assert.Equal("BC_FS_UNSUPPORTED_OFFSET", Assert.Single(Errors(Translate(Asm(a => { a.movq(xmm0, __qword_ptr.fs[0x30]); a.ret(); }), OptimizationProfile.Release))).Code);
        Assert.Empty(Errors(Translate(Asm(a => { a.movq(xmm0, __qword_ptr.fs[0xE10]); a.ret(); }), OptimizationProfile.Release)));
    }

    [Fact]
    public void SegmentRegisterOperandsAndGsAreExplicitErrors()
    {
        var cases = new (string Name, byte[] Bytes, string Code)[]
        {
            ("mov eax, fs", [0x8C, 0xE0], "BC_UNSUPPORTED_INSTRUCTION"),
            ("mov fs, eax", [0x8E, 0xE0], "BC_UNSUPPORTED_INSTRUCTION"),
            ("push fs", [0x0F, 0xA0], "BC_UNSUPPORTED_INSTRUCTION"),
            ("pop fs", [0x0F, 0xA1], "BC_UNSUPPORTED_INSTRUCTION"),
            ("gs:[0]", [0x65, 0x8B, 0x05, 0, 0, 0, 0], "BC_SEGMENT_OVERRIDE"),
            ("fs: movsb", [0x64, 0xA4], "BC_SEGMENT_OVERRIDE"),
        };
        foreach (var (name, bytes, code) in cases)
            foreach (var profile in Profiles)
            {
                var d = Assert.Single(Errors(Translate(Asm(a => { a.db(bytes); a.ret(); }), profile)));
                Assert.True(d.Code == code, $"{name}: {d.Code} {d.Message}");
            }
    }

    // ---- run-time offsets -------------------------------------------------------------------------------------

    [Fact]
    public void DynamicOffsetsAddressTheTlsSlotArray()
    {
        var code = Asm(a =>
        {
            a.mov(ecx, __dword_ptr[esp + 4]); a.mov(edx, __dword_ptr[esp + 8]);
            a.mov(__dword_ptr.fs[ecx * 4 + 0xE10], edx);
            a.mov(eax, __dword_ptr.fs[ecx * 4 + 0xE10]); a.ret();
        });
        Assert.Equal(7u, Run(code, [0u, 7u]));
        Assert.Equal(8u, Run(code, [63u, 8u]));
        Assert.Equal(9u, Run(code, [0x40000003u, 9u]));          // ecx*4 wraps to 12 in 32 bits, as on IA-32
    }

    [Fact]
    public void RegisterOnlyOffsetInsideTheSlotArrayWorks()
    {
        var code = Asm(a => { a.mov(__dword_ptr.fs[0xE18], 0x77); a.mov(ecx, __dword_ptr[esp + 4]); a.mov(eax, __dword_ptr.fs[ecx]); a.ret(); });
        Assert.Equal(0x77u, Run(code, [0xE18u]));
    }

    [Theory]
    [InlineData(64u), InlineData(65u), InlineData(0x3FFFFFFFu), InlineData(0xFFFFFFFFu), InlineData(0xC0000040u)]
    public void DynamicOffsetOutsideTheSlotArrayTrapsInsteadOfReadingHostMemory(uint index)
    {
        var code = Asm(a => { a.mov(ecx, __dword_ptr[esp + 4]); a.mov(eax, __dword_ptr.fs[ecx * 4 + 0xE10]); a.ret(); });
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile);
            Assert.Empty(Errors(b));
            using var m = Load(b);
            var ex = Assert.Throws<GuestTrapException>(() => m.Bind("_f", GuestCallingConvention.Cdecl, 1).Call(index));
            Assert.Contains("fs:", ex.Message);
            Assert.Contains((PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + 4).ToString("X8"), ex.Message);
            // the module stays usable and the same call with a legal index still works
            Assert.Equal(0u, m.Bind("_f", GuestCallingConvention.Cdecl, 1).Call(3));
        }
    }

    [Fact]
    public void DynamicOffsetBoundDependsOnTheAccessWidth()
    {
        var code = Asm(a => { a.mov(ecx, __dword_ptr[esp + 4]); a.movzx(eax, __byte_ptr.fs[ecx + 0xE10]); a.ret(); });
        Assert.Equal(0u, Run(code, [0xFFu]));                              // last byte of the array is legal for a byte access
        using (var m = Load(Translate(code, OptimizationProfile.Release)))
            Assert.Throws<GuestTrapException>(() => m.Bind("_f", GuestCallingConvention.Cdecl, 1).Call(0x100));
        var wide = Asm(a => { a.mov(ecx, __dword_ptr[esp + 4]); a.mov(eax, __dword_ptr.fs[ecx + 0xE10]); a.ret(); });
        Assert.Equal(0u, Run(wide, [0xFCu]));
        using var mw = Load(Translate(wide, OptimizationProfile.Release));
        Assert.Throws<GuestTrapException>(() => mw.Bind("_f", GuestCallingConvention.Cdecl, 1).Call(0xFD));   // dword straddles the end of the array
    }

    [Fact]
    public void DynamicFsAccessPreservesTheFlagsItConsumes()
    {
        // stc; adc eax, fs:[ecx*4+0xE10]   -> eax + slot + 1
        var code = Asm(a =>
        {
            a.mov(__dword_ptr.fs[0xE10 + 8], 5); a.mov(ecx, 2); a.xor(eax, eax); a.stc();
            a.adc(eax, __dword_ptr.fs[ecx * 4 + 0xE10]); a.ret();
        });
        Assert.Equal(6u, Run(code));
        var cmov = Asm(a =>
        {
            a.mov(__dword_ptr.fs[0xE10 + 4], 5); a.mov(ecx, 1); a.mov(eax, 0); a.cmp(eax, 1);   // CF set (0 < 1)
            a.cmovb(eax, __dword_ptr.fs[ecx * 4 + 0xE10]); a.ret();
        });
        Assert.Equal(5u, Run(cmov));
    }

    // ---- threads, nesting, concurrency -------------------------------------------------------------------------

    private static Built CallbackLib(OptimizationProfile profile, Action<Assembler> body)
        => Translate(Asm(body), profile, f => f.Import("HOST.DLL", "hook"));

    [Fact]
    public void NestedCallsOnOneThreadShareTheTeb()
    {
        GuestFunction? fn = null; GuestModule? module = null;
        var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.CdeclCall(call =>
        {
            var x = call.Arg(0);
            var outerTeb = GuestModule.Current!.CurrentThreadTeb;
            if (x == 0) return outerTeb;
            var inner = fn!.Call(x - 1);                  // returns the nested call's Self pointer
            Assert.Equal(outerTeb, inner);
            Assert.Equal(module!.CurrentThreadTeb, inner);
            return inner;
        }));
        foreach (var profile in Profiles)
        {
            var b = CallbackLib(profile, a =>
            {
                a.mov(eax, __dword_ptr[esp + 4]); a.mov(__dword_ptr.fs[0x34], eax); a.push(eax); a.call(__dword_ptr[Hook]); a.add(esp, 4);
                a.mov(eax, __dword_ptr.fs[0x18]); a.ret();
            });
            using var m = Load(b, resolver);
            module = m; fn = m.Bind("_f", GuestCallingConvention.Cdecl, 1);
            Assert.Equal(fn.Call(0), fn.Call(4));
            Assert.Equal(m.CurrentThreadTeb, fn.Call(3));
        }
    }

    [Fact]
    public void ThreadsGetDistinctTebsWithIndependentExceptionListsAndSlots()
    {
        const int N = 6;
        foreach (var profile in Profiles)
        {
            using var barrier = new Barrier(N);
            var tebs = new System.Collections.Concurrent.ConcurrentDictionary<int, uint>();
            var resolver = new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.CdeclCall(call =>
            {
                tebs[Environment.CurrentManagedThreadId] = GuestModule.Current!.CurrentThreadTeb;
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));          // every thread is inside its guest call
                return 0;
            }));
            // _f(v): fs:[0] = v; slot0 = v; hook (all threads rendezvous); return fs:[0] ^ slot0 ^ ... must still be this thread's
            var b = CallbackLib(profile, a =>
            {
                a.mov(eax, __dword_ptr[esp + 4]); a.mov(__dword_ptr.fs[0], eax); a.mov(__dword_ptr.fs[0xE10], eax); a.mov(__dword_ptr.fs[0x34], eax);
                a.push(0); a.call(__dword_ptr[Hook]); a.add(esp, 4);
                var bad = a.CreateLabel();
                a.mov(eax, __dword_ptr.fs[0]); a.cmp(eax, __dword_ptr.fs[0xE10]); a.jne(bad); a.cmp(eax, __dword_ptr.fs[0x34]); a.jne(bad);
                a.ret(); a.Label(ref bad); a.xor(eax, eax); a.ret();
            });
            using var m = Load(b, resolver);
            var fn = m.Bind("_f", GuestCallingConvention.Cdecl, 1);
            var results = new uint[N]; var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = Enumerable.Range(0, N).Select(t => new Thread(() =>
            {
                try
                {
                    for (var round = 0; round < 3; round++) { results[t] = fn.Call((uint)(0x1000 * (t + 1) + round)); Assert.Equal((uint)(0x1000 * (t + 1) + round), results[t]); }
                }
                catch (Exception e) { errors.Add(e); barrier.RemoveParticipants(1); }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(60)), "threads did not finish");
            Assert.Empty(errors);
            Assert.Equal(N, tebs.Values.Distinct().Count());
            Assert.All(tebs.Values, v => Assert.Equal(0u, v & 0xFFF));
        }
    }

    [Fact]
    public void LastErrorAndSlotsPersistPerThreadAcrossCallsButNotAcrossThreads()
    {
        var set = Asm(a => { a.mov(eax, __dword_ptr[esp + 4]); a.mov(__dword_ptr.fs[0x34], eax); a.mov(__dword_ptr.fs[0xE10], eax); a.ret(); });
        var get = Asm(a => { a.mov(eax, __dword_ptr.fs[0x34]); a.add(eax, __dword_ptr.fs[0xE10]); a.ret(); });
        var fx = new PeFixtureBuilder().WithCode([.. set, .. get]).Export("_set", 0).Export("_get", (uint)set.Length);
        var image = PeReader.Read(fx.Build());
        var art = BlitzCS.BinaryCompiler.CodeGen.X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "teb2");
        using var m = GuestModule.Load(art, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        var setF = m.Bind("_set", GuestCallingConvention.Cdecl, 1); var getF = m.Bind("_get", GuestCallingConvention.Cdecl, 0);
        setF.Call(21);
        Assert.Equal(42u, getF.Call());
        uint other = 99;
        var t = new Thread(() => other = getF.Call()); t.Start(); t.Join();
        Assert.Equal(0u, other);                                    // a fresh thread has its own, zero-initialised block
        Assert.Equal(42u, getF.Call());
    }

    [Fact]
    public void TebsAreReleasedWhenTheModuleIsDisposed()
    {
        var code = Asm(a => { a.mov(eax, __dword_ptr.fs[0x18]); a.ret(); });
        var b = Translate(code, OptimizationProfile.Release);
        uint teb, peb;
        using (var m = Load(b))
        {
            teb = m.Bind("_f", GuestCallingConvention.Cdecl, 0).Call();
            peb = m.PebAddress;
            Assert.True(m.Memory.IsMapped(teb, 0x1000));
            Assert.True(m.Memory.IsMapped(peb, 16));
            Assert.True(IsMappedInProcess(teb));
        }
        // The kernel merges adjacent anonymous mappings with equal permissions, so test range coverage, not an exact start address.
        Assert.False(IsMappedInProcess(teb));
        Assert.False(IsMappedInProcess(peb));
    }

    /// <summary>True when any mapping of this process covers the address (/proc/self/maps on Linux, VirtualQuery on Windows, through the platform layer).</summary>
    private static bool IsMappedInProcess(ulong address) => BlitzCS.BinaryCompiler.Runtime.PlatformMemory.Current.QueryRanges(address, address + 1).Count > 0;

    [Fact]
    public void ReleaseAndDebugProfilesProduceTheSameTebBehaviour()
    {
        // a mixed-form program: result must be identical under both profiles (Run asserts this)
        var code = Asm(a =>
        {
            a.mov(__dword_ptr.fs[0xE10], 3); a.mov(ecx, 0); a.mov(eax, 0);
            var loop = a.CreateLabel(); a.Label(ref loop);
            a.add(eax, __dword_ptr.fs[ecx * 4 + 0xE10]); a.add(__dword_ptr.fs[ecx * 4 + 0xE10], 1); a.inc(ecx); a.cmp(ecx, 4); a.jb(loop);
            a.add(eax, __dword_ptr.fs[0xE10]); a.ret();
        });
        Assert.Equal(3u + 0 + 0 + 0 + 4u, Run(code));
    }
}
