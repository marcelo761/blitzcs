# Compatibility baseline

Compatibility is tracked along separate axes because passing one does not establish the others.

| Axis | Meaning | Current evidence / status |
|---|---|---|
| Syntax | The `.bb` source is accepted without rewriting its BlitzBasic spelling, case, separators or legacy forms. | Original tokenizer lowercases identifiers for lookup, accepts case-insensitive keywords, `:` and newline statement boundaries, and `;` comments (`blitz3d/compiler/toker.cpp` at [6beb288](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/compiler/toker.cpp)). The initial bounded subset is implemented and covered by component and execution tests; broader syntax is rejected. |
| Semantics | Accepted programs produce the original observable values, side effects, errors and control flow. | Original source tokenizes decimal integer literals separately from floating literals, supports `%` binary and `$` hexadecimal forms, and recognizes `Print` with an optional string argument. Runtime `Print` draws text at cursor coordinates, sets cursor X to zero, and advances cursor Y by font height; `End` exits through the runtime's end signal (`bbruntime/bbgraphics.cpp`, `bbruntime/bbruntime.cpp`). These are observations, not differential validation. |
| Graphics | Window, drawing, input, 3D state, assets and frame behavior match the target contract. | No graphics backend chosen or implemented. SDL GPU is a candidate interface, raw Vulkan and an engine-level abstraction remain alternatives. |
| Binary | Existing compiled Blitz binaries, object formats, calling conventions or assets load and behave compatibly. | Not investigated or claimed. Source-to-C# translation does not imply binary compatibility. |

## Implemented subset and limitations

Primitive Integer/Float/String assignments (`%`, `#`, `$`), case-insensitive identifiers, decimal
numeric literals, unary signs, `+ - * /`, parentheses, `Print` with a string or
numeric expression, `End`, comments, LF/CRLF and colon separators are implemented.
Typed `Global`, `Local`, functions, parameters, `Return` and function calls are
also implemented. If/ElseIf/Else, While, For, Repeat, Select and loop Exit,
comparison/bitwise operators and typed multidimensional Dim arrays are implemented.
`<?cs ?>`, `<?cs module ?>`, `CS::` and the typed `BB` bridge
are BlitzCS extensions; see hybrid.md for their bounded contract.
Missing integer variables initialize to zero; integer division truncates toward
zero, consistent with inspected original source (`compiler/varnode.cpp`,
`compiler/prognode.cpp`, `compiler/exprnode.cpp`). This is source-based evidence,
not differential validation.

The console test host replaces graphical `Print` solely to observe values.
Primitive conversions, typed interop and overload selection follow the bounded
contract in [typed-interop.md](typed-interop.md), grounded in original-source
inspection and local execution tests. The original x87/CRT edge behavior has not
been validated differentially. Empty-argument `Print`, binary/hexadecimal literals,
Goto/Gosub and Data/Read/Restore remain unsupported. Nominal Type/Field objects,
New/Delete, ordered lists, Insert and For Each are implemented; see
[legacy-objects.md](legacy-objects.md) for managed position lifetimes and unsupported handles.
Arrays use intentional per-axis safety checks stronger than original debug/release
behavior; see [legacy-language.md](legacy-language.md).
Decimal positive literals must be <= 2147483647; direct `-2147483648` is rejected,
although `-2147483647-1` is expressible. Addition, subtraction and multiplication
use unchecked signed 32-bit arithmetic. Boundary equivalence remains unverified.
Numeric constant zero divisors and constant signed-division overflow are diagnosed;
primitive constant conversions now include String prefixes (for example
`4 / Int("none")` fails during compilation);
variable-dependent arithmetic failures currently surface as .NET runtime errors.
`End` returns from the generated entry point; within a function it terminates
the process with exit code zero. Full cleanup/shutdown equivalence still needs a
legacy runtime contract.

## Validation status and next evidence

On 2026-10-20, the typed Release build succeeded with zero warnings/errors and
the typed milestone had 132 tests passed (66 compiler, 66 emission/CLI). The previous 84-test baseline was
executed before changes. Its existing valid behavior remains covered; previous
negative cases for newly supported types now test acceptance of the same inputs.
`hello.bb` and `answer.bb` retain their outputs. `samples/Hybrid/typed.bb` prints
`42`, `3.5`, `[Blitz] C#`, round-tripping all three types in one assembly. See
validation.md for evidence and typed-interop.md for limitations.

No original Blitz3D executable was run; no differential or graphics validation is
claimed. Before broadening compatibility claims, collect matching fixtures from
the original compiler/runtime for arithmetic boundaries, output/state, errors and
shutdown. Report syntax, semantics, graphics and binary compatibility separately.

