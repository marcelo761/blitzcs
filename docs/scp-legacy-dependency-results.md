# SCP:CB legacy dependency compilation results

Date: 2026-10-25. Source: original SCP:CB 1.3.11 snapshot at Regalis11/scpcb commit `edb8fe0840b78f14d1aef3a0bf6174630e7be296`. All 30 BASIC files retain their pinned hashes; all nine `.decls` and supplied runtime/FMOD binaries remain unchanged. The original SDK's 2,061 files were also compared against its archive with zero changes. [Portable evidence](evidence/legacy-dependency-resolution-scp.json) contains source hashes, per-symbol results and library groups.

## Before and after

The previous harness omitted the real FMOD catalog and projected away all 369 pointer-bearing declarations. It retained 842 scalar declarations and excluded `api_GetClientRect`, `ZlibWapi_Compress2` and other real contracts before the compiler saw them. Consequently, repeated unknown-symbol errors were configuration/import failures, not proof of unsupported APIs.

The new project configuration imports all 1,245 original declarations, the historical runtime profile and the unchanged FMOD binary catalog. Explicit fallback catalogs reuse existing file/string/bank and other providers without replacing the selected FMOD binary provider. All applicable names carry their source and provider provenance.

| Stage | Measured result |
|---|---|
| Declaration discovery | Nine `.decls`, 1,245 entries; all 369 pointer-bearing entries preserved. |
| Syntax and Includes | Main plus 23 Includes parse successfully; no missing Include. |
| Full semantic/type analysis | Exit 0 for unchanged Main and the complete Include closure, on both Linux .NET x64 and the experimental Windows .NET 10 x86 host. |
| Dependency audit | 348 distinct requested external symbols; zero missing declarations. |
| Selected bindings, isolated branch providers | 44 selected; 298 missing providers and six ABI/provider incompatibilities remain. |
| Additional read-only prebuilt input comparison | 57 selected; 285 missing providers and six incompatibilities remain. This does not add the external prebuilt input project as a branch dependency. |
| Managed assembly emission | Refused before output generation. First required unbound call: `Windowed3D`, `fullscreen_window_fix.bb:47`. |
| SCP packaging | Not reached; no successful game package claimed. |
| SCP execution | Not attempted; no game rendering or runtime success claimed. |

The source semantic result is separate from emission: the isolated configuration reports 12,838 unbound call sites. Repeated calls are not counted as distinct symbols in the 348-symbol audit.

## Dependency groups

These are the isolated branch's configured-provider results. Additional methods may exist in another provider/configuration; `provider_missing` does not classify a historical function as globally absent.

| Library/provider | Bound symbols | Missing provider | ABI/provider incompatibility |
|---|---:|---:|---:|
| Historical Blitz3D runtime | 0 | 256 | 0 |
| SCP modified runtime additions | 0 | 5 | 0 |
| FMOD original binary provider | 4 | 0 | 6 |
| User32 | 0 | 5 | 0 |
| Kernel32 | 0 | 2 | 0 |
| zlibwapi | 0 | 23 | 0 |
| BlitzMovie | 0 | 7 | 0 |
| Existing managed file/string/error/diagnostic/system/bank/audio providers | 40 | 0 | 0 |

The optional prebuilt input provider resolves 13 additional symbols. It was inspected and selected read-only; its code was not modified or rebuilt by this experiment.

Examples now distinguished correctly:

- `FSOUND_Init` resolves to the existing `_FSOUND_Init@12` stdcall binding from `samples/FmodAudio/fmod.json`.
- `api_GetClientRect` resolves to `user32.decls:153`, `user32.dll!GetClientRect`, with the `lpRect*` data-pointer contract preserved. A real HWND/bank adapter is still required.
- `ZlibWapi_Compress2` resolves to `zlibwapi.decls:114`, `zlibwapi.dll!compress2`; three pointer parameters survive discovery. No compatible zlib bank adapter was found in the inventoried implementations. The native compression algorithm was not reimplemented.
- `FSOUND_Stream_Open` has a genuine historical ABI discrepancy: three declared arguments versus `_FSOUND_Stream_Open@16`. This is not repaired by inventing a fourth argument.
- Other FMOD managed fallbacks are not silently mixed with the selected binary provider's stream/channel handles. Historical Close's Integer return also conflicts with the real Void binding; it is not fabricated. The report records six requested FMOD incompatibilities, not six missing BASIC instructions.

The import exposed two general frontend issues: blank-library declarations duplicated functions already implemented by the application, and `*` arguments containing BASIC objects were treated as plain Integer arguments. Both were fixed generically. The former defers to actual application functions; the latter preserves the object expression for contract checking and still refuses emission without a real adapter/provider. No native object layout or address conversion was added.

## Runtime contracts recovered from the actual provider

Inspecting the original modified runtime initially failed to load because its real `fmod.dll` dependency was absent. Supplying unchanged copies together allowed the x86 factory inspection to finish: version `66644`, 600 registrations, runtime SHA-256 `ed61cd11cfae77f9f9c47c2ec2e5aa778ff79ddc5ee2f4f7353db4ce6006f39d`.

The generic snapshot comparison found all 593 stock descriptors plus seven additions. Five are public: `ErrorLog`, `TextureLodBias` and the three previously recovered `TextureBumpEnv*` contracts. Two are internal and remain excluded. There were no stock signature/default changes. This replaces the incomplete three-function overlay without adding engine implementations.

## Execution and regression checks

The unchanged original SDK `CreateCube.bb` was compiled and executed again with the final .NET 10 x86 host and original runtime under Proton. Compilation/execution returned 0; its real 640×480 front buffer contained 25,600 nonblack cube pixels, gray center and black corner. Input hashes remained unchanged. This remains x86 original-engine execution, not a BCSBRE-recompiled x64 Blitz3D engine.

The existing original-FMOD BCSBRE demo test passed, including version 3.75, Init, real-time NOSOUND mixer playback and Close. A separate native probe exercised generic Integer-return discarding for historical Void SetVolume/StopSound commands using a real loaded sample/channel: SetOutput, Init, sample and channel checks were all 1, GetError was 0, process exit 0. No physical audio playback is claimed for NOSOUND.

Final checks:

- 338 compiler tests passed.
- 460 CLI tests passed with the original SDK enabled; three existing opt-in package/Wine cases were skipped in that run.
- Both Windows x64 default and Linux x64 package tests were then enabled separately and passed.
- Six `.bcx`/CLI integration tests and the Windows binary package test passed.
- The original FMOD demo execution test passed without skips.
- 31 relevant Python profile/runtime/provider/audit tests passed.
- Original graphical SDK execution passed again as described above.

The combined Wine-specific default/assets test was not run. No BCSBRE translation/runtime implementation, DirectX replacement, IPC layer or original game/SDK file was changed.

## Reproduction

See [configuration and resolution tracing](legacy-dependency-resolution.md). With the separately supplied library next to the unchanged FMOD catalog, use a project JSON that imports `profiles/scpcb-mavless.json`, that FMOD catalog, `samples/Audio/scp.json` as a fallback and the directory containing the original `.decls`.

```sh
blitzcs audit-dependencies /path/to/original/Main.bb --externals project.json --trace-resolution resolution.json
blitzcs check-semantics /path/to/original/Main.bb --externals project.json
blitzcs build /path/to/original/Main.bb --externals project.json --trace-resolution build-resolution.json
```

The last command retains the Windows x64 default and must fail while required providers remain unbound. A zero semantic exit code or a populated trace must never be reported as a successful executable build.
