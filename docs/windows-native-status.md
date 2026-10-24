# Windows x64 native-library status

BCSBRE accepts PE32 IA-32 libraries and emits target-specific x64 code artifacts. A Windows application loads those artifacts through the managed guest loader; they are not standalone PE64 DLLs. The original x86 DLL is input data, never passed to the operating system's 64-bit DLL loader. Changing a PE header would not adapt its ABI.

## Supported mechanisms

* Cross-translation on Linux with `BinaryCompilerOptions.TargetOs = "windows-x64"`; translation needs no Wine process. Library API defaults remain the running host for compatibility; the application build selects its output target explicitly.
* Win64 entry/import bridges, including argument registers, shadow space and nonvolatile integer/SIMD registers. Guest cdecl/stdcall/thiscall/fastcall contracts retain 32-bit values and guest memory layouts.
* Windows executable memory through `VirtualAlloc`, `VirtualProtect`, `VirtualFree` and `VirtualQuery`. Publishing RX code flushes the current process instruction cache and reports an OS error if that fails. Code is written RW and published RX; no RWX allocation is introduced.
* Low-address guest images, stacks, buffers and guest TEB/PEB/TLS, guest callbacks and cross-module exports. Guest TLS is represented explicitly, rather than using a 32-bit segment address in a 64-bit Windows thread.
* Relocation-aware guest base planning and strict artifact ABI validation. Fixed low-base libraries without relocations are rejected when they cannot fit the Windows target's image window.
* Unresolved imports, rejected instructions and invalid guest memory operations produce named failures through the existing loader/runtime contracts. Native hardware faults in translated instructions can still terminate the host process.

The instruction-cache publication step follows Microsoft's [VirtualProtect contract](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-virtualprotect#remarks) and [FlushInstructionCache API](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-flushinstructioncache).

The Win32 resolver adapts registered API contracts through existing managed services. Their .NET file/thread/memory facilities use the running Windows OS when hosted on Windows. This is a bounded compatibility surface, not unrestricted forwarding of guest imports to arbitrary system DLL exports. Opaque guest handles, callbacks, pointers and structures require per-contract adaptation before a 64-bit native API can receive them. Unsupported contracts must remain unresolved or fail explicitly. The Linux resolver and differential oracles remain regression infrastructure.

## Historical-library coverage and limits

| Scenario | Coverage and remaining limit |
| --- | --- |
| libogg | Real-library tests cover CRT startup, bit packing, page/stream operations and independent CRC checks. Requires the original fixture DLL. |
| Vorbis and VorbisFile | Real multi-module tests cover headers, malformed inputs and decoding compared with an ffmpeg reference. Requires matching DLLs and reference data. |
| EAX | Real-library tests cover COM registration/class-object contracts. This does not establish environmental audio effects or audio-device operation. |
| Packed FMOD | Controlled unpacking, selected exports and initialization/close paths have fixture tests. A fixed derived image must be unpacked at a usable target base. This does not establish FMOD audio playback. |
| Arbitrary legacy DLL | Requires supported instructions, valid relocations/base placement and complete import/ABI contracts. Unsupported graphics, device and networking APIs are not made functional by packaging. |
| Complete SCP:CB, graphics, input and audio playback | Not established by the native memory/ABI tests or by producing an executable package. Each needs dedicated runtime evidence. |

Earlier fixture-backed Windows results and setup are recorded in [wine-testing.md](wine-testing.md). Those earlier results must not be interpreted as a fresh run with unavailable historical fixtures.

## Validation of the Windows build work

On 2026-10-24 the platform, target ABI and target base-policy tests ran on Linux x64 and as a self-contained Windows x64 test assembly under Wine 11 (.NET 10.0.12): **33 passed, zero failed or skipped on each host**. The platform tests execute newly published x64 instructions and rewrite/republish them, in addition to exercising no-replace mapping and page protection. Target tests pin the preserved Linux code output and inspect Win64 argument/shadow-space rules.

Reproduce the focused Windows run from the repository root:

```bash
scripts/wine/run-tests.sh --filter PlatformMemoryTests --filter TargetOsTests --filter TargetOsBasePolicyTests BlitzCS.BinaryCompiler.Tests
```

A separate Windows run passed all 15 selected return-ABI and generated-binding tests (zero skips/failures): `ReturnAbiTests` plus `LinkingTests.DefaultsAndExplicitArgumentsUseTheCatalogMechanism`. These execute integer/floating-point/structure returns, reentrant callbacks, and a BlitzBasic program whose generated managed binding calls a translated PE32 cdecl addition export. The program checks both explicit and catalog-default arguments (`15` and `6`).

The normal application package path was also exercised from Linux with an unchanged PE32 cdecl addition fixture: `build Main.bb --externals catalog.json`, without output or target flags, creates `dist/Main/Main.exe` and packages `sum.dll`, its Windows-targeted `sum.dll.bcx`, the generated managed bridge and runtime closure. The resulting Windows executable printed `42` and exited successfully under Wine. The regression test below catches both missing binary artifacts and SDK/reference-pack incompatibility with generated bridge metadata:

```bash
BLITZCS_TEST_PUBLISH=1 dotnet test tests/BlitzCS.BinaryCompiler.Tests --filter FullyQualifiedName~WindowsPackageTests
# To also execute the package, source scripts/wine/env.sh and add BLITZCS_TEST_WINE=1.
```

The full Linux BinaryCompiler suite passed 378 tests with seven skips; the Win32 service suite passed 188 with 10 explicit historical-fixture skips; the unpacker suite passed 24 with three explicit fixture skips. `BLITZCS_REAL_PE32_DIR` and `BLITZCS_PACKED_SAMPLE` were unset for these runs. A subsequent Linux run supplied the locally available packed FMOD sample through `BLITZCS_PACKED_SAMPLE`: all three `RealPackedSampleTests` passed, exercising unpacking, rebasing and selected library calls. The ogg/Vorbis/EAX fixture DLLs were unavailable. No new claim about historical-library playback or actual native Windows hardware follows from these results. Wine is a validation environment, not a compiler dependency.
