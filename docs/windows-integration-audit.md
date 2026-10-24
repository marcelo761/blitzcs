# Windows x64 application integration audit

Scope: the path from `blitzcs build main.bb --externals catalog.json` to a running Windows x64 application that executes translated PE32 libraries (`src/BlitzCS.CLI`, `src/BlitzCS.BinaryCompiler.Runtime`,
`src/BlitzCS.BinaryCompiler.Win32`, the code generator's Win64 routines), reviewed against the code at the commit after `384d38f` plus the working tree of 2026-10-24, and exercised with
the original FMOD 3.75 (`samples/FmodAudio`) under Wine 11 (Proton 11) and on Linux. Wine is the only Windows-family environment available here; where a real Windows host behaves
differently, the item is marked **untestable here** and rests on Microsoft documentation (cited) and on facts read from the produced binaries.

Status: **fixed** (changed in this pass, with a test), **open** (reported, owner named), **ok** (checked, no defect), **risk** (cannot be tested here).

## Summary

| ID | Severity | Status | Finding |
| --- | --- | --- | --- |
| F-01 | High | fixed | A program that ends while a library thread is still inside the module (FMOD's mixer, when `FSOUND_Close` was not called) never exited: process-exit disposal waited forever |
| F-02 | Medium | fixed | The generated Win32 environment gave every library `SandboxRoot = null`, so a packaged application's library could not open any file (FMOD `Sample_Load` failed); new opt-in `fileRoot: "app"` |
| F-03 | Low | fixed | The publisher never asserted that each `.bcx` was translated for the application's target |
| F-04 | Low | fixed | `PlatformMemory.Current` was a racy lazy initializer (two instances, two allocation locks) |
| F-05 | Low | fixed | `BinaryCache` fell back to a relative `.cache` in the current directory when the user profile folder is empty |
| F-06 | - | ok | Translation target is independent of the build host; artifacts carry and the loader enforces `TargetOs` |
| F-07 | - | ok | The application package holds everything needed at run time (list below); the dotnet tool package ships the unpacker |
| F-08 | - | ok | Native call sites classified (table below): no guest handle, pointer or struct reaches a 64-bit OS API unadapted |
| F-09 | - | ok | NativeInterop's PE preflight never sees `binaryLibraries`; a PE32 file in a 64-bit process is refused with a clear message |
| F-10 | Medium | open (Win32) | The file sandbox refuses every reparse point: cloud-sync placeholders and linked folders on a real Windows host (risk) |
| F-11 | High (audio) | open (A1) | The virtual sink snapshots a waveOut buffer at `waveOutWrite`; FMOD rewrites its looped buffer in place, so the recording is silent. One-line change verified |
| F-12 | High (demo) | open (translator) | `FSOUND_Close` with WINMM output traps on an untranslated code address (a function that follows a `jmp`); cause and verified change below |
| F-13 | Medium | open (translator) | `FSOUND_SetMixer(5)` (FPU mixer) crashes the process with an access violation in translated code |
| F-14 | Medium | open (risk) | A hardware fault in guest code is fatal and cannot be reported: the OS cannot dispatch it from the guest stack |
| F-15 | - | risk | Control Flow Guard: no `SetProcessValidCallTargets` is needed (default for executable pages); the packaged exe is CFG-enabled |
| F-16 | - | risk | CET shadow stacks: the packaged exe and the runtime are `CETCOMPAT`; the translated code keeps call/return balanced by construction; unverified on hardware |
| F-17 | Medium | open (risk) | Unsigned generated binaries are blocked by Smart App Control enforcement / strict App Control; Arbitrary Code Guard cannot be supported |
| F-18 | Medium | open (risk) | The guest image base is fixed at build time inside `0x20000000-0x6FFFFFFF`; an occupant on a real machine fails the load (explicitly) |
| F-19 | Low | open (Win32) | `FileService` compares sandbox roots after `TrimEnd('/')` only |
| F-20 | Low | open | The publisher starts a bare `dotnet` when `DOTNET_HOST_PATH` is unset |
| F-21 | Low | open | The guest's original x86 code pages are mapped executable although they are never executed natively |
| F-22 | - | ok | The instruction-cache flush is thread-safe and ordered before publication |

## Fixed in this pass

### F-01 Process exit waits for a library thread forever (High)

Evidence: `BinaryLibrary.cs:55` registered `DisposeAll` for `ProcessExit`; `GuestModule.Dispose` takes the exclusive side of the module's lifetime lock
(`GuestModule.cs:574`), and a guest thread started by the library (`CreateThread`, `Kernel32ExtraService.cs`) runs inside one long `CallAddress`, holding the shared side until the
program stops it. FMOD's mixer is such a thread. A program that does not call `FSOUND_Close` (the SCP:CB snapshot has it only in a comment, `docs/fmod-audit-m14.md`) never ended.

Reproducer (before the change, under Wine, `FSOUND_SetOutput(0)`, `FSOUND_Init`, load a WAV, `FSOUND_PlaySound`, end the program): the process still ran after 60 s (`timeout` exit 124).
After: exit code 0 in about 1 s, on Wine and on a Linux x64 package.

Change (`src/BlitzCS.BinaryCompiler.Runtime`): `GuestModule.TryDispose(timeout)`, `GuestModuleSet.DisposeAtProcessExit(grace)`, and a 250 ms bound used only by the process-exit hook
(`BinaryLibrary.DisposeAll(int)`). A module whose guest code is still running after the grace period is abandoned to the operating system (no DllMain detach, no unmapping under the
running thread, and its Win32 environment is not disposed); every other module is detached exactly as before. `Dispose()` and `DisposeAll()` keep waiting. Windows itself ends the other
threads of an exiting process; a managed host cannot, so it must not wait for them.

Tests: `ProcessExitTests.AProgramEndsEvenWhileALibraryThreadIsStillInsideTheModule` (a PE32 fixture whose `CreateThread` routine loops on `Sleep`; negative control: with an infinite grace
the test fails after the 30 s limit with "process exit waits for a library thread that never returns") and `FmodDemoTests.AProgramThatNeverClosesFmodStillEnds` (the real FMOD).

### F-02 Libraries could not read files (Medium)

`BinaryBindingGenerator` created `new Win32ImportResolver()` with default options, i.e. `Win32Options.SandboxRoot = null`, and `FileService.Translate` then answers every path with
`ERROR_ACCESS_DENIED` (`FileService.cs:68`). A packaged application whose library loads its own data (FMOD loading `tone.wav`) got `FSOUND_Sample_Load = 0`, `FSOUND_GetError = 9`.
Change: the catalog `binaryLibraries` option `"fileRoot": "app"` sets `SandboxRoot` to the directory of the generated binding assembly (the package directory, where the DLL, its `.bcx`
and `--assets` live). The default stays `"none"`, so existing catalogs keep their behaviour. Windows path forms (`C:\x`, any case, nested folders, `..` clamped at the root) work and UNC
paths are refused; `..` cannot leave the package directory (`Win32EnvironmentOptionsTests`).

### F-03, F-04, F-05

* F-03: `BinaryTranslationStep.Prepare` now refuses an artifact whose `TargetOs` differs from the build's target (`BinaryTranslationStep.cs:79`); the cache key already contains the target
  (`CacheKey.cs:17`), so this is defence in depth against a damaged or hand-copied cache.
* F-04: `PlatformMemory.Current` uses `LazyInitializer.EnsureInitialized` (`IPlatformMemory.cs:45`); the old `??=` could create two `WindowsPlatformMemory` instances with separate `MapLow` locks.
* F-05: `BinaryCache.DefaultRoot` returns the temp directory when `Environment.GetFolderPath(UserProfile)` is empty instead of the relative path `.cache`.

## Checked, no defect

### F-06 Target selection

* `CliApplication.cs` (package branch): `applicationTarget = --target ?? windows-x64`; it is passed to `ExternalCatalog.Load(catalog, applicationTarget)` (base planning, unpack bases) and to
  `ApplicationPublisher.Build` -> `BinaryTranslationStep.Prepare(..., target)`. `ResolveTarget` order: `--target`, catalog `binaryTarget`, `BLITZCS_BINARY_TARGET`, `windows-x64`; the application's
  target therefore always beats the catalog and the environment.
* Translation is pure computation; the unpacker is an interpreter. The Linux host built the Windows package used in every run here.
* Cache key includes `TargetOs` and the load base. `ArtifactSerializer` stores the normalized target (`ArtifactSerializer.cs:32`); `GuestModule.Validate` refuses a mismatching artifact with a message naming both targets.
* Tested: `FmodDemoTests.TheDefaultWindowsApplicationPackageRunsTheDemo` reads `fmod.dll.bcx` back and asserts `windows-x64`; `WindowsPackageTests` does the same for a synthetic library.
* The low-level `-o x.dll` mode takes the target from the environment or the `windows-x64` default and is documented as tooling; the E2E suite sets `BLITZCS_BINARY_TARGET` to the host.

### F-07 Package contents

Observed `dist/main` (Windows target, FMOD): `main.exe` (apphost), `main.dll`/`.pdb`/`.deps.json`/`.runtimeconfig.json`, `BlitzCS.BinaryBindings.dll`, `BlitzCS.BinaryCompiler.dll` (ArtifactSerializer,
PeReader), `BlitzCS.BinaryCompiler.Runtime.dll`, `BlitzCS.BinaryCompiler.Win32.dll`, `Iced.dll`, `fmod.dll` (byte-identical to the input, asserted by the test), `fmod.dll.bcx`, `fmod.dll.derived`,
`fmod.dll.lineage.json`, the asset `tone.wav`, `coreclr.dll`, `hostfxr.dll`, the framework assemblies and the ownership marker. `BlitzCS.BinaryCompiler.Unpack.dll` is not shipped and is not needed:
`TranslatedLibrary.Read` validates the derived image against the original through the lineage file (hashes of both). The `dotnet tool` package (`dotnet pack src/BlitzCS.CLI`) contains
`tools/net10.0/any/BlitzCS.BinaryCompiler.Unpack.dll`, so `"unpack": true` works from an installed tool.

### F-08 Native call sites

| Site | Native API | What is passed | Classification |
| --- | --- | --- | --- |
| `Runtime/Platform/WindowsPlatformMemory.cs` | `VirtualAlloc`, `VirtualFree`, `VirtualProtect`, `VirtualQuery`, `FlushInstructionCache` | addresses and sizes the runtime computed (validated artifact base/size, arena scan result) | host-owned values only. A guest `VirtualAlloc` is emulated (`MemoryService.cs`: `GuestMemory.Allocate`), never forwarded |
| `Runtime/Libc.cs`, `Platform/LinuxPlatformMemory.cs` | `mmap`, `munmap`, `mprotect`, `/proc/self/maps` | same | Linux only; `PlatformMemory.Create` picks by OS, the type is never touched on Windows |
| `Runtime/GuestModule.cs:389` | indirect call into the translated host image (`EnterGuest`) | host image address, 32-bit guest ESP, host call-context pointer | host-owned |
| `Runtime/GuestImports.cs` | reverse P/Invoke thunks (`Marshal.GetFunctionPointerForDelegate`) | the guest ESP (uint); arguments are read through bounds-checked `GuestMemory` | guest -> host; no pointer is dereferenced unchecked, exceptions are contained |
| `Runtime/GuestX87.cs` | indirect call into a 27-byte helper page (RX) | a host stack scratch pointer | host-owned |
| `Win32/Audio/HostWinmmBackend.cs` (A1, reviewed in its current form) | `winmm.dll waveOut*` | HWAVEOUT is private to the backend (the guest sees a table id), the guest WAVEHDR is mirrored by a 48-byte native header, callbacks carry a numeric device id, `lpData` is played in place after `IsMapped` | adapted. Residual: in-place `lpData` relies on the guest not freeing a queued buffer (Unprepare of a queued header fails) |
| `Win32/Services/*` | none; .NET managed APIs only: `System.IO` (paths translated by `FileService.Translate`), `Console` streams, `Thread`/`Monitor`, `Stopwatch`, `Environment.ProcessId` | translated paths; guest handles are table ids | no OS handle ever reaches the guest, no guest handle reaches the OS |
| `NativeInterop/NativeLibraryOwner.cs` | `NativeLibrary.Load` | x64 libraries declared as `nativeLibraries` | separate from `binaryLibraries` (F-09) |
| `CLI/ApplicationPublisher.cs:123` | `Process.Start dotnet publish` | build-time paths | build host only |

No `Mono.Posix`, no `Environment.SpecialFolder` default that assumes Linux (the cache root checks `IsWindows`), no ELF check reachable on the Windows runtime path. Float and integer text formatting
in the compiler runtime and the CRT emulation uses `InvariantCulture` (`CompatibilityRuntime.cs`, `CrtService.cs`), which the Wine runs (invariant globalization is forced by `scripts/wine/env.sh`) would not reveal.

### F-09 NativeInterop PE preflight

`NativeBinaryPreflight.Validate` (`NativeInterop/NativeBinaryPreflight.cs`) runs for `nativeLibraries` loaded with `NativeLibrary.Load`. A `binaryLibraries` file is only read as bytes by
`TranslatedLibrary`/`PeReader`; the operating system's loader never sees it. If a PE32 file is nevertheless listed as a native library, the CLI refuses it for a Windows application
(`ApplicationPublisher.cs:20-26`: "Declare legacy PE32 libraries in binaryLibraries") and the preflight would reject it at run time (`PE machine 0x014c does not match process architecture X64`,
and on non-Windows hosts `PE libraries require Windows`). Header facts on the packaged binaries are in F-15/F-16.

### F-22 Instruction-cache flush

`WindowsPlatformMemory.Protect` calls `VirtualProtect` then `FlushInstructionCache(-1, ...)` for every transition to RX (`WindowsPlatformMemory.cs:103-108`). The pseudo handle needs no
lifetime management; the OS call is thread-safe and takes no runtime state. The call happens inside `GuestModule.Initialize`, before `hostMap` is observable to any other thread (the module is
published by `BinaryLibrary.LibraryGroup.Open` under its lock), so there is no window in which another thread could execute pages whose flush is pending. `MapLow` serializes on the
platform object's lock (F-04 made it a singleton). Under ARM64 Windows' x64 emulation this flush is also what invalidates the emulator's translation cache (risk, untestable here).

## Open

### F-10 Reparse points (Medium, Win32 component, risk)

`FileService.cs:97` returns `ERROR_ACCESS_DENIED` for any path component with `FileAttributes.ReparsePoint`. "Links are never followed" is a sound rule on Linux; on a real Windows host the
attribute is also set on cloud-sync placeholders (OneDrive and similar) and on junction/symlinked folders, so a game installed under such a folder cannot load its assets. Wine has none of
these. Suggested: refuse only links whose resolved target leaves the sandbox (`FileSystemInfo.ResolveLinkTarget`) and accept cloud placeholders.

### F-11 The virtual sink records silence for FMOD's WINMM output (High for audio; A1)

Setup: `Const OUTPUT_TYPE = FSOUND_OUTPUT_WINMM`, catalog `"audio": "virtual", "audioRecord": "fmod-output.wav"`. Everything works up to the recording: output type 1, `FSOUND_Init` = 1, the
channel plays and finishes after one second (the sink consumes buffers at real-time rate), `waveOutOpen` format 44.1 kHz/16-bit/stereo. The recording has the right duration
(1.86 s, 81920 frames in 20 passes of 4096 frames) and is **all zeros** (`tools/audio/analyze_wav.py`: peak 0.0000).

Cause: `VirtualSinkBackend.Write` copies the guest buffer once (`VirtualSinkBackend.cs:119`, `data = b.ReadData()`), and a looped header's later passes re-emit that snapshot. FMOD's WINMM
driver keeps one looped buffer and keeps writing the mix into it while the device "plays" it (as a real driver reads the memory when it plays it). Evidence for the diagnosis: the sample bytes
inside FMOD are exact (a `FSOUND_Sample_Lock` read-back equals the 440 Hz sine, first values 0, 1026, 2049, ...), and with the sink changed to re-read the guest buffer at the end of each pass
(`it.Data = it.Buffer.ReadData()` before `MakeWrite`, tried in a scratch copy, not applied) the same program records a tone: 2 channels, 44.1 kHz, peak -9.06 dBFS, dominant frequency **440.03 Hz**.
The same snapshot-at-write applies to the observer tap of `HostWinmmBackend`; the audio that reaches a real device is not affected (winmm reads the memory itself).
`FmodDemoTests.TheRecordedWinmmOutputIsTheTone` is the skipped test to enable once the sink reads buffers when they are consumed.

### F-12 `FSOUND_Close` with WINMM output (High for the demo; translator)

With WINMM output the program runs to the end and then `FSOUND_Close` throws `GuestTrapException: Guest control flow reached untranslated code at 0x2000FB60 (indirect target not discovered at
build time)` (`BinaryLibraryException` for `_FSOUND_Close@0`). With NOSOUND, `Close` works. The address is a driver routine whose pointer is stored by an instruction immediate
(`mov dword [mem], 0x2000FB60` at RVA 0xFA56 of the derived image); the image has no relocation table, so `ControlFlowGraph.DiscoverImmediateRoots` guesses roots and
`LooksLikeFunctionStart` (`ControlFlowGraph.cs:180-184`) accepts a target only if the previous byte is `ret`/`int3`/`nop` or `ret imm16`. The bytes before 0xFB60 are `... eb e6` (the end of a
`jmp short`), so the function is rejected.

Verified in a scratch copy of the sources (not applied to the tree; translator output is pinned by `TargetOsTests` and cached by compiler version): also accepting a preceding `jmp rel8`
(`mapped[t-2] == 0xEB`) or `jmp rel32` (`mapped[t-5] == 0xE9`) translates the function, and the whole WINMM program, `FSOUND_Close` included, prints `closed` and exits 0 under Wine. Until the
translator owner decides, `main.bb` stays on NOSOUND and the WINMM test stops before `FSOUND_Close`.

### F-13 `FSOUND_SetMixer(FSOUND_MIXER_QUALITY_FPU = 5)` kills the process (Medium; translator)

Reproducer: output 1 (WINMM, virtual sink), `FSOUND_SetMixer(5)` (returns 1), `FSOUND_Init`. The process ends silently with exit code 5. Wine trace (`WINEDEBUG=err+all,+seh`):
`EXCEPTION_ACCESS_VIOLATION` at a translated-code address, write to address 0 (`rcx=0`), `rsp=0x78120fa0` (guest stack), then
`err:seh:call_seh_handlers invalid frame ... NtRaiseException Exception frame is not in stack limits => unable to dispatch exception`. `FSOUND_SetMixer(1)` and `(3)` return 0 (rejected by
FMOD). The default mixer (MMX, value 7 after `Init`) is the one in use everywhere else.

### F-14 Hardware faults inside guest code are fatal (Medium, risk)

`docs/binary-windows-host.md` states it; F-13 shows the mechanism: while guest code runs, RSP points into the guest stack, outside the thread's stack limits, and the translated code has no
unwind data, so the OS cannot dispatch an exception (SEH checks the frame against the TEB limits). On Windows the process is terminated (Windows Error Reporting). The managed layer cannot
catch it. A possible design is a vectored exception handler that, for faults whose RIP is inside a registered host image, rewrites the CONTEXT to `ExitCommon` with the saved native RSP and a trap
code so the call fails with a `GuestException`; not implemented.

### F-17 Unsigned binaries and mitigation policy (Medium, risk)

* `main.exe` (SDK apphost), `BlitzCS.BinaryBindings.dll` (compiled at build time) and the other generated files are unsigned. Smart App Control in enforcement mode "blocks unsigned code unless
  the cloud reputation service predicts it is safe" and applies to all executables, not only downloaded ones
  ([overview](https://learn.microsoft.com/windows/apps/develop/smart-app-control/overview), [SmartScreen](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)).
  Distributing an application therefore needs signing of every PE (`signtool`) or an App Control policy; the packager does not sign.
* Arbitrary Code Guard (exploit protection) makes `VirtualAlloc`/`VirtualProtect` with execute fail with `STATUS_DYNAMIC_CODE_BLOCKED`
  ([reference](https://learn.microsoft.com/defender-endpoint/exploit-protection-reference#arbitrary-code-guard)). That also stops the .NET JIT, so no .NET application can run under it; the loader's
  `GuestLoadException("VirtualProtect(...) failed with error N")` names the error.
* Exploit protection's StackPivot/CallerCheck options (off by default) inspect the stack pointer or return addresses at `VirtualAlloc`/`VirtualProtect`. Every native call here happens in managed code
  on the thread's own stack (import stubs restore the native RSP before calling managed code, `X64CodeGen.cs:~245`), never from the guest stack.

### F-18 Fixed guest base on a real machine (Medium, risk)

The translation embeds absolute guest addresses, so the base chosen at build time (`ModuleBaseAllocator`: `0x20000000-0x6FFFFFFF`) must be free in the user's process. A mapping that
collides makes `GuestModule.Initialize` fail with `GuestAddressSpaceException` naming the occupant (`AddressSpace.DescribeConflicts`); it never runs wrongly. Facts: the packaged
`main.exe`, `coreclr.dll` and `clrjit.dll` all carry `HIGH_ENTROPY_VA` + `DYNAMIC_BASE` (read from the PE headers of the produced package), so a real Windows process places its
own heaps and images above 4 GiB; the Wine process of the tests does not (its heaps were observed at `0x30A20000`, `docs/wine-testing.md`), so Wine is the harder case. Security software that injects
DLLs with a fixed low preferred base remains a possible collision. Suggestion: ship the library translated for two or three alternative bases and let the loader pick the first that maps.

### F-19, F-20, F-21 (Low)

* F-19 `FileService.cs:233` compares `Path.GetFullPath(...).TrimEnd('/')`; with a `SandboxRoot` that ends in `\` on Windows, `FindFirstFileA` at the root also lists `.` and `..`. The generated environment passes the directory without a trailing separator, so only direct users of `Win32Options` are affected.
* F-20 `ApplicationPublisher.cs:113` starts `DOTNET_HOST_PATH ?? "dotnet"`. Installed as a tool on Windows without the SDK on `PATH`, the build fails with the OS "cannot find file" message. Use `Environment.ProcessPath` when it is the dotnet host.
* F-21 `GuestModule.ProtectGuest` (`GuestModule.cs:283`) gives the original x86 `.text` pages `PAGE_EXECUTE_READ` (and flushes them) although only the translated image executes. `PAGE_READONLY` is enough and removes executable, CFG-valid memory per library (240 KiB for the FMOD image).

## Real-host risks that Wine hides

### F-15 Control Flow Guard

Facts: the packaged `main.exe`, `coreclr.dll`, `hostfxr.dll` and `clrjit.dll` have `IMAGE_DLLCHARACTERISTICS_GUARD_CF`, so CFG is active in the process. Microsoft: "`VirtualProtect` and `VirtualAlloc`
will by default treat a specified region of executable and committed pages as valid indirect call targets"; only `PAGE_TARGETS_INVALID` (VirtualAlloc) or `PAGE_TARGETS_NO_UPDATE`
(VirtualProtect) change that ([Control Flow Guard](https://learn.microsoft.com/windows/win32/secbp/control-flow-guard), [Memory Protection Constants](https://learn.microsoft.com/windows/win32/memory/memory-protection-constants)).
`WindowsPlatformMemory` passes neither (RW allocation, then `VirtualProtect(PAGE_EXECUTE_READ)`), so the pages that `EnterGuest`, the import stubs and `GuestX87`'s helper live in are valid call
targets, and `SetProcessValidCallTargets` is not needed (it fails when CFG is off and is meant for hosts that mark pages invalid). Indirect calls made by the translated code itself are not instrumented,
so they are never checked. Not reproducible under Wine, which does not enforce CFG.

### F-16 CET shadow stacks (Windows 11, hardware dependent)

Facts: `main.exe`, `coreclr.dll`, `hostfxr.dll` and `clrjit.dll` carry the `CETCOMPAT` extended DLL characteristic (debug directory entry 20, read from the produced files; .NET 9+ marks the apphost
this way, [breaking change](https://learn.microsoft.com/dotnet/core/compatibility/interop/9.0/cet-support)). In compatibility mode only violations inside CETCOMPAT modules are fatal; dynamic code is not
one. Reasoning for the translated code: the code generator emits exactly one native `ret` (the end of `EnterGuest`, `X64CodeGen.cs:374`) and native `call`s only in the import stubs
(`X64CodeGen.cs:267` and `:282`, into the managed thunk); guest `call`/`ret` are `push` + `jmp` dispatches. Both native pairs are balanced even though RSP moves between stacks:
`EnterGuest` is entered by a call from managed code and left by `ret` after RSP is restored from the call context, and each import call returns before the stub continues. Untested on hardware; if a host reports a shadow-stack violation the documented opt-out is `<CetCompat>false</CetCompat>`
in the published project (the publisher's generated project does not set it today).

### Other differences

* ASLR/DEP: `main.exe` has `DYNAMIC_BASE`, `HIGH_ENTROPY_VA`, `NX_COMPAT`; no page is ever writable and executable (`ProtectGuest` throws on W+X; the host image is RW then RX).
* Windows on ARM64 runs the x64 application under emulation: generated code needs the instruction-cache flush that `Protect` performs; x87 extended precision is not guaranteed there. Untested.
* Long paths, case-insensitive names and `Z:` path forms are exercised by the Wine runs; real drive letters and UNC package locations are not.
* Defender real-time scanning of a freshly written executable image is not observable here.
* Guest TLS, the per-thread TEB/PEB pages and the stacks are ordinary `VirtualAlloc` regions in `0x78000000-0xFFFFFFFF`, found by walking `VirtualQuery` and honouring the 64 KiB granularity;
  the system's shared-data page at `0x7FFE0000` is expected to show up as an occupied region and is skipped by the same walk (not observable under Wine here). Guest stacks have a `PAGE_NOACCESS` page below them and an explicit headroom check per call
  (32 KiB); runaway recursion inside one call ends in F-14.

## How to reproduce

```bash
export PATH=<dotnet 10 sdk>:$PATH BLITZCS_PACKED_SAMPLE=<local UPX-packed fmod.dll>
dotnet test tests/BlitzCS.Linking.E2E.Tests -c Release                 # Linux; FmodDemoTests, ProcessExitTests, Win32EnvironmentOptionsTests
scripts/wine/run-tests.sh BlitzCS.Linking.E2E.Tests                     # the same tests as a Windows x64 process under Wine
. scripts/wine/env.sh; BLITZCS_TEST_PUBLISH=1 BLITZCS_TEST_WINE=1 dotnet test tests/BlitzCS.Linking.E2E.Tests -c Release --filter TheDefaultWindowsApplicationPackageRunsTheDemo
```
