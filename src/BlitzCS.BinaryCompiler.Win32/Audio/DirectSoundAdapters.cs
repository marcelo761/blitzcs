using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32.Com;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// DirectSound for a 32-bit guest through the host's native 64-bit dsound (Windows or Wine). There is NO software engine here: every method is an explicit adapter row that converts the
/// guest's 32-bit arguments and calls the host's own IDirectSound / IDirectSoundBuffer, returning the host's HRESULT unchanged.
///
/// Adapted interfaces: IDirectSound (IID 279AFA83-...) and IDirectSoundBuffer (IID 279AFA85-...). IDirectSound8 / IDirectSoundBuffer8, IDirectSoundNotify, IDirectSound3DBuffer,
/// IDirectSound3DListener, IKsPropertySet and the FX interfaces are not adapted: QueryInterface for them answers E_NOINTERFACE, DirectSoundCreate8 is not exported. Methods that exist in an
/// adapted interface but are not implemented answer E_NOTIMPL (listed below with the reason).
///
/// Vtable order (dsound.h): IDirectSound = [QI AddRef Release] CreateSoundBuffer GetCaps DuplicateSoundBuffer SetCooperativeLevel Compact GetSpeakerConfig SetSpeakerConfig Initialize;
/// IDirectSoundBuffer = [QI AddRef Release] GetCaps GetCurrentPosition GetFormat GetVolume GetPan GetFrequency GetStatus Initialize Lock Play SetCurrentPosition SetFormat SetVolume SetPan SetFrequency Stop Unlock Restore.
///
/// Not implemented (E_NOTIMPL): IDirectSound::DuplicateSoundBuffer (it would need a second shadow for a buffer sharing the device memory; no software mixer duplicates streaming buffers).
///
/// Lock/Unlock: the host buffer memory can be above 4 GiB and a guest pointer is 32 bits, so the guest never sees host memory. Each secondary buffer has a SHADOW in guest low memory with the size of
/// the host buffer (initialised to digital silence, 0x80 for 8-bit PCM else 0). Lock returns pointers into the shadow (and the documented two-region wrap-around, DSBLOCK_FROMWRITECURSOR taking the
/// host's real write cursor and DSBLOCK_ENTIREBUFFER the whole buffer); Unlock copies exactly the unlocked regions from the shadow into the host buffer (host Lock, copy, host Unlock).
/// The shadow always equals what the application wrote (the application is the only writer of a software secondary buffer), so Lock never needs to read the host buffer back. Consequences,
/// documented: the data becomes audible at Unlock, not at the moment of the write; a guest that never Unlocks leaks nothing on the host (no host lock is held between Lock and Unlock) and the
/// shadow dies with the buffer. Locking the primary buffer is refused (DSERR_INVALIDCALL below DSSCL_WRITEPRIMARY, E_NOTIMPL with it: primary memory is the driver's, not a software array).
/// Position queries (GetCurrentPosition) go to the host, so they are the REAL device play and write cursors.
/// </summary>
public static class DirectSoundAdapters
{
    public const string DeviceName = "IDirectSound", BufferName = "IDirectSoundBuffer";

    /// <summary>
    /// The guest's IDirectSound. Real DirectSound keeps the device alive while buffers created from it exist; Wine's dsound does not (releasing the last IDirectSound destroys its
    /// buffers). The host device reference is therefore dropped only when the guest released the device AND every buffer made from it is gone.
    /// </summary>
    internal sealed class DsDevice(HostComInterface host)
    {
        private readonly object gate = new();
        private int buffers;
        private bool guestReleased, hostReleased;
        public HostComInterface Host { get; } = host;
        public volatile uint CooperativeLevel;
        public void BufferCreated() { lock (gate) buffers++; }
        public void BufferDestroyed() { lock (gate) { buffers--; } ReleaseHostIfUnused(); }
        public void GuestReleased() { lock (gate) guestReleased = true; ReleaseHostIfUnused(); }
        private void ReleaseHostIfUnused()
        {
            lock (gate)
            {
                if (!guestReleased || buffers > 0 || hostReleased) return;
                hostReleased = true;
            }
            Host.Release();
        }
    }

