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
| x87 and SSE/SSE2 | Partial | `SseAndX87PassThrough`; Diff:fp, sse | Host executes the same encoding; ST0/XMM0 returns are observable through `GuestReturn` (Diff:ret_abi, regconv) |
| Packed BCD adjust (daa/das/aaa/...) | Unsupported | `UnsupportedConventionTests.Bcd_adjust_instructions_are_diagnosed_as_unsupported`; `BC_UNSUPPORTED_INSTRUCTION` | Invalid in 64-bit mode |
| VEX (SSE/AVX/AVX2/BMI/FMA valid in 32-bit mode, ymm0-7) | Supported (pass-through, host-dependent) | `TranslatorSegmentVexTests.VexIsPassedThroughAndEvexIsRejected`, `VexExecutesWhenTheHostHasAvx2` (skips without AVX2) | Re-encoded in 64-bit mode with addr32 operands. `cpuid`/`xgetbv` are native, so the guest sees the host features and picks its own consistent path. `vzeroall` is lowered to `vpxor` of ymm0-7 (the host keeps ymm8-15). No `vzeroupper` is inserted at host transitions: guest code that leaves dirty upper halves can pay the SSE/AVX transition penalty in host code (performance only, never results); guests normally issue `vzeroupper` themselves. |
| EVEX (AVX-512), XOP, 3DNow! | Unsupported | `TranslatorStructureTests.UnsupportedInstructionsProduceErrorDiagnosticsWithRva`; `BC_UNSUPPORTED_INSTRUCTION` | |
| Far/16-bit branches, privileged/system instructions, `int n` other than 29h | Unsupported | `BC_UNSUPPORTED_INSTRUCTION` | |
| Segment register reads (`mov r/m16, sreg`, `push sreg`) | Supported | `TranslatorSegmentVexTests.SegmentRegisterReadsGiveFlatSelectors` | Selectors per target: windows-x64 (WoW64) cs 0x23, ss/ds/es/gs 0x2B, fs 0x53 (documented WoW64 values, NOT validated against real Windows hardware: the PE32 oracle under Wine reads fs 0x63, gs 0x6B, Wine's own GDT); linux-sysv (matches the ELF32 oracle) cs 0x23, ss/ds/es 0x2B, fs and gs 0. 32-bit destinations zero-extend, 16-bit register destinations keep the upper half, memory destinations write 16 bits, flags preserved |
| Segment register loads (`pop ds/es`, `mov ds/es, r/m16`) | Supported (current selector only) | `PopAndMovOfTheCurrentFlatSelectorAreAcceptedAndKeepFlags`, `LoadingAnotherSelectorRaisesANamedFault` | A different value, `pop/mov ss/fs/gs`, `lds/les/lss/lfs/lgs`: Warning `BC_DEFERRED_SEGMENT_FAULT` at build time and a `GuestTrapException` naming the instruction at run time (trap kind 5) |
| `int 29h` (`__fastfail`) | Supported | `FastFailRaisesADedicatedExceptionWithTheCode` | Trap kind 4, ECX in GuestContext[8]; `GuestFastFailException` carries the code and FAST_FAIL_* name |
| Data in code | Supported (heuristic) | `AddressTableAfterANoReturnCallIsDataButRealCodeAfterACallIsNot` | A fall-through after a call, or a speculative root, whose first 16 instructions decode as arpl/bound/in/out/lds/les/push es/aaa family/invalid bytes/`add [eax],al` runs is data (`CFG_DATA_AFTER_CALL`); runs reaching ret/jmp first are code |
| Self-modifying code, code generated at run time | Unsupported: explicit refusal with `Win32SelfModifyingCodeException` | `MemoryProtectionTests.Writable_protection_on_translated_code_is_refused_by_name`, `...Jumping_into_guest_generated_code_traps_instead_of_running_it`, `RealPackedFpuMixerTests`; `TrapReportingTests.ExportIntoSectionWithoutFileDataIsAnErrorNotTranslatedZeros`; `BC_ROOT_NOT_FILE_BACKED` | Example: the FPU mixer of the packed FMOD (`FSOUND_SetMixer(5)`) asks `VirtualProtect` for its own code; `FSOUND_Init` then fails by name (`docs/windows-integration-audit.md`, F-13). The default mixers are unaffected. Code the guest generates in its own memory traps as an untranslated target (kind 1). See `binary-win32.md` |
| Undecodable bytes in reachable code | Partial | `LiftingControlFlowTests.DecodeFailureInReachableBytesIsAWarning`, `DoesNotDecodeDataEmbeddedInExecutableSections`; `CFG_DECODE_FAILURE` | Warning, path traps if executed |

## Calling conventions and returns

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| cdecl | Supported | `RuntimeLoaderTests.Call_AddMatchesReferenceForCdeclAndStdcall`; Diff:arith, memops | |
| stdcall | Supported | same; `CliBinaryIntegrationTests.StdcallArgumentSizeMismatchIsRejected`, `WrongConventionAgainstDecoratedExportIsRejected` | Argument bytes checked against decoration |
| thiscall (ECX = this), fastcall (ECX, EDX) at export level | Supported | `Differential/ConvRegisterDifferentialTests` (Diff:regconv: 0/1/2/5 args, nested across all four conventions, register pressure, int/float/double/int64/long double returns, data side effects); `AbiRegisterConventionTests` | EnterGuest loads ECX/EDX from the call context (`CtxEcxIn`/`CtxEdxIn`, zero for cdecl/stdcall). Callee pops the stack part (the runtime discards the frame either way). `Bind` takes the total word count, register words first; fastcall decoration `@f@N` is checked against it. Undecorated thiscall has no inference: the caller must say so; `Unknown` is still refused (`UnsupportedConventionTests`) |
| Guest-to-host imports using thiscall/fastcall | Unsupported | none | Import stubs are cdecl/stdcall only |
| Calling convention inference from decoration | Supported | `PeReaderTests.AbiHints`, `TranslatorStructureTests.ExportAbiHintsAreAttached` | Undecorated: catalog must say (`UnknownConventionRequiresTheCatalog`) |
| Return: Integer in EAX, Void | Supported | `CliBinaryIntegrationTests.SampleProgramCallsTheUnchangedDllEndToEnd` | |
| Return: ST0 (float/double/long double), 64-bit EDX:EAX, small structs (1/2/4/8 in EAX:EDX) | Supported (managed API) | `ReturnAbiTests`; Diff:ret_abi, regconv, abiout | `GuestFunction.CallSingle/CallDouble/Invoke(GuestReturn.St0)`, `CallUInt64`, `CallStructInRegisters` |
| Return: struct through a hidden pointer | Supported (managed API) | Diff:ret_abi (cdecl/stdcall), abiout (thiscall, fastcall, stdcall); `AbiRegisterConventionTests.Msvc*StructReturn*` | `CallStructViaHiddenPointer(size, [StructReturnPointer], args)`: runtime-allocated buffer, callee must return it in EAX (checked). Placement: MSVC rule by default (stack first; fastcall: ECX); `Ecx` = GCC thiscall (pointer in ECX, `this` on the stack) |
| Out-pointer arguments (int/float/double/int64/arrays/structs, in/out) | Supported (managed API) | Diff:abiout; `AbiRegisterConventionTests.OutputBuffersAreChecked...` | `GuestFunction.InvokeWithBuffers` with `GuestArg.Out/In/InOut`: checked guest buffer, copied back, freed on success and failure; the guest never receives host pointers. A callee that overruns the buffer is outside what the runtime can detect (page-granular allocations) |
| Return: String (pointer ownership unknown) | Unsupported | `LinkingTests.UnsupportedReturnTypesAreBuildErrors` | Build error, not a wrong value |
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
| Win32 API surface (kernel32 and others) for real libraries | Partial | `src/BlitzCS.BinaryCompiler.Win32` (about 190 APIs: heap, virtual memory, sync, TLS, process, NLS, files in a sandbox, msvcrt subset, winmm with zero devices, registry, COM apartments); `RealOggTests`, `EaxTests`, `RealPackedSampleTests` | Unimplemented APIs are absent and fault at call time naming `dll!name`. Missing: `RtlUnwind`, `RaiseException`, sockets, msacm32, mci, window APIs |
| Win32 registry / COM / user32 truthful subsets (eax.dll registration) | Supported (subset) | `RegistryTests`, `Ole32Tests`, `User32Tests`, `EaxTests` (real eax.dll `DllRegisterServer`/`DllUnregisterServer`) | No COM server hosting (`CoCreateInstance` never succeeds), no window system, W registry variants absent; SEH (`RtlUnwind`, `RaiseException`) analysed in `binary-seh.md`, not implemented |
| x87 arguments / callbacks / data imports for host imports | Supported | `GuestServicesTests` (hand-assembled IA-32: `_ftol`, `_CIpow`, `_initterm`, `qsort`, data imports, `CreateThread`, `timeSetEvent`) | `GuestCall.PopSt0Double`, `SetReturnSt0`, `CallGuest`, `IGuestDataImportResolver`; Linux x86-64 |
| Win32 services for the unpacked fmod.dll (msvcrt, winmm no-device, winsock core, threads, timers) | Supported (subset) | `MsvcrtRuntimeHostTests`, `WinmmTests`, `WinsockCoreTests`, `Kernel32ExtraTests`, `RealPackedSampleTests` (env `BLITZCS_PACKED_SAMPLE`) | DllMain of the unpacked fmod returns TRUE; `FSOUND_GetVersion` 3.75, `SetOutput/GetOutput`, `FSOUND_Init(44100,64,0)` with NOSOUND, `GetMaxChannels` 64 and `Close` run on Linux and (the stub unpacked at a host-valid `UnpackOptions.LoadBase`) under Wine/windows-x64; no sockets, MCI, ACM, windows; no audio is played |
| DllMain (attach/detach) | Supported | `RuntimeLoaderTests.DllMain_RunsAttachOnLoadAndDetachOnDispose`, `DllMain_FalseFailsInitializationAndReleasesTheImage` | Only the attach/detach tested above; no thread notifications are documented |
| Stack budget | Supported | `RuntimeLoaderTests.StackBudget_IsCheckedBeforeEnteringTheGuest` | |
| Memory safety of host access | Supported | `RuntimeLoaderTests.Memory_IsBoundsCheckedAndWriteProtected`, `Load_MapsImageWithSectionProtectionsAndNeverRwx` | Guest code itself runs unsandboxed at native speed within its mapping |

## Several modules in one process

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| Cross-module imports (`other.dll!name` / ordinal bound to the sibling's export) | Supported | `ModuleLinkTests`; `RealMultiModuleTests` (ogg.dll + vorbis.dll + vorbisFile.dll, BLITZCS_REAL_PE32_DIR) | `GuestModuleSet`; managed bridge per import; each module keeps its own TEB/PEB/stacks. `docs/binary-multimodule.md` |
| Cross-module calling convention | Supported (declared, never assumed) | `ModuleLinkTests.BadLinksAreExplicitErrors` | `ExportConventions`: explicit per-export entry, MSVC decoration, or a per-module cdecl default; otherwise the load fails naming the import. Cdecl copies a bounded window (`CdeclWindowDwords`, default 16) of caller stack dwords, stdcall pops the exact count |
| Extended import return (EDX and ST0) | Supported | `ModuleLinkTests.ThreeModules...`, `HostImportsCanReturnEdxAndSt0`; mutation-checked | `GuestCall.SetReturnEdx/SetReturnSt0`; bit 63 of the import result selects an out-of-line tail in the import stub that reloads EDX and pushes the 80-bit ST0 image. Zero cost for imports that do not use it beyond one flag test |
| Forwarded exports as cross-module targets | Unsupported (diagnosed) | `ModuleLinkTests.ForwardedExportsAreRejectedExplicitly` | Import the forwarding target directly |
| Data exports (variables) as cross-module imports | Unsupported (diagnosed) | `GuestModuleSet` error text | An IAT slot would have to hold the variable address; only functions are bridged |
| Cyclic module imports | Unsupported (diagnosed) | `ModuleLinkTests.BadLinksAreExplicitErrors` | Dependency order guarantees every DllMain runs after its dependencies' DllMain |
| Disposal order | Supported | `ModuleLinkTests.LifetimeDisposeOrderAndLiveDependents` | Reverse dependency order; a module with live dependents cannot be disposed first |

## TLS, SEH, fs

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| `fs:` access (TEB, LastError, TLS slots, ExceptionList) | Supported (modelled fields) | `TebTests`, `Differential/FsDifferentialTests` against a real `fs` segment in the x86 oracle (`fs_seh`, `fs_tls`, `fs_ops`); `BC_FS_UNSUPPORTED_OFFSET`, `BC_SEGMENT_OVERRIDE` | Unmodelled offsets are errors, never zero; run-time offsets may only reach the TLS slot array (trap otherwise). `gs:` stays unsupported |
| SEH registration chain on `fs:[0]` (push/pop, walking) | Supported (bookkeeping only) | `Differential/FsDifferentialTests` (`fs_seh`) | Exception dispatch (`RaiseException`/`RtlUnwind`) is not modelled; plan in `docs/binary-seh.md` |
| SEH exception dispatch / `RtlUnwind` / hardware faults as exceptions | Unsupported | none; fault becomes a trap (`TrapReportingTests`) | Real libraries using `__try` for control flow will not work |
| Static (implicit) TLS directory (`.tls`, callbacks) | Supported | `TebTlsTests` (template copy and zero fill, the TLS index written at load, per-thread data, callbacks for process and thread notifications, `UnsupportedTlsFormsAreExplicitTranslationErrors`); `TLS_HANDLED` (Info) | Modelled through the TEB (`docs/binary-teb-contract.md`: TLS index and pointer at `FS:[0x2C]`). Malformed or oversized forms are `BC_TLS_UNSUPPORTED` errors at translation |
| TLS API (`TlsAlloc`/`TlsGetValue`) | Partial | `SyncTlsTests` (Win32 component, host-side per-thread table) | Not yet unified with the guest `TEB+0xE10` slot array |
| Thread-per-TEB | Supported | `TebTests` (distinct TEBs on 6 threads, release at Dispose), `ConcurrencyTests` | One TEB per (module, OS thread); freed at Dispose |

## Image format

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| PE32 x86 DLL parsing, truncation resistance | Supported | `PeReaderTests.ParsesValidFixture`, `TruncationsNeverCrash`, `BadSectionBounds`, `OverlappingSections`, `BadSizeOfImageAndEntry` | PE000-PE052 diagnostics |
| PE32+ (x64), non-x86, CLR images | Unsupported | `PeReaderTests.RejectsPe32Plus`, `RejectsNonX86`, `ClrImagesAreErrors`; `PE001`, `PE002`, `PE051` | |
| Base relocations (HIGHLOW) | Supported | `PeReaderTests.RelocationApplyAdjustsHighLow`, `RelocationApplyChecksBounds`; Diff:state, cb | Only HIGHLOW; others `PE031` |
| Rebasing at translation time (`BinaryCompilerOptions.LoadBase`, `PeImage.Rebase`) | Supported | `RebaseTests`; `Differential/RebaseDifferentialTests` (Diff:state, arith, conv, cb, memops, fp at two bases, equal to the oracle and to the preferred-base run, plus a mutation check that unrelocated function-pointer/jump tables diverge) | Translated code embeds absolute addresses, so the base is a translation input and part of the cache key (`(original base, target base)` only when they differ: keys of preferred-base builds are unchanged). Images without relocations are refused (`PE_REBASE_NO_RELOCS`); `ModuleBaseAllocator` picks deterministic 64 KiB-aligned bases. `docs/binary-multimodule.md` |
| Fixed base occupied | Supported (fails closed) | `RebaseTests.LoaderRejectsMismatchedLineage_AndNamesTheOccupant`, `RuntimeLoaderTests.Load_FailsWhenFixedBaseIsOccupied`; `BC_FIXED_BASE` | Never overwrites (MAP_FIXED_NOREPLACE); `GuestAddressSpaceException` names the conflicting `/proc/self/maps` entries. The loader does not relocate by itself: re-translate for another `LoadBase` |
| Ordinal imports, ILT fallback | Supported | `PeReaderTests.OrdinalImportAndIltFallback` | |
| Forwarded exports | Partial | `PeReaderTests.ForwarderExport`; `BC_FORWARDED_EXPORT` | Parsed and reported; not resolvable as call targets |
| Delay-load imports | Supported (RVA form) | `DelayImportTests` (hand-built ImgDelayDescr directory + MSVC-style `jmp [iat]` thunks) | Each delay IAT slot is an ordinary import (`PeImport.IsDelay`): stub allocated, slot written at load, so the load stub / `__delayLoadHelper2` is never reached; unbound ones follow `AllowUnboundImports`. VA-form (Attributes bit 0 clear) is `PE052`; malformed directory is `PE050`. Not covered: a real MSVC-generated `__delayLoadHelper2` (its code is translated like any other reachable code and its LoadLibrary/GetProcAddress imports must be bound), BoundIAT/UnloadIAT, explicit unload |
| Resources, manifests | Supported (ignored) | `RealLibraryTests` | Not interpreted |
| Packed / obfuscated images | Unsupported (diagnosed) | `PackedImageAnalyzerTests`; `BC_ROOT_NOT_FILE_BACKED`, `PACK_*`; `docs/binary-packed-images.md` | Detection and explanation only; no unpacking |
| Real third-party library | Supported (libogg, libvorbis, libvorbisfile) | `RealLibraryTests.OggPageFunctionsMatchAnIndependentReference`, `RealOggTests`, `RealMultiModuleTests` (BLITZCS_REAL_PE32_DIR) | libvorbisfile decodes a real stream through three linked modules and matches ffmpeg; eax.dll (COM/DirectSound) untested |

## Platform, cache, linking layer

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| Linux x86-64 host | Supported | whole suite; mmap via libc (`Libc.cs`) | |
| Windows x86-64 host (`windows-x64` target) | Supported | Wine-tested suites (`scripts/wine/run-tests.sh`, `docs/wine-testing.md`); `WindowsPlatformMemory` | Primary target. Wine is not Windows: see `docs/windows-integration-audit.md` for what it hides |
| macOS / ARM hosts | Unsupported | `PlatformNotSupportedException` in the runtime (`IPlatformMemory.cs`) | The oracle also needs IA-32 emulation in the kernel |
| x86 oracle differential | Supported | `Differential/OracleDifferentialTests`, `HarnessSanityTests` | Skips with a reason when `gcc -m32` or IA-32 emulation is unavailable |
| Translation cache, determinism | Supported | `CacheTests`, `TranslatorStructureTests.TranslationIsDeterministic` | Key includes every input; stale artifact rejected: `StaleArtifactIsRejected` |
| BASIC-level linking (`binary` in catalog) | Supported | `CliBinaryIntegrationTests.*`, `LinkingTests`, `LinkingFloatConventionTests`; `docs/binary-linking.md` | Integer/Float/String arguments, Integer/Float/Void returns, cdecl/stdcall/thiscall/fastcall |
| Linking: Float return | Supported | `LinkingFloatConventionTests.FloatReturns...`, `ExtendedPrecisionSt0IsNarrowed...`, `FloatReturnMismatches...` | ST0 narrowed once (80-bit to single, nearest even); empty or leftover x87 stack is a run-time error |
| Linking: thiscall / fastcall | Supported | `LinkingFloatConventionTests.Thiscall...`, `Fastcall...`, `RegisterConventionMistakesAreBuildErrors` | thiscall: first declared parameter must be Integer and is `this` (ECX). fastcall: MSVC rule, first two non-Float parameters in ECX/EDX in order |
| Linking: struct/pointer-out arguments, int64 returns, callbacks from BASIC | Unsupported | catalog schema has no such types | Use the managed API (`InvokeWithBuffers`) |
| Linking: missing export, ambiguous, forwarder, wrong decoration | Supported (diagnosed) | `MissingExportIsABuildErrorNamingLibraryAndExport`, `AmbiguousExportIsRejected` | Build errors |

## Windows x64 target: memory, COM, audio, base addresses

| Feature | Status | Evidence | Notes |
|---|---|---|---|
| VirtualProtect / VirtualQuery | Supported for guest data; translated code cannot be made writable | `MemoryProtectionTests` (`Own_data_pages_change_protection_and_report_the_old_one`, `Writable_protection_on_translated_code_is_refused_by_name`, `Query_reports_free_space_and_bad_lengths`); `MemoryService.cs` | Guest data pages get the requested protection; `PAGE_EXECUTE*` on guest data is recorded but never mapped executable. A request that makes translated code writable throws `Win32SelfModifyingCodeException`. Rules: `binary-win32.md` |
| Dynamic stub pool (`BinaryCompilerOptions.DynamicStubs`, default 0) | Supported | `StubPoolTests` (`ComTests.cs`) | `GuestModule.AllocateStub` / `ReleaseStub`; exhaustion and double release throw `GuestStubException`. The catalog `dynamicStubs` option (CLI default 512, `BinaryLibrarySpec.cs`) sets the size. `binary-com.md` section 1 |
| Guest COM objects (vtable, IUnknown, refcounts) | Supported (subset) | `GuestComTests`, `HostComProxyTests` (`ComTests.cs`) | Objects are built in guest memory with vtables; IUnknown is counted and the last Release destroys once; use after destruction faults by name. No generic forwarding to host COM. `binary-com.md` section 2 |
| DirectSound via host dsound | Supported (subset) | `DirectSoundBindingTests`, `HostDirectSoundTests` (`DirectSoundTests.cs`); `FmodDemoTests.TheSameProgramDrivesFmodsDirectSoundOutputThroughTheHostDsound` | `DirectSoundCreate`, IDirectSound and IDirectSoundBuffer are adapted; `DuplicateSoundBuffer` and `IDirectSoundBuffer::Initialize` answer E_NOTIMPL. No host device: `DSERR_NODRIVER`. `binary-com.md` section 3, `binary-audio.md` |
| DirectSound 8 / 3D / capture / notify | Unsupported | interface queries answer E_NOINTERFACE; `DirectSoundCreate8` and capture are not exported | `binary-com.md` section 3 |
| waveOut backends (`HostWinmm`, `VirtualSink`, `None`) | Supported | `WaveVirtualSinkTests`, `WaveHostWinmmTests` (`WaveGuestAndHostTests.cs`) | `HostWinmm`: host devices. `VirtualSink`: software device that reads guest buffers when each slice is consumed. `None`: zero devices. Selected by `Win32Options.WaveOut` or `BLITZCS_AUDIO`. `binary-audio.md` |
| Windows image bases | Supported | `RebaseTests`, `TargetOsBasePolicyTests`; `PeRebase.cs` | Images for `windows-x64` must lie in `[0x20000000, 0x70000000)` (`BC_BASE_UNUSABLE_ON_TARGET` below). The upper half (0x50000000 onward) is tried first, because the CLR and Wine grow their heaps upward from 0x20000000 (`wine-testing.md`) |
| RtlUnwind / RaiseException | Unsupported (not implemented) | none; a call faults by name | Decision in `binary-seh.md` (no-go now, with explicit triggers). Unbound imports fail only when called |
