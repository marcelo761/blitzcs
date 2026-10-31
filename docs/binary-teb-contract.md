# Guest TEB contract

Windows x86 code reaches per-thread state through `FS:[offset]`. The recompiler models a TEB, a PEB and the TLS slot array.
Only the fields listed here are modelled. Any other `FS:` access whose offset is known at translation time is a translation
error (`BC_FS_UNSUPPORTED_OFFSET`). An access with a run-time offset (`fs:[reg]`, `fs:[reg+disp]`) is accepted and checked in the
generated code: it may only reach the TLS slot array, and anything outside it traps (trap kind 3) instead of reading unmodelled
memory. No unmodelled field ever reads as a silent zero.

One TEB (4 KiB, page aligned, in the low 4 GiB) exists per (guest process, OS thread): a module loaded on its own is its own process, and all modules of a `GuestModuleSet` are one process (`GuestProcessEnvironment`). It lives until the process
environment is disposed (module disposal, or set disposal). Nested and re-entrant calls on one thread, and code that jumps from one module into another (docs/binary-multimodule.md, section 3), share it, as on Windows. Different threads never share one, so there is no lock on the hot path.
The translated code addresses it through a callee-saved host register that is part of the per-call context.

| FS offset | Field | Initial value | Access |
|---|---|---|---|
| 0x00 | NT_TIB.ExceptionList | 0xFFFFFFFF (end of chain) | read/write |
| 0x04 | StackBase | top of the thread's guest stack | read |
| 0x08 | StackLimit | lowest committed guest stack address | read |
| 0x18 | NT_TIB.Self | address of this TEB | read |
| 0x20 | ClientId.UniqueProcess | process id | read |
| 0x24 | ClientId.UniqueThread | thread id | read |
| 0x2C | ThreadLocalStoragePointer | pointer to the thread's implicit-TLS array, or 0 when the image has no TLS directory | read/write |
| 0x30 | ProcessEnvironmentBlock | address of the PEB | read |
| 0x34 | LastErrorValue | 0 | read/write |
| 0xE10..0xF0F | TlsSlots[64] | 0 | read/write |

PEB (one per process, 4 KiB; shared by the modules of a set, ImageBaseAddress = the executable's, a DLL's only while no executable is loaded): +0x02 BeingDebugged = 0, +0x08 ImageBaseAddress = image base, +0x18 ProcessHeap = 0 (no heap is
modelled here; an image that uses it gets whatever the Win32 component provides), +0xA4 OSMajorVersion/+0xA8 OSMinorVersion/+0xAC OSBuildNumber are
left to the Win32 component and are otherwise unsupported. (Superseded: see "PEB fields (EXE start-up)" below for the fields now modelled.)

`GS:` stays unsupported (it is the x64 TEB register).


## PEB fields (EXE start-up)

The PEB page is still plain guest memory, so only the fields below carry data; **everything else reads 0 and a read of an unmodelled field cannot fault**
(a protection fault has page granularity, the modelled fields share the page with the rest, so interception is not possible). The fields a VS2015+ CRT and the usual
probes read are modelled:

| Offset | Field | Value |
|---|---|---|
| +0x02 | BeingDebugged | 0 |
| +0x08 | ImageBaseAddress | the translated base |
| +0x0C | Ldr | a PEB_LDR_DATA (Length 0x30, Initialized 1) whose three LIST_ENTRY heads (+0x0C, +0x14, +0x1C) are empty and circular (Flink = Blink = the head): code walking the module list finds no module instead of faulting |
| +0x10 | ProcessParameters | a 32-bit RTL_USER_PROCESS_PARAMETERS (0x290 bytes): Flags (+0x08) = RTL_USER_PROC_PARAMS_NORMALIZED, CurrentDirectory (+0x24), DllPath (+0x30), ImagePathName (+0x38), CommandLine (+0x40) as UNICODE_STRING32 pointing to guest wide strings, Environment (+0x48) = the wide environment block; standard handles (+0x18..0x20) are 0 |
| +0x18 | ProcessHeap | 0 until the Win32 component creates the default heap; `GetProcessHeap` then publishes the handle (`GuestModule.SetProcessHeap`) so the PEB and the API agree |
| +0x64 | NumberOfProcessors | host logical processor count |
| +0x68 | NtGlobalFlag | 0 |
| +0xA4/+0xA8/+0xAC/+0xAE/+0xB0 | OSMajorVersion, OSMinorVersion, OSBuildNumber (WORD), OSCSDVersion (WORD), OSPlatformId (2) | from `GuestModuleOptions.Process` (`Win32ProcessInfo.From(win32Options)`), i.e. the same values `GetVersionEx` reports |

Documented as **not modelled** (zero): InheritedAddressSpace, Mutant, SubSystemData, FastPebLock, ProcessHeaps/NumberOfHeaps, TlsBitmap and TlsExpansionBitmap, ApiSetMap,
ImageSubsystem*, the standard handles in ProcessParameters, the `CurrentDirectories` array, WindowTitle/DesktopInfo/ShellInfo strings. The Win32 policy for the OS version of a
program is `Win32ProcessInfo.VersionForImage` (the version the image declares, never below 5.1.2600 SP3).

## Running an EXE

`GuestModule.RunExecutable()` calls the entry point (stdcall, no arguments) on a fresh guest thread context and returns a `GuestExecutableResult`: the code of
an `IGuestProcessExit` found in the inner chain of the `GuestImportException` (the Win32 component's `Win32ExitProcessException`: ExitProcess/TerminateProcess(self)), or EAX
when the entry point returns. Any other failure (trap, unbound import, memory fault) propagates. DLL behaviour is unchanged: `DllMain` still runs at load.


## Several modules (cross-module dispatch)

* One TEB per thread for the whole set, so `fs:[0]` SEH chains, `TlsSlots`, `LastErrorValue` and `NT_TIB.Self` are the same whichever module's code reads them. The thread's `Depth` counts guest calls across all modules; the SEH chain is reset and StackBase/StackLimit are set only at the process-outermost entry, a nested entry on another module's stack
  saves and restores the two stack fields.
* Implicit TLS: `ThreadLocalStoragePointer` points to a 4 KiB array of data-block pointers shared by the modules; a module with a TLS directory is assigned the next index when it loads (written to its TLS index variable before the image is protected), a data block (template + zero fill) is created in the TEB of every existing thread at that moment and of every later thread,
  and removed again when the module is unloaded. DLL_THREAD_ATTACH TLS callbacks still run per (module, thread) the first time the thread calls into the module.
* The Win32 `LastError` is kept by the Win32 component (per thread, in the shared context); the TEB field is not mirrored (as before).