    internal sealed class DsBuffer(HostComInterface host, DsDevice device, uint flags, uint bufferBytes, int bitsPerSample)
    {
        public HostComInterface Host { get; } = host;
        public DsDevice Device { get; } = device;
        public bool IsPrimary => (flags & DsConst.DsbcapsPrimaryBuffer) != 0;
        public uint BufferBytes { get; set; } = bufferBytes;
        public int BitsPerSample { get; set; } = bitsPerSample;
        public readonly object Gate = new();
        public GuestBuffer? Shadow;
        public int OutstandingLocks;
    }

    private static HostComInterface DeviceOf(GuestComObject o) => ((DsDevice)o.State!).Host;
    private static HostComInterface BufferOf(GuestComObject o) => ((DsBuffer)o.State!).Host;

    /// <summary>A structure whose first DWORD is its own size (DSCAPS, DSBCAPS): copied in with that size (bounded by <paramref name="max"/>), copied back on success.</summary>
    private static ComArg InOutSized(int minimum, int maximum) => new DelegateComArg((s, v) =>
    {
        if (v == 0) throw new ComArgumentException("A structure pointer must not be NULL.");
        var size = s.Mem.ReadUInt32(v);
        if (size < minimum || size > maximum) throw new ComArgumentException($"dwSize {size} is not valid (expected {minimum}..{maximum}).");
        var native = s.Native.Copy(s.Mem.ReadBytes(v, (int)size));
        s.OnCompleted(hr => { if (ComHr.Succeeded(hr)) unsafe { s.Mem.Write(v, new ReadOnlySpan<byte>((void*)native, (int)size)); } });
        return native;
    });

