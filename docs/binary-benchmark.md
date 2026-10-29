# Binary compiler benchmark (M21)

This document records how much CPU the PE32 -> x64 ahead-of-time translator (`src/BlitzCS.BinaryCompiler`) and loader
(`src/BlitzCS.BinaryCompiler.Runtime`) cost compared with running the same IA-32 machine code natively, what was found by
profiling, which optimizations were made, and where the "within 1 percent" goal is **not** met.

Raw data (all distributions, every run, hardware/OS information): [`docs/evidence/m21-binary-benchmark.json`](evidence/m21-binary-benchmark.json).

For the SCP application result (226 FPS native x86 versus 55 FPS reported for BRE x64), measured bridge activity,
and the limits of extrapolating these microbenchmarks, see the [BRE application cost baseline](binary-runtime-cost.md).

## What is compared

* **Baseline ("original x86")**: one C library (`tools/x86_oracle/bench/bench_lib.c`, `gcc -m32 -O2`) is compiled once. The very
  same machine code is linked (a) into a static ELF32 driver that runs natively on this kernel's IA-32 compatibility mode and
  (b) into a flat image wrapped as a PE32 DLL, translated by the binary compiler and run through `GuestModule` in .NET.
* Both sides execute `w_init(12345)` first and must return **identical checksums** before any timing is reported (checked once
  up front and again on every timed sample; a mismatch fails the run).
* Each workload is sized so that the native run takes about 150 ms (fib about 230 ms), i.e. at least 100 ms.

| workload | what it stresses |
|---|---|
| `int` | integer xorshift/multiply/rotate loop |
| `fnv`, `crc` | FNV-1a and table CRC32 over a 64 KB buffer (loads, multiplies) |
| `mem` | `rep movsd` copy + `repne scasb` scan of 64 KB (string instructions, memory bandwidth) |
| `fib` | recursive fibonacci, call/ret with stack arguments |
| `calls` | non-inlined leaf functions and a 3-deep call chain (call/ret dispatch cost, expected worst case) |
| `switch` | bytecode-style 16-way `switch` (jump table `jmp [table+idx*4]`) |
| `ind` | indirect calls through a function-pointer table (`call reg`) |
| `lz` | LZ-style hash match finder with data-dependent branches |
| `matmul` | 40x40 integer matrix multiply (tight inner loop) |
| `x87`, `sse` | x87 and SSE2 packed multiply-accumulate (x87/SSE are translated 1:1) |

## Method

* Hardware/OS of the recorded runs: Intel Core i5-10400F (6C/12T, Comet Lake), 24 GB, Linux 7.2.9 (Bazzite/Fedora), governor
  `performance`, turbo enabled, no virtualization, .NET 10.0.12, gcc 16.2.1. The machine is a desktop with background
  applications; no isolation (isolcpus) was possible, so single runs can be perturbed (see "Noise" below).
* CPU pinning: the .NET thread is pinned with `sched_setaffinity` and the oracle is started as `taskset -c <same cpu>`.
* Samples: 15 per workload and side, **interleaved** (the order native/translated alternates every sample) after a warm-up on
  both sides. Reported: median, p10, p90, min, max, mean, stddev (see the JSON); "overhead" is `median(translated) /
  median(native) - 1`, the paired per-sample ratio is given as a cross-check.
* Native timing is taken inside the ELF32 driver around the call (`clock_gettime` and `rdtsc`, plus the external wall time of
  the process in the JSON). Translated timing is `Stopwatch` around `GuestFunction.Call` on the pinned thread.
* Counters: `perf_event_open` (no `perf` binary), user space only, hardware cycles / instructions retired / branch misses /
  cache misses. The PMU is exposed on this machine and all four counters worked. The ELF32 oracle opens the counters itself
  (32-bit `int 0x80` syscall 336) and counts exactly the window around the workload, the .NET side counts the pinned thread
  around the call. This needs no child-process wrapper and excludes process start-up on both sides.
* Everything not CPU-bound (ABI latency, load, translation, cold start, memory) is measured separately, see below.

### Reproduction

```bash
bash tools/x86_oracle/bench/build.sh /tmp/blitzcs-m21-bench          # optional: the benchmark builds it on demand
dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench -- \
    --samples 15 --cpu 3 --target-ms 150 --out docs/evidence/m21-binary-benchmark.json
# same iteration counts as a previous run (needed to compare two builds):   --reuse-n previous.json
# one workload only:                                                         --only matmul,calls
# disassemble guest vs translated code of an export (or a text range):      --disasm w_matmul    /  --disasm 600-640
# translation-only robustness check on arbitrary PE32 files:                 --translate-stress a.dll b.dll
```

Requirements: Linux x86-64 with IA-32 emulation in the kernel (for the ELF32 oracle), gcc with `-m32`, `taskset`,
`perf_event_paranoid <= 2` for user-space counters of the own process. If a counter is unavailable the JSON records `null`
for it instead of a value.

