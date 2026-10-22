# DATA, READ, RESTORE, and two-word ELSE IF

This compatibility slice adds Blitz3D-style `Data`, `Read`, and `Restore` to
the compiler, plus recognition of the original two-word spelling `Else If`.
It is aimed at source compatibility with the SCP scripts and the observed
Blitz3D runtime; it does not add control-flow labels or `Goto`/`Gosub`.

`Data` expressions must be compile-time integer, float, or string values and
must occur in the main program. Includes are flattened into the source stream,
so data rows and labels in included files participate in the same ordered
program-wide stream. A label records the next scalar data position. `Restore`
without a label resets the shared cursor to the beginning; a named restore
can refer forward to a label. The cursor is shared across functions and main
execution. `Read` accepts primitive writable variables, array elements, and
primitive object fields, including comma-separated targets.

Coercions match the legacy runtime probes: float-to-integer truncates toward
zero; string-to-number accepts the numeric prefix; and integer/float values
read as strings use BlitzCS's compatibility formatting. Reading beyond the
stream raises `Out of data` with the source location. The original runtime
shows a modal error for exhaustion, so automated differential evidence for
its process exit behavior is unavailable; the local deterministic runtime
error is tested directly.

The source audit found `Restore sky_SkyboxData`, `Read` of strings and numeric
coordinates, and the `.sky_SkyboxData` label in SCP's `Skybox.bb`, followed by
30 `Data` rows. Original-runtime probes also confirmed forward restore across
functions and includes, shared cursor behavior, float/string conversion, and
the one-block `Else If` form. Captures are recorded in
`artifacts/m10-original-evidence/`; the corresponding manifests are
`tests/Compatibility/data.json` and `tests/Compatibility/controls.json`.

The compiler intentionally rejects non-constant and non-primitive `Data`
expressions, unknown/duplicate labels, and non-writable `Read` targets. It
does not implement Blitz3D's unrelated `Goto`/`Gosub` label control flow.
