# M12 SCP native/runtime inventory

This inventory separates original Blitz3D runtime registrations from names that merely appear in the SCP source. It is an audit input for the runtime work; it does not claim compiler binder resolution or runtime compatibility.

Regenerate it with the pinned source trees used for this audit:

```sh
python3 tools/generate_scp_native_manifest.py \
  --catalog tools/scp_native_catalog.json \
  --source-dir /tmp/scpcb-audit-source \
  --output tools/scp_native_manifest.json
```

The original runtime source files are pinned by SHA-256 in `tools/scp_native_catalog.json`. The generator also records hashes for every `.bb` file in the SCP source tree and parses all `.decls` files. A changed or missing original runtime file fails generation; a different SCP tree changes the recorded source set and its evidence.

## What the catalog records

`native_inventory` contains one compact row per original `rtSym` registration, including the source descriptor, decoded return and argument types, registration file and line, functional group, exact candidate frequency, exact counts per SCP file, up to three sample locations, lexical evidence files, status, and same-name app function shadow counts. Blitz descriptor markers decode as `%` integer, `#` float, `$` string, and no return marker void. Argument defaults are retained as descriptor text. Raw underscore symbols such as `_bbAbs` have no Blitz callable descriptor; they are labeled `internal_runtime_symbol` with unknown signature instead of being presented as zero-argument commands.

`application_functions` records SCP `Function` signatures with source locations. `external_declarations` records parsed `.decls` signatures, target library symbol, parameter types, and source locations; pointer parameters are labeled `pointer`. Compact rows use the corresponding top-level `*_row_schema` arrays. `call_candidates` stores compact summaries for app function and `.decls` bindings. Native call summaries live in `native_inventory` to avoid duplicating the same call data. Each summary preserves exact frequency and per-file counts, with up to three sample locations per name. Function definition lines are excluded from the candidate scan; variable and field declaration names are excluded while call-shaped initializers remain candidates. An SCP function with the same name takes precedence and marks the native registration as shadowed. A `.decls` binding is labeled `externaldependency` unless shadowed by an SCP function.

The candidate scan accepts identifiers followed by `(` and statement-shaped identifiers after common BASIC statement prefixes. It is a lexical heuristic, not the Blitz parser or binder: array access and ambiguous statement syntax can be included. It does not type-check arguments, discover dynamic calls, or claim resolution for unrelated identifiers. The previous 593 registrations and 318 lexical-name hits were not resolved callsites; the new candidate counts preserve that distinction. Exact per-file frequencies and source hashes are retained, with at most three sample locations per name. This is not a complete call graph. The current recursive source set contains 32 `.bb` files: the earlier 30-file top-level scan omitted `Map Creator/mapcreator_new.bb` and `Map Creator/window3d.bb`.

At this snapshot, the manifest reports 593 original registrations, 321 registrations with at least one lexical-name occurrence, 700 distinct mapped call-candidate names, and 21,311 mapped candidate occurrences across the recursive source set. The candidate totals combine native registrations with mapped app function and external declaration candidates; they are not resolved native command-call totals.

The file-level groups are based on original registration units: files (`bbfilesystem.cpp`, `bbstream.cpp`), strings (`bbstring.cpp`), math (`bbmath.cpp`), system (`bbruntime.cpp`, `bbsys.cpp`, `runtime.cpp`, `std.cpp`), graphics (`bbgraphics.cpp`), entities (`bbblitz3d.cpp`), audio (`bbaudio.cpp`), input (`bbinput.cpp`), and others for remaining units. Collision-prefixed registrations in `bbblitz3d.cpp` are additionally tagged `physics`. These are inventory labels, not a claim that the original runtime's implementation is cleanly partitioned by those concepts.

## Status and dependency evidence

Status values describe evidence, not parity. `FileSize`, `FileType`, `CurrentDir`, `GetEnv`, `Chr`, and `Len` are marked `implemented` after runtime implementation and native-oracle tests. `FileSize` and `FileType` cover NUL truncation, missing directories, and a unique wildcard match; ordering for multiple wildcard matches and host Unicode differences remain unverified. `CurrentDir` and `GetEnv` cover their core behavior, including the environment-key NUL case. `Chr` and `Len` cover ASCII and CP1252 behavior; they reject text outside CP1252 explicitly. `ChangeDir` and `SetEnv` remain `pending`. `RuntimeError` is marked implemented from its existing runtime support. Other registrations and app functions remain `unverified` because no checked-in support map was available when this manifest was generated. A candidate backed by an app `.decls` row is `externaldependency` because its target is supplied through an external declaration. The generator does not infer `implemented` from an original registration or a lexical mention.

`Abs` and `Sgn` are listed separately under `compiler_intrinsics`: the compiler parses them as unary language tokens and emits its own compatibility helpers. They are implemented language intrinsics with no external dependency, not runtime registrations or external native calls.

The source-backed dependency relationships currently recorded are:

| Commands | Source-backed dependency | Evidence in original runtime |
| --- | --- | --- |
| `FileSize`, `FileType`, `CurrentDir`, `ChangeDir` | `gx_filesys`, initialized through `gx_runtime->openFileSystem(0)` | `bbfilesystem.cpp` uses `gx_filesys` for the operations; `filesystem_create()` opens it, and `bbruntime_link()` registers `filesystem_link()`. |
| `GetEnv`, `SetEnv` | C runtime environment functions `getenv` and `putenv` | `bbruntime.cpp` implements the functions directly with those calls. |
| `Chr`, `Len` | .NET `CodePagesEncodingProvider` configured for CP1252 | Current `LegacyStrings` implementation uses it for byte encoding and byte counts; neither command requires an app `.decls` dependency. |

The filesystem service must exist for those four filesystem commands to work. `GetEnv` and `SetEnv` do not use that service. Other command relationships are left unverified until their source dependencies are traced; the inventory does not fabricate a complete dependency graph.

## Initial M12 commands

The decoded signatures and current call-shaped candidate counts are:

| Command | Original descriptor | Signature | Status | Candidate frequency |
| --- | --- | --- | --- | ---: |
| `FileSize` | `%FileSize$file` | `(string) -> integer` | implemented | 9 |
| `FileType` | `%FileType$file` | `(string) -> integer` | implemented | 37 |
| `CurrentDir` | `$CurrentDir` | `() -> string` | implemented | 7 |
| `ChangeDir` | `ChangeDir$dir` | `(string) -> void` | pending | 2 |
| `GetEnv` | `$GetEnv$env_var` | `(string) -> string` | implemented | 0 |
| `SetEnv` | `SetEnv$env_var$value` | `(string, string) -> void` | pending | 0 |
| `Chr` | `$Chr%ascii` | `(integer) -> string` | implemented | 169 |
| `Len` | `%Len$string` | `(string) -> integer` | implemented | 180 |

These frequencies count candidate locations in the scanned SCP set and do not prove binder resolution. `GetEnv` and `SetEnv` have no matching call-shaped use in this source snapshot.