## Results (optimized build)

Median of 15 interleaved samples, last recorded run (three optimized runs and five baseline runs are in the JSON, with
per-run overheads in `repeatRunsAfterOptimization` / `baselineBeforeOptimization`).

| workload | n | native ms (median, p10-p90) | translated ms (median, p10-p90) | overhead (median of medians) | paired ratio median [p10..p90] | cycles x | instructions x | branch misses native / translated |
|---|---|---|---|---|---|---|---|---|
| int | 83385231 | 155.8 (154.6-157.2) | 155.5 (154.1-156.9) | -0.2% | +0.4% [-2.0..+1.0] | 1.000 | 1.00 | 56 / 308 |
| fnv | 2312 | 155.1 (153.9-156.7) | 155.5 (153.9-157.0) | +0.3% | +0.3% [-1.1..+1.5] | 1.000 | 1.00 | 2.44e+03 / 4.06e+03 |
| crc | 1141 | 159.6 (155.2-170.4) | 157.2 (155.2-176.3) | -1.4% | +0.6% [-3.2..+5.0] | 1.000 | 1.00 | 1.55e+03 / 2.18e+03 |
| mem | 4370 | 155.8 (151.2-166.8) | 156.8 (153.6-173.0) | +0.7% | +0.3% [-3.3..+11.4] | 0.999 | 1.02 | 5 / 68 |
| fib | 39 | 228.9 (225.9-241.6) | 236.5 (232.1-247.2) | +3.3% | +3.8% [+0.9..+7.0] | 1.021 | 1.22 | 2.29e+06 / 2.41e+06 |
| calls | 34684477 | 162.1 (155.3-178.6) | 169.6 (164.0-187.4) | +4.6% | +4.1% [-2.1..+11.5] | 1.031 | 1.76 | 156 / 614 |
| switch | 18969353 | 156.3 (153.7-159.3) | 165.1 (163.5-170.7) | +5.6% | +6.4% [+4.7..+7.8] | 1.059 | 1.32 | 1.79e+07 / 1.8e+07 |
| ind | 92477710 | 155.8 (153.9-157.2) | 179.5 (178.1-180.7) | +15.2% | +15.8% [+13.7..+16.6] | 1.156 | 1.96 | 1.57e+03 / 6.02e+03 |
| lz | 833 | 148.1 (147.0-150.7) | 149.3 (147.1-151.5) | +0.8% | +0.2% [-1.2..+2.1] | 1.002 | 1.00 | 3.36e+06 / 3.34e+06 |
| matmul | 4010 | 155.6 (154.7-159.3) | 157.7 (156.4-160.5) | +1.4% | +0.9% [+0.2..+2.5] | 1.017 | 1.00 | 6.53e+06 / 6.69e+06 |
| x87 | 41969 | 156.2 (153.9-167.3) | 156.5 (153.8-163.8) | +0.2% | -0.2% [-2.3..+1.6] | 1.000 | 1.00 | 4.21e+04 / 4.21e+04 |
| sse | 294527 | 162.4 (158.2-166.6) | 164.5 (158.2-176.6) | +1.3% | +0.8% [-2.3..+8.8] | 1.000 | 1.00 | 2.95e+05 / 2.95e+05 |

### Before / after the optimizations (same machine, same iteration counts, overhead of the median)

"Before" is the unmodified `CodeGen` of commit 9cc097a built in a separate worktree; values in parentheses are the individual
runs (5 before, 3 after). Dynamic instruction expansion comes from the retired-instruction counters.

| workload | before | after | instructions x before -> after |
|---|---|---|---|
| int, fnv, crc, mem, lz, x87, sse | -0.4 .. +0.1% | -0.2 .. +0.6% (noise) | 1.00 (mem 1.02) -> same |
| fib | +27.1% (26.0 .. 28.1) | +2.8% (1.7 .. 3.3) | 1.57 -> 1.22 |
| calls | +48.0% (47.0 .. 50.1) | +4.6% (4.0 .. 7.0) | 2.58 -> 1.76 |
| switch | +38.8% (38.3 .. 40.2) | +5.6% (5.0 .. 5.6) | 1.58 -> 1.32 |
| ind | +52.9% (52.8 .. 54.4) | +15.9% (15.2 .. 16.1) | 2.63 -> 1.96 |
| matmul | +8.9% (8.1 .. 15.4) | +1.4% (1.3 .. 1.6) | 1.00 -> 1.00 |

### Where the 1% goal is not met

