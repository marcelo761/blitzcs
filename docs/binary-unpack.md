# Unpacking packed PE32 libraries by controlled execution

Project `src/BlitzCS.BinaryCompiler.Unpack` (tests: `tests/BlitzCS.BinaryCompiler.Unpack.Tests`). It implements strategy (a) of
`docs/binary-packed-images.md`. The translator, the runtime and the CLI core do not reference it; it is an explicit, opt-in build step.

## Mechanism and why

A packer stub is ordinary IA-32 code whose output (the real code) only exists after it ran, and whose last act is a jump into memory it wrote.
The AOT translator cannot translate that jump target, so the stub needs something that can execute code that appears at run time. Options evaluated:

| Option | Verdict |
|---|---|
| Residual dynamic translator (translate blocks of newly written pages with Iced + X64CodeGen, cache, invalidate on write) | Rejected. The artifact is a fixed host image with a dispatch table sized at build time and the guest image is mapped W^X; growing it at run time needs RWX host pages, a mutable dispatch table, a second code-gen entry point and write-protection trapping. Large new attack surface, only to throw the result away. |
| Native x86-32 compat child under ptrace | Rejected. Needs a 32-bit trampoline, a faked Win32 answered by the tracer, runs untrusted stub code natively, and depends on kernel IA-32 emulation being enabled. Not deterministic or portable. |
| **Confined IA-32 interpreter (chosen)** | A small integer-only interpreter that decodes with Iced and runs against managed `byte[]` regions. No native memory is ever executable, so no RWX mapping exists anywhere and `GuestModule`'s W^X policy needs no exception. Instruction-bounded and deterministic. Unmodelled instructions refuse instead of misbehaving. Self-modifying code is just memory writes. |

Consequence: there is no `AllowWritableExecutableImage` option; it would be dead code and a weakening of the loader's policy, so it was deliberately not added.
The stub never runs through the Win32 component either; the sandbox answers only the loader and allocator APIs itself (below).

## What runs

`ControlledUnpacker.TryUnpack(PeImage packed, UnpackOptions?) -> UnpackResult(DerivedPe, Report, Diagnostics, Lineage)`.

* Memory: the mapped image at its preferred base, or at `UnpackOptions.LoadBase` (next section), so the dump is consistent with the translator's fixed base; a 1 MiB stack, a TEB/PEB page
  (SEH list head, stack bounds, self pointer, PEB pointer, image base) and bump-allocated `VirtualAlloc` regions. All fixed zones are checked against the image range.
* CPU: integer IA-32 (mov/alu/shifts/rotates/mul/div/string ops with rep, push/pop/pushad/popad/flags, call/jmp/ret/jcc/loop, setcc/cmovcc, bt*, bsf/bsr, bswap, xchg, fs: reads).
  FPU/SSE, gs:, far transfers and anything else: `UNPACK_UNSUPPORTED_INSTRUCTION` with address and text.
* "Windows": `LoadLibraryA`, `GetModuleHandleA`, `GetProcAddress` (name or ordinal), `VirtualAlloc/Protect/Free`, `FlushInstructionCache`, `Get/SetLastError`, `GetCurrentProcess`.
  Every `(dll, name)` the stub resolves (or has in its own import table) becomes a unique 16-byte-aligned thunk address in a reserved zone. **Resolving is free,
  calling is explicit**: reaching a thunk that is not one of the APIs above refuses with `UNPACK_UNIMPLEMENTED_IMPORT_CALLED dll!name`; nothing returns a fake zero.
* Anti-debug and environment probes are refused, never answered: `PEB.BeingDebugged`/`NtGlobalFlag` reads, `IsDebuggerPresent`, `rdtsc`/`cpuid`, `int` (`UNPACK_ANTI_DEBUG`);
  `ExitProcess` (`UNPACK_STUB_EXITED`); unmapped accesses (`UNPACK_UNMAPPED_ACCESS`); divide errors.
* Bounds: `MaxInstructions` (default 300 M, deterministic; rep iterations count) and `MaxTime` (wall-clock safety net). No clock, randomness or host state is visible to the stub.

## OEP detection

