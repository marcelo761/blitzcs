# Object lifetime memory measurements

## Method and reproducibility

`ObjectMemoryTests` compiles a BASIC program with 100,000 `New`/`Delete` pairs
into a collectible assembly. The assembly and its static registry remain alive
throughout measurement: collection is not inferred from unloading the assembly.
The generated entry point returns before collection, so its local aliases are
no longer on the stack. Small helper methods use NoInlining to keep observation
locals out of subsequent collections.

The probe counts linked positions, live BASIC objects, strong references to
deleted objects, and weak targets remaining after explicit blocking compacting
full collections. It then performs normal `Delete(null)` calls on the typed
runtime list (two maintenance visits per call, no inserted positions), checks
the remaining position count, and finally runs the diagnostic Scavenge and GC.
The count is checked **before** Scavenge, so full diagnostic cleanup is not what
makes the incremental proof pass.

Allocation is bracketed around generated-program execution. Current-thread
bytes exclude allocations by other test threads; process-wide allocations are
also recorded and labeled separately. Heap measurements include xUnit, Roslyn
and runtime infrastructure. Memory tests disable parallelization within their
test assembly, and the reported comparison below uses the same single-test
filter in separate processes.

First build Release, then run these two commands from the repository root:

```bash
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH \
  dotnet test tests/BlitzCS.Compiler.Tests --configuration Release --no-build \
  --filter FullyQualifiedName~ObjectChurnReportsAllocationLiveHeapAndGcCommittedBytes --nologo

BLITZCS_MEMORY_RUNTIME_SOURCE="$PWD/tests/BlitzCS.Compiler.Tests/Fixtures/ObjectRuntime.Before.txt" \
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH \
  dotnet test tests/BlitzCS.Compiler.Tests --configuration Release --no-build \
  --filter FullyQualifiedName~ObjectChurnReportsAllocationLiveHeapAndGcCommittedBytes --nologo
```

The first writes `artifacts/memory-after.json`; the second writes
`artifacts/memory-baseline-comparable.json`. The fixture is the exact emitted
pre-change object runtime (with only a historical-file comment added), verified
against the source snapshot taken before implementation. Only that runtime is
swapped; generated declarations, workload and probe stay the same. The explicit
baseline branch asserts that the historical retention defect remains measurable;
it does not weaken the current-runtime recovery assertions. Leave
`BLITZCS_MEMORY_RUNTIME_SOURCE` unset for normal verification.

Run the full lifecycle tests with the `FullyQualifiedName~ObjectMemoryTests`
filter, or use `scripts/verify.sh` for all tests. These test helpers use reflection
to inspect private generated structures; reflection is not introduced into BASIC
calls or runtime object operations.

## Final Release measurements — 2026-10-21

This is a BlitzCS before/after comparison, not a memory profile of the native
Blitz3D runtime. Native execution validates semantics in separate reports.

Both isolated commands completed successfully on SDK 10.0.401 / runtime 10.0.12.
Each executed 100,000 creations and 100,000 deletions.

| Measurement | Historical runtime | Managed lifetime runtime |
| --- | ---: | ---: |
| Live BASIC objects after workload | 0 | 0 |
| Strongly retained deleted objects before forced GC | 100,000 | 0 |
| Deleted weak targets after full GC | n/a | 0 |
| Position records after full GC, before maintenance | 100,000 | 12,473 |
| Normal typed-null Delete calls for maintenance | 0 | 6,237 |
| Position records after incremental maintenance | 100,000 | 0 |
| Position records after diagnostic Scavenge | 100,000 | 0 |
| Current-thread bytes allocated during workload | 4,002,472 | 10,402,928 |
| Process-wide bytes allocated during workload | 4,002,808 | 10,414,488 |
| Process managed live bytes after collection | 19,095,392 | 15,429,120 |
| GC heap size | 19,136,824 | 15,444,456 |
| GC fragmented bytes | 41,696 | 15,576 |
| GC committed bytes | 29,675,520 | 26,644,480 |
| GC-reserved virtual address space | not measured | not measured |

The structural result is deterministic for this workload: all old deleted
objects remained strongly rooted; the current list had no strong deleted-object
references, all remaining weak targets cleared after GC, and ordinary bounded
maintenance removed all empty positions. No assertion uses a byte threshold,
RSS, elapsed time or a scheduled collection deadline.

The new runtime allocates about 2.6 times as many bytes during this workload
because identities, position records and weak handles are separate. The final
live process heap was lower in this run; that is a process-wide diagnostic,
not a universal per-object saving or a claim about peak memory/throughput.
Automatic GC during churn changes how many empty positions remain for the
post-workload sweep, so that intermediate count is recorded, not hardcoded.
The original actual pre-change capture is also retained in
`artifacts/memory-before.json`; it independently found 100,000 retained deleted
objects after full GC. Its earlier Debug heap values are not mixed into this
Release comparison.

## Live, collectible, and GC-owned memory

Live BASIC objects are deliberately owned by their type list until Delete,
even without a variable pointing to them. Deleted objects held by BASIC aliases,
arrays or fields are also intentionally observable. After those strong references
are released, their remaining instances are collectible; some may already have
been collected during churn. The test therefore does not call all 100,000
historical allocations “still collectible” at the final snapshot. Weak-target
counts after full GC verify that none remain.

Position records are a separate category: 12,473 empty records remained after
GC in the final current run and were removed by subsequent normal operations.
They do not retain deleted BASIC object graphs. Without further activity after
GC, empty records may remain until another maintenance/traversal operation;
there is no timer, finalizer or background full sweep.

Committed bytes describe memory committed to the GC; they are neither live
object bytes nor reserved virtual address space. The portable managed snapshot
used here does not expose a GC-reserved-space counter. Available memory is a
budget, not reserved memory. No RSS or virtual-process-size proxy is substituted.
Measuring GC reservation would require a separate runtime/OS-specific diagnostic.

## Additional lifecycle coverage

Tests verify collection after releasing a deleted alias, collection of the other
node of a deleted reference cycle while one deleted alias remains observable,
and collection after clearing an object array slot. The hybrid executable adds
100,000 operations and forced collections between deleted-alias navigation and
Insert reordering, with C# calling BASIC in the same assembly. Differential
fixtures independently validate semantics against the original; they do not
constitute memory-reclamation measurements.
