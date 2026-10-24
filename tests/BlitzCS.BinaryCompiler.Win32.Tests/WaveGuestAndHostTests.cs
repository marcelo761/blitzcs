using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// A hand-assembled IA-32 guest that opens a wave device with CALLBACK_FUNCTION pointing at guest code. The callback stores its five stdcall arguments and the WAVEHDR flags it sees into guest memory,
/// so the tests prove that the translated callback runs, with the right arguments, after the guest header flags were updated (the callback thread is a foreign host thread).
/// </summary>
public sealed class WaveGuestRig : IDisposable
{
    private static readonly uint Base = Win32PeBuilder.ImageBase;
    public const uint CbRecord = 0, Phwo = 0x40, Fmt = 0x50, Hdr = 0x80, Pcm = 0x100;
    public static uint Data(uint offset) => Base + Win32PeBuilder.DataRva + offset;
    private static uint Text(uint offset) => Base + Win32PeBuilder.TextRva + offset;

    private readonly Win32ImportResolver resolver;
    public readonly GuestModule Module;
    public readonly System.Collections.Concurrent.ConcurrentQueue<WaveOutWrite> Writes = new();
    private readonly Dictionary<string, uint> labels = [];

    public WaveGuestRig(WaveOutBackendKind kind)
    {
        resolver = new Win32ImportResolver(new Win32Options { WaveOut = kind, WaveOutObserver = Writes.Enqueue });
        var pe = new Win32PeBuilder();
        uint open = pe.Import("WINMM.dll", "waveOutOpen"), prep = pe.Import("WINMM.dll", "waveOutPrepareHeader"), write = pe.Import("WINMM.dll", "waveOutWrite"),
            unprep = pe.Import("WINMM.dll", "waveOutUnprepareHeader"), close = pe.Import("WINMM.dll", "waveOutClose"), reset = pe.Import("WINMM.dll", "waveOutReset");
        var code = new List<byte>();
        void B(params byte[] b) => code.AddRange(b);
        void D(uint v) => code.AddRange(BitConverter.GetBytes(v));
        void Call(uint slot) { B(0xFF, 0x15); D(Base + slot); }
        void Label(string n) => labels[n] = (uint)code.Count;

        Label("wopen");
        B(0x68); D(0x30000);                  // push CALLBACK_FUNCTION
        B(0x68); D(0xBEEF);                   // push dwInstance
        B(0xFF, 0x74, 0x24, 0x14);            // push cb
        B(0xFF, 0x74, 0x24, 0x14);            // push pwfx
        B(0x6A, 0x00);                        // push device 0
        B(0xFF, 0x74, 0x24, 0x18);            // push phwo
        Call(open); B(0xC3);
        foreach (var (name, slot) in new[] { ("wprep", prep), ("wwrite", write), ("wunprep", unprep) })
        {
            Label(name);
            B(0x6A, 0x20, 0xFF, 0x74, 0x24, 0x0C, 0xFF, 0x74, 0x24, 0x0C); // push 32; push hdr; push hwo
            Call(slot); B(0xC3);
        }
        foreach (var (name, slot) in new[] { ("wclose", close), ("wreset", reset) })
        {
            Label(name);
            B(0xFF, 0x74, 0x24, 0x04); Call(slot); B(0xC3);
        }
        Label("cb");
        for (byte i = 0; i < 5; i++) { B(0x8B, 0x44, 0x24, (byte)(4 + 4 * i), 0xA3); D(Data(4u * i)); }
        B(0x8B, 0x4C, 0x24, 0x10, 0x85, 0xC9, 0x74, 0x08, 0x8B, 0x41, 0x10, 0xA3); D(Data(0x18)); // flags seen by the callback
        B(0xFF, 0x05); D(Data(0x14));         // count++ (last)
        B(0xC2, 0x14, 0x00);

        pe.WithCode([.. code]).WithData(new byte[0x1000]);
        foreach (var (n, o) in labels) pe.Export(n, o);
        var image = PeReader.Read(pe.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "wave-guest");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.All(resolver.Analyze(image), v => Assert.Equal(Win32ImportStatus.Implemented, v.Status));
        Module = GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
    }

    public uint CallbackAddress => Text(labels["cb"]);
    public uint Rd(uint dataOffset) => Module.Memory.ReadUInt32(Data(dataOffset));
    public uint F(string name, params uint[] args) => Module.Bind(name, GuestCallingConvention.Cdecl, args.Length).Call(args);

    public void SetFormat(ushort ch, uint rate, ushort bits)
    {
        var block = (ushort)(ch * bits / 8);
        Module.Memory.Write(Data(Fmt), [.. BitConverter.GetBytes((ushort)1), .. BitConverter.GetBytes(ch), .. BitConverter.GetBytes(rate), .. BitConverter.GetBytes(rate * block), .. BitConverter.GetBytes(block), .. BitConverter.GetBytes(bits)]);
    }

