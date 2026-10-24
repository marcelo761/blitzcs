# Guest TEB contract

Windows x86 code reaches per-thread state through `FS:[offset]`. The recompiler models a TEB, a PEB and the TLS slot array.
Only the fields listed here are modelled. Any other `FS:` access whose offset is known at translation time is a translation
error (`BC_FS_UNSUPPORTED_OFFSET`). An access with a run-time offset (`fs:[reg]`, `fs:[reg+disp]`) is accepted and checked in the
generated code: it may only reach the TLS slot array, and anything outside it traps (trap kind 3) instead of reading unmodelled
memory. No unmodelled field ever reads as a silent zero.

One TEB (4 KiB, page aligned, in the low 4 GiB) exists per (module, OS thread) and lives as long as the module. Nested and re-entrant
calls on one thread share it, as on Windows. Different threads never share one, so there is no lock on the hot path.
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

PEB (one per module, 4 KiB): +0x02 BeingDebugged = 0, +0x08 ImageBaseAddress = image base, +0x18 ProcessHeap = 0 (no heap is
modelled here; an image that uses it gets whatever the Win32 component provides), +0xA4 OSMajorVersion/+0xA8 OSMinorVersion/+0xAC OSBuildNumber are
left to the Win32 component and are otherwise unsupported. Only +0x02 and +0x08 are modelled by the core.

`GS:` stays unsupported (it is the x64 TEB register).
