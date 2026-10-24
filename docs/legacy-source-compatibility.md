# Original Blitz3D source compatibility

This work changes the compiler/frontend, external catalogs and build pipeline. It does not change binary translation algorithms, native runtime internals or original BASIC sources. Source-contract compatibility and runtime functionality are measured separately.

## What changed

- Existing `--externals` catalogs now import API profiles and complete their declarations with explicitly selected managed/binary providers.
- A source-backed stock Blitz3D profile supplies 534 public signatures, typed defaults and provider/resolution metadata. Three separately identified SCP patched-runtime texture declarations are an optional overlay.
- Missing provider diagnostics identify the BASIC source location, provider, profile and registration symbol. No executable or stub is emitted for an unbound required call.
- The `Pi` language constant now resolves as IEEE single precision (`0x40490FDB`), confirmed by an original compiler/runtime probe. Bare `Print` now emits an empty line, including EOF, colon and conditional boundaries. The SDK uses this syntax; it was previously rejected.
- Imported profiles remain protected build inputs, including binary `.bcx`, temporary and derived outputs. Binding conflicts and explicit metadata removal are rejected.

See [API profiles](api-profiles.md) for configuration and the public compiler API.

## Historical API audit

Language statements/control flow (`Print`, `End`, declarations, loops, types, arrays, Includes) are handled by the frontend. Arithmetic/conversion/object operations with compiler-defined semantics remain intrinsics. Engine commands enter only through per-compilation external signatures/bindings; the compiler has no automatic Blitz3D engine implementation or engine reference.

BASIC names are case insensitive and unique; there is no external BASIC overload set. A managed binding's parameter types select its exact public static overload. Catalog defaults are trailing typed source constants; explicitly empty defaults preserve required historical arguments even if a provider declares CLR optional parameters. Generated code calls the selected method directly. Existing binary bindings generate managed bridges to translated `.bcx` exports and retain their convention validation.

The source inventory at `blitz-research/blitz3d@6beb288cb5962393684a59a4a44ac11524894939` has 593 literal registrations: 536 public typed descriptors and 57 internal symbols. `Print` and `End` are language statements, leaving 534 external declarations. This is source registration evidence, not an export table or a verified native ABI. The original SDK runtime DLL has only `_bbWinMain@0` and `runtimeGetRuntime` as named PE exports. Commands such as `Graphics3D` must not be bound by assuming they are DLL exports. Calling convention and string/handle ownership remain unknown unless independently established by a real provider.

## SDK corpus

The corpus is the local official Blitz3D V1.118 ZIP, SHA-256 `eb5b07c3f2ccf5142ddf866c1c4caec5c4ee262b6a3ab0bcc718a397a822cc02`. It has 704 `.bb` files. All 2,061 extracted files were compared byte-for-byte with the archive after testing; no differences were found. Original files and binaries are not redistributed.

The baseline is commit `1732aee`, without an API catalog. The changed compiler uses `profiles/blitz3d.json`. Initial measurements used separate CLI processes; final baseline and changed-compiler passes each invoked the same public `CliApplication.Run` entry in their own isolated .NET process (retained validation harnesses and raw per-stage outcomes). The ordinary corpus tool below reproduces the stages in separate processes. Compiler/CLI/profile SHA-256 identities are recorded. Every `.bb` is checked as an independent entry, including fragments that are not standalone applications. Therefore the figures are entry-level coverage, not a claim that all game projects compile. The integrated project result below is reported separately.

| Stage, 704 entries | Baseline | API profile + frontend fix |
| --- | ---: | ---: |
| Parser accepts source/includes | 636 | 669 |
| Source semantic/contracts accepted | 30 | 656 |
| Managed assembly emitted with declaration-only profile | 30 | 31 |
| Automatically executed by full-corpus audit | 0 | 0 |

The increase of 33 parser-accepted entries comes from empty Print (29 entries) and Pi (four further entries). The 626 additional semantic successes include declarations without implementations: 625 entries pass contracts but are still blocked at emission by required providers. The unchanged original `Pi.bb` now emits and executes without an engine provider; it failed the baseline parser. Graphical, input and engine examples remain blocked at emission when a used provider is absent; no successful executable packages are claimed for them. Exact per-entry hashes, stage outcomes and first diagnostics are in [the compact evidence](evidence/legacy-api-compatibility.json). Raw local audit logs retain the full diagnostics.

The bounded nine-category set covers pure BASIC, file and input APIs, Graphics, Graphics3D, entities, cameras, world commands and TunnelRun. The first eight validate contracts with the profile; TunnelRun still fails on unsupported `Goto` at line 37. `ReadFile.bb` additionally validates after the empty-Print fix, but does not execute without file/input providers. `.decls` availability, missing provider implementations, binary linking and runtime faults are not classified as BASIC syntax failures.

## Actual execution with existing providers

Three unchanged original variable tutorials and the original `Pi.bb` emit and execute with no external provider. The Pi regression verifies the same float bits as the genuine original compiler/runtime, which wrote `40490FDB` to its result file and exited 0 under Proton. Four unchanged SDK examples (`Chr.bb`, `Len.bb`, `Left.bb`, `Replace.bb`) emit and execute through the existing explicit `LegacyStrings` provider combined with the historical profile. Outputs include `A`, length `12`, `Sha`, and `Bibb Wabbace`, respectively. No replacement provider was introduced for the test. This demonstrates usable binding composition; the string implementation predates this work.

