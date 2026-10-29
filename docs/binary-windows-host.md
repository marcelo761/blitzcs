# Binary compiler: host targets (Windows x86-64 primary, Linux x86-64 secondary)

The recompiled IA-32 code runs inside the host process. Two host ABIs are supported and an artifact is **target specific**:

| `TargetOs` | Host | Notes |
| --- | --- | --- |
| `windows-x64` | Windows x86-64 .NET process (natively, or the same Windows build under Wine/Proton) | primary target; CLI default |
| `linux-sysv` | Linux x86-64 .NET process | maintained, frozen: no new Linux-only features |

Only the **runtime routines** inside the artifact differ (`EnterGuest`, `ExitCommon`, the import stubs). The translated guest code is identical for both targets, and the `linux-sysv` output is byte-for-byte what it was before the Windows port (pinned by `TargetOsTests`).

## Selecting a target

* `BinaryCompilerOptions.TargetOs` (`"linux-sysv"` or `"windows-x64"`; aliases `linux-x64`, `win-x64`). The default is the OS of the running process, so library users and tests keep working on both hosts. An unknown value is an `ArgumentException` naming the supported ones.
* CLI: `blitzcs build ... --target linux-x64|windows-x64`, or `"binaryTarget"` in the external catalog, or the `BLITZCS_BINARY_TARGET` environment variable. The CLI default is `windows-x64` and works from a Linux machine: translation is pure computation.
* `TranslationArtifact.TargetOs` records the choice (artifact format version 3; the cache key already includes it). `GuestModule.Load` refuses an artifact whose target is not the host's with a `GuestLoadException` naming both targets; there is no silent run. Legacy artifacts without the field count as `linux-sysv`.

## Win64 host ABI (EnterGuest and import stubs)

Same logical contract: `ulong EnterGuest(ulong hostTarget, uint guestEsp, ulong callContext)`; the call context block and guest-visible registers are unchanged (R12 image base, R13 dispatch table, R14 call context, R15 guest TEB).

* Arguments: `RCX` = hostTarget, `EDX` = guestEsp, `R8` = callContext (SysV uses RDI/ESI/RDX).
* Preserved for the caller: `RBX, RBP, RDI, RSI, R12-R15` (RDI/RSI hold the guest's ESI/EDI) and `XMM6, XMM7` (the guest's XMM6/7) plus `XMM15` (dispatch scratch: the only other XMM register translated code writes; IA-32 code cannot reach XMM8-15). The saved native RSP stays 16-byte aligned.
* Import stub: calls the managed `ulong Import(uint guestEsp)` with `ECX` = guestEsp and 32 bytes of shadow space (RSP 16-aligned at the call). RDI/RSI/RBX/RBP are callee-saved for the managed callee, so only the volatile `R8` (guest ESP) is spilled.
* x87 control word, MXCSR and x87 stack discipline are unchanged. The `GuestX87` ST0 helper has a Win64 variant (destination in RCX).

Hardware faults inside guest code (for example a wild write to unmapped memory) raise a native access violation in the CLR process on Windows, as SIGSEGV does on Linux. No SEH handling is installed; managed accesses through `GuestMemory` are bounds checked and report `GuestMemoryException` instead.

## Address-space layout (Windows)

Fixed `VirtualAlloc` at addresses below `0x20000000` fails under Wine, and the low range holds host mappings, so Windows images are rebased:

| Range | Use |
| --- | --- |
| `0 - 0x1FFFFFFF` | never used |
| `0x20000000 - 0x6FFFFFFF` | guest images (`ModuleBaseAllocator` with `TargetOs = windows-x64`; the upper half `0x50000000 - 0x6FFFFFFF` is used first because the CLR and Wine grow their own heaps upward from `0x20000000` (a base near the bottom collided with them at random during long runs), the lower half is the fallback; 64 KiB aligned, deterministic scan, 64 KiB gap between modules) |
| `0x70000000 - 0x77FFFFFF` | unused buffer |
| `0x78000000 - 0xFFFEFFFF` | runtime arenas: guest stacks (with a `PAGE_NOACCESS` guard page), TEB/PEB/TLS blocks, scratch buffers. 64 KiB-aligned fixed reservations using a next/recently freed address hint, with a `VirtualQuery` walk on a stale hint |

* A library whose preferred base is below `0x20000000` (for example `0x10000000`) must be rebased: `ModuleBaseAllocator.RequiredBase` / `Allocate(..., "windows-x64")` pick the base and `PeImage.Rebase` applies it. The CLI does this for every library, a single one included. An image stripped of relocations cannot move and fails with `PeRebaseException` (`PE_REBASE_NO_RELOCS`) and a Windows-specific hint.
* The translator marks a windows-x64 artifact whose base is below `0x20000000` with the error diagnostic `BC_BASE_UNUSABLE_ON_TARGET`, and the loader also refuses such a base up front.
* Linux keeps its layout: images from `0x10000000`, guest stacks/TEB/scratch in the `MAP_32BIT` zone `0x40000000 - 0x80000000`.

## Platform layer

`IPlatformMemory` (`PlatformMemory.Current`) is the only way the runtime touches the address space: `MapFixed` (no-replace), `MapLow` (below 4 GiB), `Map`, `Protect` (`None/Read/ReadWrite/ReadExecute`, never RWX), `Free`, `QueryRanges` (diagnostics: `/proc/self/maps` on Linux, `VirtualQuery` on Windows). `LinuxPlatformMemory` wraps libc `mmap`/`mprotect`/`munmap`; `WindowsPlatformMemory` wraps kernel32. Any other OS gets `PlatformNotSupportedException`.

`WindowsPlatformMemory.MapLow` first tries a fixed reservation at the next or most recently freed low-arena address. Fixed reservations never replace an occupied mapping. A stale hint or an arena-end boundary falls back to the complete `VirtualQuery` scan, so holes remain reachable. Successful frees update the hint under the same allocator lock; failed frees do not change it. This avoids repeated full address-space walks for short-lived argument buffers as a guest heap grows.

A paired Wine benchmark with 2,048 live 4 KiB mappings measured a median **1,193.51 → 5.39 ms** for 1,000 allocate/free cycles of 64 bytes (five samples). Reserving the 2,048 held mappings took 1,272.63 → 8.93 ms. Both variants preserved all held ranges; the native contract check also occupied a stale hint, verified its sentinel stayed intact, then verified reuse of a freed hole. [Full measurements](evidence/m21-maplow-hint.json). These allocator timings do not by themselves establish total application loading time.

## Host bridge for windows, GDI and DirectX

Only on this target, user32, gdi32, ddraw and dinput8 calls of the translated program reach the HOST's own DLLs through ABI adapters (32-bit structures converted both ways,
USER/GDI handles sign-extended, window procedures re-entered from host messages through `GuestModule.CallAddress`, one guest COM identity per host object). The Linux target
does not register these APIs. Selection (`Win32Options.HostWindowing`), the adapter model and its limits: `docs/binary-user32-bridge.md`.
