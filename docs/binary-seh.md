# Structured exception handling: feasibility study

Status: analysis plus measured evidence (see "Evidence" and "Decision" below). Nothing here is implemented. `RtlUnwind` and `RaiseException`
(kernel32) are imported by ogg, vorbis, vorbisFile and eax (MSVC C++/SEH runtime) and by every Blitz3D SDK image; they stay unresolved, so a guest
reaching them faults naming the API. Decision: not needed now (no observed path requires it); the staged design stays on the shelf with explicit triggers.

## How x86 SEH works on Windows

- **Registration chain.** `fs:[0]` holds the head of a singly linked list of `EXCEPTION_REGISTRATION_RECORD { Next; Handler }` living on the
  guest stack, newest first, terminated by `0xFFFFFFFF`. MSVC prologues push a record (`push handler; push fs:[0]; mov fs:[0], esp`) and
  restore the old head on exit. `__try/__except` and C++ `throw` both use it (MSVC's `__except_handler3/4` and `__CxxFrameHandler` keep a
  scope table behind the record).
- **Raising.** `RaiseException(code, flags, nargs, args)` (and `throw`, which calls it with code `0xE06D7363`) builds an `EXCEPTION_RECORD`
  (code, flags, nested record, address, parameters) and a `CONTEXT` (all registers, eflags, eip, segment, FPU state) and walks the chain from
  `fs:[0]`. For each record it calls `Handler(ExceptionRecord*, EstablisherFrame, ContextRecord*, DispatcherContext*)` (stdcall, returns
  `EXCEPTION_DISPOSITION`: ContinueExecution 0, ContinueSearch 1, NestedException 2, CollidedUnwind 3).
- **Handling.** An `__except` filter returning "execute handler" makes the language handler call `RtlUnwind(targetFrame, targetIp, record, retval)`.
  `RtlUnwind` re-walks the chain from the head up to `targetFrame`, calling each handler again with `EH_UNWINDING` (flag 2) so they run
  `__finally`/destructors, unlinks each record, and finally resumes by loading `esp/ebp/eip` of the target frame (the `__except` block).
  ContinueExecution (resumable exceptions) instead returns from `RaiseException` with the possibly modified CONTEXT.
- **Unhandled.** Chain exhausted: the unhandled-exception filter (`SetUnhandledExceptionFilter`, already stored by ProcessService), else process termination.

## What the recompiler already has

- A per-(module, thread) TEB with `ExceptionList` at `fs:[0]` (initial `0xFFFFFFFF`, read/write; see `binary-teb-contract.md`). Translated MSVC
  prologues therefore already build real, correct chains in guest memory.
- Per-thread state in `GuestModule` (import ESP, call context, pending fault, x87 staging) and a guest-call API (`GuestFunction`) that lets a
  managed import call back into guest code on the same thread (re-entrant, same TEB).
- Traps for unsupported/untranslated code and bad memory, reported as managed exceptions; no hardware fault is turned into a guest exception.

## What software exceptions need

1. **CONTEXT capture at the import call.** The import stub already has the guest registers it saved in the per-call context (callee-saved set
   plus ESP at the call). `RaiseException` is stdcall, so the context at the call is: eip = return address, esp = after-return value, other registers
   as saved. Caller-saved registers (eax/ecx/edx) hold garbage across a call in the MSVC ABI, so this is precise enough; x87/SSE state would be
   copied from the staged state (exact FPU capture is optional and can be a documented zero).
2. **Dispatch.** The managed `RaiseException` import writes an `EXCEPTION_RECORD` and `CONTEXT` into guest memory (a scratch region on the guest
   stack below ESP) and walks `fs:[0]` reading records through `IWin32Memory`, validating each (aligned, inside the stack bounds, strictly
   ascending, as Windows does). Each handler is invoked with the guest-call API (bound by guest address, stdcall, 4 args). This part needs only
   the existing re-entrancy and is straightforward.
3. **Redirecting control flow.** The hard part: after a handler decides to unwind, execution must continue at the `__except` block with the
   frame's ESP/EBP, not at the instruction after the call. The import stub currently always returns to the caller. Proposed protocol: the managed
   import sets a *redirect* in the per-call context (`target eip`, `esp`, `ebp`, and optionally callee-saved registers) and returns a flag;
   the stub epilogue, instead of `ret n`, restores those registers and jumps through the guest-to-host address map to the target. The target
   must be a translated code address (check against the address map; fault otherwise). This is a localized change to the stub generator
   plus the runtime context layout, with a regression risk on every import, so it needs its own staged rollout.
