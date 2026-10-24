# waveOut audio for translated 32-bit libraries

A translated PE32 library that opens a wave device (for example a mixer using WINMM output) gets a real, ABI-correct waveOut API from the Win32 component
(`src/BlitzCS.BinaryCompiler.Win32`, `Services/WaveOutService.cs`, `Audio/`). The API layer adapts the 32-bit contract (WAVEHDR in guest memory, 32-bit HWAVEOUT, callback addresses) and delegates
to a pluggable `IWaveOutBackend`. Nothing is a silent no-op: an unsupported option fails with the proper `MMSYSERR_*` code (and a line on `Win32Options.DebugOutput`).

## Backends and selection

| Backend | Devices | Use |
|---|---|---|
| `HostWinmm` | the host's winmm.dll devices (Windows x64 process, including Wine) | real sound |
| `VirtualSink` | one software device "BlitzCS Virtual Output", every OS | tests, headless runs, capture of what the library mixed |
| `None` | zero devices (`waveOutGetNumDevs` = 0, ids fail `BADDEVICEID`/`NODRIVER`) | the previous behaviour, kept for hosts without audio |

Selection: `Win32Options.WaveOut` (`Default`, `HostWinmm`, `VirtualSink`, `None`); `Default` is `HostWinmm` on a Windows host and `None` elsewhere. The environment variable
`BLITZCS_AUDIO=host|virtual|none` overrides the option; any other value throws `Win32UsageException`. Choosing `HostWinmm` on a non-Windows host also throws (it is never silently downgraded).
Related options: `WaveOutRecordPath` (RIFF WAV of everything consumed/written; a format change starts `name.1.wav`, ...) and `WaveOutObserver` (`Action<WaveOutWrite>`: device id, header address, `WaveOutFormat`,
stream time in ms, a private copy of the bytes). The tap and the recorder work on top of both `VirtualSink` (when a buffer finished playing, or the played prefix on reset) and `HostWinmm` (when the buffer is written,
before it is forwarded). The tap sees samples before any volume; the virtual device volume is state only (get returns what set stored).

Implemented imports (WINMM.dll, stdcall): `waveOutGetNumDevs`(0), `waveOutGetDevCapsA/W`(3), `waveOutOpen`(6), `waveOutClose/Reset/Pause/Restart/BreakLoop`(1), `waveOutPrepareHeader/UnprepareHeader/Write`(3),
`waveOutGetPosition`(3), `waveOutGetVolume/SetVolume`(2), `waveOutGetID`(2), `waveOutGetErrorTextA/W`(3). Absent: `waveOutMessage` (no driver message channel), all `waveIn*` and `mixer*` beyond the existing
zero-device answers, and every `mci*`.

## Semantics

* Flags in the guest WAVEHDR (offsets: lpData 0, dwBufferLength 4, dwBytesRecorded 8, dwUser 12, dwFlags 16, dwLoops 20, lpNext 24, reserved 28): Prepare sets `WHDR_PREPARED`; Write sets `WHDR_INQUEUE` and clears `WHDR_DONE`;
  completion sets `WHDR_DONE` and clears `WHDR_INQUEUE` **before** the guest notification; Unprepare clears `WHDR_PREPARED`. Write of an unprepared header: `WAVERR_UNPREPARED`; of a queued header, Unprepare of a queued
  header and Close with queued buffers: `WAVERR_STILLPLAYING`. Unprepare of a header that was never prepared succeeds (as on Windows). `cbwh` below 32 or an unmapped pointer: `MMSYSERR_INVALPARAM`.
* `waveOutReset` returns every pending buffer (done) **before it returns**; on `VirtualSink` the completions (and callbacks) run on the calling thread, on `HostWinmm` it waits up to 2 s for winmm's callbacks.
* `waveOutOpen` flags: `CALLBACK_NULL`, `CALLBACK_FUNCTION`, `CALLBACK_EVENT`, `WAVE_FORMAT_QUERY` (no handle; `WAVERR_BADFORMAT` for an unsupported format), `WAVE_ALLOWSYNC`, `WAVE_FORMAT_DIRECT`.
  `CALLBACK_WINDOW`, `CALLBACK_THREAD`, `WAVE_MAPPED` and `WAVE_MAPPED_DEFAULT_COMMUNICATION_DEVICE` fail with `MMSYSERR_NOTSUPPORTED`; other bits with `MMSYSERR_INVALFLAG`. `CALLBACK_FUNCTION` needs a running guest
  module (`MMSYSERR_NOTSUPPORTED` otherwise); `CALLBACK_EVENT` needs a HANDLE from this component's `CreateEvent` (`MMSYSERR_INVALHANDLE` otherwise). `WOM_OPEN`, `WOM_DONE` (dwParam1 = guest header address) and
  `WOM_CLOSE` are delivered with `(hwo, msg, dwInstance, dwParam1, dwParam2)`; events are signalled for every message.
* `VirtualSink` formats: PCM (or EXTENSIBLE with the PCM sub-format), 1-2 channels, 8/16 bit, 8000-48000 Hz, `nBlockAlign` = channels*bits/8; buffers must be a whole number of frames. Buffers are consumed at real-time
  rate; `Pause` stops the clock; loops follow `WHDR_BEGINLOOP`/`WHDR_ENDLOOP` with the `dwLoops` of the begin header; `BreakLoop` ends the loop after the current pass. `waveOutGetPosition` supports `TIME_MS`, `TIME_SAMPLES`,
  `TIME_BYTES`; any other type answers `TIME_BYTES` (what Windows does); values are 32-bit and wrap.

