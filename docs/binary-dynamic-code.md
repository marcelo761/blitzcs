# Code generated at run time

Status: **implemented for windows-x64**; on linux-x64 the same miss is a named fault. Compiler 0.14.0 (cache keys changed), artifact format unchanged.

## Why

Some guests run code that does not exist when the image is translated. The measured case is the executable that the original Blitz3D compiler produces: the
runtime is the executable, and the compiled Blitz program is RCDATA resource #1111. At start-up the runtime calls `FindResourceA(0, 1111, RT_RCDATA)`,
`VirtualAlloc(0, size, MEM_COMMIT|MEM_RESERVE, PAGE_EXECUTE_READWRITE)`, copies the code there, applies its fix-ups (absolute and relative addresses of
runtime functions and of the code itself, all stored **before** the first execution) and calls it. The image is NX compatible (DllCharacteristics 0x140), so on
Windows the code can only run because the memory was requested executable.

The controlled unpacker (`docs/binary-unpack.md`) solves a different problem: a packer stub that runs once, at build time, and is thrown away. Here the code is
part of the running program, so it is translated inside the running process, with the same translator.

## Mechanism

1. **Executable guest data is recorded.** `VirtualAlloc`/`VirtualProtect` with `PAGE_EXECUTE*` and `HeapCreate(HEAP_CREATE_ENABLE_EXECUTE)` mark the pages
   (`GuestMemory.SetGuestExecutable`). The host page itself never becomes executable (W^X is unchanged); the guest-visible protection is what was asked for.
2. **The miss routine calls the runtime.** When no module of the process translates a dispatch target, the miss routine at host offset 0 (cross-module
   dispatch, `docs/binary-multimodule.md`) now calls the handler stored in the registry header (`ModuleRegistryLayout.MissHandler`, offset 8, formerly unused)
   before it traps: on the native stack, with every guest register, the flags (on the guest stack) and the complete x87/SSE state (`fxsave64`) saved, the host's
   x87 control word and MXCSR loaded. A non-zero answer means a record that translates the address is now published, and the walk runs again; zero keeps the kind 1 trap.
3. **The handler translates the run.** If the target lies in a guest buffer page marked executable, the unit is the contiguous run of executable pages of that
   allocation around it. Its bytes are copied (the snapshot) and given to `X64Translator.TranslateDynamic`: a synthetic one-section image at the run's address,
   rooted at every entry seen in the run so far, plus the speculative pointer/immediate roots an image without relocations gets. It is the unchanged translator:
   same code generator, same dispatch table, same diagnostics. Targets outside the run (calls into the executable, returns to it) are ordinary dispatch misses
   and reach the other module through the registry, so calls to runtime functions and their returns need nothing new.
4. **Installed like a module.** The host image is mapped (code RX, data RW), the registry cell is filled, dispatch entries at and beyond the run's end are cleared
   (the synthetic exit and stub addresses of the artifact must not claim the memory that follows the run), and a registry record `[run start, run length)` is
   published. A second entry into the same run retranslates the run with all entries; the old record is retired first, the old host image stays mapped (a thread may
   still be executing it) until the process environment is disposed.
5. **Diagnostics.** Every translation is logged (`GuestModule.DynamicTranslations`: entry, run, SHA-256 of the snapshot, entries, instructions, host code size,
   generation, time). BcRun prints them with `--verbose`, together with the write-guard counters.

Option: `GuestModuleOptions.DynamicCode` (default **true**; the module the thread entered decides), BcRun `--no-dynamic-code`. Disabled, the trap says so.
Default on because the alternative is a certain fault, and the translation is bounded by memory the guest itself made executable.

## Soundness: the snapshot is the identity