4. **RtlUnwind** is itself an import called from inside a handler that was invoked by our managed dispatcher. It therefore runs *nested* on
   the guest stack above the dispatcher's managed frame. It must (a) call the remaining handlers with EH_UNWINDING, (b) unlink records, then
   (c) redirect: the redirect must unwind *managed* frames too, i.e. throw a private managed exception through the dispatcher up to the
   outermost import boundary, which then performs the stub redirect. Managed frames between guest frames (guest -> import -> guest callback ->
   RaiseException) are only legal to unwind if every intermediate boundary is one we own; a redirect that would cross a foreign managed frame
   (a host callback that called into guest code) must fail with a named fault, never skip it.
5. **x87 stack and per-call context.** A redirect lands in a frame whose x87 depth is whatever the `__except` code expects (MSVC empties the
   x87 stack at statement boundaries but not necessarily at the throw point). The per-call context tracks staged x87 returns; on redirect it
   must be reset to the depth recorded at frame entry or `EMMS`-style cleanup applied, otherwise the next call can see a stale stack. Nested
   guest calls each own a context, so a redirect past several of them must pop the same number of contexts (depth counter in `ThreadState`).
6. **C++ `throw`.** MSVC calls `RaiseException(0xE06D7363, EH_NONCONTINUABLE, 3|4, {magic 0x19930520, object, throwInfo, [base]})`. The CRT's
   `__CxxFrameHandler3` (guest code, already translated) interprets the throw info and catch tables and calls `RtlUnwind` itself: nothing C++-
   specific is needed in the component once items 1-5 work, which makes vorbis/vorbisFile/eax paths testable.

## What cannot be supported by software exceptions alone

Hardware faults inside translated code (access violation, integer divide error, `int3`, stack overflow) arrive as host signals (SIGSEGV, SIGFPE,
SIGTRAP) on the host thread, not as a call. Mapping them to guest exceptions would mean a signal handler that builds an `EXCEPTION_RECORD`,
patches the signal context to enter a dispatcher on the guest stack and returns from the handler. Constraints: only async-signal-safe work in
the handler; the faulting host instruction pointer must be mapped back to a guest eip (address map is by basic block, so register state at
mid-block faults is not exactly recoverable under optimization); the guest stack may be the thing that faulted (needs `sigaltstack`); and the
.NET runtime owns SIGSEGV for its own null-reference handling. This is a separate, larger step and is out of scope here: such faults stay
reported as managed exceptions today.

## Staged plan and test strategy

1. Dispatcher without redirect: `RaiseException` with a chain of hand-built records in guest memory; handlers are tiny translated functions that
   return ContinueExecution/ContinueSearch. Verify the argument block (record fields, CONTEXT eip/esp), call order, and the unhandled path
   (filter then fault). Also resumable ContinueExecution returning to the caller.
2. Redirect protocol in the stub plus runtime tests with hand-assembled PE images (the existing `Win32PeBuilder` support) whose handler returns
   ContinueSearch/unwinds to a target; check ESP/EBP/EIP after redirect, nested depth counters and x87 depth.
3. `RtlUnwind` with EH_UNWINDING handler calls, chain unlinking, `__finally`-style handlers running in order, targetFrame validation.
4. Real code: an MSVC `__try/__except` image (compiled once, checked in as bytes only if it is our own build) and then eax/vorbis `throw` paths.
5. Hardware faults via signals, only if a real library needs them.