The unit of evidence is a 4 KiB page of the image. The run stops when control first enters a page that (1) the stub wrote and (2) was never executed before, (3) lies in a section other
than the stub's, (4) via jmp/ret/fall-through (a `call` into generated code is treated as a helper and noted). Confidence:

* `High`: the above, with at least `MinWrittenBytes` (256) bytes written to the image so far; additionally at least one export target must contain bytes produced by the run
  (`UNPACK_EXPORTS_NOT_POPULATED` otherwise).
* `Low`: same transfer but less written evidence: refused (`UNPACK_LOW_CONFIDENCE_OEP`) unless `AcceptLowConfidence`.
* `Medium`: jump from the stub section into a never-written executable section. Recorded as `UNPACK_ENTRY_SECTION_EXIT` and ignored unless `AcceptEntrySectionExit`.
* The stub returning to its caller without such a transfer is `UNPACK_NO_OEP`. Executing bytes the stub wrote inside its own section is allowed but warned (`UNPACK_STUB_SELF_MODIFIES`).

The report carries the evidence lines (from/to addresses and sections, bytes written, instruction count).

## Dump and rebuild (derived artifact)

A **new** PE32 is produced; the input file is never touched.

* Same headers and section table as the packed image (the packer discards the original table, so it cannot be restored); section data comes from the snapshot, `RawSize` becomes the aligned `VirtualSize`
  (the previously unbacked section is now file backed). `EntryPoint` = OEP, `SizeOfImage` recomputed.
* Every writable+executable section is split at page granularity (`<name>c<n>` = R+X, `<name>d<n>` = R+W) because the loader never maps W+X. A page is code when the normal
  `ControlFlowGraph` (roots: exports and the OEP, run on a first-pass view of the dump) decoded an instruction on it. Limits: code reachable only through data pointers (vtables, callbacks) is
  not found without relocations, and code mixed with writable data on one page becomes read-only.
* Imports: every aligned dword of the snapshot that equals a handed-out thunk is an IAT slot; consecutive slots of one DLL form one descriptor in a new `.impfix` section, names/ordinals from the
  `GetProcAddress` log. Slots inside the stub's own section (its private table) are dropped. A resolved import that is stored nowhere in the final image is reported (`UNPACK_UNSTORED_IMPORTS`).
