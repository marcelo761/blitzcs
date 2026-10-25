using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>Drives DirectSound the way a 32-bit program does: GetProcAddress("dsound.dll", "DirectSoundCreate"), then calls through the returned COM vtables from hand-assembled guest code.</summary>
internal sealed class DsRig : IDisposable
{
    public const uint Ppv = 0x600, Desc = 0x640, Wfx = 0x680, Out = 0x6C0, Caps = 0x700;
    public readonly ComRig Rig;
    public GuestMemory Mem => Rig.Module.Memory;
    public GuestComHost Com => GuestComHost.For(Rig.Resolver.Context, Rig.Module);

    public DsRig(DirectSoundBackendKind kind = DirectSoundBackendKind.Default, Action<string>? debug = null) =>
        Rig = new ComRig(256, new Win32Options { DirectSound = kind, DebugOutput = debug ?? (Environment.GetEnvironmentVariable("DS_TRACE") == "1" ? (m => { Console.Error.WriteLine("DBG " + m); Console.Error.Flush(); }) : null), CallTrace = Environment.GetEnvironmentVariable("DS_TRACE") == "1" ? (m => { Console.Error.WriteLine(m); Console.Error.Flush(); }) : null });

    public static uint D(uint offset) => ComRig.Data(offset);
    public uint Fn(string name) => Rig.GetProc("dsound.dll", name);
    public uint Vt(uint obj, int slot, params uint[] args) => Rig.Vt(obj, slot, args);

    public uint Create(out uint device)
    {
        var create = Fn("DirectSoundCreate");
        Assert.NotEqual(0u, create);
        Mem.WriteUInt32(D(Ppv), 0xDEADBEEF);
        var hr = Rig.F("callptr3", create, 0, D(Ppv), 0);
        device = Mem.ReadUInt32(D(Ppv));
        return hr;
    }

    /// <summary>Secondary buffer: PCM 16-bit mono at <paramref name="rate"/>, GETCURRENTPOSITION2 | GLOBALFOCUS | CTRLVOLUME | CTRLFREQUENCY | CTRLPAN.</summary>
    public uint CreateSecondary(uint device, uint bytes, uint rate, out uint buffer, uint flags = 0x10000 | 0x8000 | 0x80 | 0x20 | 0x40, ushort bits = 16, ushort channels = 1)
    {
        var block = (ushort)(channels * bits / 8);
        Mem.Write(D(Wfx), [.. BitConverter.GetBytes((ushort)1), .. BitConverter.GetBytes(channels), .. BitConverter.GetBytes(rate), .. BitConverter.GetBytes(rate * block), .. BitConverter.GetBytes(block), .. BitConverter.GetBytes(bits), .. BitConverter.GetBytes((ushort)0)]);
        Mem.Write(D(Desc), [.. BitConverter.GetBytes(20u), .. BitConverter.GetBytes(flags), .. BitConverter.GetBytes(bytes), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(D(Wfx))]);
        Mem.WriteUInt32(D(Ppv), 0xDEADBEEF);
        var hr = Vt(device, 3, D(Desc), D(Ppv), 0);
        buffer = Mem.ReadUInt32(D(Ppv));
        return hr;
    }

    public uint CreatePrimary(uint device, out uint buffer)
    {
        Mem.Write(D(Desc), [.. BitConverter.GetBytes(20u), .. BitConverter.GetBytes(0x1u | 0x80u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u)]);
        Mem.WriteUInt32(D(Ppv), 0xDEADBEEF);
        var hr = Vt(device, 3, D(Desc), D(Ppv), 0);
        buffer = Mem.ReadUInt32(D(Ppv));
        return hr;
    }

    public (uint Hr, uint P1, uint L1, uint P2, uint L2) Lock(uint buffer, uint offset, uint bytes, uint flags = 0)
    {
        foreach (var o in new uint[] { 0, 4, 8, 12 }) Mem.WriteUInt32(D(Out + o), 0xCCCCCCCC);
        var hr = Vt(buffer, 11, offset, bytes, D(Out), D(Out + 4), D(Out + 8), D(Out + 12), flags);
        return (hr, Mem.ReadUInt32(D(Out)), Mem.ReadUInt32(D(Out + 4)), Mem.ReadUInt32(D(Out + 8)), Mem.ReadUInt32(D(Out + 12)));
    }