| workload | overhead now | gap to 1% | why |
|---|---|---|---|
| ind | +15.9% | 14.9 points | gcc hoists the table load into a register (`mov edx,[tab+idx*4]` ... `call edx`), so there is no `[table+idx*4]` operand to shadow and the call goes through the generic 9-instruction dispatch; the callee `ret` is predicted statically. |
| switch | +5.6% | 4.6 points | `jmp [table+idx*4]` is 5 host instructions (range check, table base, load, rebase, jump) instead of 1. |
| calls | +4.6% | 3.6 points | guest `call` is 3 host instructions (store return address, adjust ESP, jump) instead of 1 and there is no return-stack-buffer prediction; the `ret` is a compare+branch (2-3 instructions) instead of 1. |
| fib | +2.8% | 1.8 points | same; the return mispredictions of the recursion are equal on both sides (2.4e6 vs 2.3e6), so the gap is instruction count. |
| matmul | +1.4% | 0.4 points | still sensitive to code alignment (before the work: +8..15%). |
| sse / mem / crc | up to +1.3% | within noise | p90 of the paired ratio is higher than +1% on a noisy desktop; medians of the five baseline runs are within +-0.5%. |

All other workloads are within +-1% (int, fnv, crc, mem, lz, x87, sse), i.e. translated straight-line and loop code runs at native
speed: 1 guest instruction becomes 1 host instruction (cycles ratio 1.000), only the encoding is a byte longer because of the
`addr32` prefix. The remaining cost is concentrated in control transfers that cannot be 1:1: call, ret and indirect branches.

## Where the overhead came from (profile before optimizing)

Instruction-count and PMU deltas plus disassembly (`--disasm`) of hot loops with Iced showed:

1. **call/ret through the shared dispatch routine.** `call rel32` = `mov dword [esp-4],ret; lea esp,[esp-4]; jmp f` (3
   instructions) and `ret` = `mov r8d,[esp]; lea esp,[esp+4]; jmp Dispatch` followed by the 9-instruction dispatch
   (`mov r9d,r8d; lea; movd; pminud; movd; mov r8d,[table]; lea; jmp r8`): 12 host instructions for the `ret` alone,
   `calls` retired 2.58x the instructions and ran 1.48x the cycles. The conditional-branch/indirect predictors coped (branch
   misses were not the problem: 5e3 vs 44 over 1e9 instructions); it was front-end/instruction throughput.
2. **The flag-preserving SSE clamp** (`movd`/`pminud`/`movd`) in the dispatch. An unsafe experiment replacing it with `cmp`/`jae`
   (not shipped as is) removed about 15-20 points from `ind`, `calls`, `switch` and `fib`, so it is worth a dedicated path
   (see O2: used only when no dispatch target consumes flags).
3. **Code alignment.** `matmul`'s inner loop (21 bytes, gcc-aligned to 16 at its guest address) landed across a 32-byte fetch
   window in the translation although instruction counts were identical (cycles x1.08..1.16, 8-15% slower). 15 alternating
   samples with and without padding: +7.8..9.0% vs +2.0..2.3% (before the fixed-point/32-byte refinements of O1).
4. **Not a problem:** `addr32` prefixes (no measurable cost: cycles x1.000 on `int`/`fnv`/`crc`/`lz`), push/pop lowering in
   straight-line code (already merged in Release), x87/SSE (1:1), `rep movs/scas` (1:1).

## Optimizations made (all in `src/BlitzCS.BinaryCompiler/CodeGen`)

Each one is covered by regression tests (`tests/BlitzCS.BinaryCompiler.Tests/DispatchOptimizationTests.cs`, plus the existing
Debug-vs-Release, flag-survival and oracle-differential tests); the full binary-compiler suite (165 tests) and the CLI suite
(392 tests) pass. Nothing relaxes a safety check: an untranslated or out-of-range target still traps, and every shortcut
falls back to the unmodified generic path for anything it does not recognise.

* **O1 - alignment preserved.** Branch targets that the compiler preceded with NOP padding (loop heads, function entries) are
  aligned in the translation (16 bytes, or 32 for loops that fit one 32-byte window); the compiler's padding NOPs are dropped
  in favour of one multi-byte NOP sized for the translated layout; function entries nothing falls into are also aligned
  (free, and it keeps the layout from drifting). Padding is iterated to a fixed point; pads only grow, which makes the
  branch-relaxation interaction converge (without that rule large images oscillated and never converged). matmul +8.9 -> +1.4%.
* **O2 - flag-clobbering bounds check where flags are provably dead.** `ret`, `call [mem|reg]` and recognised table `jmp`
  inline the dispatch and use `cmp`+`jae` instead of the SSE clamp **iff** no statically known dispatch target (every call
  return site, every non-speculative function entry) consumes the arithmetic flags it is entered with
  (`DispatchTargetsIgnoreFlags`); otherwise the exact flag-preserving dispatch is kept (e.g. `stc; call f; sbb eax,eax`). Calls
  and returns are assumed not to take flags as input (the i386 ABIs leave EFLAGS undefined across calls). **This is an
  assumption the maintainers should review**: it is violated only by code that enters a function through a computed address
  with flags live, which no ABI-conforming compiler emits; relocation-derived pointer candidates are excluded from the check
  because they cannot be told apart from data tables inside `.text`.
