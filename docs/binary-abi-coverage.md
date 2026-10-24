# BCSBRE ABI and runtime coverage matrix

Honest status of what the binary recompiler (PE32 x86 to x64) supports. Statuses: **Supported** (implemented and covered by a test),
**Partial** (works in a stated subset), **Unsupported** (refused with a diagnostic, never silently wrong), **Planned (in progress)**
(being delivered now; statuses below are as of this snapshot and are updated by the integration step). Evidence is a test class/method
(`tests/BlitzCS.BinaryCompiler.Tests`) or a diagnostic code. `Diff:<lib>` means the library `<lib>` of the x86 oracle differential
(`Differential/OracleDifferentialTests`, each vector run natively on x86 and on the guest, in every optimization profile).

## Instruction classes

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| Integer arithmetic/logic with flags, shifts, mul/div | Supported | `EndToEndTranslatorTests.ArithmeticAndLogicWithFlags`, `Shifts`, `MultiplyAndDivide`; Diff:arith, intops1, intops2 | Flags checked against the oracle |
| Condition codes, setcc/cmov/bt/xchg/cmpxchg/lock | Supported | `ConditionCodesAndSetcc`, `SetccCmovBitTestXchgCmpxchgLock`; Diff:exotic | |
| Addressing forms (32-bit, wraparound, negative displacement) | Supported | `AddressingFormsIncludingNegativeDisplacementAndWrap`; `TranslatorStructureTests.AbsoluteDisplacementEncodesAs32BitAbsoluteWithAddressSizePrefix` | 16-bit addressing: `BC_UNSUPPORTED_INSTRUCTION` |
| Stack ops, push/pop/call/ret, pushad/popad, pushfd/popfd, enter/leave | Supported | `PushPopCallRet`, `PushadPopadPushfdPopfd`, `EnterAndLeave`, `FlagsSurviveCallRetAndIndirectJump` | Lowered to explicit 32-bit stack sequences |
| Loops, jecxz, string operations (rep movs/stos/...) | Supported | `LoopsAndJecxz`, `StringOperations`; Diff:memops | |
| Switch jump tables | Supported | `JumpTableSwitch`, `DispatchOptimizationTests.ReadOnlyJumpTableUsesEmbeddedHostOffsetsAndKeepsGenericFallback`; Diff:state | Bounded scan; `CFG_JUMP_TABLE`, `CFG_INDIRECT_JUMP` Info when unrecognised |
| Indirect calls/jumps, function-pointer tables, relocation-derived roots | Supported | `IndirectCallsThroughRegisterAndPointer`, `LiftingControlFlowTests.RelocationTargetsInCodeBecomeFunctionPointerRoots`; Diff:state, cb | Untranslated target traps: `TrapReportingTests`, `BC_UNTRANSLATED_TARGET` |
| x87 and SSE/SSE2 | Partial | `SseAndX87PassThrough`; Diff:fp, sse | Host executes the same encoding; float return registers not observable (`UnsupportedConventionTests.Floating_point_return_registers_are_not_observable`, skipped) |
| Packed BCD adjust (daa/das/aaa/...) | Unsupported | `UnsupportedConventionTests.Bcd_adjust_instructions_are_diagnosed_as_unsupported`; `BC_UNSUPPORTED_INSTRUCTION` | Invalid in 64-bit mode |
| VEX/EVEX/XOP, 3DNow! | Unsupported | `TranslatorStructureTests.UnsupportedInstructionsProduceErrorDiagnosticsWithRva`; `BC_UNSUPPORTED_INSTRUCTION` | |
| Far/16-bit branches, segment-register operands, privileged/system instructions | Unsupported | `BC_UNSUPPORTED_INSTRUCTION` | |
| Self-modifying code, code generated at run time | Unsupported | `TrapReportingTests.ExportIntoSectionWithoutFileDataIsAnErrorNotTranslatedZeros`; `BC_ROOT_NOT_FILE_BACKED` | See packed images |
| Undecodable bytes in reachable code | Partial | `LiftingControlFlowTests.DecodeFailureInReachableBytesIsAWarning`, `DoesNotDecodeDataEmbeddedInExecutableSections`; `CFG_DECODE_FAILURE` | Warning, path traps if executed |

