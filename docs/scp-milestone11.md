# Milestone 11: syntax probe and SCP audit

## Result

The probe can now run an optional syntax-only pass with `--check-syntax`. This
pass reports parser/include validation separately from full compilation. A
successful syntax result explicitly leaves semantics, external binding, native
resolution, and assembly emission unresolved. Without the flag, the existing
probe commands and report shape are unchanged.

The Release audit covered the same 30 SCP source files as the M10 report; all 30
source SHA-256 values match. Both audits use `samples/Native/runtime.json` as
their external catalog. Full compilation remains **2/30**. The first
diagnostic categories changed as follows:

| First diagnostic category | M10 | M11 |
| --- | ---: | ---: |
| Compiled | 2 | 2 |
| Lexical | 0 | 0 |
| Syntactic | 5 | 2 |
| Semantic/type | 7 | 8 |
| Native name not registered | 14 | 16 |
| External missing | 2 | 2 |
| Infrastructure | 0 | 0 |

These are counts of each file's first full-compile result, not a count of every
error in the project. The M10 baseline is `artifacts/scp-m10-after.json`; the
M11 run is `artifacts/scp-m11-after.json`.

Syntax-only validation succeeded for **27/30** files. Three did not validate:

| File | First syntax-only result |
| --- | --- |
| `LoadAllSounds.bb` | Line 4: expected `Close`, found `,` |
| `MusicPlayer.bb` | Line 1: include `BlitzAL.bb` is missing from this source set |
| `Save.bb` | Line 594: expected `Close`, found `,` |

The `LoadAllSounds.bb` and `Save.bb` files are checked independently, without
their declarations from the surrounding program. Their errors occur at
multidimensional array assignments whose dimensions are declared elsewhere in
the project. These standalone results therefore show that those files need
their project context; they do not establish that the assignment syntax fails
when the declarations are present. `MusicPlayer.bb` is independently blocked by
the absent `BlitzAL.bb` include.

The integrated `Main.bb` syntax check succeeded across its static Include
closure: 23 included files, maximum static depth 2, no missing Includes. The
integrated **full compile still fails** at `Main.bb:12` because `filesize` is
not registered. That is a name-resolution result; syntax success does not imply
the included program has valid types, external bindings, or engine support.

The separate M10 integrated compile stopped earlier at a syntax error in
`Skybox.bb:11`. M11 reaches the `filesize` name-resolution diagnostic instead.
Both integrated reports retain only the first observed diagnostic, so later
errors remain unknown.

## Native metadata and limits

[`tools/scp_native_manifest.json`](../tools/scp_native_manifest.json) expands
the source metadata in [`tools/scp_native_catalog.json`](../tools/scp_native_catalog.json)
into **593 original `rtSym` registrations**, grouped by their 19 original
runtime source files. Each registration keeps its original Blitz descriptor,
source file, and line. Each registration is a row with `name`, original
descriptor, original line, and an optional list of SCP files containing that
name as a case-insensitive lexical identifier after BASIC comments and strings
are blanked. This found lexical occurrences for 318 of the 593 registrations.
The row schema and source hashes are in the manifest. Regenerate it with:

```sh
python3 tools/generate_scp_native_manifest.py \
  --source-dir /tmp/scpcb-audit-source
```

These occurrences are inventory evidence only. The manifest does not resolve
call sites, argument counts or types, overloads, runtime implementations, or
engine availability. A name appearing in a file does not establish that it is
invoked or that BlitzCS supports it. The compiler's `native_unregistered`
category is likewise a first-diagnostic classification, not a complete API
coverage measurement. No native functions are mocked or executed by this
manifest.

## Reproduction

```sh
python3 tools/scp_compile_probe.py \
  --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Native/runtime.json \
  --check-syntax \
  --report artifacts/scp-m11-after.json
```

The source snapshot is external to this repository. The report records its
directory and per-file hashes, while the checked-in baseline identifies the
declared upstream commit. The probe does not independently verify that the
external directory came from that commit.

## Verified checkpoint and remaining work

The checked-in [evidence snapshot](evidence/m11-validation.json) preserves source
hashes, first diagnostics, emitted-assembly flags and the actual differential
captures. Original sources and native binaries remain external to Git.

M11 removes the observed binary-literal blocker at `Skybox.bb:11` and the decimal
literal blocker subsequently found at `Main.bb:2707`. Their distinct overflow
semantics were executed on official V1.118; see
[m11-language-research.md](m11-language-research.md). No source patch to SCP was used.

The compiler did **not** emit the integrated game assembly. Native symbol
resolution stops before complete semantic analysis and Roslyn emission, so final
assembly reference binding and functional game execution were not reached.
Further language/type errors may appear after genuine external implementations
are registered. Goto/Gosub and other unsupported constructs have not been shown
necessary by this successful integrated syntax pass; standalone parser success
must not be generalized to all BlitzBasic programs.

Next steps: implement a small real native subsystem in a separate runtime,
register its exact signatures, rerun integrated compilation, and resolve each
new observed error. The [Blitz3DCS architecture proposal](blitz3dcs-architecture.md)
is ready for review; it introduces no renderer, physics or audio implementation.

Release validation: 523 .NET tests (281 compiler, 242 CLI), 37 Python tests and
81 local fixtures passed. All 81 fixtures were freshly captured with the genuine
Blitz3D adapter via Proton: **80 matches, one known `upper-bound-error`
divergence**, no adapter failures or pending comparisons. The divergence remains
the intentional nonzero .NET runtime-error exit versus original exit zero.
Memory stress again performed two batches of 100,000 New/Delete/Handle operations:
zero retained handle entries and zero positions after collection/scavenging.

A targeted Debug lifetime test also fails at baseline M10 commit `62c1dbb`:
`IntegerHandlesDoNotRootDeletedObjectsAndLiveObjectsStillResolve` observes a live
WeakReference after forced collection. Release passes. No lifetime runtime code
was changed in M11; a Debug/JIT temporary root is suspected but not established.
This preexisting limitation is recorded rather than hidden or weakened.

To repeat the genuine differential batch, first provision the dedicated
Proton prefix/display and environment documented in [original-adapter.md](original-adapter.md).
Then, from the repository root with `dotnet` on PATH:

```sh
for manifest in tests/Compatibility/*.json; do
  name=$(basename "$manifest" .json)
  BLITZ3D_EVIDENCE_DIR="artifacts/m11-repeat/$name/raw" \
    python3 tools/differential.py --manifest "$manifest" \
      --original-runner "$PWD/tools/original_blitz3d_adapter.py" \
      --require-original --timeout 30 \
      --report "artifacts/m11-repeat/$name/report.json"
done
```

Each report retains its own totals. The baseline manifest's command returns 1
for the known upper-bound-error mismatch; inspect every report rather than taking
the final loop command's exit code as an aggregate pass. The ordinary
`scripts/verify.sh` only runs local fixtures and does not prove fresh original
comparison by itself. Repeating into the same evidence directory replaces prior
captures, so use a fresh directory when retaining multiple runs.