The native Linux oracle has no Windows SEH, so the expected behaviour is semantic: the documented algorithm above, modelled independently in the
test (a C# reference walker over the same hand-built records) and compared with what the component does, plus the observable behaviour of
MSVC-compiled `__try/__except` (resume point, `__finally` order, `GetExceptionCode` values).

## Observed on eax.dll today

`DllRegisterServer`, `DllUnregisterServer`, `DllCanUnloadNow` and `DllGetClassObject` with an unknown CLSID complete without reaching `RaiseException` or
`RtlUnwind`; they matter only on the C++ exception paths of the factory/creation code.

## Evidence

All numbers come from `tools/survey` (`BcSurvey seh`, command lines at the end of this section). Method: the translator's recovered control-flow graph
(`ControlFlowGraph.Build`, the same one `X64Translator.Translate` uses) gives every decoded instruction, marked **non-speculative** when it is reachable
from an export, the PE entry point or a TLS callback by direct control flow and jump tables, and **speculative** when the only way to it is a code pointer
found by scanning relocations/data/immediates (callback tables, COM vtables, exception handlers whose address is only pushed). A "call site" is
`call/jmp dword ptr [iat_slot]` or a call to a one-instruction `jmp [iat_slot]` thunk. "Function" is the span between recovered function entries
(approximate). Reachability is by direct calls and tail jumps; indirect calls through registers or vtables are not followed, so "not reachable" always
means "not reachable by direct calls" and the count of indirect call sites inside each closure is reported as the caveat.

### (a) SEH-adjacent imports and their call sites

Images: the four real libraries in `$BLITZCS_REAL_PE32_DIR` (ogg, vorbis, vorbisFile, eax), the packed FMOD copy
`artifacts/m13-fmod/fmod.dll` after the controlled unpacker, and the six PE32 files of the Blitz3D V1.118 SDK. Sites are "all / non-speculative".

| image | RtlUnwind | RaiseException | SetUnhandledExceptionFilter | UnhandledExceptionFilter | IsBad*Ptr | other (IsDebuggerPresent, OutputDebugStringA) |
|---|---|---|---|---|---|---|
| ogg.dll | 2 / 0 | - | - | - | - | - |
| vorbis.dll | 2 / 0 | 1 / 1 | - | - | - | - |
| vorbisFile.dll | 2 / 0 | 1 / 1 | - | - | - | - |
| eax.dll | 3 / 0 | 1 / 0 | 2 / 0 | - | 3 / 0 (Read, Write, Code) | - |
| fmod.dll (unpacked) | - | - | - | - | - | OutputDebugStringA 5 / 0 |
| Blitz3D.exe | 2 / 0 | 1 / 1 | 4 / 3 | 3 / 3 | - | IsDebuggerPresent 2 / 2 |
| blitzcc.exe | 2 / 0 | 2 / 2 | 4 / 3 | 3 / 3 | - | IsDebuggerPresent 2 / 2 |
| linker.dll | 2 / 0 | 1 / 0 | 3 / 3 | 3 / 3 | - | IsDebuggerPresent 2 / 2 |
| runtime.dll | 4 / 0 | 2 / 1 | 3 / 3 | 3 / 3 | - | IsDebuggerPresent 2 / 2, OutputDebugStringA 14 / 0 |
| debugger.dll | 2 / 0 | 2 / 2 | 3 / 3 | 3 / 3 | - | IsDebuggerPresent 3 / 2, OutputDebugStringA 1 / 1 |
| ide.exe | 2 / 0 | 2 / 1 | 4 / 3 | 3 / 3 | - | IsDebuggerPresent 3 / 2, OutputDebugStringA 1 / 1 |

No image imports `_except_handler3`, `__CxxFrameHandler`, `_CxxThrowException`, `AddVectoredExceptionHandler` or `RtlRaiseException` by name: the MSVC runtime
is statically linked and the handlers are guest code (identified by shape below), which is the favourable case for the planned design. `IsBadReadPtr`,
`IsBadWritePtr` and `IsBadCodePtr` are already implemented by the Win32 component as mapped-range checks, so they do not need SEH.

What the call sites are (read from the disassembly, `BcSurvey dump`):

* **RtlUnwind: 0 non-speculative sites in 11 images.** Every site is in a two-function pair shaped exactly like the MSVC CRT `_global_unwind2` (push 0, 0, return label,
  target frame, call the RtlUnwind wrapper) and the `__except_handler3` scope-table walker (ogg 0x56E0/0x58E0 and handler 0x57D8: reads the scope table at `[ebx+8]`,
  the try level at `[ebx+0xC]`, calls the filter, then `_global_unwind2`). The handler's address is only ever pushed as an immediate (to be stored in an
  `EXCEPTION_REGISTRATION_RECORD`), which is why those instructions are speculative. RtlUnwind therefore runs only when an exception dispatcher calls the handler.
