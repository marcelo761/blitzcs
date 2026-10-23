# M18 — checked memory banks and native buffer borrowing

M18 implements real managed memory banks and a reusable synchronous native-buffer bridge. It does **not** implement `GetClientRect`, create a Win32 window, or emit the integrated SCP game assembly.

## Implemented operations

`BlitzCS.LegacyRuntime.BankStore` owns `NativeBuffer` instances behind checked positive Int32 tokens. `LegacyBanks` supplies optional static bindings for:

- `CreateBank([size=0])`, `FreeBank`, `BankSize`, `ResizeBank`, `CopyBank`.
- `PeekByte`, `PeekShort`, `PeekInt`, `PeekFloat`.
- `PokeByte`, `PokeShort`, `PokeInt`, `PokeFloat`.

Byte and short reads are unsigned; narrow writes retain low bits. Integer is signed Int32 and Float is IEEE Single. Reads and writes use explicit little-endian encoding, including unaligned accesses. Copying overlapping ranges has memmove semantics. Empty banks and zero-length copies are supported.

Bank handles are identifiers, **never native addresses**. Tokens start at `0x40000000`, remain positive for the legacy game's `bankHandle < 1` checks, are shared across stores, never reused, and fail explicitly on exhaustion. A token is valid only in its issuing store. Other resource tables retain their own contracts: an arbitrary BASIC integer does not prove resource provenance, and must never be reinterpreted as a pointer or HWND.

Freeing null, unknown or already-freed tokens is a no-op. Other operations on invalid tokens fail. Free and Dispose remove buffer roots; no tombstone table retains freed bytes. Live banks are owned until explicitly freed or the host closes the store. The process-wide convenience facade requires `LegacyBanks.CloseBanks()` at program teardown; independent embedded programs should bind separate stores. No finalizers, weak references or manual refcounts were introduced.

The default size ceiling is `Array.MaxLength`; hosts can configure a lower per-bank ceiling. Allocation failures are real managed failures, not successful fake allocations. Store operations are serialized. Same-bank copies allocate no intermediate bytes; cross-bank copies use a temporary checked-length snapshot. This trades an allocation proportional to copy length for simple ownership and locking.

## Native ownership and ABI

`BlitzCS.NativeInterop.NativeBuffer` owns managed bytes. `WithBytes` lends a bounded Span; `WithPointer` pins only for a synchronous trusted-host callback and always unpins in `finally`. Resize, Dispose and reentrant borrowing are rejected while borrowed. Concurrent access waits for the lifetime lock. Callbacks must not retain or return pointers, expose them to BASIC, or wait on another thread accessing the same store.

The callback receives a pointer-sized `nint` and the checked byte count. Alignment must be a positive power of two and is checked against the actual address. This is not an aligned allocator. Empty slices provide a null pointer with count zero. Range checks use subtraction to avoid overflow and cover the entire primitive width.

The owned C test library validates a four-Int32, 16-byte RECT-shaped layout, offsets 0/4/8/12 and alignment 4. Its real `cdecl` export writes `{0x01020304,-2,300,-400}` through the borrowed bytes. This synthetic ABI fixture is **not** a `GetClientRect` replacement. Native code is trusted to respect the supplied length; this mechanism is not a sandbox for malicious libraries.

The hybrid example exercises BASIC → C# → BASIC, shared banks and an actual C export. Publication copies `ManagedBinding → LegacyRuntime → NativeInterop` plus the owned native library. On the validated Linux x64 host it prints:

```text
42
1
16909060
-2
300
-400
64
1
0
```

Reproduce after building the solution (Linux, .NET 10 and `cc`):

```sh
dotnet build BlitzCS.slnx -c Release
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/LegacyMemory/hybrid-native.bb -o artifacts/m18/hybrid/main.dll \
  --externals samples/LegacyMemory/native.json
dotnet artifacts/m18/hybrid/main.dll
```

## Historical reference and deliberate safety differences

