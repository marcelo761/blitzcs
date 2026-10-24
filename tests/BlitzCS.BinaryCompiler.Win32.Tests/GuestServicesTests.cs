using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// Hand-assembled IA-32 guest (no compiler needed) that exercises what a host-only call cannot: the x87 argument convention (_ftol, _CIpow), x87 results,
/// callbacks into guest code (_initterm, qsort, a thread start routine, a multimedia timer), DATA imports (__mb_cur_max, _pctype) and real guest threads.
/// </summary>
[Collection("GuestRuntime")]
public sealed class GuestServicesTests : IDisposable
{
    private static readonly uint Base = Win32PeBuilder.ImageBase;
    private readonly Win32ImportResolver resolver = new();
    private readonly GuestModule module;

    private static uint Text(uint offset) => Base + Win32PeBuilder.TextRva + offset;
    private static uint Data(uint offset) => Base + Win32PeBuilder.DataRva + offset;

    private sealed class Asm
    {
        public readonly List<byte> Bytes = [];
        public readonly Dictionary<string, uint> Labels = [];
        public Asm Label(string n) { Labels[n] = (uint)Bytes.Count; return this; }
        public Asm B(params byte[] b) { Bytes.AddRange(b); return this; }
        public Asm D(uint v) { Bytes.AddRange(BitConverter.GetBytes(v)); return this; }
        public Asm CallIat(uint slotRva) => B(0xFF, 0x15).D(Base + slotRva);
    }

    public GuestServicesTests()
    {
        var pe = new Win32PeBuilder();
        uint ftol = pe.Import("MSVCRT.dll", "_ftol"), cipow = pe.Import("MSVCRT.dll", "_CIpow"), sin = pe.Import("MSVCRT.dll", "sin"),
            initterm = pe.Import("MSVCRT.dll", "_initterm"), qsort = pe.Import("MSVCRT.dll", "qsort"),
            mbmax = pe.Import("MSVCRT.dll", "__mb_cur_max"), pctype = pe.Import("MSVCRT.dll", "_pctype"),
            createThread = pe.Import("KERNEL32.dll", "CreateThread"), wait = pe.Import("KERNEL32.dll", "WaitForSingleObject"), exitCode = pe.Import("KERNEL32.dll", "GetExitCodeThread"),
            setEvent = pe.Import("WINMM.dll", "timeSetEvent"), kill = pe.Import("WINMM.dll", "timeKillEvent");
        const uint cnt = 0, table = 4, cell = 0x40, cnt2 = 0x48;
        var a = new Asm();
        a.Label("ftol").B(0xDD, 0x44, 0x24, 0x04).CallIat(ftol).B(0xC3);
        a.Label("pow").B(0xDD, 0x44, 0x24, 0x04, 0xDD, 0x44, 0x24, 0x0C).CallIat(cipow).B(0xC3);
        a.Label("sinx").B(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08).CallIat(sin).B(0x83, 0xC4, 0x08, 0xC3);
        a.Label("init").B(0x68).D(Data(table + 12)).B(0x68).D(Data(table)).CallIat(initterm).B(0x83, 0xC4, 0x08, 0xC3);
        a.Label("qs").B(0xFF, 0x74, 0x24, 0x0C, 0x6A, 0x04, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10).CallIat(qsort).B(0x83, 0xC4, 0x10, 0xC3);
        a.Label("cmp").B(0x8B, 0x44, 0x24, 0x04, 0x8B, 0x00, 0x8B, 0x4C, 0x24, 0x08, 0x2B, 0x01, 0xC3);
        a.Label("fa").B(0xC7, 0x05).D(Data(cnt)).D(1).B(0xC3);
        a.Label("fb").B(0xC1, 0x25).D(Data(cnt)).B(3, 0xC3);
        a.Label("mbmax").B(0xA1).D(Base + mbmax).B(0x8B, 0x00, 0xC3);
        a.Label("pct").B(0xA1).D(Base + pctype).B(0x8B, 0x00, 0x8B, 0x4C, 0x24, 0x04, 0x0F, 0xB7, 0x04, 0x48, 0xC3);
        a.Label("thr").B(0x8B, 0x44, 0x24, 0x04, 0x05).D(100).B(0xA3).D(Data(cell)).B(0xC2, 0x04, 0x00);
        a.Label("mkthread").B(0x6A, 0x00, 0x6A, 0x00, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10, 0x6A, 0x00, 0x6A, 0x00).CallIat(createThread).B(0xC3);
        a.Label("waitx").B(0x68).D(5000).B(0xFF, 0x74, 0x24, 0x08).CallIat(wait).B(0xC3);
        a.Label("exitx").B(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08).CallIat(exitCode).B(0xC3);
        a.Label("tstart").B(0x6A, 0x01, 0x6A, 0x00, 0xFF, 0x74, 0x24, 0x0C, 0x6A, 0x01, 0x6A, 0x0A).CallIat(setEvent).B(0xC3);
        a.Label("tcb").B(0xFF, 0x05).D(Data(cnt2)).B(0xC2, 0x14, 0x00);
        a.Label("tkill").B(0xFF, 0x74, 0x24, 0x04).CallIat(kill).B(0xC3);

        var data = new byte[0x100];
        BitConverter.GetBytes(Text(a.Labels["fa"])).CopyTo(data, table);
        BitConverter.GetBytes(Text(a.Labels["fb"])).CopyTo(data, table + 8);
        // table = [fa, NULL, fb] followed by one more dword (the end pointer is table + 12)
        pe.WithCode([.. a.Bytes]).WithData(data);
        foreach (var (name, off) in a.Labels) pe.Export(name, off);
        var image = PeReader.Read(pe.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "h1-guest");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.All(resolver.Analyze(image), v => Assert.Equal(Win32ImportStatus.Implemented, v.Status));
        module = GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        exports = a.Labels;
    }