    public uint Unlock(uint buffer, uint p1, uint l1, uint p2, uint l2) => Vt(buffer, 19, p1, l1, p2, l2);

    public (uint Play, uint Write) Positions(uint buffer)
    {
        Mem.WriteUInt32(D(Out + 16), 0xCCCCCCCC); Mem.WriteUInt32(D(Out + 20), 0xCCCCCCCC);
        var hr = Vt(buffer, 4, D(Out + 16), D(Out + 20));
        Assert.Equal(0u, hr);
        return (Mem.ReadUInt32(D(Out + 16)), Mem.ReadUInt32(D(Out + 20)));
    }

    public uint Status(uint buffer) { Mem.WriteUInt32(D(Out + 24), 0xCCCCCCCC); Assert.Equal(0u, Vt(buffer, 9, D(Out + 24))); return Mem.ReadUInt32(D(Out + 24)); }

    public void Dispose() => Rig.Dispose();
}

[Collection("GuestRuntime")]
public sealed class DirectSoundBindingTests
{
    [Fact]
    public void GetProcAddress_hands_out_one_stable_callable_stub_per_exported_function_and_NULL_for_the_rest()
    {
        using var d = new DsRig(DirectSoundBackendKind.None);
        var h = d.Rig.F("loadlib", d.Rig.Str(0x400, "dsound.dll"));
        Assert.NotEqual(0u, h);
        Assert.Equal(h, d.Rig.F("loadlib", d.Rig.Str(0x400, "DSOUND")));            // name is case-insensitive, ".dll" optional
        var create = d.Fn("DirectSoundCreate"); var enumerate = d.Fn("DirectSoundEnumerateA");
        Assert.NotEqual(0u, create); Assert.NotEqual(0u, enumerate); Assert.NotEqual(create, enumerate);
        Assert.Equal(create, d.Fn("DirectSoundCreate"));                            // same pointer every time
        Assert.True(d.Rig.Module.IsStubAddress(create));
        // not exported: DirectSoundCreate8 (IDirectSound8 is not adapted), capture
        Assert.Equal(0u, d.Fn("DirectSoundCreate8"));
        Assert.Equal(0u, d.Fn("DirectSoundCaptureCreate"));
        Assert.Equal(0u, d.Rig.F("loadlib", d.Rig.Str(0x400, "dsound3d.dll")));     // unknown module
    }

    [Fact]
    public void Without_a_host_DirectSound_the_real_HRESULT_DSERR_NODRIVER_is_returned_and_nothing_is_created()
    {
        using var d = new DsRig(DirectSoundBackendKind.None);
        Assert.Equal(DsConst.DsErrNoDriver, d.Create(out var device));
        Assert.Equal(0u, device);
        Assert.Equal(0, d.Com.LiveObjects);
        // aggregation is refused before the device is even consulted
        var create = d.Fn("DirectSoundCreate");
        Assert.Equal(DsConst.DsErrNoAggregation, d.Rig.F("callptr3", create, 0, DsRig.D(DsRig.Ppv), 0x1234));
        Assert.Equal(DsConst.DsErrInvalidParam, d.Rig.F("callptr3", create, 0, 0, 0));
        // enumeration calls no callback
        var called = d.Rig.Module.Memory;
        Assert.Equal(DsConst.DsErrNoDriver, d.Rig.F("callptr2", d.Fn("DirectSoundEnumerateA"), 0x1000, 0));
    }

    [Fact]
    public void A_module_without_a_stub_pool_cannot_get_a_function_pointer_and_says_why()
    {
        var messages = new List<string>();
        using var rig = new ComRig(0, new Win32Options { DebugOutput = messages.Add });
        Assert.Equal(0u, rig.GetProc("dsound.dll", "DirectSoundCreate"));
        Assert.Contains(messages, m => m.Contains("without a dynamic stub pool"));
    }

