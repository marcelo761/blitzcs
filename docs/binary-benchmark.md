# Binary compiler benchmark (M21)

This document records how much CPU the PE32 -> x64 ahead-of-time translator (`src/BlitzCS.BinaryCompiler`) and loader
(`src/BlitzCS.BinaryCompiler.Runtime`) cost compared with running the same IA-32 machine code natively, what was found by
profiling, which optimizations were made, and where the "within 1 percent" goal is **not** met.

Raw data (all distributions, every run, hardware/OS information): [`docs/evidence/m21-binary-benchmark.json`](evidence/m21-binary-benchmark.json).

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
