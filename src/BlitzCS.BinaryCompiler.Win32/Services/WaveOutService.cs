using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Per-context waveOut state: the lazily created backend.</summary>
internal sealed class WaveOutState(Win32Context ctx) : IDisposable
{
    private readonly object gate = new();
    private IWaveOutBackend? backend;
    public IWaveOutBackend Backend { get { lock (gate) return backend ??= WaveOutBackends.Create(ctx.Options); } }
    public void Dispose() { lock (gate) { backend?.Dispose(); backend = null; } }
}

/// <summary>
/// One open waveOut handle: bridges the guest's 32-bit world (WAVEHDR in guest memory, HWAVEOUT id, callback target) and an <see cref="IWaveOutDevice"/>.
/// Locking: <c>gate</c> only guards the header table and the QUEUED/PREPARED bookkeeping; it is never held while calling the device or guest code.
/// </summary>
internal sealed class WaveOutStream : IWaveOutSink, IDisposable
{
    private sealed class Header(WaveOutBuffer buffer) { public readonly WaveOutBuffer Buffer = buffer; public bool Queued; }

    private readonly object gate = new();
    private readonly Dictionary<uint, Header> headers = [];
    private int queued;
    public readonly Win32Context Ctx;
    public readonly IWin32Memory Mem;
    private readonly GuestModule? module;
    private readonly uint callbackKind, callbackTarget, instance;
    private readonly KernelEvent? ev;
    private bool closed;

    public IWaveOutDevice Device = null!;
    public uint Handle;

    public WaveOutStream(Win32Context ctx, IWin32Memory mem, GuestModule? module, uint callbackKind, uint callbackTarget, uint instance, KernelEvent? ev)
    {
        Ctx = ctx; Mem = mem; this.module = module; this.callbackKind = callbackKind; this.callbackTarget = callbackTarget; this.instance = instance; this.ev = ev;
    }

    private bool TryWriteFlags(uint header, Func<uint, uint> change)
    {
        try { Mem.WriteUInt32(header + 16, change(Mem.ReadUInt32(header + 16))); return true; }
        catch (Exception e) when (e is GuestMemoryException or ObjectDisposedException) { return false; }
    }

    public uint Prepare(uint header, uint cbwh)
    {
        if (cbwh < WaveOutConst.HeaderSize32 || header == 0 || !Mem.IsMapped(header, WaveOutConst.HeaderSize32)) return WaveOutConst.MmsyserrInvalParam;
        var data = Mem.ReadUInt32(header);
        var length = Mem.ReadUInt32(header + 4);
        if (data == 0 || !Mem.IsMapped(data, length)) return WaveOutConst.MmsyserrInvalParam;
        var buffer = new WaveOutBuffer(Mem, header) { DataAddress = data, PreparedLength = length, Length = length };
        lock (gate)
        {
            if (closed) return WaveOutConst.MmsyserrInvalHandle;
            if (headers.ContainsKey(header)) return 0; // already prepared: Windows accepts it again
            headers[header] = new Header(buffer);
        }
        var r = Device.Prepare(buffer);
        if (r == 0) { TryWriteFlags(header, f => (f | WaveOutConst.WhdrPrepared) & ~WaveOutConst.WhdrDone); return 0; }
        lock (gate) headers.Remove(header);
        return r;
    }

    public uint Unprepare(uint header, uint cbwh)
    {
        if (cbwh < WaveOutConst.HeaderSize32 || header == 0 || !Mem.IsMapped(header, WaveOutConst.HeaderSize32)) return WaveOutConst.MmsyserrInvalParam;
        Header? h;
        lock (gate)
        {
            if (!headers.TryGetValue(header, out h)) return 0; // not prepared: Windows reports success
            if (h.Queued) return WaveOutConst.WaverrStillPlaying;
        }
        var r = Device.Unprepare(h.Buffer);
        if (r != 0) return r;
        lock (gate) { headers.Remove(header); TryWriteFlags(header, f => f & ~WaveOutConst.WhdrPrepared); }
        return 0;
    }

