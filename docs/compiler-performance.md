# Compiler execution performance

## Enable optimization in emitted assemblies

The explicit DLL emission path uses Roslyn Release optimization, retaining portable
PDBs and BASIC source mappings. Building the compiler with `-c Release` alone did
not previously accomplish this: emitted games carried DebuggableAttribute flags
`0x107`, including DisableOptimizations. Optimized output carries `0x2`. Execution
tests check the JIT flag and source locations in runtime error stack traces.

## Allocation-free array indexing

Before this change, every `Dim` array access emitted an `int[]` containing its
indices. A read and a write in a loop therefore allocated two buffers per iteration,
regardless of array size or whether its contents changed.

Array accesses now pass collection expressions to a scoped `ReadOnlySpan<int>`.
Roslyn can store dynamic indices in stack locals and constant indices in static
data, without heap index buffers. This avoids explicit `stackalloc` inside loops,
which could repeatedly grow the stack. Array storage, dimensional layout, inclusive
bounds, default values, re-dimensioning and reference-return assignment are unchanged.
The scoped parameter cannot escape through the returned reference. Dimensioning
still allocates its actual storage and metadata; this optimization applies to access.

All index expressions still evaluate once, left to right, before the assigned value.
Dimensioning and per-axis bounds checks, with the array name and BASIC location,
remain enabled. This does not bypass language semantics or modify original sources.

### Measured workloads

`tests/benchmarks/compiler-arrays.bb` performs one million two-dimensional writes
and reads per measured round. It warms the generated BASIC function first, checks
the result, and records elapsed time and current-thread managed allocations. Both
baseline and optimized assemblies use the same Release emitter and fixture.
Baseline compiler: `3118845`, before the array access change.

.NET 10.0.13 Linux x64, three alternating before/after process pairs, five rounds
per process (15 samples per version), without concurrent builds:

| Metric per round | Before | After |
| --- | ---: | ---: |
| Median time | 16.2591 ms | 6.2327 ms |
| Observed range | 15.2540–18.4527 ms | 6.1136–7.1079 ms |
| Managed allocated bytes | 64,000,000 | 0 |

A separate Windows .NET 10.0.13 x86 / Proton 11.0 test runs the same compiled
fixture through the real original-runtime host, with engine startup outside the
measurement. An idle paired run measured 18.8791 → 12.0565 ms and 40,000,000 → 0
allocated bytes. A second idle pair measured 18.9960 → 11.4095 ms, again with zero
index-buffer allocations. A prior x86 allocation-verification run overlapped package
publishing; its timings are excluded. None of these microbenchmark ratios are SCP
frame-rate multipliers.

To reproduce, compile the **same fixture** with each compiler into separate output
directories, then execute those saved assemblies serially:

```sh
dotnet build src/BlitzCS.CLI -c Release
dotnet run --project src/BlitzCS.CLI -c Release --no-build -- \
  build tests/benchmarks/compiler-arrays.bb -o artifacts/compiler-perf/after/arrays.dll
dotnet artifacts/compiler-perf/after/arrays.dll
```

Output is JSON with five timing/allocation samples; a wrong checksum fails execution.
For x86, supply the saved assembly to the existing original-runtime host's `--run`
command with the unchanged original `runtime.dll`. Keep the same host, DLL and Proton
configuration for both versions. No engine commands are substituted.

### Regression coverage and original applications

Array tests verify zero allocation for 100,000 read/write pairs in one, two and five
dimensions, including the long-loop stack behavior. Existing integer/float/string,
object, inclusive-bound, re-dimensioning, loop-counter and invalid-index tests still
pass. A side-effecting index/value test checks evaluation order explicitly.

The unchanged SCP:CB source recompiles with this change. Its generated code contains
1,305 array-access call sites: every corresponding temporary heap index-array site
is replaced, without changing the BASIC inputs. Execution reached the initial 3D
room after New Game with the original engine and HUD. Headless rendering was used
only for functional validation; desktop GPU execution was checked separately.
This static count is not a claim
about how many accesses execute per frame. The compiler's default Windows x64
package target and Linux-hosted operation remain separate from the experimental
original engine's Windows x86 requirement.

## Related measurements

See [original-runtime binding performance](../providers/OriginalBlitz3D/PERFORMANCE.md)
for typed native calls, string ownership/conversion and real-engine rendering tests.