Reference: [Blitz3D revision 6beb288](https://github.com/blitz-research/blitz3d/tree/6beb288cb5962393684a59a4a44ac11524894939), especially `bbruntime/bbbank.cpp`, `blitz/libs.cpp` and `compiler/exprnode.cpp`.

Original x86 banks store a data pointer separately from the bank object; `.decls` `*` arguments lower a bank to its data pointer. Copying the original object offset/pointer arithmetic into .NET x64 would be incorrect. The original SCP `api_GetClientRect%(hwnd%, lpRect*)` wrapper allocates 16 bytes, reads four integers, and frees the bank.

A fresh genuine V1.118 execution under Proton/x86 completed successfully with deterministic captured output. The owned [bank program](../samples/LegacyMemory/banks.bb) matches that capture, including default/zero size, initial zeroing, unsigned narrowing, unaligned little-endian reads, Single bit representation, resize prefix, overlap copying and repeated invalid frees. [Original capture](evidence/m18-original.json) records its source SHA256. This is **one differential program**, not one independent comparison per output line.

Deliberate differences and limits:

- New bytes are always zeroed. Original growth within capacity zeros bytes; the reallocation branch copies the prefix but leaves new bytes uninitialized. The probe intentionally does not compare that undefined tail.
- All managed accesses check the full width. Original source checks only the starting offset for some Poke operations; unchecked behavior and memory corruption are not reproduced.
- Invalid SDK accesses did not consistently yield the debug diagnostics suggested by source. Some read garbage; null bank-size access exited abnormally without a capturable diagnostic. These observations are not deterministic expected-output fixtures and are not counted as equivalence.
- Managed failure uses .NET exceptions with BASIC PDB source locations, rather than reproducing legacy dialog/exit behavior.
- `ReadBytes`/`WriteBytes` need the legacy stream subsystem; `CallDLL` needs its distinct ABI contract. They remain unimplemented here. There is no invented `FillBank` command.

To repeat the genuine probe with a separately obtained SDK, set `BLITZ3D_HOME`, `BLITZ3D_WINE`, `BLITZ3D_WINEPREFIX` and provide `Xvfb` plus the adapter's existing dependencies on PATH:

```sh
python3 tools/probe_banks_original.py --report artifacts/m18-original/report.json \
  --evidence-dir artifacts/m18-original/raw
python3 tools/differential.py --manifest tests/Compatibility/milestone18.json \
  --externals samples/LegacyMemory/banks.json --report artifacts/m18/local-differential.json
python3 tools/compare_m18_evidence.py
```

The comparison command replays the committed genuine capture; it does not launch the original compiler itself. SDK files and SCP sources/assets are not redistributed.

## SCP progress: declaration versus implementation

The catalog adds `api_GetClientRect` as a **declaration-only managed adapter shape** (two Int32 resource tokens). Its descriptive `nativeContract` metadata records real Windows HWND ownership, `RECT*`, minimum 16 bytes, alignment 4, out direction and synchronous lifetime. That metadata is documentation, not an automatically enforced native ABI binding. A real adapter must explicitly translate a host-owned OS handle and borrow bank bytes; no integer-to-pointer conversion is generated. Automatic `.decls` pointer discovery remains rejected.

With that declaration the unchanged integrated sources now stop at:

```text
fullscreen_window_fix.bb(84,2): Unknown BASIC function or external symbol 'movemouse'.
```

`MoveMouse` belongs to engine input/window hosting, not the language or bank runtime. The standalone declaration example passes semantic analysis with an unbound-call diagnostic and fails assembly generation. Win32 execution, AppHWND ownership, graphics and game initialization remain unavailable.

## Output safety

The emitter preflights assembly, PDB, generated C# and runtime configuration paths, as well as copied dependency destinations. It protects compiler assets, native/managed inputs and source/catalog inputs, resolving symlinks and directory aliases. Dependency discovery precedes writes. Each generated or copied file is staged beside its destination and atomically replaces that directory entry, preserving input bytes even through a hardlink alias. This is not a transaction across the entire artifact set, nor protection against concurrent hostile filesystem replacement.

Actual resolved include inputs include empty and comment-only files. Semantic-progress output validation also protects CLI companions and external assets, rejecting hardlinks to protected inputs and between output files. Discovery writes temporary working catalogs; subprocess timeout/failure leaves an existing output catalog intact. Previously fixed comment registrations, unsupported duplicate descriptors, literal BASIC backslashes and nonfinite timeout/iteration inputs retain regression coverage. Known signatures are never silently turned into executable stubs.

## Verified validation

- Release and Debug: **710 .NET tests** each, zero failures/skips, zero build warnings/errors.
- **59 Python tests** and **90 local fixtures** pass.
- Historical **85/86** comparisons and M17 **3/3** are preserved; the new bank program matches its genuine original capture. Combined bounded language comparison coverage is **89/90**, with the known `upper-bound-error` exit-status divergence unchanged.
- `scripts/verify.sh` passes, including hybrid/native deployment, real C ABI, SDL PCM/playback, expected declaration-only build rejection and out-of-range bank failure with BASIC source line 2. An expected unhandled .NET exception may print the shell's abort message; the script checks its nonzero status and source diagnostic.
- The 100,000 create/free stress loop leaves only deliberately live bank entries, then zero after cleanup. This structural ownership check does not claim an RSS or GC-reserved-memory measurement.
- The full 30-file SCP audit has no infrastructure failures; all source hashes match M17. Syntax passes for Main and 23 include targets. Semantic analysis and build stop at MoveMouse; no integrated assembly or game execution is claimed.

See [validation evidence](evidence/m18-validation.json), [SCP matrix](evidence/m18-scp.json), [bank comparison](evidence/m18-new-comparison.json) and [historical replay](evidence/m18-regression-differential.json). Next work requires a genuine input/window host and explicit OS-handle ownership; stream-bank I/O remains a separate runtime dependency.
