# M19 — historical API coverage and the string runtime family

M19 starts subsystem-based implementation with a complete inventory of the pinned runtime's registrations and one functional family: the 18 `bbstring` APIs. It adds no compiler features or engine dependencies to BlitzCS. The unchanged SCP:CB snapshot advances from **3/30 to 5/30 standalone assemblies**. Its integrated semantic/build blocker remains `MoveMouse`; no integrated game assembly or game execution is claimed.

## Inventory and coverage definitions

The [human inventory](blitz3d-api-inventory.md) and [machine-readable inventory](blitz3d-api-inventory.json) cover **593 literal registrations**, including **536 public typed signatures** and **57 internal ABI symbols**, across 39 original source/header files. There are no unsupported descriptors or same-name conflicts in this snapshot. The reference is [Blitz3D revision 6beb288](https://github.com/blitz-research/blitz3d/tree/6beb288cb5962393684a59a4a44ac11524894939).

Each row records the original descriptor, parameter types/defaults, result type, registration location, implementation symbol, direct includes, subsystem, current binding metadata and evidence. Descriptors describe BASIC calls, not native C/C++ ABI signatures or pointer-marshalling contracts. The following are separate states:

1. Historical signature known.
2. Typed declaration available, possibly without an implementation.
3. Managed binding configured with an implementation source present.
4. Functional scope backed by tests.
5. Historical compatibility backed by a scoped comparison.

A source-configured binding does not prove that a native host is available or that all behavior matches. The current SCP catalog intersects **115** historical public declarations and **52** source-backed managed bindings. **30 APIs** have scoped comparison evidence: 13 banks, one selected system-property contract and 16 deterministic strings. These are API coverage markers, **not 30 independent differential programs**. The existing FSOUND adapter is a separate external integration, not an implementation of the 17 built-in Blitz3D audio registrations.

| Subsystem | Registrations, including internal symbols where applicable |
| --- | ---: |
| Graphics/display/windowing | 30 |
| Keyboard/mouse/joystick | 41 |
| 2D drawing and buffers | 82 |
| Entities/3D | 74 |
| Meshes/surfaces/materials/textures | 85 |
| Cameras/light/animation | 35 |
| Collisions/movement | 39 |
| Built-in audio | 17 |
| Files/memory/timing/system | 66 |
| Remaining: internal ABI, strings, math, networking and other modules | 124 |

Completeness is limited to literal `rtSym` registrations outside comments, including conditional registrations without preprocessing. Compiler intrinsics and language operations such as `Abs`, casts, `Object` and `Handle` are explicitly outside this runtime inventory. Dependencies record direct source evidence; transitive runtime dependencies remain marked unverified. Lexical SCP frequencies are ranking aids, not resolved call counts. These limitations prevent registration coverage from being mistaken for complete Blitz3D compatibility.

The generator rejects modified historical runtime sources and protects original inputs, catalogs and evidence from output aliasing. To regenerate from a separately obtained pinned source checkout:

```sh
python3 -m tools.generate_blitz3d_api_inventory --original-root "$BLITZ3D_SOURCE" \
  --scp-source "$SCP_SOURCE"
```

## Implemented family

All 18 registrations from `bbruntime/bbstring.cpp` now have real static .NET bindings in `Blitz3DCS.Runtime.LegacyStrings`:

| Group | APIs |
| --- | --- |
| Construction and bytes | `String`, `Chr`, `Asc`, `Len` |
| Slicing and searching | `Left`, `Right`, `Mid`, `Instr` |
| Transforming and padding | `Replace`, `Upper`, `Lower`, `Trim`, `LSet`, `RSet` |
| Numeric formatting | `Hex`, `Bin` |
| Local clock formatting | `CurrentDate`, `CurrentTime` |

`Chr` and `Len` existed previously; the other 16 implementations and bindings are new. They share one encoding boundary and require no window, graphics device, handle registry or native library. Stateless operations own no persistent string resources; the codec is shared and clock functions read the actual host clock.

Verified contracts include:

- Strict CP1252: supported characters map to one legacy byte; unsupported managed Unicode fails explicitly. Embedded NUL is retained.
- `Asc("")=-1`, unsigned byte values, and `Chr` retaining the low eight bits.
- One-based `Mid`/`Instr`; default `Mid` count `-1`, default `Instr` start `1`; every negative Mid count means the remaining suffix. Oversized offsets clamp or return not-found as appropriate.
- Negative size/invalid offset errors preserve the original diagnostic text. Zero sizes are valid.
- Ordinal, non-overlapping replacement without rescanning inserted text.
- ASCII-only case conversion. All bytes 128–255 remain unchanged in `Upper`/`Lower`.
- C-locale graphical trimming: only bytes 33–126 survive at the edges. Controls, NUL, DEL and high bytes are stripped at the edges; interior bytes remain intact. Unicode `Trim` would produce different results.
- `Hex` uses eight uppercase digits and `Bin` uses 32 bits, including signed two's-complement representations.
- `CurrentDate`/`CurrentTime` use real local time and invariant `dd MMM yyyy` / `HH:mm:ss` formats. These have functional clock tests, **not an original-clock differential claim**.

Deliberate safe differences: `Replace` with a nonempty input and an empty search throws instead of reproducing the original's nontermination; empty input still returns empty. Allocation overflow/failure is a managed error. Error termination is nonzero with BASIC source diagnostics, rather than the original SDK's successful exit status after a runtime-error dialog. CP1252 is an explicit compatibility target, not every possible Windows ANSI locale or arbitrary Unicode behavior.

## Differential and integration evidence

The [owned reference program](../samples/LegacyStrings/reference.bb) was genuinely executed with the official V1.118 SDK under Proton/x86, in an isolated SDK copy and Xvfb display. [Original evidence](evidence/m19-original.json) records source and SDK hashes. Its 269 output rows, including all 256 byte values for casing/trimming, match BlitzCS exactly. The six separate invalid-argument programs agree in phase and diagnostic; their zero-versus-nonzero termination is explicitly recorded as six divergences.

[Comparison evidence](evidence/m19-new-comparison.json) reports **one strict match, six known termination divergences, zero unexpected differences**. Together with the preserved earlier captures this is **90 strict matches out of 97 bounded cases**, with seven intentional error-exit differences including the existing `upper-bound-error` case. It does not establish full semantic parity for all inputs.

The [multifile hybrid example](../samples/LegacyStrings/Hybrid/main.bb) exercises BASIC → C# → BASIC, a shared String global, Include processing and direct external runtime calls. It prints:

```text
door
Door
  0000002A
a\b\c
x  :  x
===
```

Reproduce after building Release:

```sh
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/LegacyStrings/Hybrid/main.bb -o artifacts/m19/hybrid/main.dll \
  --externals samples/LegacyStrings/runtime.json
dotnet artifacts/m19/hybrid/main.dll
python3 tools/differential.py --manifest tests/Compatibility/milestone19.json \
  --externals samples/LegacyStrings/runtime.json --report artifacts/m19/local-differential.json
python3 tools/compare_m19_evidence.py
```

The comparison command replays committed genuine captures. For a fresh original run, provide `BLITZ3D_HOME`, `BLITZ3D_WINE`, `BLITZ3D_WINEPREFIX`, Xvfb and the existing adapter dependencies:

```sh
python3 tools/probe_strings_original.py --invalid-cases \
  --report artifacts/m19-original/report.json --evidence-dir artifacts/m19-original/raw
```

SDK files and game sources/assets are not distributed.

## Performance

Strict CP1252 validation initially dominated ASCII search cost. A measured optimization now uses .NET's ASCII span validation before falling back to the strict encoder for non-ASCII input. It retains rejection of invalid characters after long ASCII prefixes and does not allocate encoded copies. Frequent calls use direct static bindings, with no reflection or locks in the string operations.

The [benchmark](../benchmarks/Blitz3DCS.Strings/Program.cs) runs the ordinal baseline, an owned pre-optimization scalar-validation control and the current implementation in one executable. It performs five measured rounds after warm-up, keeps string results observable, and records median wall time and thread allocation bytes. This is not a native Blitz3D speed comparison or a frame-rate measurement.

```sh
dotnet run --project benchmarks/Blitz3DCS.Strings -c Release -- 500000
```

[Measured results](evidence/m19-string-benchmark.json), Linux x64 / .NET 10:

| Operation | Scalar-validation control | Current | Current allocations/call |
| --- | ---: | ---: | ---: |
| `Instr`, 64 ASCII characters | 124.33 ns | 29.41 ns | 0 B |
| `Instr`, 4096 ASCII characters | 7280.96 ns | 159.74 ns | 0 B |
| `Left`, 64 characters → 8 | 124.46 ns | 22.74 ns | 40 B |

The substring result accounts for the 40 B in both the ordinal baseline and compatibility implementation. Unchanged uppercase conversion and replacement without a match also measured zero allocations. Timings are host/load/JIT dependent and are not acceptance assertions. Non-ASCII validation still performs an encoding scan.

## SCP:CB progress and remaining dependencies

The same 30 source hashes were recovered from pinned upstream revision `edb8fe0840b78f14d1aef3a0bf6174630e7be296`; `Main.bb` declares version **1.3.11** at lines 45–46. Nine original `.decls` inputs were also recovered for external-dependency classification. The original game executable/assets were not available for a functional game run; original-runtime testing here concerns the owned language/runtime probes.

| Stage | Verified result |
| --- | --- |
| Original `.bb` files modified | None; all 30 hashes match M18 |
| Main and 23 Include targets, syntax | Pass |
| Standalone assemblies | 5/30, previously 3/30 |
| Newly compiling files | `Blitz_Basic_Bank.bb` (`Mid`), `Blitz_File_FileName.bb` (`Right`) |
| Integrated semantics/build | Stops at `fullscreen_window_fix.bb:84:2`, `MoveMouse` |
| Integrated assembly | Not emitted |
| Game initialization/execution | Not performed |

The other standalone successes remain `Difficulty.bb`, `FMod.bb`, `KeyName.bb`. [Full audit](evidence/m19-scp.json) records first errors and categories; fragment compilation errors are not proof of equivalent failures in a correctly resolved full project. Windowing/input, native window handles, graphics and other runtime families remain real dependencies. `GetClientRect` is still a declaration-only adapter contract, and no fake host or silent stub was registered.

Next cohesive candidates are scalar math with exact Single/PRNG contracts, stream/file I/O with shared checked resource ownership, and genuine window/input hosting for the integrated blocker. They require their own bounded original comparisons. SCP: Nine-Tailed Fox has not been audited or executed in this milestone.

## Final validation

- **784 .NET tests** pass in each of Release and Debug; both solution builds have zero warnings/errors and no skipped tests. This preserves all 713 baseline tests and adds 71 string-family cases, including long-prefix encoding boundaries. The three process-isolated native lock-order scenarios remain passing.
- **76 Python tests** pass, including inventory integrity, input/output protection and comparison tamper/timeout tests.
- **97 local fixtures** pass: 90 previous plus seven M19 programs. Local success and genuine differential comparisons are reported separately above.
- The full Release verification pipeline passed, including native ABI/publication, managed object/bank/handle stress, SDL audio/PCM and hybrid checks. After the measured ASCII optimization and its seven extra unit cases, the full Release .NET suite was repeated; Debug built and tested final code. The final Python suite was also run separately.
- No fresh original runs were substituted for the historical replay. The seven M19 original programs were genuinely captured for this milestone.

[Validation summary](evidence/m19-validation.json) and [historical replay](evidence/m19-regression-differential.json) retain counts, hashes and scope. The original M18 checkpoint and its concurrency follow-up are preserved. No renderer, window host or input API was added in this bounded milestone.
