# Testing the Windows x64 build under Wine

The primary target is Windows x64: the recompiled code runs inside a Windows x64 .NET process. On a Linux machine the Windows build of the test suite is
run under Wine, with a real IA-32 reference ("oracle") that also runs under Wine. Everything below lives under `scripts/wine/`, `tools/WinTestRunner/` and
`tools/x86_oracle/`; build outputs go to `artifacts/` (git-ignored).

## Quick start

```
scripts/wine/run-tests.sh                                  # default: tests/BlitzCS.BinaryCompiler.Win32.Tests
scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Tests     # also builds the PE32 oracle EXEs on first use
scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Tests --filter Differential --threads 2
```

`run-tests.sh [options] [test-project ...]` publishes each project as `win-x64` self-contained (`dotnet publish -r win-x64 --self-contained`, into
`artifacts/wine/app/<project>`, build cache in `artifacts/wine/ab`), overlays the `tools/WinTestRunner` runner, copies the result to a short path
(`$BLITZCS_WINE_WORK/tN`, default `/tmp/blitzcs-w-<uid>`), runs `wine WinTestRunner.exe --assembly <project>.dll`, prints everything except passing
tests, and exits non-zero on any failure. Full logs: `artifacts/wine/<project>.log`. Options: `--filter <text>` (repeatable), `--threads <n>`, `--config`,
`--oracle-dir`, `--no-build`.

`tools/WinTestRunner` is a small xunit v2 runner (`xunit.runner.utility` 2.9.3 `AssemblyRunner`): `--assembly`, `--filter` (substring of the display name,
case-insensitive), `--threads` (default 1; with more, xunit runs different `[Collection]`s in parallel but never two tests of one collection, so the
`GuestRuntime` collection stays serialised), `--verbose`, `--list`. It prints PASS/FAIL/SKIP with durations, failure output, a summary and exit code
(0 ok, 1 failures/errors, 2 usage or nothing ran). It resolves the test assembly's dependencies from its folder / `.deps.json`. It is not in `BlitzCS.slnx`;
it also runs natively (`dotnet run --project tools/WinTestRunner -- --assembly <dll>`), which is handy to compare Linux and Wine results.

## Prerequisites

* A Wine with WoW64 i386 support. Default: Steam Proton 11 (`$HOME/.local/share/Steam/steamapps/common/Proton 11.0/files`); override with
  `BLITZCS_WINE_ROOT=<dir containing bin/wine>`.
* Xvfb (a virtual X server; started on `:97` when missing; `BLITZCS_WINE_DISPLAY`, or `BLITZCS_WINE_NO_XVFB=1` to skip).
* The .NET 10 SDK on PATH, and network access for the first publish (win-x64 runtime pack and apphost from NuGet).
* For oracle tests: gcc with 32-bit support, `ld`, `objcopy`, `python3` (only to produce the oracle EXEs; the Windows process never compiles).
* `scripts/wine/env.sh` (sourceable) sets `WINEPREFIX` (default `$HOME/.cache/blitzcs/wineprefix`, created on first use, `BLITZCS_WINE_PREFIX`),
  `WINEDEBUG=-all`, `WINEDLLOVERRIDES=mshtml=d` (do not disable `mscoree`: Wine needs it to load IL-only assemblies), `PATH`/`LD_LIBRARY_PATH` of the Wine,
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, `DISPLAY`.

## The Windows x86 oracle

Oracle tests compare the translated guest with real IA-32 execution. On Linux that is the ELF32 oracle built by `tools/x86_oracle/build.sh`; on Windows
(and Wine) it is a PE32 console EXE `<name>.oracle.exe` built beforehand on Linux from the same machine code:

```
tools/x86_oracle/build_all_exe.sh artifacts/x86_oracle      # ORACLE_EXE=1 is implied; also writes <name>.code.bin / .data.bin / .exports.json
BLITZCS_ORACLE_DIR=<that folder (Windows or Wine path)>      # read by OracleRunner on Windows; run-tests.sh sets it automatically
```