    public bool WaitCount(uint count, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (Rd(0x14) >= count) return true; Thread.Sleep(1); }
        return Rd(0x14) >= count;
    }

    public void Dispose() { Module.Dispose(); resolver.Dispose(); }
}

[Collection("GuestRuntime")]
public sealed class WaveGuestCallbackTests
{
    private static void RoundTrip(WaveOutBackendKind kind, int audioMs)
    {
        using var g = new WaveGuestRig(kind);
        g.SetFormat(1, 8000, 8); // 8000 bytes/s
        var bytes = audioMs * 8;
        var pcm = Enumerable.Range(0, bytes).Select(i => (byte)(0x80 + (i % 7))).ToArray();
        g.Module.Memory.Write(WaveGuestRig.Data(WaveGuestRig.Pcm), pcm);
        var hdr = WaveGuestRig.Data(WaveGuestRig.Hdr);
        g.Module.Memory.WriteUInt32(hdr, WaveGuestRig.Data(WaveGuestRig.Pcm));
        g.Module.Memory.WriteUInt32(hdr + 4, (uint)bytes);

        var rc = g.F("wopen", WaveGuestRig.Data(WaveGuestRig.Phwo), WaveGuestRig.Data(WaveGuestRig.Fmt), g.CallbackAddress);
        if (rc != 0)
        {
            Assert.True(kind == WaveOutBackendKind.HostWinmm && rc is 2 or 6, $"waveOutOpen failed with {rc}");
            return; // a host without any wave device (documented in the HostWinmm tests)
        }
        var hwo = g.Module.Memory.ReadUInt32(WaveGuestRig.Data(WaveGuestRig.Phwo));
        Assert.NotEqual(0u, hwo);
        Assert.True(g.WaitCount(1, 2000));
        Assert.Equal((hwo, 0x3BBu, 0xBEEFu, 0u), (g.Rd(0), g.Rd(4), g.Rd(8), g.Rd(0xC))); // WOM_OPEN(hwo, WOM_OPEN, dwInstance, 0, 0)

        Assert.Equal(0u, g.F("wprep", hwo, hdr));
        Assert.Equal(0u, g.F("wwrite", hwo, hdr));
        Assert.True(g.WaitCount(2, 5000), "WOM_DONE was not delivered to the guest callback");
        Assert.Equal((hwo, 0x3BDu, 0xBEEFu, hdr), (g.Rd(0), g.Rd(4), g.Rd(8), g.Rd(0xC)));
        Assert.Equal(3u, g.Rd(0x18));                                          // PREPARED|DONE and no INQUEUE when the callback ran
        Assert.Equal(3u, g.Module.Memory.ReadUInt32(hdr + 16));
        Assert.Equal(0u, g.F("wunprep", hwo, hdr));
        Assert.Equal(0u, g.F("wclose", hwo));
        Assert.True(g.WaitCount(3, 2000));
        Assert.Equal((hwo, 0x3BCu), (g.Rd(0), g.Rd(4)));                       // WOM_CLOSE
        Assert.Equal(pcm, g.Writes.SelectMany(w => w.Data).Take(bytes).ToArray()); // the tap saw exactly what the translated guest wrote
    }

    [Fact]
    public void VirtualSinkDeliversOpenDoneCloseToTranslatedGuestCode() => RoundTrip(WaveOutBackendKind.VirtualSink, 100);

    [RequiresWindowsFact]
    public void HostWinmmDeliversOpenDoneCloseToTranslatedGuestCode() => RoundTrip(WaveOutBackendKind.HostWinmm, 100);

    [Fact]
    public void ResetFromGuestCodeMarksTheQueuedBufferDoneBeforeItReturns()
    {
        using var g = new WaveGuestRig(WaveOutBackendKind.VirtualSink);
        g.SetFormat(1, 8000, 8);
        var hdr = WaveGuestRig.Data(WaveGuestRig.Hdr);
        g.Module.Memory.WriteUInt32(hdr, WaveGuestRig.Data(WaveGuestRig.Pcm));
        g.Module.Memory.WriteUInt32(hdr + 4, 0x800); // 256 ms
        Assert.Equal(0u, g.F("wopen", WaveGuestRig.Data(WaveGuestRig.Phwo), WaveGuestRig.Data(WaveGuestRig.Fmt), g.CallbackAddress));
        var hwo = g.Module.Memory.ReadUInt32(WaveGuestRig.Data(WaveGuestRig.Phwo));
        Assert.Equal(0u, g.F("wprep", hwo, hdr));
        Assert.Equal(0u, g.F("wwrite", hwo, hdr));
        Assert.Equal(33u, g.F("wclose", hwo));
        Assert.Equal(0u, g.F("wreset", hwo));
        Assert.Equal(3u, g.Module.Memory.ReadUInt32(hdr + 16));
        Assert.Equal(0u, g.F("wunprep", hwo, hdr));
        Assert.Equal(0u, g.F("wclose", hwo));
    }
}

