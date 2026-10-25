using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// WINMM. waveOut* is implemented by <see cref="WaveOutApi"/> over a pluggable backend (HostWinmm / VirtualSink / None, see docs/binary-audio.md); with the None backend the machine has NO multimedia hardware.
/// waveIn and mixer stay hardware-less: waveInGetNumDevs and mixerGetNumDevs return 0. Consequently, for the None backend and for waveIn/mixer
/// (MMSYSERR_* values from mmsystem.h): any call that takes a device id fails - WAVE_MAPPER (0xFFFFFFFF) with MMSYSERR_NODRIVER (6), a numbered device with MMSYSERR_BADDEVICEID (2);
/// waveOutOpen/waveInOpen/mixerOpen can therefore never produce a handle, so every call taking a wave/mixer handle fails with MMSYSERR_INVALHANDLE (5) for ANY value. Mixer calls
/// taking a HMIXEROBJ look at the MIXER_OBJECTF_* type bits: HANDLE -> INVALHANDLE, any id-based type -> BADDEVICEID. No audio is ever played or recorded. mciSendCommandA and
/// mciGetErrorStringA are NOT provided (an MCI device model is out of scope; the guest faults naming them).
/// timeGetTime is the system uptime in milliseconds (32-bit wrap). timeSetEvent implements the multimedia timer for TIME_CALLBACK_FUNCTION: a host thread calls the guest procedure
/// <c>(uTimerID, 0, dwUser, 0, 0)</c> after uDelay ms (TIME_ONESHOT) or every uDelay ms (TIME_PERIODIC) through <c>GuestModule.CallAddress</c>; the event-signalling callback modes
/// (TIME_CALLBACK_EVENT_SET/PULSE) fail with NULL and ERROR_NOT_SUPPORTED. Resolution is ignored (the host timer is used). timeKillEvent returns 0, or MMSYSERR_INVALPARAM (11) for an
/// unknown/finished timer; with TIME_KILL_SYNCHRONOUS it waits for a running callback. Faults inside a callback are recorded in <see cref="Kernel32ExtraService.ThreadFaults"/>.
/// </summary>
public sealed class WinmmService : Win32Service
{
    public override string Name => "Winmm";
    private const string W = "WINMM.dll";
    private const uint NoDriver = 6, BadDeviceId = 2, InvalHandle = 5, InvalParam = 11, WaveMapper = 0xFFFFFFFF;

    private sealed class Timer(uint id, CancellationTokenSource cts)
    {
        public readonly uint Id = id;
        public readonly CancellationTokenSource Cts = cts;
        public volatile bool Running;
        public bool Synchronous;
        public int CallbackThread;
    }

    private sealed class Timers { public readonly ConcurrentDictionary<uint, Timer> Map = new(); public int Next; }
    private static readonly ConditionalWeakTable<Win32Context, Timers> Table = new();

    private static uint DeviceError(uint id) => id == WaveMapper ? NoDriver : BadDeviceId;

    protected override void Define()
    {
        WaveOutApi.Define((name, argc, handler) => Std(W, name, argc, handler));
        Std(W, "waveInGetNumDevs", 0, _ => 0);
        Std(W, "mixerGetNumDevs", 0, _ => 0);
        Std(W, "waveInGetDevCapsA", 3, c => DeviceError(c[0]));
        Std(W, "waveInOpen", 6, c => DeviceError(c[1]));
        Std(W, "mixerOpen", 5, c => BadDeviceId);
        foreach (var (name, argc) in new[]
        {
                        ("waveInClose", 1), ("waveInReset", 1), ("waveInStart", 1), ("waveInAddBuffer", 3), ("waveInPrepareHeader", 3), ("waveInUnprepareHeader", 3), ("mixerClose", 1),
        })
            Std(W, name, argc, _ => InvalHandle);
        foreach (var name in new[] { "mixerGetLineInfoA", "mixerGetLineControlsA", "mixerGetControlDetailsA", "mixerSetControlDetails" })
            Std(W, name, 3, c => (c[2] & 0x80000000) != 0 ? InvalHandle : BadDeviceId);

        Std(W, "timeGetTime", 0, _ => (uint)Environment.TickCount64);
        Std(W, "timeSetEvent", 5, SetEvent);
        Std(W, "timeKillEvent", 1, c =>
        {
            var timers = Table.GetValue(c.Ctx, _ => new Timers());
            if (!timers.Map.TryRemove(c[0], out var t)) return InvalParam;
            t.Cts.Cancel();
            if (t.Synchronous) while (t.Running && t.CallbackThread != Environment.CurrentManagedThreadId) Thread.Sleep(1);
            return 0;
        });
    }

    private static uint SetEvent(Win32Call c)
    {
        uint delay = c[0], proc = c[2], user = c[3], flags = c[4];
        var module = GuestModule.Current ?? throw new Win32UsageException("timeSetEvent needs a running guest module.");
        if ((flags & 0x30) != 0) return c.Ctx.Fail(Win32Error.NotSupported);
        if (delay == 0 || proc == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var periodic = (flags & 1) != 0;
        var timers = Table.GetValue(c.Ctx, _ => new Timers());
        var id = (uint)Interlocked.Increment(ref timers.Next);
        var cts = new CancellationTokenSource();
        var timer = new Timer(id, cts) { Synchronous = (flags & 0x100) != 0 };
        var ctx = c.Ctx;
        timers.Map[id] = timer;
        var thread = new Thread(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (cts.Token.WaitHandle.WaitOne((int)delay)) break;
                    timer.CallbackThread = Environment.CurrentManagedThreadId; timer.Running = true;
                    try { module.CallAddress(proc, id, 0, user, 0, 0); }
                    catch (Exception e) { Kernel32ExtraService.RecordFault(ctx, e); break; }
                    finally { timer.Running = false; }
                    if (!periodic) { timers.Map.TryRemove(id, out _); break; }
                }
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "guest-mm-timer" };
        thread.Start();
        return id;
    }
}
