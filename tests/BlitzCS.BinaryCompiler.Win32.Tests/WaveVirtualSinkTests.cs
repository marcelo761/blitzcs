using System.Diagnostics;
using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class WaveVirtualSinkTests
{
    private const uint Prepared = 2, InQueue = 0x10, Done = 1, BeginLoop = 4, EndLoop = 8;

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 3)).ToArray();

    [Fact]
    public void EnumeratesOneNamedDeviceWithCapsInBothLayouts()
    {
        using var r = new WaveRig();
        Assert.Equal(1u, r.Call("waveOutGetNumDevs"));
        var a = r.World.Buf(64); var w = r.World.Buf(96);
        Assert.Equal(0u, r.Call("waveOutGetDevCapsA", 0, a, 52));
        Assert.Equal(0u, r.Call("waveOutGetDevCapsW", 0, w, 84));
        Assert.Equal("BlitzCS Virtual Output", r.World.Cstr(a + 8));
        Assert.Equal("BlitzCS Virtual Output", r.Mem.ReadWideString(w + 8));
        Assert.Equal(2, r.Mem.ReadUInt16(a + 44));          // wChannels
        Assert.Equal(2, r.Mem.ReadUInt16(w + 76));
        Assert.Equal(0xFFFFu, r.Mem.ReadUInt32(a + 40));     // dwFormats
        Assert.Equal(0u, r.Call("waveOutGetDevCapsA", 0xFFFFFFFF, a, 52)); // WAVE_MAPPER
        Assert.Equal(2u, r.Call("waveOutGetDevCapsA", 1, a, 52));           // BADDEVICEID
        Assert.Equal(11u, r.Call("waveOutGetDevCapsA", 0, 0, 52));          // INVALPARAM
        Assert.Equal(11u, r.Call("waveOutGetDevCapsA", 0, 0xDEAD0000, 52)); // unmapped
    }

    [Theory]
    [InlineData(1, 8000u, 8, 0u), InlineData(2, 48000u, 16, 0u), InlineData(1, 22050u, 16, 0u), InlineData(2, 44100u, 8, 0u),
     InlineData(3, 44100u, 16, 32u), InlineData(1, 7999u, 16, 32u), InlineData(1, 48001u, 16, 32u), InlineData(1, 44100u, 24, 32u), InlineData(1, 44100u, 32, 32u)]
    public void FormatQueryAcceptsPcmWithinLimitsAndRejectsOthersWithBadFormat(int ch, uint rate, int bits, uint expected)
    {
        using var r = new WaveRig();
        var fmt = r.Format((ushort)ch, rate, (ushort)bits);
        var (rc, h) = r.Open(fmt, 1); // WAVE_FORMAT_QUERY
        Assert.Equal(expected, rc);
        Assert.Equal(0u, h); // a query never produces a handle
    }

    [Fact]
    public void NonPcmTagAndInconsistentBlockAlignAreBadFormat()
    {
        using var r = new WaveRig();
        Assert.Equal(32u, r.Open(r.Format(1, 22050, 16, tag: 3), 1).Result);  // IEEE float
        var f = r.Format(2, 22050, 16);
        r.Mem.WriteUInt32(f + 12, 2 | 16u << 16); // nBlockAlign 2 instead of 4, bits 16
        Assert.Equal(32u, r.Open(f, 1).Result);
    }

    [Fact]
    public void OpenValidatesFlagsPointersAndUnsupportedCallbackModes()
    {
        using var r = new WaveRig();
        var f = r.Format(1, 8000, 16);
        Assert.Equal(10u, r.Open(f, 0x100).Result);                       // unknown flag bit
        Assert.Equal(8u, r.Open(f, 0x10000).Result);                      // CALLBACK_WINDOW
        Assert.Equal(8u, r.Open(f, 0x20000).Result);                      // CALLBACK_THREAD
        Assert.Contains(r.Diagnostics, d => d.Contains("CALLBACK_WINDOW"));
        Assert.Equal(8u, r.Open(f, 0x30000, 0x1000).Result);             // FUNCTION without a guest module
        Assert.Equal(5u, r.Open(f, 0x50000, 0x1234).Result);             // EVENT with a handle that is no event
        Assert.Equal(11u, r.Call("waveOutOpen", 0, 0, f, 0, 0, 0));       // phwo NULL
        Assert.Equal(11u, r.Call("waveOutOpen", r.World.Buf(4), 0, 0, 0, 0, 0)); // pwfx NULL
        Assert.Equal(11u, r.Call("waveOutOpen", r.World.Buf(4), 0, 0xDEAD0000, 0, 0, 0));
        Assert.Equal(2u, r.Open(f, 0, 0, 0, 7).Result);                   // BADDEVICEID
    }

    [Fact]
    public void BufferLifecycleFlagsTimingAndEventCallback()
    {
        using var r = new WaveRig();
        var ev = r.World.Kernel("CreateEventA", 0, 0, 0, 0);
        var (rc, hwo) = r.Open(r.Format(1, 8000, 16), 0x50000, ev); // CALLBACK_EVENT, 16000 bytes/s
        Assert.Equal(0u, rc);
        Assert.NotEqual(0u, hwo);
        Assert.Equal(0u, r.World.Kernel("WaitForSingleObject", ev, 0)); // WOM_OPEN signalled the event
        var pcm = Ramp(1600); // 100 ms
        var (hdr, _) = r.Buffer(pcm);

        Assert.Equal(34u, r.Call("waveOutWrite", hwo, hdr, 32)); // unprepared
        Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
        Assert.Equal(Prepared, r.Flags(hdr));
        var sw = Stopwatch.StartNew();
        Assert.Equal(0u, r.Call("waveOutWrite", hwo, hdr, 32));
        Assert.Equal(Prepared | InQueue, r.Flags(hdr));
        Assert.Equal(33u, r.Call("waveOutWrite", hwo, hdr, 32));          // still queued
        Assert.Equal(33u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32)); // cannot unprepare while queued
        Assert.Equal(33u, r.Call("waveOutClose", hwo));                    // cannot close while playing
        Assert.Equal(0u, r.World.Kernel("WaitForSingleObject", ev, 2000)); // WOM_DONE
        Assert.True(r.WaitFlags(hdr, Done, 2000));
        sw.Stop();
        Assert.Equal(Prepared | Done, r.Flags(hdr));
        Assert.InRange(sw.ElapsedMilliseconds, 85, 400);                  // real time: 100 ms of audio
        Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
        Assert.Equal(Done, r.Flags(hdr));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
        Assert.Equal(5u, r.Call("waveOutClose", hwo));                     // handle is gone for good
        Assert.Equal(5u, r.Call("waveOutWrite", hwo, hdr, 32));
        var w = Assert.Single(r.Writes);
        Assert.Equal(pcm, w.Data);
        Assert.Equal(8000u, w.Format.SamplesPerSec);
        Assert.Equal(hdr, w.HeaderAddress);
    }

    [Fact]
    public void BadHeaderPointersAndSizes()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(1, 8000, 16));
        Assert.Equal(11u, r.Call("waveOutPrepareHeader", hwo, 0, 32));
        Assert.Equal(11u, r.Call("waveOutPrepareHeader", hwo, 0xDEAD0000, 32));
        var (hdr, _) = r.Buffer(new byte[16]);
        Assert.Equal(11u, r.Call("waveOutPrepareHeader", hwo, hdr, 31));      // cbwh too small
        r.Mem.WriteUInt32(hdr, 0xDEAD0000);                                  // lpData unmapped
        Assert.Equal(11u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));    // never prepared: success, as on Windows
        Assert.Equal(11u, r.Call("waveOutGetPosition", hwo, r.World.Buf(12), 8));
        Assert.Equal(11u, r.Call("waveOutGetPosition", hwo, 0, 12));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
    }

    [Fact]
    public void WriteOfAHalfFrameIsInvalidParam()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(2, 8000, 16)); // block align 4
        var (hdr, _) = r.Buffer(new byte[6]);
        Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
        Assert.Equal(11u, r.Call("waveOutWrite", hwo, hdr, 32));
        Assert.Equal(Prepared, r.Flags(hdr)); // the failed write left no INQUEUE bit
        Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
    }

    [Fact]
    public void PositionArithmeticInBytesSamplesAndMilliseconds()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(2, 8000, 16)); // 32000 bytes/s, 4-byte frames
        var (hdr, _) = r.Buffer(new byte[3200]);       // 100 ms
        Assert.Equal(0u, r.Position(hwo, 4, out var t));
        Assert.Equal(4u, t);
        r.Call("waveOutPrepareHeader", hwo, hdr, 32);
        r.Call("waveOutWrite", hwo, hdr, 32);
        Assert.True(r.WaitFlags(hdr, Done, 2000));
        Assert.Equal(3200u, r.Position(hwo, 4, out t)); Assert.Equal(4u, t);   // TIME_BYTES
        Assert.Equal(800u, r.Position(hwo, 2, out t)); Assert.Equal(2u, t);    // TIME_SAMPLES
        Assert.Equal(100u, r.Position(hwo, 1, out t)); Assert.Equal(1u, t);    // TIME_MS
        Assert.Equal(3200u, r.Position(hwo, 0x20, out t)); Assert.Equal(4u, t); // TIME_TICKS unsupported: falls back to bytes, as Windows does
        Assert.Equal(0u, r.Call("waveOutReset", hwo));
        Assert.Equal(0u, r.Position(hwo, 4, out _)); // reset rewinds the position
        r.Call("waveOutUnprepareHeader", hwo, hdr, 32);
        r.Call("waveOutClose", hwo);
    }

    [Fact]
    public void PauseStopsTheClockAndRestartResumes()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(1, 8000, 16));
        var (hdr, _) = r.Buffer(new byte[1600]); // 100 ms
        r.Call("waveOutPrepareHeader", hwo, hdr, 32);
        Assert.Equal(0u, r.Call("waveOutPause", hwo));
        r.Call("waveOutWrite", hwo, hdr, 32);
        Thread.Sleep(250);
        Assert.Equal(0u, r.Flags(hdr) & Done);
        Assert.Equal(0u, r.Position(hwo, 4, out _));
        var sw = Stopwatch.StartNew();
        Assert.Equal(0u, r.Call("waveOutRestart", hwo));
        Assert.True(r.WaitFlags(hdr, Done, 2000));
        Assert.InRange(sw.ElapsedMilliseconds, 85, 400);
        r.Call("waveOutUnprepareHeader", hwo, hdr, 32);
        r.Call("waveOutClose", hwo);
    }

    [Fact]
    public void ResetReturnsEveryPendingBufferSynchronouslyAndTapsThePlayedPrefix()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(1, 8000, 16));
        var pcm = Ramp(16000); // 1 s
        var (h1, _) = r.Buffer(pcm); var (h2, _) = r.Buffer(pcm);
        foreach (var h in new[] { h1, h2 }) { r.Call("waveOutPrepareHeader", hwo, h, 32); r.Call("waveOutWrite", hwo, h, 32); }
        Thread.Sleep(120);
        Assert.Equal(0u, r.Call("waveOutReset", hwo));
        foreach (var h in new[] { h1, h2 })
        {
            Assert.Equal(Prepared | Done, r.Flags(h)); // done immediately, INQUEUE clear
            Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, h, 32));
        }
        var w = Assert.Single(r.Writes);
        Assert.InRange(w.Data.Length, 1000, 6000);       // roughly the 120 ms that played, frame aligned
        Assert.Equal(0, w.Data.Length % 2);
        Assert.Equal(pcm.AsSpan(0, w.Data.Length).ToArray(), w.Data);
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
    }

    [Fact]
    public void LoopsRepeatTheMarkedBlockDwLoopsTimes()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(1, 8000, 8)); // 8000 bytes/s
        var a = Ramp(400); var b = Enumerable.Range(0, 400).Select(i => (byte)(255 - i)).ToArray();
        var (ha, _) = r.Buffer(a, BeginLoop, 3);      // BEGINLOOP with 3 passes
        var (hb, _) = r.Buffer(b, EndLoop);
        var (hc, _) = r.Buffer(new byte[80]);          // plays once after the loop
        foreach (var h in new[] { ha, hb, hc }) { r.Call("waveOutPrepareHeader", hwo, h, 32); r.Call("waveOutWrite", hwo, h, 32); }
        var sw = Stopwatch.StartNew();
        Assert.True(r.WaitFlags(hc, Done, 3000));
        Assert.InRange(sw.ElapsedMilliseconds, 280, 700);   // (400+400)*3 + 80 = 2480 bytes = 310 ms
        Assert.True(r.WaitFlags(ha, Done, 100)); Assert.True(r.WaitFlags(hb, Done, 100));
        var expected = a.Concat(b).Concat(a).Concat(b).Concat(a).Concat(b).Concat(new byte[80]).ToArray();
        Assert.Equal(expected, r.Writes.SelectMany(w => w.Data).ToArray());
        Assert.Equal(7, r.Writes.Count);
        Assert.Equal(2480u, r.Position(hwo, 4, out _));
        foreach (var h in new[] { ha, hb, hc }) r.Call("waveOutUnprepareHeader", hwo, h, 32);
        r.Call("waveOutClose", hwo);
    }

    [Fact]
    public void BreakLoopEndsTheLoopAfterTheCurrentPass()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(1, 8000, 8));
        var (h, _) = r.Buffer(new byte[800], BeginLoop | EndLoop, 1000); // 100 ms per pass, 100 s requested
        r.Call("waveOutPrepareHeader", hwo, h, 32); r.Call("waveOutWrite", hwo, h, 32);
        Thread.Sleep(150);
        Assert.Equal(0u, r.Call("waveOutBreakLoop", hwo));
        Assert.True(r.WaitFlags(h, Done, 1500));
        Assert.InRange(r.Writes.Count, 2, 4);
        r.Call("waveOutUnprepareHeader", hwo, h, 32); r.Call("waveOutClose", hwo);
    }

    [Fact]
    public void WavRecordingIsAValidRiffThatContainsExactlyWhatWasWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), "blitz-wave-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            byte[] one = Ramp(641), two = Ramp(960).Reverse().ToArray();
            using (var r = new WaveRig(record: path))
            {
                var (_, hwo) = r.Open(r.Format(1, 16000, 8));
                var (h1, _) = r.Buffer(one); var (h2, _) = r.Buffer(two);
                foreach (var h in new[] { h1, h2 }) { r.Call("waveOutPrepareHeader", hwo, h, 32); r.Call("waveOutWrite", hwo, h, 32); }
                Assert.True(r.WaitFlags(h2, Done, 3000));
                r.Call("waveOutClose", hwo);
            }
            var f = File.ReadAllBytes(path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(f, 0, 4));
            Assert.Equal((uint)(f.Length - 8), BitConverter.ToUInt32(f, 4));
            Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(f, 8, 8));
            Assert.Equal(16u, BitConverter.ToUInt32(f, 16));
            Assert.Equal(1, BitConverter.ToUInt16(f, 20));
            Assert.Equal(1, BitConverter.ToUInt16(f, 22));
            Assert.Equal(16000u, BitConverter.ToUInt32(f, 24));
            Assert.Equal(16000u, BitConverter.ToUInt32(f, 28));
            Assert.Equal(8, BitConverter.ToUInt16(f, 34));
            Assert.Equal("data", Encoding.ASCII.GetString(f, 36, 4));
            var n = (int)BitConverter.ToUInt32(f, 40);
            Assert.Equal(641 + 960, n);
            Assert.Equal(one.Concat(two).ToArray(), f.AsSpan(44, n).ToArray());
            Assert.Equal(44 + n + 1, f.Length); // odd data length: RIFF pad byte
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VolumeIsStatePerDeviceAndErrorTextComesFromTheTable()
    {
        using var r = new WaveRig();
        var p = r.World.Buf(4);
        Assert.Equal(0u, r.Call("waveOutGetVolume", 0, p)); Assert.Equal(0xFFFFFFFFu, r.Mem.ReadUInt32(p));
        Assert.Equal(0u, r.Call("waveOutSetVolume", 0, 0x40008000));
        var (_, hwo) = r.Open(r.Format(1, 8000, 8));
        Assert.Equal(0u, r.Call("waveOutGetVolume", hwo, p)); Assert.Equal(0x40008000u, r.Mem.ReadUInt32(p));
        Assert.Equal(11u, r.Call("waveOutGetVolume", hwo, 0));
        Assert.Equal(2u, r.Call("waveOutGetVolume", 5, p));
        var buf = r.World.Buf(200);
        Assert.Equal(0u, r.Call("waveOutGetErrorTextA", 33, buf, 200));
        Assert.StartsWith("Cannot perform this operation while media data is still playing", r.World.Cstr(buf));
        Assert.Equal(0u, r.Call("waveOutGetErrorTextW", 5, buf, 200));
        Assert.StartsWith("The specified device handle is invalid", r.Mem.ReadWideString(buf));
        Assert.Equal(0u, r.Call("waveOutGetErrorTextA", 33, buf, 5));
        Assert.Equal("Cann", r.World.Cstr(buf)); // truncated, NUL terminated
        Assert.Equal(9u, r.Call("waveOutGetErrorTextA", 999, buf, 200));
        Assert.Equal(0u, r.Call("waveOutGetID", hwo, p)); Assert.Equal(0u, r.Mem.ReadUInt32(p));
        r.Call("waveOutClose", hwo);
    }

    [Fact]
    public void EnvironmentOverrideBeatsTheOptionAndRejectsUnknownValues()
    {
        var old = Environment.GetEnvironmentVariable("BLITZCS_AUDIO");
        try
        {
            Environment.SetEnvironmentVariable("BLITZCS_AUDIO", "none");
            using (var r = new WaveRig(WaveOutBackendKind.VirtualSink)) Assert.Equal(0u, r.Call("waveOutGetNumDevs"));
            Environment.SetEnvironmentVariable("BLITZCS_AUDIO", "virtual");
            using (var r = new WaveRig(WaveOutBackendKind.None)) Assert.Equal(1u, r.Call("waveOutGetNumDevs"));
            Environment.SetEnvironmentVariable("BLITZCS_AUDIO", "loud");
            using (var r = new WaveRig()) Assert.Throws<Win32UsageException>(() => r.Call("waveOutGetNumDevs"));
        }
        finally { Environment.SetEnvironmentVariable("BLITZCS_AUDIO", old); }
    }

    [Fact]
    public void ManyThreadsManyBuffersAllCompleteAndAccountForEveryByte()
    {
        using var r = new WaveRig();
        const int streams = 8, buffers = 40, size = 480; // mono 8-bit 48 kHz: 10 ms per buffer
        var errors = new List<string>();
        var threads = Enumerable.Range(0, streams).Select(t => new Thread(() =>
        {
            try
            {
                var (rc, hwo) = r.Open(r.Format(1, 48000, 8));
                if (rc != 0) throw new Exception("open " + rc);
                var hs = Enumerable.Range(0, buffers).Select(i => r.Buffer(Enumerable.Repeat((byte)(t * 16 + i % 16), size).ToArray()).Header).ToArray();
                foreach (var h in hs) { if (r.Call("waveOutPrepareHeader", hwo, h, 32) != 0) throw new Exception("prepare"); }
                foreach (var h in hs) { if (r.Call("waveOutWrite", hwo, h, 32) != 0) throw new Exception("write"); }
                foreach (var h in hs) { if (!r.WaitFlags(h, Done, 10000)) throw new Exception("not done"); }
                foreach (var h in hs) { if (r.Call("waveOutUnprepareHeader", hwo, h, 32) != 0) throw new Exception("unprepare"); }
                if (r.Call("waveOutClose", hwo) != 0) throw new Exception("close");
            }
            catch (Exception e) { lock (errors) errors.Add(e.Message); }
        })).ToList();
        var sw = Stopwatch.StartNew();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.Equal(streams * buffers, r.Writes.Count);
        Assert.Equal(streams * buffers * size, r.Writes.Sum(w => w.Data.Length));
        Assert.InRange(sw.ElapsedMilliseconds, 380, 8000); // 400 ms of audio per stream, streams run concurrently
    }

    private static byte[] Sine(int hz, int frames, int rate)
    {
        var b = new byte[frames * 2];
        for (var i = 0; i < frames; i++) BitConverter.TryWriteBytes(b.AsSpan(2 * i), (short)(12000 * Math.Sin(2 * Math.PI * hz * i / rate)));
        return b;
    }

    private static double Tone(short[] x, int from, int count, int hz, int rate)
    {
        double k = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
        for (var i = from; i < from + count; i++) { var s0 = x[i] + k * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(s1 * s1 + s2 * s2 - k * s1 * s2) * 2 / count;
    }

    [Fact]
    public void ABufferRewrittenInPlaceWhilePlayingIsRecordedWithTheLateValues()
    {
        // F-11: the original mixer keeps one looped WAVEHDR buffer and rewrites it behind the device. The sink must read the memory when each slice is consumed.
        const int rate = 8000;
        var path = Path.Combine(Path.GetTempPath(), "blitz-rewrite-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var r = new WaveRig(record: path))
            {
                var (_, hwo) = r.Open(r.Format(1, rate, 16));
                var early = Sine(500, 800, rate);          // 100 ms loop block
                var (hdr, data) = r.Buffer(early, BeginLoop | EndLoop, 100000);
                Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
                var sw = Stopwatch.StartNew();
                Assert.Equal(0u, r.Call("waveOutWrite", hwo, hdr, 32));
                var rewriter = new Thread(() => { while (sw.ElapsedMilliseconds < 500) Thread.Sleep(1); r.Mem.Write(data, Sine(1000, 800, rate)); });
                rewriter.Start();
                while (sw.ElapsedMilliseconds < 1000) Thread.Sleep(5);
                rewriter.Join();
                Assert.Equal(0u, r.Call("waveOutBreakLoop", hwo));
                Assert.True(r.WaitFlags(hdr, Done, 2000));
                Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
                r.Call("waveOutClose", hwo);
            }
            var f = File.ReadAllBytes(path);
            var n = (int)BitConverter.ToUInt32(f, 40) / 2;
            var pcm = new short[n];
            Buffer.BlockCopy(f, 44, pcm, 0, n * 2);
            Assert.True(n > rate * 0.9, $"{n} samples recorded");
            Assert.True(Tone(pcm, 0, 2400, 500, rate) > 20 * Tone(pcm, 0, 2400, 1000, rate), "first 300 ms must carry the early 500 Hz values");
            Assert.True(Tone(pcm, 5600, 2000, 1000, rate) > 20 * Tone(pcm, 5600, 2000, 500, rate), "samples after the rewrite must carry the late 1000 Hz values");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ObserverSlicesAreConsumeTimeAndTheAggregatedViewStillCarriesWholePasses()
    {
        var slices = new System.Collections.Concurrent.ConcurrentQueue<WaveOutWrite>();
        var w = new World(new Win32Options { WaveOut = WaveOutBackendKind.VirtualSink, WaveOutSliceObserver = slices.Enqueue, WaveOutObserver = null });
        using var rig = w;
        var f = w.Buf(32); w.Mem.Write(f, [1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0]);
        var phwo = w.Buf(4);
        Assert.Equal(0u, w.Call("WINMM.dll", "waveOutOpen", phwo, 0, f, 0, 0, 0));
        var hwo = w.Mem.ReadUInt32(phwo);
        var d = w.Buf(3200); var h = w.Buf(32);
        w.Mem.WriteUInt32(h, d); w.Mem.WriteUInt32(h + 4, 3200);
        w.Call("WINMM.dll", "waveOutPrepareHeader", hwo, h, 32);
        w.Call("WINMM.dll", "waveOutWrite", hwo, h, 32);
        var sw = Stopwatch.StartNew();
        while ((w.Mem.ReadUInt32(h + 16) & 1) == 0 && sw.ElapsedMilliseconds < 2000) Thread.Sleep(1);
        Assert.InRange(slices.Count, 15, 60);                          // 200 ms in slices of at most 10 ms, produced every ~5 ms
        Assert.All(slices, s => Assert.True(s.Data.Length <= 160 + 2));
        Assert.Equal(3200, slices.Sum(s => s.Data.Length));
        var times = slices.Select(s => s.StreamTimeMs).ToArray();
        Assert.Equal(times.OrderBy(t => t), times);
        w.Call("WINMM.dll", "waveOutUnprepareHeader", hwo, h, 32);
        w.Call("WINMM.dll", "waveOutClose", hwo);
    }

    [Fact]
    public void AGuestThatFreesTheBufferWhilePlayingMakesTheSinkStopReadingAndCompleteSafely()
    {
        using var r = new WaveRig();
        var (_, hwo) = r.Open(r.Format(1, 8000, 16));
        var alloc = r.Mem.Allocate(32000);                         // 2 s of audio
        var hdr = r.Header(alloc.Address, 32000);
        Assert.Equal(0u, r.Call("waveOutPrepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutWrite", hwo, hdr, 32));
        Thread.Sleep(100);
        alloc.Dispose();                                           // buggy guest: free while queued
        Assert.True(r.WaitFlags(hdr, Done, 1000), "the sink must complete the buffer instead of reading freed memory");
        Assert.Equal(0u, r.Flags(hdr) & InQueue);
        Assert.Equal(0u, r.Call("waveOutUnprepareHeader", hwo, hdr, 32));
        Assert.Equal(0u, r.Call("waveOutClose", hwo));
        Assert.True(r.Writes.Sum(w => w.Data.Length) < 32000);
    }
}