* **O3 - static return-site prediction.** `ret` first compares the popped address with up to 4 return sites known at
  translation time and jumps straight to the translated site (the conditional predictor handles it per site); more callers
  than that means no prediction. Anything else goes through the generic dispatch.
* **O4 - fused argument pushes and call.** `push a; call f` shares one ESP adjustment.
* **O5 - host-offset shadow tables.** `jmp/call [table+idx*4]` with the table in a non-writable section and relocated pointers
  to translated blocks uses an in-code table of host offsets after an `idx < count` check; the translator lays code out until
  the layout is a fixed point so the tables are exact, and the generic path handles any other index (including 32-bit address
  wrap-around). switch +38.8 -> +5.6%.

Measured step by step (single 9-sample runs, not contemporaneous with the baseline, shown only to document the order of
magnitude of each step): inline dispatch alone ind -9 points; cmp/jae with dead flags fib 27 -> 18, calls 40 -> 28, switch 34 ->
19, ind 48 -> 38; return-site prediction fib 18 -> 10, calls 28 -> 5; push+call fusion fib 9 -> 1; shadow table switch 20 -> 10.

### Costs of the optimizations

Translated code is larger and takes longer to produce. Over 25 real PE32 files (0.1-2 MB, translation only): median host code
size x1.24, median translation time x1.56 (comctl32 1.1 s -> 2.0 s, d3dcompiler 2.2 s -> 4.6 s), diagnostics unchanged and no
exceptions (`translatorRobustness` in the JSON). For the 2.9 KB benchmark library host code grows from 3.7 KB to 5.2 KB.
The translation cache key (`CompilerVersion = 0.1.0`) does not change with the generated code: cached artifacts from before this
change stay valid but unoptimized, so bumping `BinaryCompilerOptions.CompilerVersion` is recommended.

## Non-CPU measurements (this run)

| item | value |
|---|---|
| Empty export call (managed -> guest -> managed) | 110-135 ns median across runs (this run 135, p10 133, p90 139) |
| Export call with three arguments | 113-145 ns |
| Import callback (guest -> managed -> guest return, trivial handler, amortized in a guest loop) | 48-61 ns per callback |
| Nested: import callback whose handler re-enters the guest (`nop_fn`) | 138-166 ns per iteration, i.e. about 90-105 ns for the nested managed -> guest call |
| Reference | unmanaged function pointer call 2.0 ns; native ELF32 call through a pointer 1.4 ns; uncontended `Monitor` enter/exit 17-19 ns; recursive `ReaderWriterLockSlim` read lock enter/exit 21-28 ns |
| `GuestModule.Load` | 0.30 ms median (p90 0.49) for a 750 KB host image; RSS delta of one loaded module 1.07 MB |
| Host image | 762,208 bytes total = 8,192 code + 754,016 RW (dispatch table = 4 bytes per RVA of the 188,416-byte guest image); original code 2,900 bytes |
| AOT translation, cold (parse + translate + cache write) | 16.7 ms median (first sample 148 ms includes JIT); PE parse alone 0.5 ms |
| Cache hit | 2.4 ms median |
| Whole-process cold start | .NET host start 25.7 ms; process that reads the cached artifact, loads it and makes one call 76 ms (about 50 ms above the runtime floor, mostly JIT of the translator/loader paths); native ELF32 process launch 0.8 ms |

The empty-call latency is dominated by managed-side synchronization in `GuestModule.Invoke` (a recursion-supporting
`ReaderWriterLockSlim` plus a `Monitor` account for roughly 40-50 ns of the ~110-135 ns), not by the guest entry/exit stub.
That is a loader topic and was not changed here.

## Noise and caveats

* Desktop machine with background applications: one earlier optimized run had a single workload (fib) at +25% because of a
  background burst (the native p90 of that run was 294 ms vs 238 ms in the others); it was repeated, not dropped. Compare
  runs through `--reuse-n` and look at the cycles ratio, which is less sensitive than wall time.
* Code layout dependence: matmul, calls and fib move by a few percent when unrelated code earlier in the image changes size.
  The host image is mapped at a random address (ASLR) while the native ELF32 is not PIE, so branch-predictor aliasing can differ
  from process to process.
* The baseline is the same machine code run in the kernel's IA-32 compatibility mode, not a Windows process, and it measures a
  statically linked freestanding C library; a real Blitz3D-era DLL with imports will additionally pay the managed import cost
  above on every host call.
* "Within 1 percent" is a goal for straight-line and loop code (met) and not for call/indirect-heavy code (not met, see above).

## Remaining work