    private readonly Dictionary<string, uint> exports;

    private GuestFunction F(string name, int argc, GuestCallingConvention c = GuestCallingConvention.Cdecl) => module.Bind(name, c, argc);
    private static (uint Lo, uint Hi) Words(double v) { var b = BitConverter.DoubleToUInt64Bits(v); return ((uint)b, (uint)(b >> 32)); }
    private ulong Ftol(double v) { var (lo, hi) = Words(v); return F("ftol", 2).CallUInt64(lo, hi); }
    private double Pow(double x, double y) { var (a, b) = Words(x); var (c, d) = Words(y); return F("pow", 4).CallDouble(a, b, c, d); }

    public void Dispose() { module.Dispose(); resolver.Dispose(); }

    [Fact]
    public void FtolReadsTheX87ArgumentAndTruncatesTowardZeroIntoEdxEax()
    {
        Assert.Equal(unchecked((ulong)-3L), Ftol(-3.99));
        Assert.Equal(3UL, Ftol(3.99));
        Assert.Equal(10_000_000_000UL, Ftol(1e10));              // needs EDX
        Assert.Equal(unchecked((ulong)-10_000_000_000L), Ftol(-1e10));
        Assert.Equal(0x8000000000000000UL, Ftol(double.NaN));     // x87 integer indefinite
        Assert.Equal(0x8000000000000000UL, Ftol(1.5e19));
        Assert.Equal(0UL, Ftol(0.99999));
    }

    [Fact]
    public void CiPowTakesBaseInSt1AndExponentInSt0AndReturnsInSt0()
    {
        Assert.Equal(1024.0, Pow(2, 10));
        Assert.Equal(0.01, Pow(10, -2), 15);
        Assert.Equal(9.0, Pow(3, 2));
        Assert.Equal(2.0, Pow(4, 0.5), 15);
        Assert.True(double.IsNaN(Pow(-8, 1.0 / 3)));
    }

