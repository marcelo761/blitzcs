# BRE application cost baseline

Recorded on 2026-10-28, before implementing a successor strategy. The intended direction is to replace the BRE for this runtime workload and subsequently deprecate it. This record neither selects that replacement nor removes, disables or formally deprecates the existing implementation.

## Application result

SCP - Containment Breach v1.3.11 showed **226 FPS on native BlitzCS x86**, compared with the user's earlier **55 FPS on BRE x64**. The user reported the same room, camera position and scenario. The x86 screenshot still displayed an incorrect, hardcoded `Runtime x64` caption; the tested Win32 executable was independently identified as PE32/i386. A caption alone is not an architecture check.

| Quantity | Native x86 | BRE x64 |
|---|---:|---:|
| Observed/reported FPS | 226 | 55 |
| Implied time per frame, `1000 / FPS` | 4.42 ms | 18.18 ms |

The implied difference is **13.76 ms/frame**, a **4.11× frame-time ratio**, **310.9% additional frame time**, or **75.7% fewer FPS**. These are arithmetic conversions of HUD/reported FPS, not directly measured frame-time distributions. They describe the observed application gap; they do not isolate the translator's CPU cost.

The pair was not an automated alternating benchmark. Exact executable identities, matched graphics settings, warmup, distributions and run-to-run variation were not captured for both observations. Treat it as a regression reference and motivation for replacement, not a controlled attribution of every extra millisecond. [Curated measurements and provenance](evidence/bre-scpcb-cost-baseline.json).

The user also reported prolonged loading at **45%**. A later x64 smoke reproduced a long stay at that progress value, but no controlled x86/x64 loading-time pair was completed. There is no measured loading slowdown ratio and no established attribution to storage throughput. The progress percentage itself does not identify a unique operation.

## What the implementation adds

The x64 application executes native x64 instructions emitted from the original PE32 runtime. It is not executing every instruction through a generic interpreter, and it is not a source-level rebuild of the engine for the x64 ABI. The original DLL can remain PE32 on disk while its translated code executes in a PE32+ host process.

The following costs are present in the implementation; their individual shares of the 13.76 ms application gap have not been measured:

| Layer | Additional work in the BRE path | Implementation |
|---|---|---|
| Translated control flow | Guest returns and indirect branches can require address lookup and a jump to translated code. Eligible return sites have a prediction fast path; a normal native `CALL/RET` sequence is not preserved universally. | `X64CodeGen.EmitDispatchPreservingFlags`, translated `Retnd` handling in [X64CodeGen.cs](../src/BlitzCS.BinaryCompiler/CodeGen/X64CodeGen.cs) |
| Command entry | Commands enter the guest execution context, acquire lifetime protection, arrange guest stack/thread state and marshal arguments/results as required. Native x86 also has managed/native interop, so its entire interop cost must not be counted as BRE-specific. | `BinaryCommandExport.Run`, `GuestModule.InvokeCore` in [BinaryLibrary.cs](../src/BlitzCS.BinaryCompiler.Runtime/BinaryLibrary.cs) and [GuestModule.cs](../src/BlitzCS.BinaryCompiler.Runtime/GuestModule.cs) |
| Host graphics bridge | COM interfaces, pointer widths and structures cross the guest/host boundary. Vertex-buffer Lock allocates a guest shadow and copies host data; Unlock copies writable data back and disposes that shadow. | `LockVertexBuffer` and `UnlockVertexBuffer` in [Direct3DAdapters.cs](../src/BlitzCS.BinaryCompiler.Win32/DirectX/Direct3DAdapters.cs) |
| Bridge memory bookkeeping | Shadow allocation/removal updates mapped-region metadata. Earlier full-array snapshots made updates scale with region count; a persistent-tree candidate reduces metadata copying, but has no demonstrated whole-game FPS gain in this record. Bounds/lifetime checks on managed bridge accesses also remain work. | [GuestMemory.cs](../src/BlitzCS.BinaryCompiler.Runtime/GuestMemory.cs) |

These costs occur at different frequencies. Improving an arithmetic loop or adding SIMD does not by itself remove command crossings, translated branch lookup, COM adaptation or buffer copying. The evidence does not establish x87 arithmetic as the dominant bottleneck, nor does it establish one graphics bridge operation as the sole cause.

