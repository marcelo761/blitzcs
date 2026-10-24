using System.Diagnostics;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// A software audio device available on every OS: "BlitzCS Virtual Output". PCM 8/16-bit, 1-2 channels, 8000-48000 Hz. Buffers are CONSUMED at real-time rate: a worker thread per stream completes a buffer when its
/// playback duration has elapsed (pause stops the clock, reset returns everything, WHDR_BEGINLOOP/ENDLOOP with dwLoops repeat a block). Each consumed pass is offered to the observer and the WAV recorder.
/// The device volume is state only (get returns what set stored); the tap sees the samples before any volume.
/// </summary>
public sealed class VirtualSinkBackend : IWaveOutBackend
{
    public const string DeviceName = "BlitzCS Virtual Output";
    private readonly WaveOutObserver? observer;
    private readonly WavRecorder? recorder;
    private uint volume = 0xFFFFFFFF;
    private int disposed;
    internal readonly System.Collections.Concurrent.ConcurrentQueue<Exception> faults = new();

    /// <summary>Exceptions thrown by the observer, the recorder or a sink notification on a worker thread (kept, never dropped).</summary>
    public IReadOnlyCollection<Exception> Faults => faults;

    public VirtualSinkBackend(WaveOutObserver? observer = null, WavRecorder? recorder = null)
    {
        this.observer = observer;
        this.recorder = recorder;
    }

    public string Name => "VirtualSink";
    public uint DeviceCount => 1;
    public string? GetErrorText(uint code) => WaveOutErrors.Text(code);
    public uint GetVolume(uint deviceId, out uint v) { v = Volatile.Read(ref volume); return Check(deviceId); }
    public uint SetVolume(uint deviceId, uint v) { var r = Check(deviceId); if (r == 0) Volatile.Write(ref volume, v); return r; }
    private static uint Check(uint id) => id is 0 or WaveOutConst.WaveMapper ? 0 : WaveOutConst.MmsyserrBadDeviceId;

    public uint GetCaps(uint deviceId, out WaveOutCaps? caps)
    {
        caps = null;
        if (Check(deviceId) is var r and not 0) return r;
        caps = new WaveOutCaps(0xFFFF, 0xFFFF, 0x0100, DeviceName, 0xFFFF, 2, 4 | 8 | 0x20);
        return 0;
    }

    /// <summary>True when the format is one this device plays.</summary>
    public static bool Supports(WaveOutFormat f) =>
        f.IsPcm && f.Channels is 1 or 2 && f.BitsPerSample is 8 or 16 && f.SamplesPerSec is >= 8000 and <= 48000 && f.BlockAlign == f.Channels * f.BitsPerSample / 8;

    public uint Open(uint deviceId, WaveOutFormat format, uint flags, IWaveOutSink sink, out IWaveOutDevice? device)
    {
        device = null;
        if (Volatile.Read(ref disposed) != 0) return WaveOutConst.MmsyserrNoDriver;
        if (Check(deviceId) is var r and not 0) return r;
        if (!Supports(format)) return WaveOutConst.WaverrBadFormat;
        if ((flags & WaveOutConst.WaveFormatQuery) != 0) return 0;
        device = new VirtualDevice(this, format, sink);
        return 0;
    }

    internal void Emit(WaveOutWrite w)
    {
        recorder?.Append(w);
        observer?.Invoke(w);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        recorder?.Dispose();
    }

    private sealed class Item(WaveOutBuffer buffer, byte[] data)
    {
        public readonly WaveOutBuffer Buffer = buffer;
        public readonly byte[] Data = data;
        public long Pos;
    }

    private abstract record Outbound;
    private sealed record Emission(WaveOutWrite Write) : Outbound;
    private sealed record Completion(WaveOutBuffer Buffer) : Outbound;

    private sealed class VirtualDevice : IWaveOutDevice
    {
        private readonly VirtualSinkBackend owner;
        private readonly WaveOutFormat format;
        private readonly IWaveOutSink sink;
        private readonly object gate = new();
        private readonly List<Item> queue = [];
        private readonly List<Outbound> outbox = [];
        private readonly double bytesPerTick;
        private readonly int blockAlign;
        private readonly Thread thread;
        private int cur;
        private int loopStart = -1;
        private uint loopLeft;
        private bool paused, closed;
        private long lastPump;
        private double budget;
        private long played;

        public VirtualDevice(VirtualSinkBackend owner, WaveOutFormat format, IWaveOutSink sink)
        {
            this.owner = owner; this.format = format; this.sink = sink;
            blockAlign = format.BlockAlign;
            bytesPerTick = (double)format.SamplesPerSec * blockAlign / Stopwatch.Frequency;
            lastPump = Stopwatch.GetTimestamp();
            thread = new Thread(Run) { IsBackground = true, Name = "blitzcs-virtual-waveout" };
            thread.Start();
        }

        public uint DeviceId => 0;

        public uint Prepare(WaveOutBuffer b) => 0;
        public uint Unprepare(WaveOutBuffer b) => 0;

        public uint Write(WaveOutBuffer b)
        {
            if (b.Length % (uint)blockAlign != 0) return WaveOutConst.MmsyserrInvalParam;
            byte[] data;
            try { data = b.ReadData(); }
            catch (Exception e) when (e is BlitzCS.BinaryCompiler.Runtime.GuestMemoryException) { return WaveOutConst.MmsyserrInvalParam; }
            lock (gate)
            {
                if (closed) return WaveOutConst.MmsyserrInvalHandle;
                Pump(Stopwatch.GetTimestamp());
                queue.Add(new Item(b, data));
                Monitor.PulseAll(gate);
            }
            return 0;
        }

