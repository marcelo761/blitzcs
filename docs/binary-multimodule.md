# Several translated modules in one process

Real programs ship several DLLs, and the ones measured here (ogg.dll, vorbis.dll, vorbisFile.dll, eax.dll) **all prefer ImageBase
0x10000000**; vorbis.dll and vorbisFile.dll additionally import functions from ogg.dll and vorbis.dll. Two pieces make that work:
rebasing at translation time and cross-module linking at load time.

## 1. Rebasing at translation time

Translated code embeds absolute guest addresses (guest address == host address), so the base is a *translation input*:

* `BinaryCompilerOptions.LoadBase` (uint?, default null = preferred base). `X64Translator.Translate` and `BinaryBuild.Translate` rebase
  the image first, then run the unchanged translator on it. A build for another base therefore has identical code shape
  (`RebaseTests.RebasedCodeHasTheSameShape...` compares the address map; the benchmark workloads are unchanged).
* `PeImage.Rebase(newBase)` returns a new image: HIGHLOW relocations applied to a copy of the **file** bytes, ImageBase updated, and all
  derived data (TLS VAs, tables, relocation list) obtained by re-parsing those bytes. The original bytes are never touched.
  `Sha256` stays the **original** file hash; `PeImage.RebasedFrom` (`PeRebaseInfo.OriginalImageBase`) records the lineage, which is also stored in
  `TranslationArtifact.RebasedFromImageBase` (artifact format version 2) and in the cache key (written only when original and target base differ,
  so keys of preferred-base builds did not change).
* Refused with `PeRebaseException`: no relocations (`PE_REBASE_NO_RELOCS`, a stripped image cannot move), malformed/unsupported relocations
  (`PE_REBASE_BAD_RELOCS`), a base that is not 64 KiB aligned or leaves the low 4 GiB (`PE_REBASE_BAD_BASE`), a relocation not backed by file data
  (`PE_REBASE_UNBACKED_RELOC`).
* `ModuleBaseAllocator.Allocate(modules)` chooses deterministic non-overlapping 64 KiB-aligned bases (with a 64 KiB gap) for a program: non-relocatable
  images keep their base (conflicts are errors), relocatable ones keep their preferred base when free, otherwise take the first free range of a fixed
  scan (0x10000000..0x40000000, then 0x80000000..0xE0000000). It never hands out the first 16 MiB or 0x40000000..0x80000000, the MAP_32BIT zone where guest
  stacks, TEBs, PEBs and scratch buffers live. It cannot see the live address space.
* Loading: `MAP_FIXED_NOREPLACE` as before; when the range is taken, `GuestAddressSpaceException` names the occupants read from `/proc/self/maps`.
  Nothing is ever overwritten. A loaded artifact must be paired with the image from `Rebase` (`TranslatedLibrary.Open` does this itself from the
  artifact's lineage); a mismatch is a `GuestLoadException`.

Differential evidence: the oracle libraries state, arith, conv, cb, memops, fp run at the preferred base and at 0x20000000 / 0x7A5B0000 in both
profiles; return values, callback traces and data-section hashes must match the native ELF32 oracle. Absolute pointers stored in the data section
(function-pointer tables, jump tables) are normalised back by the base delta at every relocation site before comparison. Mutation check:
the same code with the data relocations dropped diverges. (The cb library embeds IAT addresses as literals, so the harness registers
relocations for them, as a real linker would.)

## 2. Cross-module linking (`GuestModuleSet`)

```csharp
var plan = ModuleBaseAllocator.Allocate([("ogg", oggImage), ("vorbis", vorbisImage)]);
// translate each with LoadBase = plan[i].Base (artifact), keep image.Rebase(plan[i].Base) for loading
using var set = new GuestModuleSet(name => new Win32ImportResolver());      // one host resolver per module (or a single shared one)
set.Add("ogg", oggArtifact, oggRebased, ExportConventions.AllCdecl)
   .Add("vorbis", vorbisArtifact, vorbisRebased, ExportConventions.AllCdecl);
set.LoadAll();                                                              // dependency order, DllMain of dependencies first
set["vorbis"].Bind("vorbis_synthesis_headerin", GuestCallingConvention.Cdecl, 3).Call(...);
```

* Names are matched case-insensitively with the extension optional (`ogg`, `ogg.dll`, `OGG.DLL`).
* Resolution per import, in order: the host resolver (Win32 component for kernel32 etc.; `ChainedGuestImportResolver` composes resolvers without
  Runtime depending on Win32), then the sibling module named by the import's DLL. Sibling imports resolve against the target's export table by name or ordinal.
  A sibling named by an import that lacks the symbol is an error (never an unbound stub); forwarders are rejected explicitly; data exports are not supported.
* **Convention (never assumed).** PE exports carry no signature. The target's `ExportConventions` is consulted: explicit entry (`Exports["name"]` or `"#ordinal"`)
  -> unambiguous MSVC decoration read by the compiler (`_f` cdecl, `_f@12` stdcall with exact count) -> the module's `Default` (only `Cdecl` is accepted as a default;
  stdcall always needs a count). Otherwise the load fails naming the import. An explicit entry that contradicts a decoration is an error.