* **RaiseException in the 1990s/2000s MSVC libraries (vorbis 0x16BF4, vorbisFile 0x6C4A, eax 0x1CD4E)** is the CRT floating-point helper that raises an FP exception when the
  x87 status/control word has an unmasked exception (`fld` operands, `push ebx` count, `lea eax,[ebp+8]` argument array, then `call [RaiseException]`). It fires only if the guest
  unmasked FP exceptions (`_controlfp`); the default control word masks them all.
* **RaiseException in the SDK images** sits in the CRT's C++ catch/rethrow machinery (Blitz3D.exe 0x2870 is in a function that loads the EH magic `0x19930520`) and in `_CxxThrowException`
  (functions loading `0xE06D7363`: 11 to 12 per SDK image). `UnhandledExceptionFilter`/`SetUnhandledExceptionFilter`/`IsDebuggerPresent` are the `__report_gsfailure` / `abort`
  paths (the same function also contains the `int 29h` fast-fail and the segment-register capture listed in `docs/blitz3d-readiness.md`).
* **C++ `throw`**: direct call sites of the throw function: ogg/vorbis/vorbisFile/fmod 0; eax 7 (all speculative, i.e. in COM vtable methods); Blitz3D.exe 15 (3 non-speculative);
  blitzcc.exe 68 (44 non-speculative, 33 in the entry-point closure: the compiler reports errors with `throw`); linker.dll 33 (0); runtime.dll 357 (4; 0 in the entry closure);
  debugger.dll 54 (7; 4 in the entry closure); ide.exe 66 (4; 4).

### (b) Reachability from the scenarios that are already green

The real-library tests and demos (`tests/BlitzCS.BinaryCompiler.Win32.Tests/RealOggTests.cs`, `RealMultiModuleTests.cs`, `EaxTests.cs`, `tests/BlitzCS.BinaryCompiler.Tests/RealLibraryTests.cs`,
`tests/BlitzCS.Linking.E2E.Tests/RealOggTests.cs` and `FmodDemoTests.cs`, `samples/BinaryLinking/Real`, `samples/FmodAudio`) all load with `AllowUnboundImports = true` and
`RunEntryPoint = true`, and none of `RtlUnwind`, `RaiseException`, `UnhandledExceptionFilter` is bound (the Win32 component has no service for them; `SetUnhandledExceptionFilter`
is). An unbound import aborts the guest call with a `GuestImportException` naming `dll!name`, so a green run proves that **in that run** no guest call reached the import. It
does not prove the code is unreachable. The "test roots" below are the exports whose names occur as tokens in those test/sample sources (plus the exports a later module of the
set imports from the library, e.g. vorbisFile into vorbis and ogg) and the PE entry point; closure = direct calls and tail jumps.

| library | exports | test-exercised roots | functions (all / closure of tested roots + entry / entry only) | SEH call-site functions inside the tested closure | indirect call sites in that closure (not followed) |
|---|---|---|---|---|---|
| ogg | 64 | 36 | 213 / 105 / 61 | none (RtlUnwind functions: 0 of 2) | 39 |
| vorbis | 45 | 25 | 638 / 219 / 80 | **1 RaiseException function (the FP helper), statically reachable from five exports, among them `vorbis_synthesis_headerin`, which the tests call**; 0 of 2 RtlUnwind | 56 |
| vorbisFile | 34 | 8 | 356 / 125 / 61 | none (RaiseException function is reached only from `ov_bitrate`, not tested) | 45 |
| eax | 7 | 4 | 1331 / 123 / 109 | none (all 9 SEH-adjacent call sites are speculative; 78% of eax's decoded instructions are speculative because the objects are reached through vtables) | 83 |
| fmod (unpacked) | 232 | 17 | 1159 / 352 / 4 | none (OutputDebugStringA only, speculative) | 135 |

Precise reading: the one place where a green scenario could in principle have reached an exception import is vorbis' FP helper, and the green run shows it was not executed (FP
exceptions masked). The eax result is weaker than the others because most of eax is vtable code that direct-call reachability cannot follow (an unexplored 78%); the green
`DllGetClassObject(unknown CLSID)` path completing says nothing about the C++ `throw` sites in COM methods.