The unchanged original `Chr.bb` also produced a self-contained Windows x64 package using the new composed catalog. Running `Chr.exe` through Proton 11.0 returned 0 and captured ` The character for ASCII value 65 is: A`. Windows command redirection captured the application stdout explicitly; a launcher exit code alone was not treated as proof of output. The unchanged original `Pi.bb` also produced a Windows x64 package without an external provider; Proton execution returned 0 and captured `The Value of Pi is:3.14159`. No graphics rendering is claimed.

A separate integration regression compiles a profile declaration with an original default argument into an actual translated PE32 addition-export binding, verifies `.bcx` publication and unchanged DLL bytes, and executes the result with output `42`. This synthetic ABI fixture establishes integration behavior, not historical engine compatibility.

## SCP:CB 1.3.11

The exact read-only source snapshot is `Regalis11/scpcb@edb8fe0840b78f14d1aef3a0bf6174630e7be296`: all 30 `.bb` hashes match the pinned prior evidence, and nine `.decls` sidecars are present. No original file was edited.

`Main.bb` passes syntax validation; its include closure contains 23 included files and no missing paths. Windows slash and case handling are already supported. Whole-program type/global/function collection resolves definitions visible across Includes. Compiling individual include fragments in isolation can falsely suggest missing arrays, custom types or helper functions; those are not counted as integrated frontend failures.

Without a catalog, Main first stops at `FileSize` on line 12. With the historical engine profile and supported scalar `.decls` contracts, integrated semantics reaches `fullscreen_window_fix.bb:70`: `api_GetClientRect` is declared as `api_GetClientRect% (hwnd%, lpRect*) : "GetClientRect"`, but the current primitive catalog projection cannot represent its special `*` parameter contract.

Original compiler source treats `*` as a bank/custom-object-data parameter, not an arbitrary integer pointer. A real provider needs checked bank/object borrowing and an owned window-handle contract. Erasing `*` to Integer would neither validate the ABI nor supply the x64 marshaling; it is not accepted as a compatibility result. No such mapping is shipped.

An explicitly invalid, declaration-only pointer-erasure probe was used only to locate subsequent dependencies. It reached `TextureBumpEnvMat` at `MapSystem.bb:36`. SCP's instructions require a patched runtime (`mavless_bin.zip`), rather than stock Blitz3D. The optional `scpcb-mavless.json` overlay records the three matching descriptor strings, artifact identities and byte offsets; it does not remove the earlier pointer/adapter blocker or implement those commands.

The nine `.decls` sidecars name `BlitzMovie.dll`, `FreeImage.dll`, `cpuid.dll`, `fmod.dll`, `gdi32.dll`, `kernel32.dll`, `user32.dll` and `zlibwapi.dll`; `DevilParticleSystem.decls` uses a blank library marker and overlaps project-defined functions. Declaring their scalar signatures does not establish library availability, pointer conversion, calling conventions or native linking.

Thus full SCP semantic compilation, native linking, executable packaging and game execution are **not established**. The first integrated contract blocker is special `.decls` pointer semantics; additional providers and SCP-specific engine contracts remain required. There is no evidence here of a new integrated parsing defect in the exact Main/include tree.

## Reproduction and validation

```sh
# Recreate the declaration profile and check tool regressions.
python3 tools/generate_blitz3d_profile.py
python3 -m unittest discover -s tests/differential -p test_blitz3d_profile.py
python3 -m unittest discover -s tests/differential -p test_sdk_compatibility_audit.py

# Compiler, catalog, binding and unchanged SDK source tests.
dotnet test tests/BlitzCS.Compiler.Tests -c Release
BLITZCS_ORIGINAL_SDK=/path/to/Blitz3D-V1.118 \
  dotnet test tests/BlitzCS.CLI.Tests -c Release

# Existing and new binary integration tests (no binary implementation changes).
dotnet test tests/BlitzCS.BinaryCompiler.Tests -c Release \
  --filter 'FullyQualifiedName~CliBinaryIntegrationTests|FullyQualifiedName~ExternalProfileBinaryTests'

# Opt-in real SDK packaging on Linux; Windows x64 remains the default.
BLITZCS_TEST_PUBLISH=1 dotnet test tests/BlitzCS.CLI.Tests -c Release \
  --filter 'FullyQualifiedName~ApplicationPackageTests.BuildWithoutOutput|FullyQualifiedName~ApplicationPackageTests.BuildWithLinux'
BLITZCS_TEST_PUBLISH=1 dotnet test tests/BlitzCS.BinaryCompiler.Tests -c Release \
  --filter FullyQualifiedName~WindowsPackageTests

# Full source corpus: declarations only; emitted outputs remain temporary and are never run.
python3 tools/audit_sdk_compatibility.py --sdk /path/to/Blitz3D-V1.118 \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --profile profiles/blitz3d.json --all --emit --out artifacts/sdk-profile-final.json
```

Final verification: **322 compiler tests**, **421 CLI tests**, **six binary/profile integration tests**, **nine generated-profile tests** and **nine corpus-tool tests** passed. The original SDK tests ran with `BLITZCS_ORIGINAL_SDK` set; absent originals are explicitly skipped, not reported as passing. The normal CLI suite explicitly skipped three opt-in packaging/Wine tests; the two packaging tests plus the native `.bcx` package test were separately enabled and passed. The combined Wine-specific default/assets test was not run; actual Windows x64 Chr and Pi packages were instead executed through Proton with captured output. No graphics execution coverage is claimed.

The default remains `blitzcs build Main.bb` → `dist/Main/Main.exe`, Windows x64 even on Linux. No change to `.bcx` serialization or native runtime contracts is included. Remaining source blockers include `Goto`/`Gosub`, special `.decls` parameter semantics and corpus-specific source/context errors; runtime blockers include unimplemented graphics/input/device providers and verified native marshaling. These are explicit limits, not silently generated substitutes.