## Legacy milestone evidence

The 132-test baseline was executed before new changes. Current control-flow,
arrays, hybrid examples and differential-harness results are recorded in
[validation.md](validation.md). No original executable was found: captured
original results remain absent and comparison statuses are pending. Local expected
outputs are not original golden captures. Known and unverified differences are
listed in [legacy-language.md](legacy-language.md).

## Legacy objects and genuine original execution — 2026-10-21

Earlier pending statements above describe earlier checkpoints. The official
Blitz3D V1.118 compiler/runtime now runs through installed Proton in an isolated
prefix. [original-adapter.md](original-adapter.md) records binary hashes, version,
commands and the deterministic Print-to-WriteLine instrumentation. This compares
computed values and flow, not graphical Print behavior or full compatibility.

Source findings preceded the object implementation; see
[legacy-object-research.md](legacy-object-research.md). Managed object references
preserve nominal typing and primitive hybrid interoperability. Dead aliases
compare equal to Null; their position supports traversal while observable.
Milestone 6 separates position records from identities: the list holds deleted
objects weakly and scavenges empty positions. Native refcount/reuse is not
reproduced; records can remain until subsequent activity after GC. See
[object-lifetime-design.md](object-lifetime-design.md) and the memory measurements.
The final counts and observed divergences are recorded in [validation.md](validation.md).


Runtime errors use the explicit [modern failure contract](runtime-errors.md).
An uncaught failure remains nonzero; the genuine original's exit 0 after an
array bounds error is retained as an intentional differential mismatch. No
legacy error-exit profile is implemented.


## Multi-file and real-world audit — Milestone 7

Include now processes source files in order and inherited scope, with the
original entry-directory base and per-compilation duplicate suppression.
Main is intentionally not pre-seeded in that set; a cycle back to main can
parse it again. Positions carry original paths through language, Roslyn and
runtime diagnostics. See [multifile.md](multifile.md) and
[include-research.md](include-research.md) for verified behavior.

The [official SCP source audit](scp-compatibility.md) classifies coverage and
records actual first diagnostics. A supported unmodified module compiles and
runs through an Include wrapper; the complete game does not compile. Static
include-graph coverage is not a claim that every file passed parsing/binding.
No engine dependencies or compatibility stubs were introduced.

## Milestone 8: fixed vectors and Const

Typed fixed vectors in Global, Local and Field declarations and scalar Const
expressions are implemented. Their bounds, initialization and bounded original
comparisons are documented in [fixed-vectors.md](fixed-vectors.md) and
[fixed-vectors-research.md](fixed-vectors-research.md). Whole-vector assignment,
vector parameters and direct vector export through BB remain unsupported.
[scp-milestone8.md](scp-milestone8.md) records progress on unmodified SCP source;
engine commands are not silently replaced. Historical checkpoints above are
not the current original-runtime validation status.

## Milestone 9: language and external runtime boundary

Trailing optional arguments, typed constant defaults, signed 32-bit hexadecimal
literals and accepted-but-not-executed Field initializer expressions are covered
by the [M9 source and runtime research](m9-language-research.md). Statement calls
accept historical grouped first arguments and colon-separated zero-argument calls.
The [external registry](external-functions.md) binds primitive static .NET methods
at compile time; engine APIs remain separate and opt-in. The initial real
RuntimeError module does not imply coverage of other Blitz3D commands.
[The SCP M9 audit](scp-milestone9.md) records first-error categories and preserves
unchanged source hashes; passing files do not establish whole-game compatibility.

## Milestone 10: managed handles and shared Data

Object.Tipo and Handle are implemented without native addresses, preserving
managed deletion/lifetime. IDs are removed on Delete, never recycled, and map
only live objects of the exact requested nominal type. Safe exhaustion behavior
and scoped limitations are in [handles.md](handles.md).
Abs/Sgn preserve primitive types; the official V1.118 distinction between folded
negative-zero Abs and runtime Float Abs is covered by actual differential probes.
[Data/Read/Restore](data-M10.md) supports a shared constant primitive stream,
forward labels for Restore and primitive writable targets. It does not implement
Goto/Gosub or their control-flow labels. Two-word Else If is accepted as historical
ElseIf, which advances the original SCP include compilation.
[SCP M10](scp-milestone10.md) reports integrated first-error depth separately
from static closure depth and individual-file successes. Binary literals
remain a real next blocker; no whole-game equivalence is claimed.