## Calling conventions and returns

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| cdecl | Supported | `RuntimeLoaderTests.Call_AddMatchesReferenceForCdeclAndStdcall`; Diff:arith, memops | |
| stdcall | Supported | same; `CliBinaryIntegrationTests.StdcallArgumentSizeMismatchIsRejected`, `WrongConventionAgainstDecoratedExportIsRejected` | Argument bytes checked against decoration |
| thiscall, fastcall (export level) | Unsupported | `UnsupportedConventionTests.Fastcall_and_thiscall_exports_are_refused_not_mistranslated`; `CliBinaryIntegrationTests` binding check | Register arguments are refused, not guessed; the hosted function body is translated normally |
| Calling convention inference from decoration | Supported | `PeReaderTests.AbiHints`, `TranslatorStructureTests.ExportAbiHintsAreAttached` | Undecorated: catalog must say (`UnknownConventionRequiresTheCatalog`) |
| Return: Integer in EAX, Void | Supported | `CliBinaryIntegrationTests.SampleProgramCallsTheUnchangedDllEndToEnd` | |
| Return: Float, String, structs, 64-bit EDX:EAX | Unsupported | `CliBinaryIntegrationTests.UnsupportedReturnTypesAreBuildErrors` | Build error, not a wrong value |
| Arguments: Integer, Float (IEEE single on stack), String (copied, NUL-terminated, Windows-1252) | Supported | `CliBinaryIntegrationTests`; `docs/binary-linking.md` | Buffers freed after the call: `RuntimeLoaderTests.Buffers_AreCheckedFreedAndVisibleToGuest` |
| Callee-saved registers and stack across calls/imports | Supported | `EndToEndTranslatorTests.ImportCallsPreserveCalleeSavedRegistersAndStack`; `RuntimeLoaderTests.Canary_DetectsCalleeWritingAboveItsArguments` | |

## Imports, callbacks, reentrancy, concurrency

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| Imports bound to managed functions | Supported | `RuntimeLoaderTests.Bind_RejectsUnknownMismatchedUnsupportedAndMissing`; Diff:cb | cdecl and stdcall import callbacks |
| Unbound import | Supported (fails closed) | `ImportFaultTests.UnboundImportFailsLoadByDefaultAndNamesTheSymbolWhenAllowed`, `RuntimeLoaderTests.Load_UnboundImportNamesDllAndFunctionAndLeaksNothing` | Names DLL and function |
| Callbacks into the guest (reentrancy, same thread) | Supported | `RuntimeLoaderTests.Imports_CanCallBackIntoTheGuestOnTheSameThread`; Diff:cb (`k_nested`) | Runaway nesting: `RunawayNesting_ThrowsGuestStackExceptionInsteadOfCrashing` |
| Import fault containment | Supported | `ImportFaultTests.FaultingImportAbandonsTheGuestCallInsteadOfLettingItRunOn`, `RuntimeLoaderTests.ImportFault_DoesNotUnwindThroughGuestAndSurfacesFromTheOuterCall` | |
| Concurrency (separate guest stacks per call) | Supported | `ConcurrencyTests.ParallelCallsOnSeparateGuestStacksAreCorrect`, `RuntimeLoaderTests.ConcurrentCallsFromManyThreadsAreCorrect`, `CallbackBlockingOnAnotherThreadThatCallsTheGuestDoesNotDeadlock` | Guest globals are shared and not synchronised for the library |
| Lifetime / dispose with in-flight calls | Supported | `RuntimeLoaderTests.Dispose_WaitsForInFlightCalls`, `Dispose_FromInsideAnImportIsRejected`, `Dispose_IsIdempotentAndUseAfterDisposeIsRejected` | |
| Win32 API surface (kernel32 and others) for real libraries | Planned (in progress) | Win32 component (`src/BlitzCS.BinaryCompiler.Win32`) | Until then, real libraries that import heap/CRT functions need managed bindings |
| DllMain (attach/detach) | Supported | `RuntimeLoaderTests.DllMain_RunsAttachOnLoadAndDetachOnDispose`, `DllMain_FalseFailsInitializationAndReleasesTheImage` | Only the attach/detach tested above; no thread notifications are documented |
| Stack budget | Supported | `RuntimeLoaderTests.StackBudget_IsCheckedBeforeEnteringTheGuest` | |
| Memory safety of host access | Supported | `RuntimeLoaderTests.Memory_IsBoundsCheckedAndWriteProtected`, `Load_MapsImageWithSectionProtectionsAndNeverRwx` | Guest code itself runs unsandboxed at native speed within its mapping |

