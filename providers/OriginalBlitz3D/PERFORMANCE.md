# Original-runtime binding performance

The provider calls the unchanged original x86 DLLs in the managed game's process.
The generated entry points now call typed `delegate* unmanaged[Stdcall]` pointers.
Pointers belong to a runtime session and are resolved only after checking the
original registration descriptor against the API profile. No pointer survives
unloading that session. Public managed signatures and catalogs are unchanged.

Warmed numeric calls avoid argument/type arrays, boxing, dictionary lookup and
reflection-generated object dispatch. Selected `BLITZCS_ORIGINAL_TRACE` commands
still use the diagnostic path; tracing is excluded from performance measurements.
Native GC transitions remain enabled, including for blocking/rendering calls.

String calls use typed pointers too. Original BBStr helpers are cached per session;
small encoding buffers use the stack and large buffers use an array pool. Returned
native text is decoded directly into the result string. Ownership transfer, strict
Windows-1252 conversion, embedded NULs and cleanup of arguments when a subsequent
conversion fails are preserved. Native strings are constructed and consumed by
the original engine, without guessing C++ object layouts or caching native strings.

FMOD/BlitzMovie bindings reuse resolved functions per thread/provider root. Changing
`BLITZCS_ORIGINAL_NATIVE_ROOT` is still observed and a missing root still fails,
even after warming the cache. This deliberately retains the environment lookup
and its overhead. No FMOD function has been reimplemented.

## Measured comparison

.NET 10.0.13 x86, Proton 11.0, real original SCP runtime version 66644 (600
registrations), real FMOD 3.75. Runtime SHA-256:
`ed61cd11cfae77f9f9c47c2ec2e5aa778ff79ddc5ee2f4f7353db4ce6006f39d`.
Baseline uses the bridge before these binding optimizations. Each row is the
median of five warmed rounds in one process; timings are milliseconds and memory
is managed allocation on the calling thread, excluding startup, assertions outside
the timed workload and frame capture. Both versions execute the same benchmark.

| Workload per round | Before ms | After ms | Before allocated bytes | After allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| 100,000 PositionEntity + EntityX + EntityY triplets | 89.089 | 23.391 | 40,800,000 | 0 |
| 10,000 NameEntity + EntityName pairs, accented text and embedded NULs | 30.173 | 12.476 | 6,920,000 | 360,000 |
| 100,000 real FMOD GetVersion queries | 553.322 | 480.037 | 38,000,000 | 17,200,000 |

Before/after timing ranges: numeric 69.957–101.570 / 23.351–24.505; strings
29.129–39.586 / 10.752–22.995; media 552.870–557.912 / 478.638–481.958.
An earlier pair also showed numeric 85.474 → 23.861 and strings 39.946 → 14.352.
These microbenchmarks establish binding cost improvements, **not SCP frame-rate
multipliers**. Native engine allocations are not included in managed-byte counts.

Each successful benchmark additionally verifies empty/long/accented/NUL string
round trips, multiple string arguments, rejecting unencodable text after an earlier
argument was allocated, provider-root validation after a cache hit, forced GC,
and 120 rendered frames. Both captures contain 25,647 nonblack pixels at 640×480,
center RGB (127,127,127), background (0,0,0), and the host exits 0. A separate
correctness run with selected command tracing also passes. The zero-allocation
numeric guard fails on the baseline (40,800,000 bytes) and passes after the change.

## Reproduce

Use an unchanged original runtime and original `fmod.dll` in the same directory.
Build the fixture and publish the host, with separate build output:

```sh
dotnet build tests/benchmarks/OriginalRuntimeBindings/OriginalRuntimeBindings.csproj -c Release
dotnet publish providers/OriginalBlitz3D/Host/Host.csproj -c Release -r win-x86 \
  --self-contained true --artifacts-path artifacts/binding-benchmark-build \
  -o artifacts/binding-benchmark-host
python3 tools/benchmark_original_bindings.py \
  --host artifacts/binding-benchmark-host/BlitzCS.OriginalRuntime.Host.exe \
  --runtime /path/to/original/runtime.dll --output artifacts/binding-measurement \
  --require-zero-allocation
```

On Linux, add `--proton /path/to/proton`; set `DISPLAY`, `PROTON_USE_WINED3D=1`,
`SteamAppId=0`, `SteamGameId=0`, `STEAM_COMPAT_DATA_PATH` to an owned prefix and
`STEAM_COMPAT_CLIENT_INSTALL_PATH` to the Steam installation. The tool converts
absolute Unix paths to Wine Z: paths. Run before/after serially with the same
runtime, display, environment and fixture, without concurrent builds or games.

Every output directory must be new. The runner checks native execution exit,
measured assertions and captured rendered pixels, records input hashes, and saves
raw samples plus summary JSON. Diagnostic host logs go only into that test's
evidence directory; normal game logging remains independently configurable.
To test diagnostic dispatch without timing contamination, use
`--correctness-only --trace NameEntity,EntityName`.

## Optimization ledger

- Typed numeric dispatch: retained; removes all 40.8 MB of warmed numeric allocations
  and improves timing well beyond observed variance.
- Typed string dispatch alone: retained; initial string median 39.946 → 34.472 ms,
  allocation 6.92 → 4.68 MB.
- Cache original string-helper delegates: retained; subsequent median
  34.472 → 13.302 ms, allocation 4.68 → 0.84 MB.
- Stack/pooled input encoding and direct output decoding: retained for the measured
  memory reduction, 0.84 → 0.36 MB. Isolated timing was 13.302 → 14.602 ms; no
  independent speed claim for this step.
- Media root/function cache: retained; final paired media median
  553.322 → 480.037 ms, allocation 38.0 → 17.2 MB.
- Concurrent-build timing run: excluded from the paired comparison.

Remaining costs include necessary native execution/GC transitions, owned result
strings, media environment queries, engine rendering and the managed BASIC program
itself. This experiment does not profile the entire SCP game, remove frame
limiting/focus behavior, bypass validation or optimize native engine internals.

## Compiler emission and JIT optimization

The explicit `.dll` emission path previously hardcoded Roslyn's Debug optimization
level, even when the compiler/host was built with `-c Release`. Inspection of the
compiled SCP assembly confirmed DebuggableAttribute flags `0x107`, including
DisableOptimizations. The native bindings assembly already had optimized flags
`0x2`, so optimizing only bindings could not remove that caller-side restriction.

The emitter now uses Release optimization while retaining portable PDBs and BASIC
source mappings. The rebuilt, unchanged SCP source has flags `0x2` and reached the
rendered 3D room. Regression tests execute emitted code to check JIT optimization
is enabled and verify a thrown error still reports the mapped BASIC source line.
The original SDK cube and native string/bank/GC rendering test also pass after
recompilation through this emitter. The binding microbenchmarks above use an
MSBuild Release fixture in both versions and do not mix in this compiler change.
