# Testing the Windows x64 build under Wine

The primary target is Windows x64: the recompiled code runs inside a Windows x64 .NET process. On a Linux machine the Windows build of the test suite is
run under Wine, with a real IA-32 reference ("oracle") that also runs under Wine. Everything below lives under `scripts/wine/`, `tools/WinTestRunner/` and
`tools/x86_oracle/`; build outputs go to `artifacts/` (git-ignored).

## Quick start

```
scripts/wine/run-tests.sh                                  # default: BinaryCompiler.Tests, Win32.Tests, Unpack.Tests, Linking.E2E.Tests
scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Tests     # one assembly (builds the PE32 oracle EXEs on first use)
scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Tests --filter Differential --threads 2
BLITZCS_REAL_PE32_DIR=/path/to/dlls BLITZCS_PACKED_SAMPLE=/path/fmod.dll scripts/wine/run-tests.sh   # real-library tests (Linux paths are mapped to Z:\...)
```

**Real-library tests are SKIPPED, not passed, without their binaries.** `BLITZCS_REAL_PE32_DIR` (a directory with `ogg.dll`, `vorbis.dll`, `vorbisFile.dll`, `eax.dll`) and
`BLITZCS_PACKED_SAMPLE` (a UPX-packed `fmod.dll`) name local third-party files that are never copied into the repository. Every test that needs them carries
`[RealLibraryFact]`, `[RealLibraryTheory]` or `[PackedSampleFact]` (`tests/Shared/RealLibraryAttributes.cs`, linked into each test project): at discovery they set `Skip` with the reason
(`BLITZCS_REAL_PE32_DIR is not set`, `... lacks vorbis.dll`, ...), so the runner prints `SKIP <test> -- <reason>` instead of a green PASS that tested nothing. The same holds on Linux (`dotnet test` shows
the tests as skipped with the reason). A test must never `return` early on a missing input: that reports PASS in 0 ms and hides regressions. When you check a run, count the skips and
read the per-test durations: a real-library test takes tens to thousands of milliseconds, not 0-2 ms. `run-tests.sh` prints
`WARNING: <variable> is not set: real-library tests will be SKIPPED` for each unset variable. The vorbisFile decode test also needs reference audio (ffmpeg with libvorbis does not exist for
Windows/Wine): when `BLITZCS_REAL_PE32_DIR` is set, `run-tests.sh` generates it on Linux into `artifacts/wine/vorbis-reference` (`BLITZCS_VORBIS_REFERENCE_DIR`); without ffmpeg that one test is skipped with the reason.

`run-tests.sh [options] [test-project ...]` publishes each project as `win-x64` self-contained (`dotnet publish -r win-x64 --self-contained`, into
`artifacts/wine/app/<project>`, build cache in `artifacts/wine/ab`), overlays the `tools/WinTestRunner` runner, copies the result to a short path
(`$BLITZCS_WINE_WORK/tN`, default `/tmp/blitzcs-w-<uid>`), runs `wine WinTestRunner.exe --assembly <project>.dll`, prints everything except passing
tests, and exits non-zero on any failure. Full logs: `artifacts/wine/<project>.log`. Options: `--filter <text>` (repeatable), `--threads <n>`, `--config`,
`--oracle-dir`, `--no-build`. The script exports `BLITZCS_REPO_ROOT` (the repository as `Z:\...`, for tests that read `samples/`) and converts a Linux path in
`BLITZCS_REAL_PE32_DIR` / `BLITZCS_PACKED_SAMPLE` to its Wine `Z:` form.

`tools/WinTestRunner` is a small xunit v2 runner (`xunit.runner.utility` 2.9.3 `AssemblyRunner`): `--assembly`, `--filter` (substring of the display name,
case-insensitive), `--threads` (default 1; with more, xunit runs different `[Collection]`s in parallel but never two tests of one collection, so the
`GuestRuntime` collection stays serialised), `--verbose`, `--list`, and `--run <assembly.dll> [args]`. It prints PASS/FAIL/SKIP with durations, failure output, a summary and exit code
(0 ok, 1 failures/errors, 2 usage or nothing ran). It resolves the test assembly's dependencies from its folder / `.deps.json`. It is not in `BlitzCS.slnx`;
`--run` is what the child-process tests use where a Linux run would start `dotnet <generated.dll>`: it loads the generated assembly and its dependencies from its folder
(own load context + `AssemblyDependencyResolver`), invokes the entry point, forwards stdout/stderr and returns the program's exit code (an unhandled exception prints
`Unhandled exception. ...` on stderr and returns 134). Tests find it through `BLITZCS_WINTESTRUNNER` or `WinTestRunner.exe` next to the test assemblies
(`Support/GeneratedProgramRunner.cs`). It also runs natively (`dotnet run --project tools/WinTestRunner -- --assembly <dll>`), which is handy to compare Linux and Wine results.

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
Each EXE maps at 0x10000000 as a 32-bit process under Wine; the translated guest is moved to 0x60000000 on Windows (`OracleDifferential.LoadImage`). The PE32 EXEs are not pinned to a x87 control word: they run at Windows' natural 0x27F (see `docs/binary-differential.md`).

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