* Writes **before** the first execution are free (the Blitz runtime's fix-ups): nothing is translated yet.
* After translation the run's pages are host **read-only** (guest-visible protection unchanged, VirtualQuery reports what the guest asked for, kind
  `GuestPageKind.DynamicCode`). A vectored exception handler (`DynamicCodeGuard`, installed first in the chain on Windows) inspects every write fault there:
  * the stored bytes (memory operand size of the faulting host instruction) overlap a **translated instruction byte** and the store comes from translated code:
    the guest call ends with trap kind 6, `GuestSelfModifyingCodeException` (address, run, snapshot hash). Nothing is written;
  * otherwise it is a store to **data** that shares the pages (compilers that emit one blob of code and data): the page is made writable, the store is
    single-stepped (TF) and the page is protected again. Counted in `GuestModule.DynamicCodeGuardCounters`.
* Managed writes (`GuestMemory.Write*`, i.e. every Win32 service) follow the same rule: instruction bytes are refused with `GuestSelfModifyingCodeException`,
  data next to them is written.
* `VirtualProtect` asking to make such pages writable is refused with `Win32SelfModifyingCodeException` (the existing policy for translated code); read-only and
  execute requests are no-ops, `PAGE_NOACCESS` is refused.
* `VirtualFree` (release or decommit) and freeing a large heap block **invalidate**: the record is retired (a later jump there is a miss again, and new code
  at the same address is translated afresh), the pages lose the guard. Executing freed memory is a guest bug on Windows too.

Why fault instead of invalidating on a write to code: invalidation is only safe when no thread executes the old translation, which includes the writing thread
itself when code patches its own caller; that cannot be established cheaply, and the measured guest never does it (the hello run below: 0 guarded stores).

## Errors

* Bytes that do not decode at the target: trap with the address ("do not decode as an IA-32 instruction"); undecodable bytes reached later inside a translation
  are the usual kind 2 trap with the address. Translator error diagnostics are reported with code and address.
* Target in guest memory that was never made executable: named ("never made executable"), not translated.
* linux-x64: named ("needs the write guard ... only the windows-x64 runtime has"). A guarded store on Linux would need a SIGSEGV handler chained in front of the
  .NET runtime's own, which is not implemented; translating without the guard would not be sound.

## Threads

The handler serialises translation with one lock per process environment; registry publication is the existing release-store protocol, so other threads see
either no record or a complete one. A concurrent miss on the same address waits for the lock and then finds the record. One TEB per thread is unchanged (R14/R15
are not switched by the miss path). Limit: while one thread single-steps a data store, another thread's store to the same page is not checked.

## Evidence: Blitz3D `Print "hello"` (windows-x64, Wine 11)

`out_hello.exe` (SHA-256 `d7d54f53...`, built by the original blitzcc, `docs/blitz3d-reference.md`): one translation, entry `0x781A0000`, run
`0x781A0000-0x781A1000`, 28 instructions, snapshot `73818552...`, about 130 ms; 0 guarded stores, 0 refused. The program draws "hello" with the runtime's font
(five glyph `Blt`s into the back buffer, `Blt` to the front buffer, `WM_PAINT`), `End` posts the close, the runtime tears down and calls `ExitProcess(0)`:
BcRun exits 0. With `Delay 6000` after the `Print`, a screenshot of the Xvfb display shows the window with "hello" at its top left.

## Tests

`DynamicCodeTests` (Win32.Tests; Windows/Wine for the translating cases): position-independent code copied by the guest's own `rep movsb`; code relocated by
guest stores (rel32 and absolute) that calls into the image and returns; data on the code page written by the dynamic code, by image code and by a managed write;
a store into its own instruction byte (refused, nothing written) and a managed one; `VirtualProtect` to read-write refused by name; `VirtualFree` and new code
translated again; a second entry retranslating the run; undecodable bytes; memory never made executable; the option off; the Linux message.
`DynamicTranslationTests` (BinaryCompiler.Tests): snapshot identity, instruction-byte map, out-of-run targets, entry roots, argument contract.

## Limits

Each new entry retranslates the whole run (fine for one program blob, quadratic for code that grows entry by entry). Replaced host images are kept until the
process ends. A host library (not translated code) writing into translated instruction bytes is not named: the access violation is passed on. A write to
instruction bytes that bypasses both guards cannot happen through guest or managed stores; if it did, the next retranslation of the run detects the changed
byte and refuses by name.
