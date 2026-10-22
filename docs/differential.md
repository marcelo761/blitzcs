# Differential compatibility harness

Run the checked-in compatibility fixtures against BlitzCS with:

```sh
python3 tools/differential.py
```

The defaults use `dotnet`, `src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll`,
`tests/Compatibility/fixtures.json`, and write `artifacts/differential.json`.
Override them with `--dotnet`, `--blitz-cli`, `--manifest`, and `--report`.
Each BASIC source is compiled to a temporary DLL and run with `dotnet`; each
process has a 20 second timeout by default (`--timeout` changes it). The local
phase is `compile_error` when compilation fails, `run` when execution exits
successfully, and `runtime_error` when execution fails. Captured stdout and
stderr are retained. A process timeout always fails the local fixture, even
when its phase matches the manifest, and cannot produce a matching
differential comparison. Expected phase and optional expected stdout are
checked against the manifest, with CRLF and CR normalized to LF.
Fixture names must be unique, and every relative source path must resolve to an
existing file beside the manifest.

The manifest has this shape, with each `source` resolved relative to the
manifest file:

```json
{
  "fixtures": [
    {
      "name": "hello",
      "source": "fixtures/hello.bb",
      "expected_phase": "run",
      "expected_stdout": "Hello\n"
    }
  ]
}
```

## Original compiler adapter

An optional executable can be supplied with `--original-runner PATH`. The
harness invokes it as `PATH ABSOLUTE_SOURCE_PATH`. For each fixture it must
capture the original program's behavior and print one JSON object to stdout:

```json
{"phase":"run","stdout":"Hello\n","exit_code":0}
```

`phase` is `run`, `compile_error`, or `runtime_error`. The adapter itself must
exit zero when it successfully captured a result, including when the original
program had a compile or runtime error. A nonzero adapter exit, timeout,
malformed JSON, or invalid response is reported as `adapter_error`; it does
not count as a pending comparison. Comparisons require equal phases, equal
success/failure exit semantics, and equal stdout for successful runs and
runtime errors (including partial output). Compile-error output and exact
nonzero error codes need not match. The adapter's captured JSON is recorded as
`original_capture` in the report.

An executable adapter is now available: [original-adapter.md](original-adapter.md)
records the official V1.118 distribution and its execution under Proton. Without
`--original-runner`, comparisons still remain `pending`; local expectations
never stand in for captures. The local manifest checks still run. `--require-original` makes pending
comparisons fail the command, which is useful when running in an environment
where the adapter is expected.

Original Blitz3D `Print` uses graphical output while BlitzCS runs with a
console host. An adapter must capture the original program's actual printed
text through a platform-specific mechanism and return that text in the
protocol's `stdout` field. Returning the adapter process's own stdout as a
substitute does not constitute an original capture. Unit
tests exercise harness mechanics with fake subprocess data only; they are not
original-versus-BlitzCS differential results.

The report gives per-fixture `local_status`, `actual`, and
`comparison_status`, plus totals for local passes/failures, matches,
mismatches, adapter errors, and pending comparisons. The command exits
nonzero for a local failure, mismatch, or adapter error, and also for pending
comparisons when `--require-original` is set.