## Instrumented gameplay evidence

A separate BRE command profile retained 112 consecutive gameplay windows: **5,351 frames in 113.197 seconds**, averaging **47.27 FPS / 21.15 ms per frame**. Later pause/menu windows were excluded. This is an instrumented result, distinct from the user's 55 FPS reference and the 226 FPS native screenshot.

| Command | Calls in the selected interval | Inclusive measured time |
|---|---:|---:|
| RenderWorld | 10,702 | 20.68 s |
| EntityDistance | 1,641,009 | 14.04 s |
| EntityX | 2,037,272 | 8.92 s |
| HideEntity | 1,673,334 | 7.62 s |
| SetAnimTime | 21,404 | 6.87 s |
| FileType | 10,706 | 6.18 s |
| EntityZ | 1,426,864 | 6.09 s |
| EntityY | 990,916 | 4.27 s |
| EntityVisible | 15,396 | 4.20 s |
| UpdateWorld | 5,351 | 2.99 s |

`EntityX/Y/Z`, `EntityDistance` and `HideEntity` alone average approximately **1,452 calls/frame**. This identifies frequently crossed boundaries, but the times include both useful runtime work and wrapper/instrumentation cost. They cannot be subtracted from native frame time as pure overhead or added across nested measurements without checking overlap.

The profile also recorded approximately **9.80 GB of frame-owner managed allocations**, about **86.6 MB/s**, and **1,560 process-wide Gen0 collection deltas**. The allocation measurement explicitly includes profiler overhead; collection counts are not pause durations. There is no equivalent native allocation capture, so these numbers do not establish production allocation rate or explain the complete FPS gap.

The evidence JSON preserves the capture fingerprint, exact source runtime fingerprint, selected interval, counters and unrounded aggregates. It bundles curated results rather than the full raw capture. Sampled instruction pointers were too sparse, and host/Wine mappings insufficiently resolved, to provide a reliable per-layer CPU attribution.

## What previous improvements establish

The [binary benchmark](binary-benchmark.md) records isolated CPU, command, memory and graphics results. Some arithmetic workloads approach native speed; an isolated INI workload achieved negative elapsed overhead. A synthetic COM-copy workload improved candidate FPS by about 29%. Those results retain their workload-specific meaning.

They do **not** establish zero or negative overhead for SCP gameplay or loading. The user reported no corresponding whole-game improvement from the previous candidate and continued to observe approximately 55 FPS and prolonged 45% loading. No whole-game gain is claimed here for the persistent region index or other unvalidated candidates.

After the Win32 bank-pointer and calling-convention fixes, an isolated native probe passed bank access and floating-point calls, and one full-game run reached rendered gameplay and menus, then exited through Quit with status 0. These are correctness checks for that tested path, not evidence that the BRE performance gap is resolved. No replacement strategy has yet demonstrated negative whole-game overhead.

## Reference for replacement and later deprecation

Use native x86 as the application control and retain the BRE as a comparison implementation while evaluating the successor. Preserve the existing synthetic evidence rather than treating it as a substitute for application measurements.

Before claiming that a replacement closes the gap:

1. Record exact executable/runtime identities and verify process bitness independently of window captions.
2. Use the same save, room, camera, assets, resolution, graphics options, frame limiting and host environment. Distinguish startup, save loading, map generation, gameplay and pause/menu rendering.
3. Run alternating, repeated native/BRE/successor samples after matched warmup. Disable command/COM timing for the performance comparison; report frame-time distributions, sample duration and variation alongside FPS.
4. Measure loading phases separately and collect allocation/GC and CPU attribution in separate diagnostic runs. Do not present instrumented FPS as the clean baseline.
5. Verify behavior, rendering, native ABI, save compatibility and normal shutdown. A performance claim must not rely on omitted work or weakened checks.

The 226 FPS observation implies a useful initial reference of 4.42 ms/frame, not a permanent hardware-independent threshold. Zero or negative application overhead must be demonstrated against fresh paired native measurements with uncertainty reported. Formal deprecation, migration scope and compatibility policy remain to be recorded after selecting and validating the successor.