    public uint Write(uint header, uint cbwh)
    {
        if (cbwh < WaveOutConst.HeaderSize32 || header == 0 || !Mem.IsMapped(header, WaveOutConst.HeaderSize32)) return WaveOutConst.MmsyserrInvalParam;
        Header h;
        lock (gate)
        {
            if (!headers.TryGetValue(header, out h!)) return WaveOutConst.WaverrUnprepared;
            if (h.Queued) return WaveOutConst.WaverrStillPlaying;
            var b = h.Buffer;
            var length = Mem.ReadUInt32(header + 4);
            if (length > b.PreparedLength) return WaveOutConst.MmsyserrInvalParam;
            b.Length = length;
            b.User = Mem.ReadUInt32(header + 12);
            b.Flags = Mem.ReadUInt32(header + 16);
            b.Loops = Mem.ReadUInt32(header + 20);
            h.Queued = true; queued++;
            TryWriteFlags(header, f => (f | WaveOutConst.WhdrInQueue) & ~WaveOutConst.WhdrDone);
        }
        var r = Device.Write(h.Buffer);
        if (r != 0)
            lock (gate)
            {
                if (h.Queued) { h.Queued = false; queued--; }
                TryWriteFlags(header, f => f & ~WaveOutConst.WhdrInQueue);
            }
        return r;
    }

    /// <summary>Called by the backend (any thread, no backend lock held): WHDR_DONE into the guest header first, then the guest's notification.</summary>
    public void BufferDone(WaveOutBuffer buffer)
    {
        lock (gate)
        {
            if (!headers.TryGetValue(buffer.HeaderAddress, out var h) || h.Buffer != buffer || !h.Queued) return;
            h.Queued = false; queued--;
            // Under the gate: Unprepare cannot succeed (and the guest cannot free the header) while this update runs; it is refused for queued headers.
            TryWriteFlags(buffer.HeaderAddress, f => (f | WaveOutConst.WhdrDone) & ~WaveOutConst.WhdrInQueue);
            Monitor.PulseAll(gate);
        }
        Notify(WaveOutConst.WomDone, buffer.HeaderAddress, 0);
    }

    public void Notify(uint message, uint param1, uint param2)
    {
        try
        {
            if (callbackKind == WaveOutConst.CallbackFunction) module!.CallAddress(callbackTarget, Handle, message, instance, param1, param2);
            else if (callbackKind == WaveOutConst.CallbackEvent) ev!.Set();
        }
        catch (ObjectDisposedException) { } // the module is gone: there is no one to notify
        catch (Exception e) { Kernel32ExtraService.RecordFault(Ctx, e); }
    }

    public uint Reset()
    {
        var r = Device.Reset();
        if (r != 0) return r;
        // Windows marks every pending buffer done before waveOutReset returns. The backend normally completes them on this thread; a backend that completes on its own thread gets a bounded wait.
        var deadline = Environment.TickCount64 + 2000;
        lock (gate)
            while (queued > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0) { Ctx.Options.DebugOutput?.Invoke($"waveOutReset: {queued} buffer(s) were not returned by the backend within 2 s"); return WaveOutConst.MmsyserrError; }
                Monitor.Wait(gate, (int)left);
            }
        return 0;
    }

    public uint Close()
    {
        lock (gate)
        {
            if (closed) return WaveOutConst.MmsyserrInvalHandle;
            if (queued > 0) return WaveOutConst.WaverrStillPlaying;
        }
        var r = Device.Close();
        if (r != 0) return r;
        lock (gate) { closed = true; headers.Clear(); }
        Ctx.RemoveHandle(Handle, out _);
        Notify(WaveOutConst.WomClose, 0, 0);
        return 0;
    }

    public void Dispose()
    {
        lock (gate) closed = true;
        Device.Dispose();
    }
}

/// <summary>The waveOut import contracts (mmsystem.h / Microsoft Learn "Waveform-Audio Output"), delegating to the context's <see cref="IWaveOutBackend"/>. See docs/binary-audio.md.</summary>
internal static class WaveOutApi
{
    private static readonly ConditionalWeakTable<Win32Context, WaveOutState> Table = new();
    private const uint OpenFlagMask = WaveOutConst.CallbackTypeMask | WaveOutConst.WaveFormatQuery | WaveOutConst.WaveAllowSync | WaveOutConst.WaveMapped | WaveOutConst.WaveFormatDirect | WaveOutConst.WaveMappedDefaultCommunicationDevice;