* Relocations are dropped and `IMAGE_FILE_RELOCS_STRIPPED` set (the dump is for the base the stub ran at: the preferred base, or `LoadBase`, which becomes the derived `ImageBase`); `DYNAMIC_BASE` cleared; exports, resources and TLS are kept; bound/IAT/debug directories cleared.
* Lineage (`UnpackLineage`): `DerivedFromSha256`, `UnpackerVersion`, options digest, derived SHA-256. `UnpackBuild.TranslateWithUnpack` stores the derived image and a `.lineage.json` in
  `<cache>/unpacked/<key>.dll`, reuses it when hash and lineage match, and adds `unpack:<orig>:<version>:<options>` to the translation cache key's dependencies. The original hash does not
  appear in the BCX artifact itself (`SourceSha256` is the derived image's); the sidecar and the cache key carry the link.

## Running the stub at a chosen base (`UnpackOptions.LoadBase`)

windows-x64 cannot map an image below `0x20000000` (`BC_BASE_UNUSABLE_ON_TARGET`, see `docs/binary-windows-host.md`), and an unpacked image has no relocation table, so a library that
prefers `0x10000000` (FMOD, most DLLs) could not be loaded there. The stub is therefore run with the image **mapped at the base the build wants**:

* `UnpackOptions.LoadBase` (default `null` = the preferred base; 64 KiB aligned, image below 4 GiB, outside the sandbox zones, else `UNPACK_BAD_LOAD_BASE` / `UNPACK_ADDRESS_CONFLICT`).
* The sandbox first does what the Windows loader does for a DLL loaded away from its preferred base: it applies the **packed file's own** base relocations (for UPX typically the one
  entry of the stub), stores the actual base into the in-memory optional header, and hands the base out as the module handle (`DllMain`'s `hinstDLL`, `GetModuleHandle(NULL)`, `PEB.ImageBaseAddress`).
  A packed file without relocations cannot be moved (the real loader could not either): `UNPACK_LOAD_BASE_NO_RELOCS`.
* A packer handles the rest itself, exactly as it does on a real system: the stub derives `delta = runtime base - preferred base` from its own runtime address and adds it to every location of
  its packed relocation list in the unpacked image. After the OEP is reached the snapshot is therefore correct for `LoadBase`; the derived PE gets `ImageBase = LoadBase` and needs no table.
* Limits: the derived image is **fixed at `LoadBase`**. It carries no `.reloc` (the stub consumed its relocation data and does not leave a usable table in memory), so `PeImage.Rebase` refuses it
  (`PE_REBASE_NO_RELOCS`); a different base means unpacking again with another `LoadBase` (the options digest, and with it the derived-file key, lineage and translation cache key, include the base).
  A stub that does not relocate (a packer that assumes its preferred base) produces a dump that is wrong for any other base: not detectable generically, the run simply faults.
* Build policy (CLI): `BinaryTranslationStep.PlanUnpackBases` runs **before** any stub, from `ExternalCatalog.Load` (the target comes from `--target`, the catalog or `BLITZCS_BINARY_TARGET`).
  For `windows-x64` the whole library set is planned with `ModuleBaseAllocator` (packed images keep their preferred base when usable, otherwise get the next free base of the image window) and each
  `unpack` library is unpacked at its planned base through `IBinaryPreprocessor.Preprocess(path, workDirectory, loadBase)` (additive overload; preprocessors that ignore the base keep working).
  The derived image is then fixed at that base, which the later allocation honours. For `linux-x64` the preferred base is used (`LoadBase = null`). A packed library that has no relocations and a
  preferred base below `0x20000000` fails the windows-x64 build with `PE_REBASE_NO_RELOCS` and the Windows hint, before the stub runs.
* Tests: `UnpackLoadBaseTests` (synthetic packer with a runtime-delta relocating stub: unpack at base A and at base B, each image works at its own base, and the two images differ exactly by `B - A`
  at the relocated locations and nowhere else), `UnpackRefusalTests` (fixed low-base image: `BC_BASE_UNUSABLE_ON_TARGET` for windows-x64, `PE_REBASE_NO_RELOCS`), `PackedUnpackTests` (CLI policy
  for windows-x64, run on the host's base), `RealPackedSampleTests` (the real FMOD, see below).

## Use

```csharp
var r = UnpackBuild.TranslateWithUnpack(path, new BinaryCompilerOptions(), cacheDir);     // BinaryBuild.Translate is unchanged
// r.Build.Artifact, r.DerivedImage (pass both to GuestModule.Load), r.Lineage, r.UnpackCacheHit
```

CLI opt-in (wired, see the last section): `IBinaryPreprocessor` (in `BlitzCS.BinaryCompiler`) is the seam; `UnpackPreprocessor` (`Id = "unpack"`) implements it. The CLI should, only when a library entry has
`"unpack": true`, load `BlitzCS.BinaryCompiler.Unpack.dll` from its output directory (`Assembly.LoadFrom`, then instantiate the single `IBinaryPreprocessor` whose `Id == "unpack"`), call
`Preprocess(path, cacheDir)`, translate `DerivedPath` instead of the input and add `LineageToken` to the dependency list. The CLI project keeps no reference to the Unpack project.

Runtime hook added to `GuestModule`: `BoundImports` and `GetImportStubAddress(dll, name)` (the guest-callable stub address of a bound import), for
`Win32Context.SetExportStubAddressProvider`, so a loaded guest's `GetProcAddress` can hand out stubs for imports that exist in a module.

## Evidence: local UPX-packed `fmod.dll` (env `BLITZCS_PACKED_SAMPLE`, read only)

* Stub executed: 4,946,106 interpreted instructions, about 1.3 s; 694,601 bytes written into `UPX0`.
* OEP reached at RVA 0x3CD05, confidence High (first `jmp` from `UPX1` into a written, never-executed `UPX0` page); 232 of 232 export targets populated.
* The stub resolved 152 distinct `dll!name` through LoadLibraryA/GetProcAddress and called nothing else (only LoadLibraryA and GetProcAddress themselves); 164 IAT slots rebuilt in 19 descriptors
  (ADVAPI32, MSACM32, MSVCRT, ole32, USER32, WINMM, WSOCK32 ordinals, KERNEL32).
* Derived image: 52 code pages, 94 data pages, 13 sections; translates with 0 errors and 0 warnings (2 Info: jump tables, fixed base); 232 exports translated.
* Win32 component coverage of the 164 imports (initially 46 implemented, 118 missing; now 130 implemented incl. registry/COM/user32 subsets, 34 missing: msacm32 8, user32 window APIs, mci 2, sockets 11, DeviceIoControl/OpenFile/VirtualLock/VirtualProtect, sscanf). DllMain returns TRUE. Previously (winmm 31, msvcrt 33, wsock32 14, user32 10, kernel32 11, msacm32 8, advapi32 6, ole32 5).
* `_FSOUND_GetVersion@0` (stdcall, no arguments, float in ST0) loaded with `RunEntryPoint=false`, `AllowUnboundImports=true` returned **3.75** (observed, not assumed).
* `DllMain(DLL_PROCESS_ATTACH)` (the OEP) runs with `Win32ImportResolver` and returns TRUE. State-only exports answer (GetError 0, GetNumDrivers 0 on a host without devices, SetOutput/GetOutput round trip),
  `FSOUND_Init(44100, 64, 0)` with NOSOUND completes (the translator finds the output-driver function table by scanning data for code pointers), `GetMaxChannels` is 64 and `FSOUND_Close` returns.
  Exports that need device I/O fault naming their missing import.
* **At a chosen base** (`UnpackOptions.LoadBase = 0x30000000`, the run used on windows-x64): the packed file has 1 base relocation (the stub's); the stub reaches the same OEP (0x3CD05, High) after the same 4,946,106
  instructions; the derived image has `ImageBase = 0x30000000`, 0 relocations, 164 imports, 232 exports, translates for windows-x64, and the whole sequence above (GetVersion 3.75, DllMain TRUE, SetOutput/GetOutput,
  Init, GetMaxChannels, Close) passes both natively on Linux and as a win-x64 process under Wine (`RealPackedSampleTests.Real_sample_unpacked_at_a_chosen_base_runs_like_the_native_original`).
  Comparing the images unpacked at 0x10000000 and at 0x30000000: 5,181 four-byte locations differ by exactly 0x20000000 and not a single other byte differs
  (`Real_sample_unpacked_at_two_bases_differs_only_by_relocated_addresses`): UPX's own relocation processing ran. Observation, not a limit of the feature: this two-base difference is also a way to recover the
  relocation table of an unpacked image (a future option would unpack twice and emit a `.reloc`; not implemented, so the derived image stays fixed at its `LoadBase`).

## Limits

IA-32 integer subset only; no FPU/SSE in stubs. Multi-layer packers are run until the first generated-code entry; layered results need a second `TryUnpack` on the derived file
(not automatic). Stubs that rely on exceptions, timing or debugger checks are refused. Imports that are never stored in the image (kept in registers/stack) and API thunks
generated per call cannot be rebuilt. Only the user's own libraries should be unpacked; the stub is interpreted locally and nothing is forwarded to the host.

## Uso pela CLI (`"unpack": true`)

No catálogo: `"binaryLibraries": [{"path": "packed.dll", "unpack": true}]`. Só então a CLI carrega
`BlitzCS.BinaryCompiler.Unpack.dll` (ao lado do compilador) pelo nome, via `IBinaryPreprocessor` (id `unpack`), sem
referência em tempo de compilação. A imagem derivada fica no cache (chave de linhagem), é traduzida (a chave de cache
inclui a linhagem) e os bindings são validados contra ela. A saída recebe a DLL original (bytes intactos), `<dll>.bcx`,
`<dll>.derived` (PE derivado) e `<dll>.lineage.json`; o runtime confere a linhagem contra a DLL original antes de carregar.
Se o unpacker recusar, o build falha com o código dele ("Unpacking failed: UNPACK_...: ..."). O erro
`BC_ROOT_NOT_FILE_BACKED` de uma biblioteca empacotada sugere `"unpack": true`.
