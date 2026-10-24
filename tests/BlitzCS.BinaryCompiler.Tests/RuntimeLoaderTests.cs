using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[CollectionDefinition("GuestRuntime", DisableParallelization = true)]
public sealed class GuestRuntimeCollection;

[Collection("GuestRuntime")]
public sealed class RuntimeLoaderTests
{
    private static GuestModule Load(FakeGuest g, Func<uint, uint>? hook = null, GuestModuleOptions? options = null) =>
        GuestModule.Load(g.Artifact, g.Image, Resolver(hook), options);

    internal static DictionaryGuestImportResolver Resolver(Func<uint, uint>? hook = null) =>
        new DictionaryGuestImportResolver().Add("HOST.DLL", "hook", GuestImportBuilder.Stdcall(hook ?? (x => x)));

    private static List<(ulong Start, ulong End, string Perms)> Maps() =>
        File.ReadAllLines("/proc/self/maps").Select(l =>
        {
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var range = parts[0].Split('-');
            return (Convert.ToUInt64(range[0], 16), Convert.ToUInt64(range[1], 16), parts[1]);
        }).ToList();

    private static string PermsAt(ulong address) =>
        Maps().FirstOrDefault(m => address >= m.Start && address < m.End).Perms ?? "unmapped";

    [Fact]
    public void Load_MapsImageWithSectionProtectionsAndNeverRwx()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        var b = (ulong)g.Image.ImageBase;
        Assert.Equal("r--p", PermsAt(b));               // headers
        Assert.Equal("r-xp", PermsAt(b + 0x1000));      // .text
        Assert.Equal("rw-p", PermsAt(b + 0x2000));      // .data
        Assert.Equal("rw-p", PermsAt(b + 0x3000));      // .idata
        var host = (ulong)m.HostImageBase;
        Assert.Equal("r-xp", PermsAt(host));            // host code
        Assert.Equal("rw-p", PermsAt(host + 0x1000));   // host data
        foreach (var (start, end, perms) in Maps().Where(x => (x.Start >= b && x.Start < b + g.Artifact.GuestImageSize + 0x10000) || (x.Start >= host && x.Start < host + 0x2000)))
            Assert.False(perms.StartsWith("rwx"), $"RWX mapping {start:X}-{end:X}");
        // IAT slot holds the guest address of import stub 0
        Assert.Equal(g.Image.ImageBase + g.Artifact.ImportStubBaseRva, m.Memory.ReadUInt32(g.Image.ImageBase + g.Image.Imports[0].IatRva));
    }

    [Fact]
    public void Call_AddMatchesReferenceForCdeclAndStdcall()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        var add = m.Bind("add", GuestCallingConvention.Cdecl, 2);
        var addStd = m.Bind("add_std", GuestCallingConvention.Unknown, 2); // convention taken from the artifact
        Assert.Equal(GuestCallingConvention.Stdcall, addStd.Convention);
        var rng = new Random(1234);
        for (var i = 0; i < 500; i++)
        {
            var (x, y) = ((uint)rng.NextInt64(0, uint.MaxValue), (uint)rng.NextInt64(0, uint.MaxValue));
            Assert.Equal(unchecked(x + y), add.Call(x, y));
            Assert.Equal(unchecked(x + y), addStd.Call(x, y));
        }
    }

    [Fact]
    public void Call_PlacesExitGuestAsOutermostReturnAddress()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        Assert.Equal(g.Image.ImageBase + g.Artifact.ExitGuestRva, m.Bind("getret", GuestCallingConvention.Cdecl, 0).Call());
    }

    [Fact]
    public void Bind_RejectsUnknownMismatchedUnsupportedAndMissing()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        Assert.Throws<ArgumentException>(() => m.Bind("mystery", GuestCallingConvention.Unknown, 0));
        Assert.Equal(GuestCallingConvention.Cdecl, m.Bind("mystery", GuestCallingConvention.Cdecl, 0).Convention);
        Assert.Throws<ArgumentException>(() => m.Bind("add", GuestCallingConvention.Stdcall, 2));
        Assert.Throws<ArgumentException>(() => m.Bind("add_std", GuestCallingConvention.Stdcall, 3)); // ArgBytes says 8
        Assert.Equal(GuestCallingConvention.Fastcall, m.Bind("fast", GuestCallingConvention.Fastcall, 1).Convention); // register conventions are bindable (AbiRegisterConventionTests)
        Assert.Throws<GuestExportException>(() => m.Bind("nope", GuestCallingConvention.Cdecl, 0));
        Assert.Throws<ArgumentException>(() => m.Bind("add", GuestCallingConvention.Cdecl, 2).Call(1u));
    }

    [Fact]
    public void Load_UnboundImportNamesDllAndFunctionAndLeaksNothing()
    {
        var g = FakeGuest.Create();
        var ex = Assert.Throws<GuestImportException>(() => GuestModule.Load(g.Artifact, g.Image, new DictionaryGuestImportResolver()));
        Assert.Contains("host.dll!hook", ex.Message);
        using var again = Load(g); // base address was not left mapped
    }

    [Fact]
    public void Load_FailsWhenFixedBaseIsOccupied()
    {
        var g = FakeGuest.Create();
        using var first = Load(g);
        Assert.Throws<GuestAddressSpaceException>(() => Load(g));
        Assert.Equal(7u, first.Bind("add", GuestCallingConvention.Cdecl, 2).Call(3, 4));
    }

    [Fact]
    public void Load_RejectsTlsAndTranslationErrorsAndMismatchedArtifacts()
    {
        var g = FakeGuest.Create();
        var tls = new PeImage
        {
            Bytes = g.Image.Bytes, Sha256 = g.Image.Sha256, Machine = g.Image.Machine, IsDll = true, ImageBase = g.Image.ImageBase,
            SizeOfImage = g.Image.SizeOfImage, EntryPointRva = g.Image.EntryPointRva, Sections = g.Image.Sections, Imports = g.Image.Imports,
            Exports = g.Image.Exports, Relocations = g.Image.Relocations, Diagnostics = [], Tls = new PeTls(0, 0, 0, 0),
        };
        Assert.Throws<GuestUnsupportedException>(() => GuestModule.Load(g.Artifact, tls, Resolver()));
        var bad = g.Artifact with { Diagnostics = [new BinaryDiagnostic(DiagnosticSeverity.Error, "X86_UNSUPPORTED", "opcode", 0x1000)] };
        var e = Assert.Throws<GuestLoadException>(() => GuestModule.Load(bad, g.Image, Resolver()));
        Assert.Contains("X86_UNSUPPORTED", e.Message);
        Assert.Throws<GuestLoadException>(() => GuestModule.Load(g.Artifact with { SourceSha256 = "00" }, g.Image, Resolver()));
        Assert.Throws<GuestLoadException>(() => GuestModule.Load(g.Artifact with { CodeSize = 0x123 }, g.Image, Resolver()));
    }

    [Fact]
    public void DllMain_RunsAttachOnLoadAndDetachOnDispose()
    {
        var g = FakeGuest.Create();
        var reasons = new List<uint>();
        var m = Load(g, x => { reasons.Add(x); return 0; });
        Assert.Equal([1u], reasons);
        m.Dispose();
        Assert.Equal([1u, 0u], reasons);
    }

    [Fact]
    public void DllMain_FalseFailsInitializationAndReleasesTheImage()
    {
        var g = FakeGuest.Create(dllMainReturnsTrue: false);
        Assert.Throws<GuestInitializationException>(() => Load(g));
        var ok = FakeGuest.Create();
        using var m = Load(ok);
    }

    [Fact]
    public void Dispose_IsIdempotentAndUseAfterDisposeIsRejected()
    {
        var g = FakeGuest.Create();
        var m = Load(g);
        var add = m.Bind("add", GuestCallingConvention.Cdecl, 2);
        using var buf = m.Memory.Allocate(16);
        m.Dispose();
        m.Dispose();
        Assert.True(m.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => add.Call(1, 2));
        Assert.Throws<ObjectDisposedException>(() => m.Memory.ReadUInt32(g.Image.ImageBase));
        Assert.Throws<ObjectDisposedException>(() => m.Memory.Allocate(8));
        Assert.Throws<ObjectDisposedException>(() => m.Bind("add", GuestCallingConvention.Cdecl, 2));
        Assert.Equal("unmapped", PermsAt(g.Image.ImageBase));
        buf.Dispose(); // freeing a buffer after the module is gone is a no-op
    }

    [Fact]
    public void Memory_IsBoundsCheckedAndWriteProtected()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        var b = g.Image.ImageBase;
        Assert.Throws<GuestMemoryException>(() => m.Memory.ReadUInt32(0));
        Assert.Throws<GuestMemoryException>(() => m.Memory.ReadUInt32(b - 4));
        Assert.Throws<GuestMemoryException>(() => m.Memory.ReadUInt32(b + g.Artifact.ImportStubBaseRva + 0x10000));
        Assert.Throws<GuestMemoryException>(() => m.Memory.WriteUInt32(b + 0x1000, 1)); // .text is not writable
        m.Memory.WriteUInt32(b + 0x2000, 0xDEADBEEF);
        Assert.Equal(0xDEADBEEFu, m.Memory.ReadUInt32(b + 0x2000));
    }

    [Fact]
    public void Buffers_AreCheckedFreedAndVisibleToGuest()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        var writer = m.Bind("writer", GuestCallingConvention.Cdecl, 2);
        var buf = m.Memory.Allocate(64);
        Assert.True(buf.Address < 0x80000000u);
        writer.Call(buf.Address + 8, 0x01020304);
        var bytes = buf.ToArray();
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, bytes[8..12]);
        buf.Write(0, [9, 9]);
        Assert.Equal(9, m.Memory.ReadByte(buf.Address));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(63, [1, 2]));
        Assert.Throws<GuestMemoryException>(() => m.Memory.ReadUInt32(buf.Address + 62)); // past the logical length
        buf.Dispose();
        buf.Dispose();
        Assert.Throws<GuestMemoryException>(() => m.Memory.ReadByte(buf.Address));
        Assert.Throws<ObjectDisposedException>(() => buf.ToArray());
    }

    [Fact]
    public void Canary_DetectsCalleeWritingAboveItsArguments()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        var f = m.Bind("overrun", GuestCallingConvention.Cdecl, 1);
        Assert.Throws<GuestStackException>(() => f.Call(1));
        m.Dispose();
        using var lenient = Load(FakeGuest.Create(), options: new GuestModuleOptions { CheckStackCanary = false });
        Assert.Equal(0u, lenient.Bind("overrun", GuestCallingConvention.Cdecl, 1).Call(1));
    }

    [Fact]
    public void StackBudget_IsCheckedBeforeEnteringTheGuest()
    {
        var g = FakeGuest.Create();
        var tiny = new GuestModuleOptions { StackSize = 4096, StackReserve = 8192 };
        var init = Assert.Throws<GuestInitializationException>(() => Load(g, options: tiny)); // DllMain cannot fit either
        Assert.IsType<GuestStackException>(init.InnerException);
        using var m = Load(g, options: new GuestModuleOptions { StackSize = 4096, StackReserve = 8192, RunEntryPoint = false });
        var add = m.Bind("add", GuestCallingConvention.Cdecl, 2);
        Assert.Throws<GuestStackException>(() => add.Call(1, 2));
        Assert.Throws<GuestStackException>(() => add.Call(1, 2)); // stack was returned to the pool, still consistent
    }

    [Fact]
    public void Imports_CanCallBackIntoTheGuestOnTheSameThread()
    {
        var g = FakeGuest.Create();
        GuestFunction? add = null;
        GuestFunction? callimp = null;
        uint Hook(uint x)
        {
            if (x == 1) return 0; // DLL_PROCESS_ATTACH
            if (x == 0) return 0;
            if (x >= 1000) return add!.Call(x, 5) * 2;
            return callimp!.Call(x + 1) + 1; // deeper nesting: guest -> import -> guest -> import ...
        }
        using var m = Load(g, Hook);
        add = m.Bind("add", GuestCallingConvention.Cdecl, 2);
        callimp = m.Bind("callimp", GuestCallingConvention.Cdecl, 1);
        Assert.Equal((1000u + 5) * 2, callimp.Call(1000));
        Assert.Equal((1000u + 5) * 2 + 20, callimp.Call(980)); // 20 nested levels
    }

    [Fact]
    public void ImportFault_DoesNotUnwindThroughGuestAndSurfacesFromTheOuterCall()
    {
        var g = FakeGuest.Create();
        var calls = 0;
        var fail = true;
        using var m = Load(g, x =>
        {
            if (x == 1 || x == 0) return 0;
            calls++;
            if (fail) throw new InvalidOperationException("boom");
            return x + 1;
        });
        var twice = m.Bind("callimp_twice", GuestCallingConvention.Cdecl, 1);
        var ex = Assert.Throws<GuestImportException>(() => twice.Call(50));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(1, calls);                       // the second import call is short-circuited after the fault
        Assert.Same(ex.InnerException, m.LastImportFault);
        fail = false;
        Assert.Equal(102u, twice.Call(50));           // module remains usable; fault flag was cleared
    }

    [Fact]
    public void RunawayNesting_ThrowsGuestStackExceptionInsteadOfCrashing()
    {
        var g = FakeGuest.Create();
        GuestFunction? callimp = null;
        using var m = Load(g, x => x <= 1 ? 0 : callimp!.Call(x + 1), new GuestModuleOptions { StackSize = 64 * 1024, StackReserve = 16 * 1024 });
        callimp = m.Bind("callimp", GuestCallingConvention.Cdecl, 1);
        var ex = Assert.Throws<GuestImportException>(() => callimp.Call(2));
        Assert.IsType<GuestStackException>(ex.InnerException);
    }

    [Fact]
    public void ConcurrentCallsFromManyThreadsAreCorrect()
    {
        var g = FakeGuest.Create();
        using var m = Load(g);
        var add = m.Bind("add", GuestCallingConvention.Cdecl, 2);
        var failures = 0;
        Parallel.For(0, 8, t =>
        {
            var rng = new Random(t);
            for (var i = 0; i < 300; i++)
            {
                var (x, y) = ((uint)rng.Next(), (uint)rng.Next());
                if (add.Call(x, y) != unchecked(x + y)) Interlocked.Increment(ref failures);
            }
        });
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightCalls()
    {
        var g = FakeGuest.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var m = Load(g, x => { if (x > 1) { entered.Set(); release.Wait(); } return x; });
        var callimp = m.Bind("callimp", GuestCallingConvention.Cdecl, 1);
        var call = Task.Run(() => callimp.Call(77));
        entered.Wait();
        var dispose = Task.Run(m.Dispose);
        await Task.Delay(150);
        Assert.False(dispose.IsCompleted);
        release.Set();
        Assert.Equal(77u, await call);
        await dispose;
        Assert.True(m.IsDisposed);
    }

    [Fact]
    public void Dispose_FromInsideAnImportIsRejected()
    {
        var g = FakeGuest.Create();
        GuestModule? self = null;
        using var m = Load(g, x => { if (x > 1) self!.Dispose(); return 0; });
        self = m;
        var ex = Assert.Throws<GuestImportException>(() => m.Bind("callimp", GuestCallingConvention.Cdecl, 1).Call(5));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
