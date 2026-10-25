# Packed and obfuscated images

BCSBRE is an ahead-of-time recompiler: it translates the bytes that are in the file. A packed image stores compressed or encrypted
code and creates the real code at run time; those bytes do not exist when the translator runs.

## What happens today

* `PackedImageAnalyzer.Analyze(PeImage | bytes)` (`src/BlitzCS.BinaryCompiler/Pe/PackedImageAnalyzer.cs`) is a standalone, structure-only
  analysis returning `PackedImageReport` (verdict `None`/`Suspected`/`Likely`, score, reasons, `PACK_*` Info/Warning diagnostics, section
  entropies, optional name hint). It never fails a build and contains no packer-specific logic.
* The only error remains `BC_ROOT_NOT_FILE_BACKED` (an export, entry point or relocation target in memory without file data). The CLI step
  (`BinaryTranslationStep`) appends the report summary to that error: "this image appears to be packed (reasons); AOT recompilation cannot
  see code produced at run time".
* Nothing is unpacked, guessed or translated as zeros.

### Evidence and weights

| Code | Evidence | Score |
|---|---|---|
| `PACK_UNBACKED_EXEC_SECTION` | executable section with RawSize 0 and VirtualSize > 0 | 3 |
| `PACK_EXPORT_UNBACKED` | export RVAs in memory that is not in the file | 3 |
| `PACK_ENTRY_UNBACKED` | entry point in memory not in the file | 3 (1 if the section itself already counted) |
| `PACK_HIGH_ENTROPY` | executable section, >= 256 raw bytes, Shannon entropy >= 7.1 bits/byte (first 1 MiB) | 2 |
| `PACK_ENTRY_IN_HIGH_ENTROPY` | entry point outside the first file-backed executable section and inside a high-entropy one | 2 |
| `PACK_MINIMAL_IMPORTS` | only loader APIs (LoadLibrary*/GetProcAddress/VirtualAlloc...), or loader pair plus exactly one function in each of >= 3 other DLLs | 1 |
| `PACK_WX_SECTION` | writable and executable section | 1 |
| `PACK_NAME_HINT` | name such as UPX*, .aspack, .themida, .vmp* (Info only) | 0, never proof |

Verdict: score >= 4 `Likely`, >= 2 `Suspected`. A writable section without file data (`.bss`) is normal and not flagged. Zero imports is
not flagged (controlled DLLs have none).

### Measured facts

Computed by the analyzer on the local `artifacts/m13-fmod/fmod.dll` (read only, not copied):

* Sections: `UPX0` VA 0x1000, VirtualSize 0x6D000, RawSize 0 (flags executable|readable|writable); `UPX1` VA 0x6E000, VirtualSize 0x25000, RawSize
  0x25000, entropy 7.92, executable|writable; `.rsrc` VA 0x93000, entropy 5.41. SizeOfImage 0x96000.
* Entry RVA 0x92D10, inside `UPX1` (the stub, the only file-backed executable section; hence no entry-point anomaly is reported). 232 exports, all pointing into `UPX0` (first:
  `_FMUSIC_FreeSong@4` at 0x20626).
* Imports: 9, only the stub's: LoadLibraryA, GetProcAddress and one function from each of ADVAPI32, MSACM32, MSVCRT, ole32, USER32, WINMM,
  WSOCK32 (ordinal).
* Verdict `Likely`, score 10 (unbacked exec section 3, exports 3, high entropy 2, W+X 1, minimal imports 1); `UPX0` name is an Info hint only.
* Control: libogg/libvorbis/vorbisFile (GTA_SA copies, entropy 2.0-6.6, no unbacked sections) are `None` with score 0.

## Strategy design (not implemented)

The input DLL must stay untouched and every derived artifact must be traceable to it. Three layers, in order of preference.

### (a) Build-time unpack by controlled execution (generic, default candidate)

A packer stub is ordinary x86 code that is present in the file. It needs only a few imports (LoadLibraryA, GetProcAddress, sometimes
VirtualAlloc/VirtualProtect) and fs/PEB access. The generic flow:

1. Translate the image's *stub only* (entry point and what it reaches, with the file-backed sections). Unbacked sections are mapped as
   zero-filled RWX guest memory by a special "unpack session" loader, which the normal loader never does.