* Native `call`/`ret` pairing on a separate host stack (guest ESP in another register) would restore return-stack-buffer
  prediction and remove most of the remaining call/ret cost; it is an invasive change of the register convention.
* Register-indirect calls loaded from tables (`mov edx,[tab+idx*4]; call edx`) could use the shadow-table idea if the load is
  tracked back to the table, or profile-guided inline caches (`OptimizationProfile.ReleasePgo` is unused).
* Remove the last redundant `mov r9d,r8d` in the dispatch by letting the trap routine derive the original address.
* Reduce translation time (fewer layout passes, incremental per-function layout) and code growth (do not inline the dispatch
  at cold return sites).
* Loader: `GuestModule` never reads the trap record (`CtxTrapKind`/`CtxTrapAddress`), so a trap (return to an untranslated
  address, unsupported instruction) makes the export return 0 instead of raising an exception; tests currently observe it as a
  zero result.

## Multi-module change (rebasing, extended import return)

Rerun with identical iteration counts (`--reuse-n`, 9 samples, cpu 3) before (HEAD) and after the multi-module work, same machine, interleaved native/translated
samples: the instruction-count ratios are identical for every workload (int 1.00, fib 1.22, calls 1.76, switch 1.32, ind 1.96, matmul 1.00 ...), and no workload
got slower relative to its native baseline (translated/native: int 1.00 -> 1.00, calls 1.24 -> 0.97, ind 1.32 -> 1.16, matmul 1.07 -> 1.02; the absolute times of the two
runs differ because the machine was not isolated). The only generated-code change is `test r9d,r9d; js` in each import body, which these workloads do not execute.
Rebased builds have the same layout as preferred-base builds (`RebaseTests.RebasedCodeHasTheSameShapeAndRunsIdenticallyAtBothBases`).

## Cross-module dispatch (artifact format 5)

The miss routine at host offset 0 gained the registry lookup; the hit path of ret, indirect call and jump-table dispatch is untouched. Instruction-count ratios of the four dispatch-heavy workloads are identical before and after (fib x1.22, calls x1.76, switch x1.32, ind x1.96).
Wall time (`--samples 9 --cpu 3 --target-ms 150`, a loaded desktop, overhead = translated/native - 1):

| workload | before | after (run 1) | after (run 2) |
|---|---|---|---|
| fib | 1.3 % | 7.2 % | 2.5 % |
| calls | -1.9 % | 7.2 % | 3.1 % |
| switch | 4.9 % | 6.5 % | 5.8 % |
| ind | 15.3 % | 16.9 % | 15.5 % |

The spread between two runs of the same build is as large as the change, so no effect is claimed beyond "no extra instructions on a hit".


### Foreign dispatch with many modules (compiler 0.14.1)

The per-module MRU cache removes the registry walk when repeated foreign targets belong to the last matched module.
The opt-in benchmark loads a consumer first and a provider last, calls the final provider through a guest function pointer,
and validates the checksum before and during measurement. Calls return through the provider's foreign dispatch too.
This measures the complete guest invocation, including managed call overhead, rather than just the emitted lookup instructions.

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench -- --registry-dispatch --registry-iterations 100000 --samples 9 --cpu 3
```

Paired measurements on Linux x64, pinned to CPU 3, 100,000 calls per sample and nine samples:

| Registry records | Linear scan, median ns/call | MRU cache, median ns/call |
|---|---:|---:|
| 2 | 210.1 | 198.6 |
| 17 | 206.8 | 211.0 |
| 65 | 243.4 | 222.9 |
| 257 | 342.6 | 224.2 |
| 1025 | 802.2 | 225.3 |

At 1,025 records the complete invocation is about 3.6 times faster (72% less time). A preceding paired run measured
782.2 → 207.9 ns/call at that size, confirming that the improvement exceeds the observed variance. Small-registry
results fluctuate, so no general speedup is claimed there. This steady-state workload favors the one-entry cache;
alternating targets from different modules can still require linear scans. These numbers do not establish the game's total loading time.
Full distributions for the table are in [the registry cache evidence](evidence/m21-registry-dispatch-cache.json).


### Newest-first fallback for changing foreign targets (compiler 0.14.2)

A one-entry cache misses when calls alternate between modules or continually enter newly translated units.
The fallback now snapshots the published registry count and scans from the newest record toward the oldest.
It still visits every record for an absent target; this favors recent modules rather than guaranteeing constant-time lookup.

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench -- --registry-dispatch-alternating-late --registry-iterations 10000 --samples 5 --cpu 3
```

This workload alternates function-pointer calls between the final two providers and checks the result of each call.
Paired Linux x64 measurements, pinned to CPU 3, include complete guest invocation overhead:

