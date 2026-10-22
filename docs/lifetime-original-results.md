# Object lifetime: source findings and original-runtime fixtures

## Source findings

The inspected Blitz3D source is `/tmp/blitz-research/blitz3d` at commit
`6beb288cb5962393684a59a4a44ac11524894939`. This is reference source, not a claim
that it exactly matches the separately downloaded V1.118 binary.

In `bbruntime/basic.cpp`, `_bbObjNew` obtains a node from the type's free list,
initializes its fields, inserts it into the used list and starts its reference
count at one. `_bbObjStore` retains the new reference before releasing the old
one, including during self-assignment. `_bbObjDelete` is idempotent for null or
already-deleted objects: it clears/releases every field, sets `fields` to null,
then releases its own reference. A deleted node remains in the used-list links
while another reference still points to it. When its count reaches zero,
`_bbObjRelease` unlinks it and moves it to the free list. Thus references to a
deleted object compare as null (`_bbObjCompare` compares the `fields` pointer),
but a live alias keeps the deleted node's navigation links available.

`_bbObjNext` and `_bbObjPrev` follow those links and skip nodes whose fields are
null. `First` and `Last` use the same traversal. `For Each` advances from the
current variable on each step; it does not snapshot the list. The second
`EachFirst/EachNext` pair writes the current node directly, consistent with the
compiler's temporary loop slot. `_bbObjDeleteEach` saves the next link before
deleting the current node, so it can walk and delete the used list safely.
These details explain why collection membership and the lifetime of an alias
are distinct concerns.

The current BlitzCS runtime uses managed object references, but its per-type
list links retain every created node, including deleted nodes, until the
program's list becomes unreachable. `Delete` clears fields and marks a node
dead; traversal skips it. That preserves navigation for aliases, but the linked
list is also a strong root for deleted nodes. The fixtures below record the
semantics a lifetime change must preserve. They do not claim that managed
memory is reclaimed; allocation/reclamation measurements are recorded by the
Milestone 6 runtime validation.

## Genuine V1.118 comparisons

`tests/Compatibility/lifetime.json` keeps local expected output separate from
original captures. `artifacts/lifetime-differential.json` was produced by
`tools/differential.py` with `tools/original_blitz3d_adapter.py`, the official
Blitz Research V1.118 compiler/runtime and Proton. The adapter changes only
`Print` output to file writes; the tested object operations execute in the
original compiler/runtime. No screenshots or OCR are involved.

The pre-lifetime Release run produced **10/10 original comparisons matching
and 10/10 local fixture assertions passing**. Its report is preserved as
`artifacts/lifetime-before-differential.json`. After the managed-lifetime
runtime change, the final Release rerun again produced **10/10 original
comparisons matching and 10/10 local fixture assertions passing** in
`artifacts/lifetime-differential.json`. Per-stage commands and native
stdout/stderr from the final run are preserved under
`artifacts/final-original-lifetime-evidence/`. The fixtures cover:

| Fixture | Observed behavior |
| --- | --- |
| `lifetime-mass` | 100,000 `New`/`Delete` pairs complete; the list is empty afterward. |
| `lifetime-aliases` | Both deleted aliases compare equal to `Null`; `Before` is empty and `After` still finds the live neighbor. |
| `lifetime-deleteforeach` | Deleting each current node visits 1, 2, 3, 4 once and leaves an empty list. |
| `lifetime-reorder` | Inserts produce traversal order 4, 2, 3, 1. |
| `lifetime-circular` | Deleting two nodes in a reference cycle clears their fields and releases links; the remaining node's links compare as null. |
| `lifetime-multiple-deleted-navigation` | Navigation through multiple retained deleted aliases skips dead nodes and observes later live-node deletions. |
| `lifetime-mutating-iteration` | A node appended during `For Each` is visited; deleting the current node does not skip the following live node. |
| `lifetime-arrays-fields` | Arrays and object fields can refer to each other; `Delete Each` clears object references and leaves array aliases null-equivalent. |
| `lifetime-release-alias` | A surviving deleted alias remains null-equivalent while a later object becomes the first live list item. |
| `lifetime-deleted-neighbor-reorder` | After moving c before a, the deleted b alias has no live successor and a as predecessor. Moving c after a then makes c the deleted node's predecessor; its successor remains empty. |

These results apply to the captured V1.118 binary and these inputs. They are not
a claim of complete Blitz3D compatibility or proof of managed-memory reclamation.

The same final Release was checked against the other two original-runtime
manifests in serial order: the legacy manifest had 15/15 local passes, 14
matches, and the documented `upper-bound-error` exit-status divergence; the
object manifest had 3/3 local passes and 3 matches. Their reports are
`artifacts/original-differential.json` and
`artifacts/original-object-differential.json` respectively. Across all three
manifests, 27 of 28 comparisons match; the remaining result is the intentional
nonzero BlitzCS process status versus exit code 0 after the original runtime's
upper-bound error.

## Reproduction

With the official binary and isolated Proton prefix configured as in
`docs/original-adapter.md`, run:

```bash
BLITZ3D_TIMEOUT=20 BLITZ3D_EVIDENCE_DIR=artifacts/lifetime-original-evidence \
  python3 tools/differential.py \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --original-runner tools/original_blitz3d_adapter.py \
  --manifest tests/Compatibility/lifetime.json \
  --report artifacts/lifetime-differential.json \
  --timeout 30 --require-original
```

The environment variables `BLITZ3D_HOME`, `BLITZ3D_WINE`,
`BLITZ3D_WINEPREFIX`, and `BLITZ3D_DISPLAY` must point to the installed original,
Proton executable, dedicated temporary prefix, and dedicated X display. Run
captures serially because the adapter dismisses a native runtime-error dialog
on that display.