### (c) The fs:[0] chain patterns

Non-speculative code only; speculative `fs:[0]` accesses are counted separately.

| image | inline installs (`push handler; mov eax,fs:[0]; push eax; mov fs:[0],esp`) | functions calling an `fs:[0]` prolog helper (`__SEH_prolog4`/`__EH_prolog3` style) | write-only restore functions | distinct inline handlers | functions that install a chain and are reachable from the entry point |
|---|---|---|---|---|---|
| ogg | 2 | 0 | 2 | 1 (`_except_handler3`-shaped) | 2 of 2 |
| vorbis | 2 | 0 | 2 | 1 | 2 of 2 |
| vorbisFile | 2 | 0 | 2 | 1 | 2 of 2 |
| eax | 7 | 0 | 5 | 3 (2 are C++ stubs `mov eax,imm; jmp`) | 6 of 7 |
| fmod | 0 | 0 | 0 | 0 | 0 |
| Blitz3D.exe | 2 | 38 | 19 | 2 | 23 of 40 |
| blitzcc.exe | 34 | 195 | 47 | 28 | 115 of 221 |
| linker.dll | 2 | 57 | 22 | 2 | 26 of 59 |
| runtime.dll | 3 | 70 | 22 | 3 | 26 of 73 |
| debugger.dll | 3 | 998 | 25 | 3 | 37 of 1001 |
| ide.exe | 3 | 1045 | 20 | 3 | 32 of 1048 |

The chain bookkeeping the TEB model already supports (`fs:[0]` read/write, initial `0xFFFFFFFF`, validated by `FsDifferentialTests`) is exactly what all of these images
use. Many installers are reachable from the entry point (CRT start-up and initialisation code; all of them in ogg, vorbis and vorbisFile), so with `RunEntryPoint = true` chains are very
likely built and torn down in the green runs (not traced); only the dispatch side (RaiseException, a hardware fault) is missing. The only other `fs:` offsets in non-speculative code are `fs:[0x18]` (TEB.Self, used by the CRT start-up lock via
`[eax+4]` = StackBase) and `fs:[0x30]` (PEB pointer); both are modelled. The PEB reads behind `fs:[0x30]` are a separate finding in `docs/blitz3d-readiness.md`.

Reproduce (about 1 s for the GTA set, 5 s for the SDK files; add `--unpack-cache <dir>` to keep the unpacked FMOD image):

```
export PATH=<your .NET SDK dir>:$PATH
dotnet build tools/survey/BcSurvey/BcSurvey.csproj -c Release -o /tmp/bcsurvey
G=$BLITZCS_REAL_PE32_DIR; T=tests
dotnet /tmp/bcsurvey/BcSurvey.dll seh --out /tmp/seh.md \
  "ogg=$G/ogg.dll|$T/BlitzCS.BinaryCompiler.Win32.Tests/RealOggTests.cs,$T/BlitzCS.BinaryCompiler.Tests/RealLibraryTests.cs,$T/BlitzCS.Linking.E2E.Tests/RealOggTests.cs,samples/BinaryLinking/Real/ogg.bb,samples/BinaryLinking/Real/ogg.json,$T/BlitzCS.BinaryCompiler.Win32.Tests/RealMultiModuleTests.cs|$G/vorbis.dll,$G/vorbisFile.dll" \
  "vorbis=$G/vorbis.dll|$T/BlitzCS.BinaryCompiler.Win32.Tests/RealMultiModuleTests.cs,$T/BlitzCS.Linking.E2E.Tests/RealOggTests.cs|$G/vorbisFile.dll" \
  "vorbisFile=$G/vorbisFile.dll|$T/BlitzCS.BinaryCompiler.Win32.Tests/RealMultiModuleTests.cs|" \
  "eax=$G/eax.dll|$T/BlitzCS.BinaryCompiler.Win32.Tests/EaxTests.cs|" \
  "fmod=unpack:artifacts/m13-fmod/fmod.dll|samples/FmodAudio/main.bb,samples/FmodAudio/fmod.json|"
# SDK images (extract the zip yourself into a new empty directory first)
dotnet /tmp/bcsurvey/BcSurvey.dll seh --out /tmp/seh-sdk.md "Blitz3D.exe=$SDK/Blitz3D.exe||" "blitzcc.exe=$SDK/bin/blitzcc.exe||" "ide.exe=$SDK/bin/ide.exe||" ...
# disassemble the recovered stream from an RVA (read-only)
dotnet /tmp/bcsurvey/BcSurvey.dll dump $G/ogg.dll 56E0 22
```