    /// <summary>WAVEFORMATEX (packed, 18 bytes + cbSize extra bytes; no pointers, so guest and host layouts are identical): a native copy.</summary>
    private static ComArg InWaveFormat(bool optional) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("A WAVEFORMATEX pointer must not be NULL.");
        return s.Native.Copy(ReadWaveFormat(s.Mem, v));
    });

    internal static byte[] ReadWaveFormat(IWin32Memory mem, uint address)
    {
        var head = mem.ReadBytes(address, 18);
        var extra = BitConverter.ToUInt16(head, 16);
        if (extra > 64) throw new ComArgumentException($"WAVEFORMATEX.cbSize {extra} is implausible.");
        return extra == 0 ? head : mem.ReadBytes(address, 18 + extra);
    }

    // ---- IDirectSound ----

    public static GuestComInterface DeviceInterface()
    {
        var i = new GuestComInterface(DeviceName, DsConst.IidDirectSound);
        i.Add(new GuestComMethod("CreateSoundBuffer", 3, CreateSoundBuffer));
        i.Add(new HostComMethod("GetCaps", 4, InOutSized(96, 96)).ToGuest(DeviceOf));
        i.NotImplemented("DuplicateSoundBuffer", 2, "a duplicate would need a second shadow over the same device memory; software mixers do not duplicate streaming buffers");
        i.Add(new GuestComMethod("SetCooperativeLevel", 2, SetCooperativeLevel));
        i.Add(new HostComMethod("Compact", 7).ToGuest(DeviceOf));
        i.Add(new HostComMethod("GetSpeakerConfig", 8, ComArgs.OutU32()).ToGuest(DeviceOf));
        i.Add(new HostComMethod("SetSpeakerConfig", 9, ComArgs.Value).ToGuest(DeviceOf));
        i.Add(new HostComMethod("Initialize", 10, ComArgs.Guid(optional: true)).ToGuest(DeviceOf));
        return i;
    }

    private static uint SetCooperativeLevel(GuestComCall c)
    {
        var device = (DsDevice)c.Object.State!;
        nint window;
        try { window = DsHost.MapWindow(c.Ctx.Options, c.Arg(0)); }
        catch (ComArgumentException e) { c.Ctx.Options.DebugOutput?.Invoke($"IDirectSound::SetCooperativeLevel: {e.Message}"); return ComHr.InvalidArg; }
        var hr = device.Host.Invoke(6, window, (nint)c.Arg(1));
        if (ComHr.Succeeded(hr)) device.CooperativeLevel = c.Arg(1);
        return hr;
    }

    /// <summary>DSBUFFERDESC: guest layout (dwSize 20 or 36; lpwfxFormat is a 32-bit pointer at 16) to the host's 64-bit layout (dwSize 24 or 40; lpwfxFormat is 8 bytes at 16, guid3DAlgorithm at 24).</summary>
    private static uint CreateSoundBuffer(GuestComCall c)
    {
        var device = (DsDevice)c.Object.State!;
        var descPtr = c.Arg(0); var outPtr = c.Arg(1);
        if (outPtr == 0) return ComHr.Pointer;
        c.Mem.WriteUInt32(outPtr, 0);
        if (descPtr == 0) return ComHr.InvalidArg;
        if (c.Arg(2) != 0) return DsConst.DsErrNoAggregation;
        var size = c.Mem.ReadUInt32(descPtr);
        if (size != 20 && size != 36) return ComHr.InvalidArg;
        var guest = c.Mem.ReadBytes(descPtr, (int)size);
        var flags = BitConverter.ToUInt32(guest, 4); var bytes = BitConverter.ToUInt32(guest, 8);
        var formatPtr = BitConverter.ToUInt32(guest, 16);
        using var scope = new NativeScope();
        var host = scope.AllocZeroed(size == 20 ? 24 : 40);
        unsafe
        {
            var d = (byte*)host;
            *(uint*)d = size == 20 ? 24u : 40u;
            *(uint*)(d + 4) = flags; *(uint*)(d + 8) = bytes; *(uint*)(d + 12) = BitConverter.ToUInt32(guest, 12);
            var bits = 0;
            if (formatPtr != 0)
            {
                var format = ReadWaveFormat(c.Mem, formatPtr);
                *(nint*)(d + 16) = scope.Copy(format);
                bits = BitConverter.ToUInt16(format, 14);
            }
            if (size == 36) guest.AsSpan(20, 16).CopyTo(new Span<byte>(d + 24, 16));
            nint nativeBuffer;
            var hr = device.Host.Invoke(3, host, (nint)(&nativeBuffer), 0);
            if (!ComHr.Succeeded(hr)) return hr;
            if (nativeBuffer == 0) return ComHr.Fail;
            return WrapBuffer(c, device, new HostComInterface(nativeBuffer, BufferName), flags, bits, outPtr);
        }
    }

    private static uint WrapBuffer(GuestComCall c, DsDevice device, HostComInterface host, uint flags, int bits, uint outPtr)
    {
        // The real size comes from the host (for a primary buffer it is the driver's size and may be 0 until a format is set).
        var caps = new byte[20]; BitConverter.TryWriteBytes(caps, 20u);
        uint bufferBytes = 0;
        unsafe
        {
            fixed (byte* p = caps)
            {
                var hr = host.Invoke(3, (nint)p);
                if (ComHr.Succeeded(hr)) bufferBytes = BitConverter.ToUInt32(caps, 8);
            }
        }
        var state = new DsBuffer(host, device, flags, bufferBytes, bits);
        device.BufferCreated();
        var obj = c.Host.Create(BufferName, state, BufferInterface(), o =>
        {
            var s = (DsBuffer)o.State!;
            lock (s.Gate) { try { s.Shadow?.Dispose(); } catch (ObjectDisposedException) { } s.Shadow = null; }
            s.Host.Release();
            s.Device.BufferDestroyed();
        });
        c.Mem.WriteUInt32(outPtr, obj.Address);
        return ComHr.Ok;
    }

    // ---- IDirectSoundBuffer ----

    private static GuestComInterface? bufferInterface;
    public static GuestComInterface BufferInterface() => bufferInterface ??= BuildBufferInterface();

    private static GuestComInterface BuildBufferInterface()
    {
        var i = new GuestComInterface(BufferName, DsConst.IidDirectSoundBuffer);
        i.Add(new HostComMethod("GetCaps", 3, InOutSized(20, 20)).ToGuest(BufferOf));
        i.Add(new HostComMethod("GetCurrentPosition", 4, ComArgs.OutU32(optional: true), ComArgs.OutU32(optional: true)).ToGuest(BufferOf));
        i.Add(new GuestComMethod("GetFormat", 3, GetFormat));
        i.Add(new HostComMethod("GetVolume", 6, ComArgs.OutU32()).ToGuest(BufferOf));
        i.Add(new HostComMethod("GetPan", 7, ComArgs.OutU32()).ToGuest(BufferOf));
        i.Add(new HostComMethod("GetFrequency", 8, ComArgs.OutU32()).ToGuest(BufferOf));
        i.Add(new HostComMethod("GetStatus", 9, ComArgs.OutU32()).ToGuest(BufferOf));
        i.NotImplemented("Initialize", 2, "buffers are created initialised by IDirectSound::CreateSoundBuffer; only IDirectSound::DuplicateSoundBuffer / CoCreateInstance paths call Initialize, and neither is provided");
        i.Add(new GuestComMethod("Lock", 7, Lock));
        i.Add(new HostComMethod("Play", 12, ComArgs.Value, ComArgs.Value, ComArgs.Value).ToGuest(BufferOf));
        i.Add(new HostComMethod("SetCurrentPosition", 13, ComArgs.Value).ToGuest(BufferOf));
        i.Add(new GuestComMethod("SetFormat", 1, SetFormat));
        i.Add(new HostComMethod("SetVolume", 15, ComArgs.Signed).ToGuest(BufferOf));
        i.Add(new HostComMethod("SetPan", 16, ComArgs.Signed).ToGuest(BufferOf));
        i.Add(new HostComMethod("SetFrequency", 17, ComArgs.Value).ToGuest(BufferOf));
        i.Add(new HostComMethod("Stop", 18).ToGuest(BufferOf));
        i.Add(new GuestComMethod("Unlock", 4, Unlock));
        i.Add(new HostComMethod("Restore", 20).ToGuest(BufferOf));
        return i;
    }

    private static uint GetFormat(GuestComCall c)
    {
        var buffer = (DsBuffer)c.Object.State!;
        var dest = c.Arg(0); var allocated = c.Arg(1); var writtenPtr = c.Arg(2);
        using var scope = new NativeScope();
        var native = dest == 0 ? 0 : scope.AllocZeroed((int)Math.Min(allocated, 4096u));
        var written = scope.AllocZeroed(4);
        unsafe
        {
            var hr = buffer.Host.Invoke(5, native, (nint)Math.Min(allocated, 4096u), writtenPtr == 0 ? 0 : written);
            if (!ComHr.Succeeded(hr)) return hr;
            var count = writtenPtr == 0 ? 0u : *(uint*)written;
            if (writtenPtr != 0) c.Mem.WriteUInt32(writtenPtr, count);
            if (dest != 0) c.Mem.Write(dest, new ReadOnlySpan<byte>((void*)native, (int)Math.Min(writtenPtr == 0 ? Math.Min(allocated, 4096u) : count, Math.Min(allocated, 4096u))));
            return hr;
        }
    }

    private static uint SetFormat(GuestComCall c)
    {
        var buffer = (DsBuffer)c.Object.State!;
        if (c.Arg(0) == 0) return ComHr.InvalidArg;
        byte[] format;
        try { format = ReadWaveFormat(c.Mem, c.Arg(0)); }
        catch (ComArgumentException) { return ComHr.InvalidArg; }
        using var scope = new NativeScope();
        var hr = buffer.Host.Invoke(14, scope.Copy(format));
        if (ComHr.Succeeded(hr)) buffer.BitsPerSample = BitConverter.ToUInt16(format, 14);
        return hr;
    }

    private static uint Lock(GuestComCall c)
    {
        var b = (DsBuffer)c.Object.State!;
        uint offset = c.Arg(0), bytes = c.Arg(1), p1 = c.Arg(2), l1 = c.Arg(3), p2 = c.Arg(4), l2 = c.Arg(5), flags = c.Arg(6);
        if (p1 == 0 || l1 == 0) return ComHr.InvalidArg;
        c.Mem.WriteUInt32(p1, 0); c.Mem.WriteUInt32(l1, 0);
        if (p2 != 0) c.Mem.WriteUInt32(p2, 0);
        if (l2 != 0) c.Mem.WriteUInt32(l2, 0);
        if ((flags & ~(DsConst.DsblockFromWriteCursor | DsConst.DsblockEntireBuffer)) != 0) return ComHr.InvalidArg;
        if (b.IsPrimary) return b.Device.CooperativeLevel == DsConst.DsSclWritePrimary ? DsConst.DsErrUnsupported : DsConst.DsErrInvalidCall;

        uint status = 0;
        unsafe { var hrs = b.Host.Invoke(9, (nint)(&status)); if (!ComHr.Succeeded(hrs)) return hrs; }
        if ((status & DsConst.DsbstatusBufferLost) != 0) return DsConst.DsErrBufferLost;

        lock (b.Gate)
        {
            var size = b.BufferBytes;
            if (size == 0) return DsConst.DsErrInvalidCall;
            if ((flags & DsConst.DsblockFromWriteCursor) != 0)
            {
                uint play = 0, write = 0;
                unsafe { var hrp = b.Host.Invoke(4, (nint)(&play), (nint)(&write)); if (!ComHr.Succeeded(hrp)) return hrp; }
                offset = write;
            }
            if ((flags & DsConst.DsblockEntireBuffer) != 0) bytes = size;   // dwBytes is ignored; the offset (or the write cursor) still positions the start, so the region may wrap
            if (bytes > size || offset >= size) return ComHr.InvalidArg;
            b.Shadow ??= CreateShadow(c, b, size);
            var shadow = b.Shadow.Address;
            var first = Math.Min(bytes, size - offset);
            var second = bytes - first;
            if (second != 0 && (p2 == 0 || l2 == 0)) { second = 0; }      // the caller cannot receive a second region: the lock is limited to the first one
            c.Mem.WriteUInt32(p1, shadow + offset); c.Mem.WriteUInt32(l1, first);
            if (second != 0) { c.Mem.WriteUInt32(p2, shadow); c.Mem.WriteUInt32(l2, second); }
            b.OutstandingLocks++;
            return ComHr.Ok;
        }
    }

    private static GuestBuffer CreateShadow(GuestComCall c, DsBuffer b, uint size)
    {
        var shadow = c.Host.Module.Memory.Allocate((int)size);
        if (b.BitsPerSample == 8) shadow.Write(0, Enumerable.Repeat((byte)0x80, (int)size).ToArray());
        return shadow;
    }

    private static unsafe uint Unlock(GuestComCall c)
    {
        var b = (DsBuffer)c.Object.State!;
        uint p1 = c.Arg(0), l1 = c.Arg(1), p2 = c.Arg(2), l2 = c.Arg(3);
        lock (b.Gate)
        {
            if (b.IsPrimary) return DsConst.DsErrInvalidCall;
            if (b.Shadow is null || b.OutstandingLocks == 0) return DsConst.DsErrInvalidCall;
            var shadow = b.Shadow.Address; var size = b.BufferBytes;
            if (p1 == 0 || p1 < shadow || p1 - shadow >= size || l1 > size - (p1 - shadow)) return ComHr.InvalidArg;
            if (l2 != 0 && (p2 != shadow || l2 > size)) return ComHr.InvalidArg;
            if (l2 == 0 && p2 != 0 && p2 != shadow) return ComHr.InvalidArg;
            var hr = CopyRegion(b, c.Host.Module, p1 - shadow, l1);
            if (ComHr.Succeeded(hr) && l2 != 0) hr = CopyRegion(b, c.Host.Module, 0, l2);
            if (ComHr.Succeeded(hr)) b.OutstandingLocks--;
            return hr;
        }
    }

    /// <summary>Shadow -> host buffer for one non-wrapping region: host Lock, copy, host Unlock.</summary>
    private static unsafe uint CopyRegion(DsBuffer b, GuestModule module, uint offset, uint length)
    {
        if (length == 0) return ComHr.Ok;
        nint h1 = 0, h2 = 0; uint hl1 = 0, hl2 = 0;
        var hr = b.Host.Invoke(11, (nint)offset, (nint)length, (nint)(&h1), (nint)(&hl1), (nint)(&h2), (nint)(&hl2), 0);
        if (!ComHr.Succeeded(hr)) return hr;
        try
        {
            if (hl1 != length || h2 != 0) return DsConst.DsErrInvalidCall;      // a non-wrapping region must come back as one piece
            b.Shadow!.Read((int)offset, new Span<byte>((void*)h1, (int)hl1));
        }
        finally { b.Host.Invoke(19, h1, (nint)hl1, h2, (nint)hl2); }
        return ComHr.Ok;
    }
}