`run-tests.sh` builds `artifacts/x86_oracle` when it has no `*.oracle.exe` (delete the folder to rebuild after changing `tools/x86_oracle`). Without the
directory the oracle tests skip with an explicit reason. Details, equivalence results and Windows findings: `docs/binary-differential.md`, `docs/binary-fs-oracle.md`.
Each EXE maps at 0x10000000 as a 32-bit process under Wine; the translated guest is moved to 0x30000000 on Windows (`OracleDifferential.LoadImage`).

## Known Wine limits

* The low 512 MiB (below 0x20000000) cannot be reserved by a 64-bit process (`VirtualAlloc` at a fixed address fails with `ERROR_INVALID_ADDRESS`); it works above.
  Guests must live at 0x20000000 or higher (translator diagnostic `BC_BASE_UNUSABLE_ON_TARGET`). 32-bit PE processes (the oracle) do get 0x10000000.
* ICU is not available: invariant globalization is forced (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`).
* Long paths are fragile: the published folder is copied to a short path before running.
* The x87 control word of a Windows process is 0x27F (53-bit precision), unlike Linux (0x37F): see the findings in `docs/binary-differential.md`.
* The file system is case-insensitive; tests that assume a case-sensitive host fail.
* Wine prints `ntsync: up and running.` on stderr; harmless.

## Troubleshooting

* `no Wine found`: set `BLITZCS_WINE_ROOT`. Hangs or `err:` noise: `wineserver -k`, then retry; delete the prefix to recreate it.
* `Could not find/load ... xunit.execution.dotnet.dll` / test assembly not found: publish again without `--no-build`; the runner must be published into the same folder.
* All oracle tests skipped: read the skip reason (usually `BLITZCS_ORACLE_DIR` unset or no `arith.oracle.exe`).
* Run one EXE by hand: `. scripts/wine/env.sh && wine artifacts/x86_oracle/arith.oracle.exe | head`.

## Results (Wine 11 / Proton 11, Release, at the time of writing; W1's win-x64 runtime work still in progress)

| Assembly | Passed | Failed | Skipped |
|---|---|---|---|
| BlitzCS.BinaryCompiler.Win32.Tests | 187 | 10 | 5 |
| BlitzCS.BinaryCompiler.Unpack.Tests | 13 | 1 | 0 |
| BlitzCS.BinaryCompiler.Tests | 199 | 184 | 1 |

Failure causes (expected to fail until the Windows x64 runtime/codegen port and the fixture changes land; not test-infrastructure faults):

* Fixtures linked at 0x10000000 (`PeFixtureBuilder.ImageBase`) are rejected by the translator on win-x64 (`BC_BASE_UNUSABLE_ON_TARGET`): about 130 tests in
  TranslatorStructure, ReturnAbi, AbiRegisterConvention, TebTests, TebTlsTests, EndToEnd, DispatchOptimization, CfgPointerRoots, DelayImport, Concurrency, Rebase,
  ModuleLink, CliBinaryIntegration, Unpack SyntheticPacker and 9 of Win32 GuestServicesTests. Fix: let the fixture builder/TranslatorHarness use a base at or above
  0x20000000 on Windows (as `OracleDifferential.LoadImage` does for the differential tests).
* `RuntimeLoaderTests` (16): artifacts translated explicitly for `linux-sysv` are refused on a windows-x64 host.
* x87 precision: translated `fp`, `regconv`, `ret_abi` differ from the 0x37F oracle (8 tests): the .NET process runs x87 at 53-bit precision.
* Assumptions about the host: `Win32 FileTests.PathTranslationIsSandboxedAndCaseInsensitive` expects a case-sensitive file system; `LinkingTests.SampleProgramCallsTheUnchangedDllEndToEnd`
  needs the repository root; `CacheTests.ConcurrentPutsAndGets` hits an access-denied path; `LinkingTests`/`LinkingFloatConventionTests` (about 15) have message/value differences to triage once the
  runtime lands.