    private static IWaveOutBackend Backend(Win32Context ctx)
    {
        if (!Table.TryGetValue(ctx, out var state))
        {
            state = Table.GetValue(ctx, x => new WaveOutState(x));
            ctx.AddDisposable(state);
        }
        return state.Backend;
    }

    private static WaveOutStream? Stream(Win32Call c, int arg) => c.Ctx.GetHandle<WaveOutStream>(c[arg]);

    private static uint Diagnose(Win32Call c, uint code, string text)
    {
        c.Ctx.Options.DebugOutput?.Invoke("waveOut: " + text);
        return code;
    }

    public static void Define(Action<string, int, Win32Handler> std)
    {
        std("waveOutGetNumDevs", 0, c => Backend(c.Ctx).DeviceCount);
        std("waveOutGetDevCapsA", 3, c => GetCaps(c, false));
        std("waveOutGetDevCapsW", 3, c => GetCaps(c, true));
        std("waveOutOpen", 6, Open);
        std("waveOutClose", 1, c => Stream(c, 0) is { } s ? s.Close() : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutPrepareHeader", 3, c => Stream(c, 0) is { } s ? s.Prepare(c[1], c[2]) : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutUnprepareHeader", 3, c => Stream(c, 0) is { } s ? s.Unprepare(c[1], c[2]) : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutWrite", 3, c => Stream(c, 0) is { } s ? s.Write(c[1], c[2]) : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutReset", 1, c => Stream(c, 0) is { } s ? s.Reset() : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutPause", 1, c => Stream(c, 0) is { } s ? s.Device.Pause() : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutRestart", 1, c => Stream(c, 0) is { } s ? s.Device.Restart() : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutBreakLoop", 1, c => Stream(c, 0) is { } s ? s.Device.BreakLoop() : WaveOutConst.MmsyserrInvalHandle);
        std("waveOutGetPosition", 3, GetPosition);
        std("waveOutGetID", 2, c =>
        {
            if (Stream(c, 0) is not { } s) return WaveOutConst.MmsyserrInvalHandle;
            return Out(c, c[1], s.Device.DeviceId);
        });
        std("waveOutGetVolume", 2, c =>
        {
            uint v;
            var r = Stream(c, 0) is { } s ? s.Device.GetVolume(out v) : Backend(c.Ctx).GetVolume(c[0], out v);
            return r != 0 ? r : Out(c, c[1], v);
        });
        std("waveOutSetVolume", 2, c => Stream(c, 0) is { } s ? s.Device.SetVolume(c[1]) : Backend(c.Ctx).SetVolume(c[0], c[1]));
        std("waveOutGetErrorTextA", 3, c => ErrorText(c, false));
        std("waveOutGetErrorTextW", 3, c => ErrorText(c, true));
    }

    private static uint Out(Win32Call c, uint pointer, uint value)
    {
        if (pointer == 0 || !c.Mem.IsMapped(pointer, 4)) return WaveOutConst.MmsyserrInvalParam;
        c.Mem.WriteUInt32(pointer, value);
        return 0;
    }

    private static uint GetCaps(Win32Call c, bool wide)
    {
        var backend = Backend(c.Ctx);
        var id = Stream(c, 0) is { } s ? s.Device.DeviceId : c[0];
        var r = backend.GetCaps(id, out var caps);
        if (r != 0) return r;
        var bytes = caps!.ToBytes(wide);
        var n = Math.Min(c[2], (uint)bytes.Length);
        if (c[1] == 0 || !c.Mem.IsMapped(c[1], n)) return WaveOutConst.MmsyserrInvalParam;
        c.Mem.Write(c[1], bytes.AsSpan(0, (int)n));
        return 0;
    }

    private static uint Open(Win32Call c)
    {
        var backend = Backend(c.Ctx);
        uint phwo = c[0], id = c[1], pwfx = c[2], callback = c[3], instance = c[4], flags = c[5];
        if (backend.DeviceCount == 0) return backend.GetCaps(id, out _); // no device: the id is what fails
        if ((flags & ~OpenFlagMask) != 0) return WaveOutConst.MmsyserrInvalFlag;
        var kind = flags & WaveOutConst.CallbackTypeMask;
        if (kind is not (WaveOutConst.CallbackNull or WaveOutConst.CallbackWindow or WaveOutConst.CallbackThread or WaveOutConst.CallbackFunction or WaveOutConst.CallbackEvent))
            return WaveOutConst.MmsyserrInvalFlag;
        if (kind is WaveOutConst.CallbackWindow or WaveOutConst.CallbackThread)
            return Diagnose(c, WaveOutConst.MmsyserrNotSupported, $"waveOutOpen: {(kind == WaveOutConst.CallbackWindow ? "CALLBACK_WINDOW" : "CALLBACK_THREAD")} needs a window system / thread message queue, which this process model does not have.");
        if ((flags & (WaveOutConst.WaveMapped | WaveOutConst.WaveMappedDefaultCommunicationDevice)) != 0)
            return Diagnose(c, WaveOutConst.MmsyserrNotSupported, "waveOutOpen: WAVE_MAPPED / WAVE_MAPPED_DEFAULT_COMMUNICATION_DEVICE are not supported.");
        var query = (flags & WaveOutConst.WaveFormatQuery) != 0;
        if (!query && (phwo == 0 || !c.Mem.IsMapped(phwo, 4))) return WaveOutConst.MmsyserrInvalParam;
        var format = WaveOutFormat.Read(c.Mem, pwfx);
        if (format is null) return WaveOutConst.MmsyserrInvalParam;

        GuestModule? module = null;
        KernelEvent? ev = null;
        if (!query)
        {
            if (kind == WaveOutConst.CallbackFunction)
            {
                if (callback == 0) return WaveOutConst.MmsyserrInvalParam;
                module = GuestModule.Current;
                if (module is null) return Diagnose(c, WaveOutConst.MmsyserrNotSupported, "waveOutOpen: CALLBACK_FUNCTION needs a running guest module to call into.");
            }
            else if (kind == WaveOutConst.CallbackEvent)
            {
                ev = c.Ctx.GetHandle<KernelEvent>(callback);
                if (ev is null) return WaveOutConst.MmsyserrInvalHandle;
            }
        }

        var stream = new WaveOutStream(c.Ctx, c.Mem, module, kind, callback, instance, ev);
        var r = backend.Open(id, format, flags & ~WaveOutConst.CallbackTypeMask, stream, out var device);
        if (r != 0 || query) return r;
        stream.Device = device!;
        stream.Handle = c.Ctx.NewHandle(stream);
        c.Mem.WriteUInt32(phwo, stream.Handle);
        stream.Notify(WaveOutConst.WomOpen, 0, 0);
        return 0;
    }

    private static uint GetPosition(Win32Call c)
    {
        if (Stream(c, 0) is not { } s) return WaveOutConst.MmsyserrInvalHandle;
        if (c[2] != WaveOutConst.MmTimeSize || c[1] == 0 || !c.Mem.IsMapped(c[1], WaveOutConst.MmTimeSize)) return WaveOutConst.MmsyserrInvalParam;
        var r = s.Device.GetPosition(c.Mem.ReadUInt32(c[1]), out var type, out var value);
        if (r != 0) return r;
        c.Mem.WriteUInt32(c[1], type);
        c.Mem.WriteUInt32(c[1] + 4, value);
        return 0;
    }

    private static uint ErrorText(Win32Call c, bool wide)
    {
        if (c[1] == 0 || c[2] == 0) return WaveOutConst.MmsyserrInvalParam;
        var text = Backend(c.Ctx).GetErrorText(c[0]);
        if (text is null) return WaveOutConst.MmsyserrBadErrNum;
        var n = (int)Math.Min(c[2] - 1, (uint)text.Length);
        var size = (wide ? 2u : 1u) * (uint)(n + 1);
        if (!c.Mem.IsMapped(c[1], size)) return WaveOutConst.MmsyserrInvalParam;
        var bytes = new byte[size];
        for (var i = 0; i < n; i++)
            if (wide) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), text[i]); else bytes[i] = text[i] < 256 ? (byte)text[i] : (byte)'?';
        c.Mem.Write(c[1], bytes);
        return 0;
    }
}
