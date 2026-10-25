# Guest COM and DirectSound for translated 32-bit libraries

A translated PE32 library can only call addresses that are translated-code dispatch targets, and an artifact has one import stub per real import. Anything the library obtains at run
time (`GetProcAddress`, a COM vtable) therefore needs a stub that did not exist at build time. This document covers the pieces that solve that, and DirectSound built on them.

## 1. Dynamic stub pool

* `BinaryCompilerOptions.DynamicStubs = N` (0..4096, default 0) makes the translator emit N extra import-style stubs after the real imports (stub `Imports.Count + j` at
  `ImportStubBaseRva + 16 * index`, thunk slot `8 * index`), for Win64 and SysV alike. N is part of the cache key (only when non-zero) and of the artifact (`TranslationArtifact.DynamicStubs`,
  serializer version 4, compiler version 0.8.0). With N = 0 the host image is byte-identical to the previous compiler (pinned for linux-sysv and windows-x64 in `TargetOsTests`).
* Runtime: `GuestModule.AllocateStub(handler, popBytes) -> guest address`, `ReleaseStub(address)`, `IsStubAddress`, `StubCapacity`, `StubsInUse`. Thread-safe and bounded; exhaustion, a module
  without a pool and a double release throw `GuestStubException`. Each stub owns one permanent thunk whose target is swapped atomically, so releasing never frees code another thread may be
  running. A stub that is not allocated faults by name and index when the guest calls it ("dynamic stub #3 ... is released / was never handed out").
* CLI: a library that imports `LoadLibrary*`/`GetProcAddress` gets 512 stubs automatically; the catalog entry may say `"com": true|false` or `"dynamicStubs": N`.
* `GetProcAddress` on an emulated DLL (`ProcessService`) now returns a pool stub bound to the same handler as the import (one stub per dll/name, stable across calls). Without a pool it still
  answers NULL / ERROR_PROC_NOT_FOUND and writes the reason to `Win32Options.DebugOutput`.

## 2. Guest COM layer (`Win32/Com`)

* `GuestComInterface` (IID, methods in vtable order with exact stdcall argument counts; `NotImplemented(name, argc, reason)` answers E_NOTIMPL only for slots that exist but are not implemented;
  `Answers(iid)` for IS-A IIDs). `GuestComHost.For(context, module).Create(name, state, interfaces, onDestroy)` builds an object: block `[vtable ptr][instance id]` per face in guest low memory,
  vtables of pool stubs shared per interface.
* IUnknown: one atomic count per object; QueryInterface answers IUnknown (primary face), the IID tables, else `*ppv = NULL` and E_NOINTERFACE; the last Release destroys exactly once, runs the
  destroy callback and switches the block to a dead vtable, so a double Release or use after free faults by name (`IDirectSoundBuffer #3 ... after the object was destroyed`). Blocks are not
  reused. Disposing the context destroys the objects the guest never released (newest first) so host resources are freed even after the module is gone.
* `Absent(name, argc, reason)`: a slot that exists but is not bridged faults naming `Interface::Method` and the reason (the DirectX tables use it instead of E_NOTIMPL).
  `GuestComHost.Create(..., queryFallback)` and `FaceFor(obj, iface)` add faces lazily: `HostComRegistry` (`Com/HostComProxy.cs`) wraps native objects with one guest identity
  per host object (docs/binary-user32-bridge.md, "COM objects").
* Host proxy: `HostComInterface` calls a native 64-bit interface through its vtable with pointer-sized arguments in the host's native convention (Win64 on Windows), returning the host HRESULT
  unchanged. `HostComMethod` rows (name, native slot, one `ComArg` per guest argument: `Value`, `Signed`, `Guid`, `AnsiString`, `InStruct`, `OutU32/OutBytes`, `InOutStruct`, `OutInterface`,
  `Handle`) are the explicit adapter table; a bad argument answers E_INVALIDARG without reaching the native API. There is no generic forwarding.

## 3. DirectSound (`Audio/DirectSoundAdapters.cs`, `Services/DirectSoundService.cs`)

`dsound.dll!DirectSoundCreate` and `DirectSoundEnumerateA` go to the HOST's dsound.dll (Windows, or Wine's built-in with winepulse). On a host without one, or with
`Win32Options.DirectSound = None`, they answer the real `DSERR_NODRIVER` (0x88780078). Adapted: IDirectSound and IDirectSoundBuffer (all methods; `DuplicateSoundBuffer` and
`IDirectSoundBuffer::Initialize` are E_NOTIMPL with the reason in the table). Not adapted, answered truthfully: IDirectSound8/Buffer8, 3D, notify, FX (E_NOINTERFACE); `DirectSoundCreate8` and
capture are not exported (GetProcAddress NULL).

* **Descriptors:** `DSBUFFERDESC` (guest dwSize 20/36) is rebuilt in the host layout (dwSize 24/40, 64-bit `lpwfxFormat` pointing at a native copy of the WAVEFORMATEX, 3D GUID moved to offset 24).
* **HWND:** with the host window bridge (the default on a Windows host, docs/binary-user32-bridge.md) the guest's HWNDs are host windows (sign-extended, checked with the host's
  IsWindow). With `HostWindowing = None` the guest's windows are opaque sandbox handles. The desktop handle maps to the host's real desktop window, which Wine's dsound accepts as the focus window of `SetCooperativeLevel`
  (verified); NULL stays NULL; any other value is E_INVALIDARG. `GetForegroundWindow` returns NULL (no window system). FMOD calls it, gets NULL, and still initialises.
* **Lock/Unlock:** each secondary buffer has a shadow in guest low memory (silence-initialised). Lock returns shadow pointers with the documented two-region wrap, `DSBLOCK_FROMWRITECURSOR`
  (host write cursor) and `DSBLOCK_ENTIREBUFFER`; Unlock copies exactly the unlocked regions into the host buffer (host Lock, copy, host Unlock). No host lock is held between guest Lock and
  Unlock, so a guest that never unlocks leaks nothing; its data is simply never committed. Positions come from the host (real play/write cursors). Primary buffers cannot be locked.
* **Device lifetime:** the host device reference is dropped only after the guest released it AND all its buffers are gone (Wine destroys buffers with the device, Windows does not).
* Tests: `StubPoolTests`, `GuestComTests`, `HostComProxyTests`, `DirectSoundBindingTests` (all hosts), `HostDirectSoundTests` (Windows/Wine; `BLITZCS_REQUIRE_HOST_AUDIO=1` makes "no audio
  device" a failure), `FmodDemoTests.TheSameProgramDrivesFmodsDirectSoundOutputThroughTheHostDsound`.
* Diagnostics: `BLITZCS_WIN32_TRACE=1` prints every Win32 and COM call of a generated program to stderr.

## 4. Results (Wine 11, winepulse, null sink, `scripts/wine/audio-capture.sh` + `tools/audio/analyze_wav.py`)

| Run | dominant | peak |
|---|---|---|
| Win32 test, looping 440 Hz secondary buffer | 439.99 Hz | 0.5000 |
| Original FMOD 3.75, FSOUND_OUTPUT_DSOUND | 440.09 Hz | 0.3526 (WINMM baseline 0.3526) |
| Same with NOSOUND (negative control) | silent | 0.0000 |

Side effect found by FMOD: `DeleteCriticalSection` on a section the CALLING thread still owns is now tolerated (Windows does; FMOD does it closing DirectSound); a section owned by another thread is still refused.