## TLS, SEH, fs

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| `fs:` access (TEB, LastError, TLS slots, ExceptionList) | Planned (in progress) | `docs/binary-teb-contract.md`; oracle side ready: `docs/binary-fs-oracle.md`, libs `fs_seh`, `fs_tls`; `BC_FS_UNSUPPORTED_OFFSET`, `BC_SEGMENT_OVERRIDE` | Unmodelled offsets are errors, never zero. `gs:` stays unsupported |
| SEH registration chain on `fs:[0]` (push/pop, walking) | Planned (in progress) | oracle lib `fs_seh` | Only data-structure semantics; dispatch is not modelled |
| SEH exception dispatch / `RtlUnwind` / hardware faults as exceptions | Unsupported | none; fault becomes a trap (`TrapReportingTests`) | Real libraries using `__try` for control flow will not work |
| Static (implicit) TLS directory (`.tls`, callbacks) | Unsupported | `RuntimeLoaderTests.Load_RejectsTlsAndTranslationErrorsAndMismatchedArtifacts`, `PeReaderTests.ParsesTls`; `BC_TLS_UNSUPPORTED` | Refused at translation/load |
| TLS API (`TlsAlloc`/`TlsGetValue`) | Planned (in progress) | via TEB slots + Win32 component | |
| Thread-per-TEB | Planned (in progress) | contract: one TEB per (module, OS thread) | |

## Image format

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| PE32 x86 DLL parsing, truncation resistance | Supported | `PeReaderTests.ParsesValidFixture`, `TruncationsNeverCrash`, `BadSectionBounds`, `OverlappingSections`, `BadSizeOfImageAndEntry` | PE000-PE051 diagnostics |
| PE32+ (x64), non-x86, CLR images | Unsupported | `PeReaderTests.RejectsPe32Plus`, `RejectsNonX86`, `ClrAndDelayImportsAreErrors`; `PE001`, `PE002`, `PE051` | |
| Base relocations (HIGHLOW) | Supported | `PeReaderTests.RelocationApplyAdjustsHighLow`, `RelocationApplyChecksBounds`; Diff:state, cb | Only HIGHLOW; others `PE031` |
| Fixed preferred base; base occupied | Partial | `RuntimeLoaderTests.Load_FailsWhenFixedBaseIsOccupied`; `BC_FIXED_BASE` | Translated code is bound to the image base; no rebasing: load fails if the range is taken |
| Ordinal imports, ILT fallback | Supported | `PeReaderTests.OrdinalImportAndIltFallback` | |
| Forwarded exports | Partial | `PeReaderTests.ForwarderExport`; `BC_FORWARDED_EXPORT` | Parsed and reported; not resolvable as call targets |
| Delay-load imports | Planned (in progress) | `PE050` today (`PeReaderTests.ClrAndDelayImportsAreErrors`) | Currently an error |
| Resources, manifests | Supported (ignored) | `RealLibraryTests` | Not interpreted |
| Packed / obfuscated images | Unsupported (diagnosed) | `PackedImageAnalyzerTests`; `BC_ROOT_NOT_FILE_BACKED`, `PACK_*`; `docs/binary-packed-images.md` | Detection and explanation only; no unpacking |
| Real third-party library | Supported (one) | `RealLibraryTests.OggPageFunctionsMatchAnIndependentReference` (BLITZCS_REAL_PE32_DIR) | libogg; others untested |

## Platform, cache, linking layer

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| Linux x86-64 host | Supported | whole suite; mmap via libc (`Libc.cs`) | |
| Windows / macOS / ARM hosts | Unsupported | `PlatformNotSupportedException` in the runtime | The oracle also needs IA-32 emulation in the kernel |
| x86 oracle differential | Supported | `Differential/OracleDifferentialTests`, `HarnessSanityTests` | Skips with a reason when `gcc -m32` or IA-32 emulation is unavailable |
| Translation cache, determinism | Supported | `CacheTests`, `TranslatorStructureTests.TranslationIsDeterministic` | Key includes every input; stale artifact rejected: `StaleArtifactIsRejected` |
| BASIC-level linking (`binary` in catalog) | Supported | `CliBinaryIntegrationTests.*`; `docs/binary-linking.md` | Integer/Float/String arguments, Integer/Void returns, cdecl/stdcall |
| Linking: thiscall/fastcall, struct/pointer-out arguments, callbacks from BASIC | Unsupported | `CliBinaryIntegrationTests.UnsupportedReturnTypesAreBuildErrors`, binding checks | |
| Linking: missing export, ambiguous, forwarder, wrong decoration | Supported (diagnosed) | `MissingExportIsABuildErrorNamingLibraryAndExport`, `AmbiguousExportIsRejected` | Build errors |