        private double Ticks(long now) => now - lastPump;

        /// <summary>Advances the play clock to <paramref name="now"/>; fills the outbox. Caller holds the gate.</summary>
        private void Pump(long now)
        {
            if (!paused && cur < queue.Count) budget += Ticks(now) * bytesPerTick;
            lastPump = now;
            while (cur < queue.Count && !paused)
            {
                var it = queue[cur];
                var remain = it.Data.Length - it.Pos;
                if (budget >= remain)
                {
                    budget -= remain; played += remain;
                    outbox.Add(new Emission(MakeWrite(it, it.Data.Length, 0)));
                    it.Pos = 0;
                    EndPass(it);
                }
                else
                {
                    var n = (long)budget / blockAlign * blockAlign;
                    budget -= n; it.Pos += n; played += n;
                    break;
                }
            }
            if (cur >= queue.Count) budget = 0;
            if (outbox.Count > 0) Monitor.PulseAll(gate);
        }

        private WaveOutWrite MakeWrite(Item it, int length, int from) =>
            new(0, it.Buffer.HeaderAddress, format, (played - length + from) * 1000 / (format.SamplesPerSec * (long)blockAlign), it.Data.AsSpan(from, length).ToArray());

        private void EndPass(Item it)
        {
            var flags = it.Buffer.Flags;
            if (loopStart < 0 && (flags & WaveOutConst.WhdrBeginLoop) != 0 && it.Buffer.Loops > 1) { loopStart = cur; loopLeft = it.Buffer.Loops; }
            if (loopStart >= 0)
            {
                if ((flags & WaveOutConst.WhdrEndLoop) == 0) { cur++; return; }
                if (--loopLeft > 0) { cur = loopStart; return; }
                for (var i = loopStart; i <= cur; i++) outbox.Add(new Completion(queue[i].Buffer));
                queue.RemoveRange(loopStart, cur - loopStart + 1);
                loopStart = -1; cur = 0;
                return;
            }
            outbox.Add(new Completion(it.Buffer));
            queue.RemoveAt(cur);
        }

        private int WaitMs()
        {
            if (paused || cur >= queue.Count) return Timeout.Infinite;
            var it = queue[cur];
            var missing = it.Data.Length - it.Pos - budget;
            return (int)Math.Clamp(Math.Ceiling(missing / (bytesPerTick * Stopwatch.Frequency) * 1000), 1, 1000);
        }

        private void Run()
        {
            while (true)
            {
                Outbound[] batch;
                lock (gate)
                {
                    if (closed) return;
                    Pump(Stopwatch.GetTimestamp());
                    if (outbox.Count == 0) { Monitor.Wait(gate, WaitMs()); continue; }
                    batch = [.. outbox];
                    outbox.Clear();
                }
                Deliver(batch);
            }
        }

        private void Fault(Exception e) => owner.faults.Enqueue(e);

        public uint Reset()
        {
            Outbound[] batch;
            lock (gate)
            {
                Pump(Stopwatch.GetTimestamp());
                if (cur < queue.Count && queue[cur].Pos > 0)
                {
                    var it = queue[cur];
                    outbox.Add(new Emission(MakeWrite(it, (int)it.Pos, 0)));
                }
                foreach (var it in queue) outbox.Add(new Completion(it.Buffer));
                queue.Clear();
                cur = 0; loopStart = -1; budget = 0; played = 0;
                batch = [.. outbox];
                outbox.Clear();
            }
            // As on Windows, every pending buffer is marked done before Reset returns: the completions run on the calling thread (no backend lock is held).
            Deliver(batch);
            return 0;
        }

        private void Deliver(Outbound[] batch)
        {
            foreach (var o in batch)
            {
                try
                {
                    if (o is Emission e) owner.Emit(e.Write); else sink.BufferDone(((Completion)o).Buffer);
                }
                catch (Exception ex) { Fault(ex); }
            }
        }

        public uint Pause() { lock (gate) { Pump(Stopwatch.GetTimestamp()); paused = true; } return 0; }

        public uint Restart() { lock (gate) { paused = false; lastPump = Stopwatch.GetTimestamp(); Monitor.PulseAll(gate); } return 0; }

        public uint BreakLoop() { lock (gate) { if (loopStart >= 0) loopLeft = 1; } return 0; }

        public uint GetPosition(uint type, out uint typeReturned, out uint value)
        {
            long p;
            lock (gate) { Pump(Stopwatch.GetTimestamp()); p = played; }
            var rate = format.SamplesPerSec;
            switch (type)
            {
                case WaveOutConst.TimeMs: typeReturned = type; value = unchecked((uint)(p / blockAlign * 1000 / rate)); break;
                case WaveOutConst.TimeSamples: typeReturned = type; value = unchecked((uint)(p / blockAlign)); break;
                default: typeReturned = WaveOutConst.TimeBytes; value = unchecked((uint)p); break;
            }
            return 0;
        }

        public uint GetVolume(out uint volume) => owner.GetVolume(0, out volume);
        public uint SetVolume(uint volume) => owner.SetVolume(0, volume);

        public uint Close()
        {
            lock (gate)
            {
                if (queue.Count > 0) return WaveOutConst.WaverrStillPlaying;
                closed = true;
                Monitor.PulseAll(gate);
            }
            if (Thread.CurrentThread != thread) thread.Join(2000);
            return 0;
        }

        public void Dispose()
        {
            lock (gate) { closed = true; Monitor.PulseAll(gate); }
            if (Thread.CurrentThread != thread) thread.Join(2000);
        }
    }
}
