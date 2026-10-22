# Handle and Object.Type in original Blitz3D

## Findings

The official Blitz3D V1.118 runtime gives a live object its first handle when
`Handle(obj)` is evaluated. Handles begin at 1, remain stable for repeated calls
on the same live object, and increase for later objects. A direct
`Handle(Null)` literal compiles and returns 0; typed-null references and objects
after `Delete` also return 0. Deletion removes any handle mapping, so a stale
handle resolves to `Null`.

`Object.Type(handle)` resolves the integer through the runtime's handle map and
returns `Null` when the handle is absent or the object's type does not match
the requested type. The stock source checks exact type identity. BlitzForge's
fork source instead routes this through its inheritance-aware `_bbObjCast`, so
derived-to-base casts are a fork behavior and were not used as evidence for
stock Blitz3D.

The stock runtime maintains separate maps from object pointer to integer and
integer to object. `_bbObjToHandle` first rejects null/deleted objects, reuses
an existing mapping, or increments `next_handle`; `_bbObjFromHandle` looks up
the integer and checks exact `BBObjType` equality. `_bbObjDelete` erases both
map entries before releasing the object. `basic_create` resets the counter to
zero at runtime startup. The object allocator keeps type-specific free lists in
blocks of 512 objects, so memory addresses can be recycled; the handle counter
is separate from those free lists. The language-level probe confirms the next
observed handle after deletion is 2, while source establishes monotonic IDs
through normal execution. It does not claim a particular pointer address was
reused, which the language does not expose.

The expanded captures confirm that `Object.Type` accepts integer-compatible
Float and String expressions. For an allocated handle 1, `Object.Node(0.9)` and
`Object.Node("1")` resolve the object, while `Object.Node(1.9)` resolves to
`Null`; invalid numeric text (`"junk"`) resolves to `Null`. Integer handles
and object references stored in arrays both resolve correctly; deleting their
object makes stale lookups return `Null`. `Delete Each` removes handles for all
visited objects. A deleted alias still preserves its list position long enough
for `Before alias` and `After alias` to find the live neighbors. A 1000-iteration
allocate/handle/delete probe observed handle IDs through 1000 with zero
monotonicity or stale-handle errors.

## Genuine runtime capture

`tests/Compatibility/fixtures/handles/lifecycle.bb` (SHA-256
`db14d2bc8e043dea44a59878153adef7640446f7da11cec5e5fb087a5388b21a`) was run
with the official Blitz3D V1.118 binaries under Proton 11 and the isolated Wine
prefix described in [`original-adapter.md`](original-adapter.md). The original
adapter reported phase `run`, exit code 0, and this exact standard output:

```text
1
1
1
1
1
1
1
1
1
1
2
1
1
1
1
1
```

The lines exercise, in order: typed-null `Handle`, first handle value, stable
repeat, matching type, wrong type, zero handle, negative unknown handle, large
unknown handle, a deleted alias, the old handle after deletion, the next handle
value, ordering of the two IDs, stale lookup after creating another object,
live lookup of the new object, `Handle` after deleting that object, and its
stale lookup. The source uses equality-to-`Null` checks so each successful `1`
is an observed assertion from the original runtime, not a prefilled answer.

The original array observations were initially conflated and are now explicitly
separated by source path and SHA-256. The baseline
`tests/Compatibility/fixtures/arrays.bb` (SHA-256
`4cd36f27e5051a434d56ee0b6083d2998b1b09140b57c8d347c34d5a1c46f5c0`) prints
`0` and `42`. A prior unlogged five-line `1` response was mistakenly associated
with this baseline and then with the distinct handle-array fixture; that
association is retracted because its process log was overwritten. The
handle-specific `tests/Compatibility/fixtures/handles/arrays.bb` (SHA-256
`e2d770241d6008ffa8ace1d40fdd07505087416bbc610c0cc8e66e0be4cfafb8`) has a
successful targeted capture with five `1` lines at
`artifacts/m10-original-evidence/handle-arrays-targeted-20261022T042142Z-422520/`.
It preserves the instrumented stdout bytes and completion marker. Its separate
earlier retry ending in `-11` remains recorded in `artifacts/m10-original-evidence/arrays.json`.

The complete hash-matched first observations and provenance links are in
`artifacts/m10-original-evidence/first-observed-adapter-results.json`;
`artifacts/m10-original-evidence/fresh-m10-cases.json` joins the current
handle, numeric, control and data manifests without associating observations
from different fixture paths.

## Source cross-check

The original source checkout is `/tmp/blitz-research/blitz3d`, commit
`6beb288cb5962393684a59a4a44ac11524894939`. In
`bbruntime/basic.cpp`, it has the maps at lines 29–34, deletion cleanup at
268–293, handle creation and lookup at 423–438, and startup reset at 520–524.
The compiler lowers `Object.Type(handle)` to `_bbObjFromHandle` and `Handle(obj)`
to `_bbObjToHandle` in `compiler/exprnode.cpp:608–626`.

The separate BlitzForge checkout at `/tmp/blitz-research/blitz-forge`, commit
`bdbfd9f`, agrees on zero for invalid/deleted objects, stable map-backed
handles, and deletion cleanup. Its lookup uses `_bbObjCast` and
`_bbObjIsKindOf` (`src/blitzrc/bbruntime/basic.cpp:540–567`), which adds
inheritance-aware type matching beyond stock's exact equality test.

## Evidence boundaries

The fixture and expected output are grounded in an actual original capture via
Proton. The implementation explanation is grounded in the checked-out source;
the source checkout is not claimed to be the exact source used to build the
commercial V1.118 binaries. Handle overflow, forged pointers, and pointer
identity after pool reuse were not probed. They are outside this compatibility
fixture.