## Fixture bases on Windows

Linux fixtures keep their historical base 0x10000000 (the translated bytes are pinned by `TargetOsTests`). On Windows `PeFixtureBuilder.ImageBase` (also
`Win32PeBuilder.ImageBase`, `SyntheticPacker.Base`) is 0x60000000. The low part of the image window [0x20000000, 0x70000000) is not free in a long-running test
process: under Wine the process's own native/managed heaps are served bottom-up from 0x20000000 and had reached 0x30A20000 after the CLI tests, so a base of
0x30000000 collided after a while. Tests that need a *planned* base ask `HostPlatform.FreeBase(wanted)` (a free, 64 KiB aligned run) or plan multi-module sets from the
upper half (`HostPlatform.AllocatorReserved()`); tests of the base *policy* itself build explicit low-base fixtures (`PeFixtureBuilder.LowBase` = 0x10000000).

## Results (Wine 11 / Proton 11, Release)

| Assembly | Passed | Failed | Skipped |
|---|---|---|---|
| BlitzCS.BinaryCompiler.Tests | 381 | 0 | 3 |
| BlitzCS.BinaryCompiler.Win32.Tests | 197 | 0 | 5 |
| BlitzCS.BinaryCompiler.Unpack.Tests | 26 | 0 | 1 |
| BlitzCS.Linking.E2E.Tests | 12 | 0 | 0 |

These are runs WITH `BLITZCS_REAL_PE32_DIR` and `BLITZCS_PACKED_SAMPLE` set (the real-library tests execute: tens to thousands of ms each). Without them the same runs report 376/0/8, 183/0/15, 24/0/3 and 10/0/2:
the difference is exactly the real-library tests, now SKIPPED with a reason (earlier they `return`ed early and showed PASS in 0 ms, which hid two real failures under Wine).
(Before the Windows work of this milestone: 199/184/1, 187/10/5, 13/1/0 and not run.) Skips, all explicit in the test output:

* `LinkingTests.SampleProgramCallsTheUnchangedDllEndToEnd`: builds `samples/BinaryLinking/binlib.c` with bash and the 32-bit gcc toolchain; the end-to-end build/run path is covered on Windows by the other `Linking*` tests and `Linking.E2E.Tests`.
* `RebaseTests.BaseAllocatorIsDeterministicAlignedAndNonOverlapping`: asserts the *Linux* base policy (first module at 0x10000000, MAP_32BIT zone avoided); the Windows policy has its own tests (`TargetOsBasePolicyTests`).
* `UnsupportedConventionTests.Floating_point_return_registers_are_not_observable` (all hosts): documented gap, unchanged.
* 5 x `Win32IntegrationTests` that need the native x86 comparison library (Linux x64 only).
* `RealPackedSampleTests.Real_sample_unpacks_translates_and_a_trivial_export_runs`: the preferred-base variant; on Windows `Real_sample_unpacked_at_a_chosen_base_runs_like_the_native_original` runs the same sequence with the stub mapped at a host-valid base.

Loader tests: `RuntimeLoaderTests` run on both hosts. `FakeGuest` assembles the same loader contract for SysV (Linux) and for Win64 (`EnterGuest(rcx, edx, r8)`, RDI/RSI callee-saved, import thunk argument in ECX with shadow space); protections are read through `/proc/self/maps` on Linux and `VirtualQuery` (platform layer) on Windows.
Real-library tests (ogg, vorbis, vorbisFile, eax, and the FMOD unpacker) run under Wine when the DLL directory is passed through `BLITZCS_REAL_PE32_DIR` / `BLITZCS_PACKED_SAMPLE`
(skipped with a reason otherwise, see the top of this document). The libraries prefer 0x10000000, which windows-x64 cannot map, so the tests rebase them first (`PeImage.Rebase` with a base from
`HostPlatform.FreeBase`, `BinaryCompilerOptions.LoadBase`); the packed FMOD is unpacked with the stub mapped at a host-valid base (`UnpackOptions.LoadBase`, `docs/binary-unpack.md`) and the full
`RealPackedSampleTests` run (DllMain, `FSOUND_Init`, `Close`) executes under Wine. The refusal of a fixed low-base unpacked image for windows-x64 (`BC_BASE_UNUSABLE_ON_TARGET`, `PE_REBASE_NO_RELOCS`) has its own
unit test on a synthetic image (`UnpackRefusalTests`).