    [Fact]
    public void CdeclDoubleArgumentsAndSt0Result()
    {
        var (lo, hi) = Words(0.5);
        Assert.Equal(Math.Sin(0.5), F("sinx", 2).CallDouble(lo, hi));
        (lo, hi) = Words(0.0);
        Assert.Equal(0.0, F("sinx", 2).CallDouble(lo, hi));
    }

    [Fact]
    public void X87ConversionRoundTrips()
    {
        foreach (var v in new[] { 0.0, -0.0, 1.0, -1.5, 1e300, 1e-300, 5e-324, double.MaxValue, Math.PI, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(BitConverter.DoubleToInt64Bits(v), BitConverter.DoubleToInt64Bits(GuestX87.ToDouble(GuestX87.FromDouble(v))));
        Assert.True(double.IsNaN(GuestX87.ToDouble(GuestX87.FromDouble(double.NaN))));
        // 1.0 in the 80-bit format: mantissa 0x8000000000000000, exponent 0x3FFF
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0x80, 0xFF, 0x3F], GuestX87.FromDouble(1.0));
    }

    [Fact]
    public void InitTermCallsNonNullEntriesInOrder()
    {
        // fa stores 1, NULL is skipped, fb shifts left by 3: 8 only if fa ran first; the reverse order gives 1
        F("init", 0).Call();
        Assert.Equal(8u, module.Memory.ReadUInt32(Data(0)));
    }

    [Fact]
    public void QsortSortsThroughTheGuestComparer()
    {
        var arr = Data(0x20);
        uint[] values = [5, 3, 9, 1, 3, 0x40000000, 7];
        for (var i = 0; i < values.Length; i++) module.Memory.WriteUInt32(arr + 4u * (uint)i, values[i]);
        F("qs", 3).Call(arr, (uint)values.Length, Text(exports["cmp"]));
        var sorted = Enumerable.Range(0, values.Length).Select(i => module.Memory.ReadUInt32(arr + 4u * (uint)i)).ToArray();
        Assert.Equal([1u, 3, 3, 5, 7, 9, 0x40000000], sorted);
    }

    [Fact]
    public void DataImportsAreVariablesNotStubs()
    {
        Assert.Equal(1u, F("mbmax", 0).Call());
        var pct = F("pct", 1);
        Assert.Equal(0x181u, pct.Call('A'));
        Assert.Equal(0x102u, pct.Call('z'));
        Assert.Equal(0x84u, pct.Call('9'));
        Assert.Equal(0x48u, pct.Call(' '));
        Assert.Equal(0u, pct.Call(0xE9));
    }

    [Fact]
    public void CreateThreadRunsTheStartRoutineOnItsOwnHostThread()
    {
        var handle = F("mkthread", 2).Call(Text(exports["thr"]), 5);
        Assert.NotEqual(0u, handle);
        Assert.Equal(0u, F("waitx", 1).Call(handle));      // WAIT_OBJECT_0
        Assert.Equal(105u, module.Memory.ReadUInt32(Data(0x40)));
        using var cell = module.Memory.Allocate(4);
        Assert.Equal(1u, F("exitx", 2).Call(handle, cell.Address));
        Assert.Equal(105u, module.Memory.ReadUInt32(cell.Address));
        Assert.Empty(Kernel32ExtraService.ThreadFaults(resolver.Context));
    }

    [Fact]
    public void MultimediaTimerCallsTheGuestPeriodicallyUntilKilled()
    {
        var id = F("tstart", 1).Call(Text(exports["tcb"]));
        Assert.NotEqual(0u, id);
        Thread.Sleep(250);
        Assert.Equal(0u, F("tkill", 1).Call(id));
        Thread.Sleep(40);
        var count = module.Memory.ReadUInt32(Data(0x48));
        Assert.InRange(count, 3u, 40u);
        Thread.Sleep(100);
        Assert.Equal(count, module.Memory.ReadUInt32(Data(0x48)));      // no calls after the kill
        Assert.Equal(11u, F("tkill", 1).Call(id));                      // already gone: MMSYSERR_INVALPARAM
        Assert.Empty(Kernel32ExtraService.ThreadFaults(resolver.Context));
    }
}