* **Bridge protocol.** The import stub of the consumer calls a managed `GuestImport` closed over the target export. It reads the caller's stack arguments,
  calls the export through the target module's normal entry (`GuestModule.Invoke`: the target's own call context, TEB, stack from its pool, canary), and returns.
  * stdcall: exactly `ArgCount` dwords are copied and the import pops `4*ArgCount` bytes.
  * cdecl: a **window** of `ModuleLinkOptions.CdeclWindowDwords` (default 16, max 256) dwords starting at the first argument is copied; the callee reads only its real
    arguments and ignores the rest (the extra words are caller locals or its return address and are never written back). The window is shortened until it lies inside
    mapped memory of the caller, so it never reads past the end of the stack. A cdecl callee with more arguments than the window would see garbage for the extra ones.
  * **Extended return**: the managed side stages EDX and, if the callee left a value on the x87 stack, the raw 80-bit ST0 (`GuestCall.SetReturnEdx/SetReturnSt0`, written into
    the caller's call context: EDX at +48, flags at +60, ST0 image at +64). Bit 63 of the import result tells the stub; the stub then jumps to a shared out-of-line tail
    that reloads EDX and `fld`s the ST0 image before the normal return, so the x87 stack stays balanced exactly as the real callee left it. Imports that stage nothing run
    the original path plus one `test r9d,r9d; js` (no memory access). The target is invoked with an internal capture mode (`St0IfPresent`) that does not treat an empty x87 stack as an error.
* **Concurrency.** Every module has per-thread TEBs and per-call contexts; the bridge takes no lock, so calls (including A -> B -> C chains) run in parallel on any number of threads
  (`ModuleLinkTests.ConcurrentNestedCallsOnManyThreads`). A fault in the callee (trap, import fault, ABI error) surfaces as the caller's import fault.
* **Memory.** One process has one address space: the set links the modules' `GuestMemory` so managed accesses (Win32 services, host callbacks) accept pointers into
  a sibling's regions (for example a buffer returned by `ogg_sync_buffer` handed to a host `read` callback).
* **Lifetime.** `Dispose` of the set disposes in reverse dependency order; `Unload(name)` and a direct `GuestModule.Dispose()` of a module that still has dependents throw
  `InvalidOperationException`. Failed `LoadAll` disposes what it loaded. `CrossModuleCalls` counts calls per bridge (proof that a call really crossed modules).
* **Cycles** are rejected (every DllMain must run after its dependencies').

## 3. Real libraries (BLITZCS_REAL_PE32_DIR; nothing is copied)

`RealMultiModuleTests` loads ogg.dll, vorbis.dll and vorbisFile.dll at three different bases, runs all three CRT start-ups (DllMain) on per-module Win32 state and:

* builds a Vorbis identification header by hand (Vorbis I, section 4.2.2) and checks `vorbis_synthesis_headerin` results (version, channels, rate, three bitrates, both
  blocksizes through `vorbis_info_blocksize`) against the encoded values, plus five malformed headers against the specified error codes; counters prove the
  bit reads (`oggpack_readinit`, `oggpack_read`) went through the bridge into ogg.dll;
* builds a comment header and checks `vorbis_comment` fields and `vorbis_comment_query(_count)`;
* encodes a 0.6 s stereo tone with ffmpeg/libvorbis (skipped when ffmpeg or its encoder is missing), opens it with `ov_open_callbacks` and decodes it with `ov_read`:
  sample count (`ov_pcm_total`, 64-bit through EDX:EAX), duration (`ov_time_total`, ST0), channels and rate agree with the request, PCM is within 2 LSB of
  ffmpeg's decode, and `ov_pcm_seek`/`ov_pcm_tell` + reading from the middle also match. Every vorbisFile -> ogg/vorbis call goes through the bridge (counted).
* Each reference value goes through a mutation hook (`BLITZCS_MUTATE=<key>`); every one was perturbed once and the suite failed as it should.

How the vorbisFile callbacks work without runtime support for managed-to-guest function pointers: `ov_open_callbacks` needs four guest-callable code addresses. vorbisFile.dll
has the static CRT (no `fread` import), so the test binds four of its own kernel32 imports that this path never calls (`ReadFile`, `SetFilePointer`, `CloseHandle`,
`FlushFileBuffers`) to the host callbacks and passes their **import stub addresses** (`GuestModule.GetImportStubAddress`) as the function pointers. The stubs are in the
vorbisFile dispatch table, so the guest's indirect call reaches the managed handler. This is a general recipe, not a library-specific hack, but it consumes an import.

## Limits

* Modules loaded after start-up by `LoadLibrary` (`GuestLibraryLoader`, docs/binary-win32.md) join the same set: `Add`/`LoadAll`/`Unload` are serialised by a lock, `GuestExportBridge.AllocateStub` hands the caller a stub that forwards to an export, and no loaded module is relocated.

* Data exports, forwarders, cycles: unsupported (diagnosed). Callbacks and function pointers passed *between* modules (module A hands its code address to module B, B calls it; a C++ vtable in
  one module reached from another) are supported by cross-module dispatch (section 3), not by the bridge.
* A cdecl bridge cannot know the real argument count (window above). Struct-by-value arguments larger than the window and register conventions (thiscall/fastcall) are not bridged.
* The allocator picks from fixed windows and cannot see the live address space; a conflict is reported at load time with the occupants named.
* The x87 stack must be empty at a bridged call (the callee's entry check reports `GuestAbiException` otherwise).
* eax.dll (COM / DirectSound) was not run: it needs ole32/advapi32 services that the Win32 component does not provide.

## 3. Cross-module dispatch (artifact format 5, compiler 0.11.0)

Measured need: the original `blitzcc.exe` calls a virtual method of the object returned by `linker.dll!linkerGetLinker`; the vtable slot is a `linker.dll` code address and the EXE's dispatch
table only covers the EXE. Every translated module has its own dispatch table (guest RVA to host offset from R12). A hit costs what it always did; the **miss** (target outside the table or entry 0) used
to be the kind 1 trap. It is now a routine at host offset 0:

1. `pushfq` (on the guest stack, below ESP: free memory), load the registry address from the cell at `ContextOffset + 0` of the module's own data area (the formerly unused 64-byte context area; 0 means the module is not in a set, so the trap stays);
2. walk the **module registry**: a native array published by the runtime, one per `GuestModuleSet`: header `u32 count` (release store, only ever grows), `u32 capacity` (1024), then 32-byte records
   `{u32 guestBase, u32 bound, u64 hostCodeBase, u64 dispatchTable}`. A record matches when `VA - guestBase < bound` (unsigned, so also `VA < guestBase`; a retired record has bound 0);
3. on a match whose table is live and whose entry is non-zero: `r12 = hostCodeBase`, `r13 = dispatchTable`, `popfq`, `jmp r12 + entry`. Otherwise the kind 1 trap runs unchanged (message: "untranslated code at ... and not inside any other module of this process", or, for an address in
   a module that was unloaded, "lies inside module 'x.dll' that has been unloaded").

Returns work symmetrically: a `ret` in B to an address of A misses B's table, takes the same path and switches back. **Switched:** R12 (code base) and R13 (dispatch table). **Not switched:** R14 (call context: trap report, native RSP, import fault flag,
x87 capture) and R15 (TEB): they belong to the thread's current guest call, not to the module. Import stubs executing in B use B's own thunk table (RIP-relative), so imports of B run as B's even when B was entered by a jump; managed imports see
`GuestModule.Current` = the module the thread *entered* (its `Memory` sees every module's regions, so this only matters for module identity APIs).

*Flags.* The miss routine preserves the arithmetic flags itself (pushfq/popfq), so a foreign call/return costs nothing in correctness over the in-module flag-preserving dispatch. It is entered from the flag-clobbering dispatch variant too (that variant is only used when
every statically known target of *that image* ignores flags, the ABI assumption already documented in `DispatchTargetsIgnoreFlags`); the flags it then hands over are the ones that variant leaves. `FlagsAreIdenticalAcrossTheForeignCallAndReturn` forces the preserving variant on both sides.

*Cost.* Zero extra instructions on a hit (benchmark: docs/binary-benchmark.md, "Cross-module dispatch"); a foreign call/return is a linear scan of the registry (a few dozen instructions). *Compatibility:* the cell and the routine make artifacts of format version 4 or older unusable
(`ArtifactSerializer.CurrentVersion` 5, `CompilerVersion` 0.11.0 so cache keys change; the loader refuses an older in-memory artifact with "re-translate the image"). The SHA pins of `TargetOsTests` were re-pinned once for this change; the only difference to the old output is the routine at offset 0.
Deviation from the written design: none in behaviour; the registry cell reuses the legacy context area instead of adding a data field, so no layout offset moved.

*Lifetime.* `Publish` completes the record, writes the table pointer, then increments the count (release stores); slots are never reused (a reader can never see another module in a slot; 1024 loads per process, then `GuestLoadException`). `Retire` writes bound 0, then
clears the table pointer, before the module's host image is freed. Translated code of one module can run inside another without that module's call accounting, so a module is **only unloaded while no other thread is in guest code anywhere in the process**
(`GuestModuleSet.Unload` throws `InvalidOperationException`: "N other guest thread(s) are executing guest code ..."; the calling thread is excluded because FreeLibrary is itself a guest call). Limit: a thread that frees the module it is currently executing in (FreeLibrary(self) from its own code) is not detected, as on Windows.

*Shared thread state.* A set owns one `GuestProcessEnvironment`: the registry, one PEB (ImageBaseAddress = the executable's; ProcessHeap shared), and **one TEB per OS thread** for all modules (SEH chain, TLS slots, LastError field, stack bounds). A call into another module that needs its own stack
(the bridge, `GetProcAddress` stubs) switches StackBase/StackLimit and restores them; the SEH chain is only reset at the outermost entry of the thread. Implicit TLS: each module with a TLS directory gets a process-wide index (written to its index variable) and a data block in every thread's pointer array, also for threads that already exist when the module is loaded.
Details: docs/binary-teb-contract.md.