2. Run the stub in the guest (the Win32 component supplies LoadLibraryA/GetProcAddress/VirtualAlloc and the TEB/PEB; Frente 2 is building it)
   with a budget, until control reaches the original entry point (OEP).
3. Snapshot the in-memory image. Emit a derived PE32 (new file, never overwriting the input) with a rebuilt import table, sections taken
   from the snapshot, and the original export table (the exports already point into the now populated memory).
4. Recompile the derived PE with the normal pipeline.

Because the stub itself is *self-modifying* (it writes the code it then executes), step 1 cannot be pure AOT for the stub's decompression
loop only when the stub is also packed/encrypted; typical stubs have a plain loader and compressed payload, so translating the stub
is sufficient. A stub that decrypts itself in place needs layer (c).

Hard parts, each a source of explicit diagnostics rather than guesses:

* **OEP detection.** Heuristics: first control transfer into memory that was written during the run (write-then-execute tracking), a
  `jmp`/`ret` out of the stub area into the unpacked section, or the first call of an export. Wrong OEP means a wrong snapshot, so the
  result must be validated: the derived image must expose the original exports and decode cleanly. Ambiguity is an error.
* **IAT reconstruction.** The stub resolves imports with GetProcAddress into its own table. Record every (DLL, name/ordinal, destination slot) resolved
  during the run and scan the snapshot for pointers equal to the returned guest thunks; rebuild a standard import directory. Imports that the
  unpacked code calls through computed addresses, or via thunks generated per call, cannot be recovered statically and stay unsupported.
* **Anti-debug and environment probes.** Stubs read `PEB.BeingDebugged`, timing (rdtsc), `fs:[0x30]` fields beyond the modelled PEB, SEH-based
  tricks. The TEB contract models BeingDebugged = 0 and only listed fields; unmodelled accesses are errors (`BC_FS_UNSUPPORTED_OFFSET`), which is
  the correct failure mode. Exception-driven control flow (deliberate faults) is out of scope.
* **Relocations.** The stub applies relocations to the decompressed code for the load address. Execute at the preferred base so the snapshot is
  consistent with the fixed base the translator requires (`BC_FIXED_BASE`); the derived image carries the relocation list it can prove (the
  stub's own relocation records), otherwise none and a diagnostic.
* **Cache key and lineage.** The derived artifact is keyed by `SHA256(original)`, the unpacker identity and version, the Win32 component and
  runtime contract versions, the run budget, and the snapshot hash; its metadata records `derived-from: <original sha256>`. The original hash
  stays in the final BCX artifact so a loader can check that the runtime library is the one the build saw. Rebuilding with a different stub
  environment must change the key.
* **Determinism.** Time, randomness and allocation addresses must be pinned during the run, or the snapshot differs between machines.
* **Legitimacy.** Only the user's own libraries, executed locally in the guest sandbox with the same import policy as any translated code. No
  system calls are forwarded to the host beyond what the Win32 component implements.

### (b) Format-specific unpackers as explicit plug-ins

For well-documented formats (UPX has a public format and an open-source tool) a plug-in interface
`IImageUnpacker { bool CanUnpack(PeImage), UnpackResult Unpack(bytes) }` can produce the derived image without executing anything. It would
be opt-in per library in the catalog (`binary.unpack: "upx"`), never automatic, with the same derived-from lineage. It is the cheapest and most
robust path for a known packer and does not touch the translator.

### (c) Residual dynamic translation

For code that cannot be unpacked before run time (self-decrypting stubs, per-call generated code) the only complete answer is a JIT-style
dynamic translator in the runtime: translate basic blocks of memory written at run time on first execution. It needs a code cache, write
tracking for invalidation, and a distinct trust model. It is a different product from the AOT recompiler and is out of scope here; images
that need it keep failing with `BC_ROOT_NOT_FILE_BACKED` and the packed-image explanation.

## Recommended order

1. Keep the analyzer and the explanatory error (done).
2. (b) for UPX if FMOD-class libraries matter (smallest, deterministic).
3. (a) once the Win32 component can run stubs; reuse the same derived-artifact and lineage machinery for (b).
4. (c) only as a separate project.