public sealed class WaveHostWinmmTests
{
    private static WaveRig Rig() => new(WaveOutBackendKind.HostWinmm);

    /// <summary>True (after asserting the host's honest zero-device answer) when this host has no wave device; the device tests then have nothing further to prove.</summary>
    private static bool NoDevices(WaveRig r)
    {
        if (r.Call("waveOutGetNumDevs") != 0) return false;
        Assert.True(r.Open(r.Format(1, 8000, 16)).Result is 2 or 6);
        return true;
    }

    [RequiresWindowsFact]
    public void EnumeratesTheHostDevicesWithCaps()
    {
        using var r = Rig();
        var n = r.Call("waveOutGetNumDevs");
        var caps = r.World.Buf(96);
        if (n == 0)
        {
            Assert.True(r.Call("waveOutGetDevCapsW", 0, caps, 84) is 2 or 6);
            return;
        }
        for (uint i = 0; i < n; i++)
        {
            Assert.Equal(0u, r.Call("waveOutGetDevCapsW", i, caps, 84));
            Assert.NotEmpty(r.Mem.ReadWideString(caps + 8));
            Assert.Equal(0u, r.Call("waveOutGetDevCapsA", i, caps, 52));
            Assert.NotEmpty(r.World.Cstr(caps + 8));
        }
        Assert.Equal(0u, r.Call("waveOutGetDevCapsW", 0xFFFFFFFF, caps, 84)); // WAVE_MAPPER
    }

    [RequiresWindowsFact]
    public void FormatQueryAndErrorText()
    {
        using var r = Rig();
        var buf = r.World.Buf(256);
        Assert.Equal(0u, r.Call("waveOutGetErrorTextA", 33, buf, 200));
        Assert.NotEmpty(r.World.Cstr(buf)); // the text is the host's (Wine words it differently from Windows)
        Assert.Equal(0u, r.Call("waveOutGetErrorTextW", 5, buf, 128));
        Assert.NotEmpty(r.Mem.ReadWideString(buf));
        if (NoDevices(r)) return;
        Assert.Equal(0u, r.Open(r.Format(1, 22050, 16), 1).Result);
        Assert.Equal(32u, r.Open(r.Format(1, 22050, 16, tag: 0x7777), 1).Result); // unknown codec: WAVERR_BADFORMAT
    }