| Registry records | MRU + oldest-first, median ns/call | MRU + newest-first, median ns/call |
|---|---:|---:|
| 3 | 205.37 | 221.66 |
| 17 | 217.04 | 214.54 |
| 65 | 252.31 | 215.17 |
| 257 | 404.10 | 213.30 |
| 1025 | 1016.28 | 208.81 |

At 1,025 records this workload takes about 4.9 times less time. The smallest registry does not improve.
Targets near the beginning can take longer on a cache miss, so this is a workload-specific improvement.
These measurements do not establish total application loading time.
[Full distributions](evidence/m21-registry-newest-first.json).


### Repeated managed guest-memory validation

`GuestMemory.Find` remembers the index of its last matching own region. Each lookup resolves that index against
its current immutable region-array snapshot, checks the complete requested range, and checks the region's current
read/write permissions. A removal or index shift cannot retain a removed region object. The measurements in this section isolate the lookaside with the original linear own/peer fallback. Zero-length
boundary queries retain the original path. The indexed fallback described below additionally improves cache misses. This relies on the existing
nonoverlapping live mappings, whose platform reservations reject address conflicts.

A Linux x64 harness loaded a small guest module, held 512, 2,048, or 5,000 actual `GuestMemory.Allocate(4096)`
buffers, then repeatedly read or wrote a UInt32 in the oldest and newest buffers. Each measurement contains
100,000 operations per sample and three samples, with default .NET tiering. Correct values were checked.

| Held buffers | Newest read before, median ns | Newest read after, median ns |
|---|---:|---:|
| 512 | 533 | 40 |
| 2048 | 2101 | 40 |

At 5,000 buffers, newest-buffer writes fell from 21,097 to 42 ns. Oldest-buffer reads remained about 36–47 ns.
In this intermediate configuration, unmapped-address exceptions still scanned the regions and peer lookups were unchanged. These repeated-own-region
measurements do not establish game loading time or identify a previously unsymbolized native CPU sample.
The same workload in a self-contained Windows x64 harness under Proton/Wine measured newest reads of
544 → 49 ns (512 buffers), 3,030 → 41 ns (2,048), and 10,589 → 41 ns (5,000). Newest writes at 5,000
buffers measured 8,250 → 44 ns. Oldest-region accesses stayed near 41–45 ns at that size. The zero-buffer
case increased from 58 → 75 ns for reads and 84 → 130 ns for writes; unmapped-address exceptions at 5,000
buffers increased from 10.2 → 13.3 µs. The hint improves repeated successful late-region lookups, not every workload.
Windows runs used default tiered compilation with `DOTNET_TieredPGO=0`.

Tests cover permission changes (including unreadable pages), removal/index shifts, replacement allocations, and concurrent
access to distinct buffers. [Full samples](evidence/m21-guestmemory-region-cache.json).


### Indexed validation for changing regions and peer pointers

Positive-length `GuestMemory.Find` queries use a sorted region snapshot on a lookaside miss, including lookups
in peer modules. Binary search finds the last range starting at or before the requested address; the entire range
must fit, and current read/write permissions are checked before access. Live mappings are nonoverlapping.

The insertion-order and sorted arrays are published together through one immutable snapshot. Add inserts into the
sorted array and appends to the ordered array; removal updates both, under the existing mutation lock. These
mutations copy one additional reference array, retaining O(N) mutation cost. Queries of zero length keep the
insertion-order behavior, including its boundary rules. Existing `FindRegion` metadata queries retain their original path.
The change addresses varied accesses that repeatedly miss a one-region lookaside, particularly own-to-peer pointers.


A paired self-contained Windows x64 benchmark under Proton/Wine loaded two linked modules, each holding the
same number of 4 KiB guest buffers. The same executable ran with the linear-fallback and indexed runtimes.
Each row measures 10,000 reads per sample, three samples, after a 10,000-read warm-up; tiered compilation and
Tiered PGO were disabled. Values were checked for correctness.

| Buffers per module | Alternating oldest/newest own, before → after ns/read | Alternating own/peer newest, before → after ns/read | Repeated peer newest, before → after ns/read |
|---|---:|---:|---:|
| 512 | 317 → 70 | 622 → 71 | 1252 → 90 |
| 2048 | 1331 → 73 | 3769 → 76 | 7746 → 97 |
| 5000 | 4625 → 77 | 9009 → 80 | 18015 → 103 |

At 5,000 buffers per module, repeated peer reads took about 174 times less time. This workload tests the lookup
cost, not total application loading. The `FindCovering` helper implements the midpoint-based range search.
[Full paired samples](evidence/m21-guestmemory-indexed.json).


An end-to-end New Game validation in SCP – Containment Breach (unmodified BASIC sources, Windows x64 package
under Proton/Wine) reached the continue prompt in 26:01 with the linear fallback and 3:47 with indexed validation.
Actual first-person 3D scenes were verified at 26:24 and 4:01 after Start, including prompt confirmation; camera
movement visibly changed the scene, and neither run logged a runtime exception. These are single observed runs
with different random seeds, not a controlled same-seed benchmark or a steady-state frame-rate result.
[Validation timings and limits](evidence/m21-scpcb-indexed-loading.json).