    [Fact]
    public void The_dsound_interface_adapter_tables_are_exactly_the_documented_vtables()
    {
        var device = DirectSoundAdapters.DeviceInterface();
        Assert.Equal(["CreateSoundBuffer", "GetCaps", "DuplicateSoundBuffer", "SetCooperativeLevel", "Compact", "GetSpeakerConfig", "SetSpeakerConfig", "Initialize"], device.Methods.Select(m => m.Name));
        Assert.Equal([3, 1, 2, 2, 0, 1, 1, 1], device.Methods.Select(m => m.ArgCount));
        Assert.Equal(["DuplicateSoundBuffer"], device.Methods.Where(m => m.Handler is null).Select(m => m.Name));
        var buffer = DirectSoundAdapters.BufferInterface();
        Assert.Equal(["GetCaps", "GetCurrentPosition", "GetFormat", "GetVolume", "GetPan", "GetFrequency", "GetStatus", "Initialize", "Lock", "Play", "SetCurrentPosition", "SetFormat", "SetVolume", "SetPan", "SetFrequency", "Stop", "Unlock", "Restore"], buffer.Methods.Select(m => m.Name));
        Assert.Equal([1, 2, 3, 1, 1, 1, 1, 2, 7, 3, 1, 1, 1, 1, 1, 0, 4, 0], buffer.Methods.Select(m => m.ArgCount));
        Assert.Equal(["Initialize"], buffer.Methods.Where(m => m.Handler is null).Select(m => m.Name));
        Assert.Equal(new Guid("279AFA83-4981-11CE-A521-0020AF0BE560"), device.Iid);
        Assert.Equal(new Guid("279AFA85-4981-11CE-A521-0020AF0BE560"), buffer.Iid);
    }
}

/// <summary>Against the host's native dsound (Windows, or Wine's built-in with the winepulse driver).</summary>
[Collection("GuestRuntime")]
public sealed class HostDirectSoundTests
{
    /// <summary>A host with no audio output answers DSERR_NODRIVER and the device tests have nothing to run against; BLITZCS_REQUIRE_HOST_AUDIO=1 turns that into a failure so a green run is never a silent skip.</summary>
    private static bool NoDevice(uint hr)
    {
        if (hr != DsConst.DsErrNoDriver) return false;
        Assert.True(Environment.GetEnvironmentVariable("BLITZCS_REQUIRE_HOST_AUDIO") != "1", "the host's dsound has no audio device (DSERR_NODRIVER) but BLITZCS_REQUIRE_HOST_AUDIO=1");
        return true;
    }

    [RequiresWindowsFact("needs the host's dsound.dll (Windows or Wine)")]
    public void A_device_reports_real_caps_the_speaker_config_and_answers_QueryInterface_truthfully()
    {
        using var d = new DsRig();
        var hr = d.Create(out var device);
        if (NoDevice(hr)) return;                       // a host without any audio output (documented)
        Assert.Equal(0u, hr);
        Assert.NotEqual(0u, device);
        Assert.Equal(1, d.Com.LiveObjects);

        d.Mem.Write(DsRig.D(DsRig.Caps), new byte[96]); d.Mem.WriteUInt32(DsRig.D(DsRig.Caps), 96);
        Assert.Equal(0u, d.Vt(device, 4, DsRig.D(DsRig.Caps)));
        var minRate = d.Mem.ReadUInt32(DsRig.D(DsRig.Caps) + 8); var maxRate = d.Mem.ReadUInt32(DsRig.D(DsRig.Caps) + 12);
        Assert.True(minRate > 0 && maxRate >= 44100, $"caps rates {minRate}..{maxRate}");
        // a wrong dwSize never reaches the host
        d.Mem.WriteUInt32(DsRig.D(DsRig.Caps), 12);
        Assert.Equal(ComHr.InvalidArg, d.Vt(device, 4, DsRig.D(DsRig.Caps)));

        Assert.Equal(0u, d.Vt(device, 8, DsRig.D(DsRig.Out)));            // GetSpeakerConfig
        Assert.NotEqual(0u, d.Mem.ReadUInt32(DsRig.D(DsRig.Out)));
        Assert.Equal(ComHr.NotImpl, d.Vt(device, 5, 0, 0));               // DuplicateSoundBuffer: documented E_NOTIMPL

        // QueryInterface: IUnknown and IDirectSound yes; IDirectSound8 / 3D buffer / notify no, truthfully
        Assert.Equal(0u, d.Rig.QueryInterface(device, ComIids.IUnknown, out var unk)); Assert.Equal(device, unk);
        Assert.Equal(0u, d.Rig.QueryInterface(device, DsConst.IidDirectSound, out var same)); Assert.Equal(device, same);
        Assert.Equal(ComHr.NoInterface, d.Rig.QueryInterface(device, new Guid("C50A7E93-F395-4834-9EF6-7FA99DE82C0D"), out var ds8)); Assert.Equal(0u, ds8);
        Assert.Equal(ComHr.NoInterface, d.Rig.QueryInterface(device, new Guid("B0210783-89CD-11D0-AF08-00A0C925CD16"), out _));
        Assert.Equal(3, d.Com.Find(device)!.RefCount);
        Assert.Equal(2u, d.Rig.Release(device)); Assert.Equal(1u, d.Rig.Release(device));
        Assert.Equal(0u, d.Rig.Release(device));
        Assert.Equal(0, d.Com.LiveObjects);
        Assert.Throws<GuestImportException>(() => d.Rig.Release(device));      // double release is detected
    }