## ABI adaptation table

| Guest item | Host handling | Why |
|---|---|---|
| HWAVEOUT (32-bit) | opaque id from the context handle table (multiples of 4, never reused); the host 64-bit HWAVEOUT lives only inside `HostWinmmBackend.HostDevice` | a 64-bit handle does not fit; stale handles must fail `INVALHANDLE` |
| WAVEHDR (32 bytes) | stays in guest memory; only `WaveOutBuffer` (copies of lpData/length/flags/loops/user) goes to the backend. `HostWinmm` mirrors it with a native 48-byte WAVEHDR (lpData 8 bytes, dwUser pointer-sized) allocated at Prepare, freed at Unprepare/Close, keyed by the guest header address | layouts differ (32 vs 64-bit); the host must never read the guest struct |
| lpData | `GuestMemory` is identity-mapped (guest address == host address, see `GuestMemory`), so on the Windows x64 host the zero-extended address is a valid host pointer; it is validated with `IsMapped(lpData, len)` first (`INVALPARAM` otherwise) and played in place. For any other `IWin32Memory` (tests, host tools) a native copy is made at Prepare and refreshed at every Write | no copy on the hot path, no unchecked pointer |
| WAVEFORMATEX / EXTENSIBLE | read with bounds checks (18 + cbSize bytes, 16 for PCM) and copied byte-for-byte into native memory (identical packed layouts) | copied, not mapped |
| WAVEOUTCAPSA/W | built from a `WaveOutCaps` record (host: `waveOutGetDevCapsW` into a private buffer) and written truncated to `cbwoc` | the A layout is derived from the W data |
| MMTIME | read/written field by field (wType + union DWORD); size must be 12 | no struct is forwarded |
| device id / WAVE_MAPPER | zero-extended to UINT_PTR; an HWAVEOUT may be passed where an id is expected (caps, volume) | mmsystem allows it |
| completion | internal `CALLBACK_FUNCTION` ([UnmanagedCallersOnly] static; dwInstance = a numeric device id, not a pointer, so a late message after close finds nothing); then the guest's own callback | guests poll the header flags and the host callback thread must not depend on guest state |
| guest callback / event | `GuestModule.CallAddress(fn, hwo, msg, dwInstance, dwParam1, dwParam2)`; `KernelEvent.Set` | see threading |

## Threading rules

* Guest threads and backend threads (the `VirtualSink` worker; winmm's callback thread) race on header state. A per-handle lock guards only the header table, the queued count and the guest flag updates; it is **never held
  while calling the device or guest code**, so a guest callback (or a winmm callback blocked on the lock) cannot deadlock a guest thread inside `waveOutWrite`.
* The `HostWinmm` callback (and the guest function) run on foreign threads. `GuestModule` creates the per-thread TEB and guest stack lazily on the first call of any host thread (the same mechanism as `timeSetEvent` and
  `CreateThread`), so calling guest code from the winmm thread works; winmm's own restrictions on what a callback may call remain the guest's responsibility. Faults in a callback are recorded in
  `Kernel32ExtraService.ThreadFaults`; a disposed module drops the notification.
* The guest may free a header only after Unprepare, which fails for queued headers; the completion path updates the guest flags under the same lock, and a header unknown to the table (or already completed) is ignored.
* `VirtualSink` never calls the sink or the observer with its own lock held.

## Limitations

DirectSound, waveIn, mixer, MCI, ACM and `waveOutMessage` are not covered (`eax.dll`/DirectSound-style output is untested). Hardware-specific behaviour (latency, device buffering) is the host's. On Wine the device list and
words of the error texts follow Wine (three devices and a Portuguese `waveOutGetErrorText` were observed here). `WaveOutRecordPath` is single-stream oriented: concurrent streams interleave by completion order. The
virtual device volume does not scale samples. Streams still open when the context is disposed are closed without notifications.

## Evidence and how to test

* Linux: `dotnet test tests/BlitzCS.BinaryCompiler.Win32.Tests` (`WaveVirtualSinkTests`, `WaveGuestCallbackTests` with a hand-assembled IA-32 callback, `WaveSelectionTests`).
* Windows / Wine: the same assembly plus `WaveHostWinmmTests` and the host variant of the guest-callback test (`scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Win32.Tests`, see `docs/wine-testing.md`).
* Proof that sound leaves the process: `WaveToneTests.HostWinmmPlaysA440HzTone` is opt-in (`BLITZCS_AUDIO_TONE_TEST=1`) because it is audible. `scripts/wine/audio-capture.sh` creates a PulseAudio null sink, runs the command with
  `PULSE_SINK=<sink>`, records `<sink>.monitor` and unloads the sink in a trap; `tools/audio/analyze_wav.py` (stdlib only) reports duration, RMS, peak, silence ratio and the dominant frequency:

```
scripts/wine/audio-capture.sh --out artifacts/audio/tone.wav -- env BLITZCS_AUDIO_TONE_TEST=1 \
    scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Win32.Tests --filter WaveToneTests
python3 tools/audio/analyze_wav.py artifacts/audio/tone.wav --expect-freq 440 --freq-tolerance 3
```