### Command entry and bounded SSE2 string scans

`GuestModule.InvokeRva` publishes its RVA lookup dictionary once, after initialization under the existing resource lock. Subsequent readers use the immutable published table without taking that lock. Module lifetime, stack checks and callback handling retain their existing guards. `BinaryCommandExport` uses a stack buffer for up to 32 argument words, with the existing heap-buffer path for larger calls. Each slot is filled before invocation.

`GuestMemory.ReadAnsiString` scans for NUL in 16-byte blocks using SSE2 equality and byte masks. A vector load requires the complete window to fit a readable guest region and the allowed length. Scalar reads handle the boundary and preserve the first failing address. Potential 32-bit address wrap uses the original byte-wise path. Latin-1 span decoding replaces character-by-character string construction. SSE2-disabled hosts retain scalar scanning; x87 extended-precision behavior is preserved. Tests cover alignments, terminator lanes, multiple vector blocks, high bytes, inclusive length limits, unreadable pages, disposed mappings and concurrent first address-map initialization.

The reproducible modes are:

```sh
DOTNET_ReadyToRun=0 COMPlus_ReadyToRun=0 DOTNET_TieredPGO=0 \
  dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench -- \
  --ansi-string-scan --cpu 3 --samples 5 --iterations 10000

DOTNET_ReadyToRun=0 COMPlus_ReadyToRun=0 DOTNET_TieredPGO=0 \
  dotnet run -c Release --project benchmarks/BlitzCS.BinaryCompiler.Bench -- \
  --command-entry-cost --cpu 3 --samples 5 --command-entry-iterations 100000
```

For paired measurements, build the harness once and invoke it with `--no-build`, replacing only its Runtime assembly between runs. The string mode reports the hash of the loaded assembly. The same harness ran three alternating baseline/candidate rounds on a pinned i5-10400F Linux core, with five samples per round. ReadyToRun was disabled for both assemblies so the Windows-published baseline could load on Linux; TieredPGO was disabled and tiered compilation used the .NET default. These settings and the raw samples are part of the evidence.

| String length, bytes | Baseline median ns/read | Revised median ns/read | Allocated bytes/read, before → after |
|---|---:|---:|---:|
| 8 | 510 | 286 | 144 → 40 |
| 24 | 1001 | 321 | 280 → 72 |
| 128 | 1198 | 512 | 824 → 280 |

The complete reader used 44–68% less time in these samples. This includes both the vector scan and changed decoding. The synthetic RVA-entry median changed from 252 to 218 ns/call; the pre-bound control varied from 364 to 379 ns/call and the guest-side loop from 1.51 to 1.59 ns/iteration. These results isolate small runtime paths and do not establish a whole-game loading or frame-rate improvement. [Full paired samples and assembly identities](evidence/m21-command-entry-sse2.json).

### Repeated INI parsing and command boundaries

An isolated parser fixture performed 288 queries against an 18,834-byte INI, producing 2,870,012 `PeekByte` and 2,793,014 `Chr` calls. The parser repeatedly scans a cached bank from its beginning. Small input size therefore does not imply little CPU work. Native x86 and translated x64 produced the same counters and checksum; their command boundary costs differed substantially.

| Configuration | Median query pass, ms |
|---|---:|
| Native x86 release | 809 |
| Translated x64 reference | 15,537 |
| Earlier experimental direct string bindings | 7,046 |
| Final guarded x64, normal bindings | 545 |

The earlier direct-string experiment used an explicit catalog with strict Unicode validation; it remains experimental. The final result instead uses the unchanged normal generated bindings. Guarded ASCII implementations handle `Left`, `Right`, `Mid`, `Upper`, `Lower`, `Trim`, `Instr`, `Asc` and `Len`. Non-ASCII input, invalid arguments and unverified cases retain native dispatch. Embedded NUL remains native for range/case/trim operations without independent native evidence; the verified `Len`, `Mid`, `Instr` and `Asc` cases preserve it.

Three final alternating native/x64 rounds measured native query times of 817/804/809 ms and optimized times of 544/545/572 ms. The optimized median used **32.63% less elapsed time than native x86** and 96.49% less than the earlier translated reference. Each run produced the same counters and checksum. This establishes negative elapsed overhead for this isolated workload, with normal bindings; it does not establish zero overhead for every command, full-game loading or rendering. Bank loading and section enumeration were excluded from the timer; counters and the checksum were included. [Fixture identities, protocol and samples](evidence/m21-ini-command-cost.json).

