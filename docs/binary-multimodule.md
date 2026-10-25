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

* Data exports, forwarders, cycles: unsupported (diagnosed). Callbacks passed *between* modules as guest function pointers (module A hands its code address to module B) are not
  possible: B's dispatch table only knows B's own code, so the call traps (`BC_UNTRANSLATED_TARGET`); route such callbacks through an import of B.
* A cdecl bridge cannot know the real argument count (window above). Struct-by-value arguments larger than the window and register conventions (thiscall/fastcall) are not bridged.
* The allocator picks from fixed windows and cannot see the live address space; a conflict is reported at load time with the occupants named.
* The x87 stack must be empty at a bridged call (the callee's entry check reports `GuestAbiException` otherwise).
* eax.dll (COM / DirectSound) was not run: it needs ole32/advapi32 services that the Win32 component does not provide.