    [RequiresWindowsFact]
    public void WritesSilenceAndMirrorsHeaderFlagsAndCompletion()
    {
        using var r = Rig();
        var buf = r.World.Buf(64);
        if (r.Call("waveOutGetNumDevs") == 0)
        {
            Assert.True(r.Open(r.Format(1, 8000, 16)).Result is 2 or 6);
            return;
        }
        var ev = r.World.Kernel("CreateEventA", 0, 0, 0, 0);
        var (rc, hwo) = r.Open(r.Format(1, 8000, 16), 0x50000, ev);
        Assert.Equal(0u, rc);
        Assert.NotEqual(0u, hwo);
        Assert.Equal(0u, r.World.Kernel("WaitForSingleObject", ev, 5000)); // WOM_OPEN
        var (hdr, _) = r.Buffer(new byte[1600]); // 100 ms of silence
        Assert.Equal(34u, r.Call("waveOutWrite", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
        Assert.Equal(2u, r.Flags(hdr) & 0xF);
        Assert.Equal(0u, r.Call("waveOutWrite", hwo, hdr, 32));
        var queued = r.Flags(hdr);
        Assert.True((queued & 0x12) == 0x12 || (queued & 3) == 3, $"flags 0x{queued:X}"); // PREPARED|INQUEUE, or already PREPARED|DONE on a very fast host
        Assert.True(r.WaitFlags(hdr, 1, 10000), "WHDR_DONE was not mirrored into the guest header");
        Assert.Equal(0u, r.Flags(hdr) & 0x10);
        Assert.Equal(0u, r.World.Kernel("WaitForSingleObject", ev, 5000)); // WOM_DONE signalled the guest event
        var w = Assert.Single(r.Writes);
        Assert.Equal(1600, w.Data.Length);
        r.Position(hwo, 4, out var type);
        Assert.True(type is 1 or 2 or 4);
        Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Flags(hdr) & 2);
        Assert.Equal(0u, r.Call("waveOutPause", hwo));
        Assert.Equal(0u, r.Call("waveOutRestart", hwo));
        Assert.Equal(0u, r.Call("waveOutGetVolume", hwo, buf));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
        Assert.Equal(5u, r.Call("waveOutClose", hwo));
    }

    [RequiresWindowsFact]
    public void CloseWhileQueuedIsStillPlayingAndResetReturnsTheBuffer()
    {
        using var r = Rig();
        if (NoDevices(r)) return;
        var (rc, hwo) = r.Open(r.Format(1, 8000, 16));
        Assert.Equal(0u, rc);
        var (hdr, _) = r.Buffer(new byte[16000]); // 1 s
        Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutWrite", hwo, hdr, 32));
        Assert.Equal(33u, r.Call("waveOutClose", hwo));
        Assert.Equal(33u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0u, r.Call("waveOutReset", hwo));
        Assert.True(sw.ElapsedMilliseconds < 800, "reset must not wait for the audio to play");
        Assert.Equal(1u, r.Flags(hdr) & 1);
        Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
    }

    [RequiresWindowsFact]
    public void CallbackWindowAndThreadAreRejectedExplicitly()
    {
        using var r = Rig();
        if (NoDevices(r)) return;
        Assert.Equal(8u, r.Open(r.Format(1, 8000, 16), 0x10000).Result);
        Assert.Equal(8u, r.Open(r.Format(1, 8000, 16), 0x20000).Result);
    }
}

/// <summary>[Fact] that makes REAL sound through the host audio stack. Opt-in: set BLITZCS_AUDIO_TONE_TEST=1 (scripts/wine/audio-capture.sh does, with PULSE_SINK pointing at a null sink); otherwise skipped so no test run ever plays on the speakers.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AudibleToneFactAttribute : FactAttribute
{
    public AudibleToneFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "needs a Windows host (Windows or Wine) with winmm.dll; this host is not Windows";
        else if (Environment.GetEnvironmentVariable("BLITZCS_AUDIO_TONE_TEST") != "1") Skip = "plays an audible tone: set BLITZCS_AUDIO_TONE_TEST=1 (use scripts/wine/audio-capture.sh to route it into a null sink)";
    }
}

public sealed class WaveToneTests
{
    /// <summary>1.5 s of a 440 Hz sine (amplitude 0.5) as 44.1 kHz mono 16-bit, written through HostWinmm in three buffers; the capture helper analyses what left the process.</summary>
    [AudibleToneFact]
    public void HostWinmmPlaysA440HzTone()
    {
        using var r = new WaveRig(WaveOutBackendKind.HostWinmm);
        Assert.NotEqual(0u, r.Call("waveOutGetNumDevs"));
        var ev = r.World.Kernel("CreateEventA", 0, 1, 0, 0); // manual reset
        var (rc, hwo) = r.Open(r.Format(1, 44100, 16), 0x50000, ev, device: 0xFFFFFFFF);
        Assert.Equal(0u, rc);
        const int per = 22050; // 0.5 s per buffer
        var headers = new uint[3];
        for (var b = 0; b < 3; b++)
        {
            var pcm = new byte[per * 2];
            for (var i = 0; i < per; i++) BitConverter.TryWriteBytes(pcm.AsSpan(2 * i), (short)(16384 * Math.Sin(2 * Math.PI * 440 * (b * per + i) / 44100.0)));
            headers[b] = r.Buffer(pcm).Header;
            Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, headers[b], 32));
        }
        foreach (var h in headers) Assert.Equal(0u, r.Call("waveOutWrite", hwo, h, 32));
        foreach (var h in headers) Assert.True(r.WaitFlags(h, 1, 10000), "buffer never completed");
        Assert.Equal(3 * per * 2, r.Writes.Sum(w => w.Data.Length));
        foreach (var h in headers) Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, h, 32));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
    }
}

public sealed class WaveSelectionTests
{
    [Fact]
    public void HostBackendOnANonWindowsHostFailsExplicitly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var r = new WaveRig(WaveOutBackendKind.HostWinmm);
        var ex = Assert.Throws<Win32UsageException>(() => r.Call("waveOutGetNumDevs"));
        Assert.Contains("Windows host", ex.Message);
    }

    [Fact]
    public void DefaultResolvesByHostOs()
    {
        var old = Environment.GetEnvironmentVariable("BLITZCS_AUDIO");
        try
        {
            Environment.SetEnvironmentVariable("BLITZCS_AUDIO", null);
            Assert.Equal(OperatingSystem.IsWindows() ? WaveOutBackendKind.HostWinmm : WaveOutBackendKind.None, WaveOutBackends.Resolve(new Win32Options()));
        }
        finally { Environment.SetEnvironmentVariable("BLITZCS_AUDIO", old); }
    }
}