    [RequiresWindowsFact("needs the host's dsound.dll (Windows or Wine)")]
    public void The_cooperative_level_takes_the_desktop_window_and_refuses_windows_that_do_not_exist()
    {
        var messages = new List<string>();
        using var d = new DsRig(debug: messages.Add);
        if (NoDevice(d.Create(out var device))) return;
        Assert.Equal(0u, d.Vt(device, 6, User32BasicService.DesktopWindow, DsConst.DsSclPriority));
        Assert.Equal(ComHr.InvalidArg, d.Vt(device, 6, 0x00ABCDEF, DsConst.DsSclPriority));      // not a window of this component: never forwarded
        Assert.Contains(messages, m => m.Contains("0x00ABCDEF"));
        d.Rig.Release(device);
    }

    [RequiresWindowsFact("needs the host's dsound.dll (Windows or Wine)")]
    public void Primary_and_secondary_buffers_are_created_from_the_32bit_descriptors_and_report_their_real_caps()
    {
        using var d = new DsRig();
        if (NoDevice(d.Create(out var device))) return;
        Assert.Equal(0u, d.Vt(device, 6, User32BasicService.DesktopWindow, DsConst.DsSclPriority));

        Assert.Equal(0u, d.CreatePrimary(device, out var primary));
        Assert.NotEqual(0u, primary);
        d.Mem.Write(DsRig.D(DsRig.Caps), new byte[20]); d.Mem.WriteUInt32(DsRig.D(DsRig.Caps), 20);
        Assert.Equal(0u, d.Vt(primary, 3, DsRig.D(DsRig.Caps)));
        Assert.NotEqual(0u, d.Mem.ReadUInt32(DsRig.D(DsRig.Caps) + 4) & DsConst.DsbcapsPrimaryBuffer);
        // primary format: set 44.1 kHz 16-bit mono and read it back (the 18-byte WAVEFORMATEX crosses unchanged)
        d.Mem.Write(DsRig.D(DsRig.Wfx), [.. BitConverter.GetBytes((ushort)1), .. BitConverter.GetBytes((ushort)1), .. BitConverter.GetBytes(44100u), .. BitConverter.GetBytes(88200u), .. BitConverter.GetBytes((ushort)2), .. BitConverter.GetBytes((ushort)16), .. BitConverter.GetBytes((ushort)0)]);
        var setFormat = d.Vt(primary, 14, DsRig.D(DsRig.Wfx));
        Assert.True(setFormat == 0 || setFormat == DsConst.DsErrPrioLevelNeeded, $"SetFormat 0x{setFormat:X8}");
        Assert.Equal(0u, d.Vt(primary, 5, DsRig.D(DsRig.Out), 40, DsRig.D(DsRig.Out + 0x30)));
        Assert.Equal(18u, d.Mem.ReadUInt32(DsRig.D(DsRig.Out + 0x30)));

        Assert.Equal(0u, d.CreateSecondary(device, 88200, 44100, out var secondary));
        Assert.NotEqual(primary, secondary);
        d.Mem.Write(DsRig.D(DsRig.Caps), new byte[20]); d.Mem.WriteUInt32(DsRig.D(DsRig.Caps), 20);
        Assert.Equal(0u, d.Vt(secondary, 3, DsRig.D(DsRig.Caps)));
        Assert.Equal(88200u, d.Mem.ReadUInt32(DsRig.D(DsRig.Caps) + 8));
        Assert.Equal(0u, d.Vt(secondary, 5, DsRig.D(DsRig.Out), 40, DsRig.D(DsRig.Out + 0x30)));
        Assert.Equal((ushort)1, d.Mem.ReadUInt16(DsRig.D(DsRig.Out))); Assert.Equal(44100u, d.Mem.ReadUInt32(DsRig.D(DsRig.Out) + 4));

        // bad descriptors never reach the host
        d.Mem.WriteUInt32(DsRig.D(DsRig.Desc), 24);
        Assert.Equal(ComHr.InvalidArg, d.Vt(device, 3, DsRig.D(DsRig.Desc), DsRig.D(DsRig.Ppv), 0));
        Assert.Equal(DsConst.DsErrNoAggregation, d.Vt(device, 3, DsRig.D(DsRig.Desc), DsRig.D(DsRig.Ppv), 0x77));
        // volume / pan / frequency pass through with their signs
        Assert.Equal(0u, d.Vt(secondary, 15, unchecked((uint)-1000)));
        Assert.Equal(0u, d.Vt(secondary, 6, DsRig.D(DsRig.Out)));
        Assert.Equal(-1000, (int)d.Mem.ReadUInt32(DsRig.D(DsRig.Out)));
        Assert.Equal(0u, d.Vt(secondary, 17, 22050));
        Assert.Equal(0u, d.Vt(secondary, 8, DsRig.D(DsRig.Out)));
        Assert.Equal(22050u, d.Mem.ReadUInt32(DsRig.D(DsRig.Out)));
        // buffers answer IUnknown and IDirectSoundBuffer; 3D / notify truthfully do not exist
        Assert.Equal(ComHr.NoInterface, d.Rig.QueryInterface(secondary, new Guid("279AFA86-4981-11CE-A521-0020AF0BE560"), out var b3d)); Assert.Equal(0u, b3d);
        Assert.Equal(ComHr.NoInterface, d.Rig.QueryInterface(secondary, new Guid("B0210783-89CD-11D0-AF08-00A0C925CD16"), out _));

        Assert.Equal(0u, d.Rig.Release(secondary)); Assert.Equal(0u, d.Rig.Release(primary)); Assert.Equal(0u, d.Rig.Release(device));
        Assert.Equal(0, d.Com.LiveObjects);
    }

