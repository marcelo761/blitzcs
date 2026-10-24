# Structured exception handling: feasibility study

Status: analysis only. Nothing here is implemented. `RtlUnwind` and `RaiseException` (kernel32) are imported by vorbis, vorbisFile and eax
(MSVC C++/SEH runtime) and stay unresolved, so a guest reaching them faults naming the API.

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