## Decision

**No-go for implementing exception machinery now.** The evidence, in order of weight:

1. There is no non-speculative `RtlUnwind` site in any of the 11 PE32 images, and the speculative ones sit in handler code that only an exception dispatcher can call. Without a
   dispatcher `RtlUnwind` is unreachable by construction; implementing it alone would be dead code.
2. The only dispatcher entry points are `RaiseException` and hardware faults. In the five libraries that matter today (ogg, vorbis, vorbisFile, eax, FMOD) `RaiseException` is the
   CRT floating-point helper (needs an unmasked FP exception) or lives in unexplored COM code; no scenario has needed it and every green run completed with it unbound.
3. The chain bookkeeping that the images use is already modelled and differential-tested (`FsDifferentialTests`); the installers in ogg/vorbis/vorbisFile are all in the entry-point closure.
4. The one place where exceptions are part of normal-looking control flow is the Blitz3D toolchain, not the libraries: blitzcc.exe has 44 `throw` sites in reachable code (33 in
   the entry closure), which are its compile-error path. That path is irrelevant until blitzcc translates and starts, which is blocked by cheaper items (see the ranking in
   `docs/blitz3d-readiness.md`: instruction lowerings, the modern kernel32 start-up set, PEB fields, an EXE entry path).

**Triggers that change the decision to go**: (1) a green-able scenario that faults naming `RaiseException`/`RtlUnwind` (for example blitzcc compiling a program with a syntax error,
or eax code that throws on a failure path the product needs); (2) a real library whose non-error path takes a `__try/__except`; (3) FP exceptions deliberately unmasked.

**Cheap step that is justified now (not implemented here, src is owned by others)**: make `RaiseException` a named terminal fault. When `fs:[0]` is `0xFFFFFFFF` (no handler) the
import can call the filter registered with `SetUnhandledExceptionFilter` and then end the guest call with a dedicated exception that carries the exception code and the guest return
address. Today the call fails with the generic unbound-import `GuestImportException`; the improvement only changes the quality of the message and costs nothing in the stub generator.

## Staged design if the decision becomes go (software exceptions only)

Reuse the sections above; the order that follows from the evidence:

1. **Dispatcher without redirect** (the item 1 and 2 plan above). `RaiseException` builds an `EXCEPTION_RECORD` and a `CONTEXT` in a scratch area below the guest ESP, walks
   `fs:[0]` through `IWin32Memory` (validating alignment, ascending addresses, inside `[StackLimit, StackBase)`), and calls each `Handler(record, frame, context, dispatcher)` through
   `GuestModule.CallAddress` (stdcall, 4 arguments). A handler returning ContinueExecution makes `RaiseException` return normally. Only handler addresses that are in the address map
   are called; anything else is a named fault. Unit-testable with hand-built chains and tiny translated handlers.
2. **Redirect protocol** in the import stub (item 3 above): after `RtlUnwind`, the managed import records `{eip, esp, ebp, callee-saved}` in the per-call context and returns a flag;
   the stub restores them and jumps through the address map. Nested guest calls own a context each, so a redirect past N of them must pop N contexts (depth counter in
   `ThreadState`); a redirect across a foreign managed frame is a named fault. This is the only step that touches every import.
3. **RtlUnwind** with `EH_UNWINDING` calls, chain unlinking, target-frame validation.
4. **Real code**: blitzcc compiling a program with an error (C++ `throw` through `__CxxFrameHandler3`, which is guest code and needs nothing C++-specific), then eax failure paths.

## Fault classes and what each can and cannot do