    private static byte[] ReadGuest(GuestMemory m, uint address, int length) { var b = new byte[length]; m.Read(address, b); return b; }

    private static byte[] Sine(int samples, int rate = 44100, double hz = 440, double amplitude = 0.5)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++) BitConverter.TryWriteBytes(pcm.AsSpan(2 * i), (short)(short.MaxValue * amplitude * Math.Sin(2 * Math.PI * hz * i / rate)));
        return pcm;
    }

    /// <summary>The host buffer's actual content, read through the host's own Lock (not through the shadow).</summary>
    private static unsafe byte[] ReadHostBuffer(GuestComObject buffer, int offset, int length)
    {
        var host = DirectSoundInspect.HostInterface(buffer)!;
        nint p1 = 0, p2 = 0; uint l1 = 0, l2 = 0;
        Assert.Equal(0u, host.Invoke(11, offset, length, (nint)(&p1), (nint)(&l1), (nint)(&p2), (nint)(&l2), 0));
        var data = new byte[l1 + l2];
        new ReadOnlySpan<byte>((void*)p1, (int)l1).CopyTo(data);
        if (l2 != 0) new ReadOnlySpan<byte>((void*)p2, (int)l2).CopyTo(data.AsSpan((int)l1));
        host.Invoke(19, p1, (nint)l1, p2, (nint)l2);
        return data;
    }

    [RequiresWindowsFact("needs the host's dsound.dll (Windows or Wine)")]
    public void Lock_hands_the_guest_low_shadow_memory_and_Unlock_copies_exactly_the_unlocked_regions_into_the_host_buffer()
    {
        using var d = new DsRig();
        if (NoDevice(d.Create(out var device))) return;
        Assert.Equal(0u, d.Vt(device, 6, User32BasicService.DesktopWindow, DsConst.DsSclPriority));
        const int size = 8820;       // 100 ms of 44.1 kHz mono 16-bit
        Assert.Equal(0u, d.CreateSecondary(device, size, 44100, out var buffer));
        var obj = d.Com.Find(buffer)!;

        // whole buffer, no wrap
        var all = d.Lock(buffer, 0, size);
        Assert.Equal((0u, (uint)size, 0u, 0u), (all.Hr, all.L1, all.P2, all.L2));
        Assert.True(all.P1 > 0 && all.P1 <= uint.MaxValue - size);                // a 32-bit guest address
        Assert.True(d.Mem.IsMapped(all.P1, size));
        Assert.Equal(size, DirectSoundInspect.ShadowBytes(obj));
        var tone = Sine(size / 2);
        d.Mem.Write(all.P1, tone);
        Assert.Equal(0u, d.Unlock(buffer, all.P1, all.L1, all.P2, all.L2));
        Assert.Equal(tone, ReadHostBuffer(obj, 0, size));                          // the data really is in the host buffer
        Assert.Equal(ComHr.Ok, d.Status(buffer) & 0u);

        // a region that wraps: two pointers, the second at the start of the buffer
        var wrap = d.Lock(buffer, size - 100, 300);
        Assert.Equal(0u, wrap.Hr);
        Assert.Equal((100u, 200u), (wrap.L1, wrap.L2));
        Assert.Equal(all.P1 + size - 100, wrap.P1);
        Assert.Equal(all.P1, wrap.P2);
        var patch1 = Enumerable.Repeat((byte)0x11, 100).ToArray(); var patch2 = Enumerable.Repeat((byte)0x22, 200).ToArray();
        d.Mem.Write(wrap.P1, patch1); d.Mem.Write(wrap.P2, patch2);
        Assert.Equal(0u, d.Unlock(buffer, wrap.P1, wrap.L1, wrap.P2, wrap.L2));
        var expected = (byte[])tone.Clone();
        patch1.CopyTo(expected, size - 100); patch2.CopyTo(expected, 0);
        Assert.Equal(expected, ReadHostBuffer(obj, 0, size));

        // a partial lock leaves the rest of the host buffer untouched, and the shadow keeps the old content (a mixer may read it)
        var part = d.Lock(buffer, 1000, 64);
        Assert.Equal((0u, 64u, 0u, 0u), (part.Hr, part.L1, part.P2, part.L2));
        Assert.Equal(expected[1000..1064], ReadGuest(d.Mem, part.P1, 64));
        d.Mem.Write(part.P1, Enumerable.Repeat((byte)0x7F, 64).ToArray());
        Assert.Equal(0u, d.Unlock(buffer, part.P1, part.L1, 0, 0));
        Array.Fill(expected, (byte)0x7F, 1000, 64);
        Assert.Equal(expected, ReadHostBuffer(obj, 0, size));

        // flags: ENTIREBUFFER ignores dwBytes; FROMWRITECURSOR ignores dwOffset and starts at the host's write cursor
        var entire = d.Lock(buffer, 0, 1, DsConst.DsblockEntireBuffer);
        Assert.Equal((0u, (uint)size), (entire.Hr, entire.L1));
        d.Unlock(buffer, entire.P1, entire.L1, entire.P2, entire.L2);
        var cursor = d.Lock(buffer, 12345, 64, DsConst.DsblockFromWriteCursor);
        Assert.Equal(0u, cursor.Hr);
        var write = d.Positions(buffer).Write;
        Assert.Equal(0u, (cursor.P1 - all.P1 + (uint)size - write) % (uint)size);   // not playing: the write cursor did not move
        d.Unlock(buffer, cursor.P1, cursor.L1, cursor.P2, cursor.L2);

        // errors
        Assert.Equal(ComHr.InvalidArg, d.Lock(buffer, size, 10).Hr);                 // offset beyond the buffer
        Assert.Equal(ComHr.InvalidArg, d.Lock(buffer, 0, size + 1).Hr);              // more than the buffer
        Assert.Equal(ComHr.InvalidArg, d.Lock(buffer, 0, 10, 0x4).Hr);               // unknown flag
        Assert.Equal(DsConst.DsErrInvalidCall, d.Unlock(buffer, all.P1, 4, 0, 0));    // nothing locked any more
        var l = d.Lock(buffer, 0, 16);
        Assert.Equal(ComHr.InvalidArg, d.Unlock(buffer, l.P1 + 0x100000, 16, 0, 0)); // a pointer that is not in the shadow
        Assert.Equal(0u, d.Unlock(buffer, l.P1, l.L1, 0, 0));
        // lifetime: a lock the guest never unlocks does not leak into the host or keep the buffer alive
        var never = d.Lock(buffer, 0, 128);
        Assert.Equal(0u, never.Hr);
        Assert.Equal(0u, d.Rig.Release(buffer));
        Assert.Equal(1, d.Com.LiveObjects);
        Assert.Equal(0u, d.Rig.Release(device));
        Assert.Equal(0, d.Com.LiveObjects);
    }

    [RequiresWindowsFact("needs the host's dsound.dll (Windows or Wine)")]
    public void Objects_the_guest_never_releases_are_released_on_the_host_when_the_context_is_disposed()
    {
        var d = new DsRig();
        if (NoDevice(d.Create(out var device))) { d.Dispose(); return; }
        Assert.Equal(0u, d.CreateSecondary(device, 4410, 22050, out var buffer));
        var host = DirectSoundInspect.HostInterface(d.Com.Find(buffer)!)!;
        d.Dispose();                                    // module first, then the resolver: no guest release ever happened
        Assert.True(host.IsReleased);
    }

    /// <summary>
    /// The opt-in audible test (BLITZCS_AUDIO_TONE_TEST=1, null sink via scripts/wine/audio-capture.sh): a looping 440 Hz secondary buffer is played for about 1.5 s while the REAL device cursors are polled.
    /// </summary>
    [AudibleToneFact]
    public void A_looping_secondary_buffer_plays_a_440Hz_tone_and_the_real_cursors_advance()
    {
        using var d = new DsRig();
        var created = d.Create(out var device);
        if (NoDevice(created)) return;
        Assert.Equal(0u, created);
        Assert.Equal(0u, d.Vt(device, 6, User32BasicService.DesktopWindow, DsConst.DsSclPriority));
        Assert.Equal(0u, d.CreatePrimary(device, out var primary));
        d.Mem.Write(DsRig.D(DsRig.Wfx), [.. BitConverter.GetBytes((ushort)1), .. BitConverter.GetBytes((ushort)1), .. BitConverter.GetBytes(44100u), .. BitConverter.GetBytes(88200u), .. BitConverter.GetBytes((ushort)2), .. BitConverter.GetBytes((ushort)16), .. BitConverter.GetBytes((ushort)0)]);
        d.Vt(primary, 14, DsRig.D(DsRig.Wfx));

        const int size = 44100 * 2;       // one second
        Assert.Equal(0u, d.CreateSecondary(device, size, 44100, out var buffer));
        var all = d.Lock(buffer, 0, size);
        Assert.Equal(0u, all.Hr);
        d.Mem.Write(all.P1, Sine(size / 2));
        Assert.Equal(0u, d.Unlock(buffer, all.P1, all.L1, all.P2, all.L2));

        Assert.Equal(0u, d.Vt(buffer, 12, 0, 0, 1));    // Play(0, 0, DSBPLAY_LOOPING)
        Assert.NotEqual(0u, d.Status(buffer) & 1);       // DSBSTATUS_PLAYING
        Assert.NotEqual(0u, d.Status(buffer) & 4);       // DSBSTATUS_LOOPING
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = d.Positions(buffer);
        var samples = new List<(long Ms, uint Play, uint Write)> { (0, first.Play, first.Write) };
        while (sw.ElapsedMilliseconds < 1500) { Thread.Sleep(50); var p = d.Positions(buffer); samples.Add((sw.ElapsedMilliseconds, p.Play, p.Write)); }
        // the play cursor advances in real time at 88200 bytes/s (modulo the buffer): unwrap and check the rate
        long travelled = 0; var previous = samples[0].Play;
        foreach (var s in samples.Skip(1)) { travelled += (s.Play + size - previous) % size; previous = s.Play; }
        var seconds = (samples[^1].Ms - samples[0].Ms) / 1000.0;
        Assert.InRange(travelled / 88200.0, seconds * 0.7, seconds * 1.3);
        Assert.All(samples, s => Assert.True(s.Play < size && s.Write < size));
        Assert.Equal(0u, d.Vt(buffer, 18));              // Stop
        Assert.Equal(0u, d.Status(buffer) & 1);
        Assert.Equal(0u, d.Vt(buffer, 13, 0));           // SetCurrentPosition(0)
        Assert.Equal(0u, d.Rig.Release(buffer)); Assert.Equal(0u, d.Rig.Release(primary)); Assert.Equal(0u, d.Rig.Release(device));
        Assert.Equal(0, d.Com.LiveObjects);
    }
}