The verified runtime specialization keeps the normal bindings. `Chr` reuses 256 strings decoded by the command bridge's own CP1252 encoding. Its result references permanent managed strings, requiring no guest-memory lifetime pin. `PeekByte` can reuse a native-validated bank identity, payload and bounds while a process-wide mutation epoch remains unchanged. A short read lease pins the payload against guest execution, host writes, protection changes and teardown, replacing a redundant module lifetime lock on that hit path. Any uncertain case takes the native command path, including a thread without initialized guest state, a changed epoch, invalid bounds or an unrecognized runtime image. The specialization is restricted to an exact source-image SHA-256 and known command RVAs; successful fingerprint checks are reused only for the same module instance.

Guest writers remain concurrent. Nonalertable host waits release only the waiting thread's writer admission and restore it before guest execution resumes; nested callbacks reacquire admission. A guarded host write already covered by that thread's guest-writer lease needs no second writer transition. The distinction matters: redundant nested transitions originally invalidated every bank snapshot, adding validation calls without saving any translated reads. Tests cover writer/read exclusion, nested callbacks, interrupted admission, mutation invalidation, released payloads, fresh threads and teardown.


### Allocation-free array indexing in generated game code

Generated accesses to BASIC arrays of rank one or two now pass indices as individual arguments rather than allocating an `int[]` for each read or write. The storage helper retains per-axis bounds checks, source-location diagnostics, and left-to-right, single evaluation of index expressions. Higher ranks retain the general indexing path; dimensioning is unchanged.

The Release-compiled regression `FixedRankArrayAccessAvoidsPerAccessIndexArrayAllocations` warms both helpers, then measures two 100,000-iteration read/write loops with `GC.GetAllocatedBytesForCurrentThread`. Before specialization each loop allocated 6,400,032 bytes; after specialization both allocate zero bytes, with checksum 6,600,000 unchanged. Separate tests cover index evaluation order, bounds diagnostics and the higher-rank fallback. This establishes removal of temporary index arrays, not a measured whole-game FPS improvement.


### Optional runtime and COM profiling

Set `BLITZCS_PROFILE_COMMAND_PATH` to an output JSON filename to record command counts, inclusive/self elapsed ticks and bounded frame windows. The first top-level `Flip` establishes the frame baseline; only its owning thread contributes frame intervals. Allocation deltas include instrumentation, and process-wide GC collection deltas do not measure GC pause duration. Set `BLITZCS_PROFILE_COM_PATH` separately to record adapted COM calls, native-inclusive time, scratch allocations and guest-to-host copy bytes. Native-inclusive time can include nested calls; do not add overlapping intervals as independent CPU time. Reports use Stopwatch timestamps and UTC anchors for correlation and retain bounded recent windows.

Profiling changes execution cost. Compare optimizations with these variables unset, using the same executable, assets, scene, warmup and timing budget. Use instrumented runs to locate work and count operations rather than to report production FPS. For an external instruction-pointer sample, `BLITZCS_PROFILE_COMMAND_METADATA_ONLY=1` together with the command output path exports module identities and guest/host translation maps while leaving command/frame timing disabled. Metadata contains virtual addresses for the captured process instance; it is not a portable symbol address table.


### Direct COM input copies without intermediate arrays

Input marshalling previously called `ReadBytes` to allocate a managed array, then copied it into isolated native scratch memory. `NativeScope.CopyFromGuest` reads into the native scratch span directly. It retains a private copy for the host call, preserves zero-filled allocation behavior, frees scratch memory on read failure, and leaves output copy-back rules unchanged. GUIDs, input/in-out structures, counted arrays, vertex inputs and clear rectangles use the direct path. The important render path is `DrawIndexedPrimitiveVB`: its counted **index** array contains 16-bit indices, rather than vertex data. The procedural fixture's 141,312 triangles account for 847,872 index bytes per frame.

The public fixture [render-grid.bb](../benchmarks/fixtures/render-grid.bb) renders 64 independently transformed spheres at 800×600, warms 120 frames, then times `UpdateWorld`, `RenderWorld` and `Flip 0` for at least ten seconds and 600 frames. Triangle counts and pixel samples are checked outside the timed interval. The same executable ran six alternating baseline/candidate samples with profiling disabled and competing builds/tests held.

| Pair | FPS across three runs | Median frame time |
|---|---|---:|
| Baseline | 1,926 / 1,928 / 1,935 | 0.518807 ms |
| Direct-input candidate | 2,524 / 2,495 / 2,445 | 0.400737 ms |

The candidate median has about **29% more FPS and 23% less frame time**. All six runs produced 141,312 triangles, 14 non-background pixel samples and the same pixel checksum. This synthetic result validates the candidate pair for this workload; it does not predict the game's frame rate. An earlier single native-x86 control produced 3,248 FPS, so the candidate still does not establish negative overhead for this rendering workload. [Protocol, assembly hashes and all six samples](evidence/m21-com-input-copy.json).