| class | example | who sees it | what is possible today | what the design above can add |
|---|---|---|---|---|
| 1. Guest software exception | `RaiseException`, C++ `throw` in guest code | the import stub (an ordinary import call on the guest stack) | faults naming the API | full dispatch to guest handlers (stages 1 to 3) |
| 2. Guest memory violation / other hardware fault inside translated code | wild write, null read, `idiv` by zero, `int 3`, stack overflow in the guest stack | the OS, as an access violation / SIGSEGV / SIGFPE on the host thread, with RIP inside the translated host image | nothing: the process ends | a native filter that turns it into a named guest failure; at best (see below) a guest-visible exception |
| 3. Host / CLR fault | managed exception inside an import callback, GC or JIT failure, `OutOfMemoryException`, `Win32ExitProcessException` | the CLR, as a managed exception unwound through the import stub's host frame | the import boundary converts managed exceptions to `GuestImportException`; `ExitProcess` is abandoned without ending the host | unchanged; a redirect must never skip a foreign managed frame |
| 4. Unrecoverable native failure | guest stack exhausted beyond the reserved headroom, corrupted dispatch table or translated code, a fault in the filter itself, kernel-level termination | nobody | process terminates (Windows Error Reporting / core dump) | none; document, fail fast |

Why class 2 cannot be isolated in-process today, precisely:

* **Managed `try/catch` cannot catch it.** The CLR turns a hardware fault into a managed `NullReferenceException`/`AccessViolationException` only when the faulting instruction
  pointer is inside code the runtime knows (JIT-compiled code, runtime stubs). The translated host image is a plain `mmap`/`VirtualAlloc` region unknown to the code manager, so the
  fault is a native exception with no managed frame to unwind to. On .NET (Core) an access violation in unmanaged code is not delivered to managed handlers at all (no
  `HandleProcessCorruptedStateExceptions` for native code), so the process fails fast. The documented Windows behaviour is in `docs/binary-windows-host.md`, the audit entry is F-14.
* **The OS cannot run frame-based handlers on that stack.** While guest code runs, RSP points into the guest stack, which is not the stack registered in the thread's TEB
  limits, and translated code has no unwind data (no `RUNTIME_FUNCTION` entries). x64 SEH unwinding needs both; hence the CLR's own frame-based handlers never see the frame either.
* **The CLR owns the signal/SEH disposition.** On Linux the runtime installs its own SIGSEGV/SIGFPE/SIGTRAP handlers and its own alternate signal stack; a handler installed from managed
  code would replace or chain it, and only async-signal-safe work is allowed in it. On Windows the CLR registers its vectored/unhandled-exception filters.
* **A vectored exception handler could (hypothesis, not measured here; there was no Windows or Wine run in this survey)**: be installed with `AddVectoredExceptionHandler` and be
  called by the dispatcher before frame-based handlers, therefore without needing valid stack limits for the handlers themselves; receive `EXCEPTION_RECORD` (code, faulting address,
  read/write flag) and `CONTEXT` (all registers); test whether `ContextRecord->Rip` is inside a registered host image; rewrite `Rip` to the `ExitCommon` label, `Rsp` to the native RSP saved in the
  call context (`[ctx+0]`), set a trap kind, and return `EXCEPTION_CONTINUE_EXECUTION`, which resumes the thread on its native stack so that `EnterGuest` returns 0 and the managed caller throws a
  `GuestException` instead of dying. It must be tiny native code generated into the host image (it runs on the guest stack and must not call managed code: a reverse P/Invoke from a stack the
  runtime does not know breaks stack probes and GC stack walking).
* **A vectored handler could not**: resume the guest at the faulting instruction with fixed-up state (the address map is per basic block and the guest registers live in host registers whose
  state mid-block is not the guest's after optimisation); run guest `__except` handlers (that needs the exact guest `CONTEXT` and a way to re-enter guest code from inside the handler, i.e. the
  software-exception machinery plus precise state); survive a stack overflow (the kernel must push the exception record and context below the faulting RSP, which is inside the guard page: the
  reserved headroom `StackReserve` helps only for calls checked by the runtime, not for translated pushes); or help when the fault is in the handler or in the CLR itself.
* **Linux equivalent**: `sigaction(SA_SIGINFO | SA_ONSTACK)` with the same `ucontext` rewrite (`REG_RIP`, `REG_RSP`), subject to the CLR's handler chaining; not attempted.

Decision for class 2: a "fault becomes a named `GuestException`" filter (audit F-14) is a candidate hardening step, independent of guest SEH, to be spiked on Windows and Linux before
anyone relies on it. Guest SEH for hardware faults is not planned.
