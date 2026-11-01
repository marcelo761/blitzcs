

<p align="center">
  <img src="blitzcs-wordmark.png" alt="Modern BlitzBasic compiler powered by C# and .NET" width="700">
</p>





# BlitzCS - Same Code. More Performance. More Integrations.

BlitzCS is an experimental, general-purpose BlitzBasic language compiler and interoperability infrastructure for .NET 10. It compiles BASIC and embedded C# into one assembly through Roslyn, with typed calls and shared state in both directions.

AI coding agents are used to support implementation, research, testing, and code review. Architectural decisions, project direction, and technical priorities are established by the project creator.

Created and led by **Marcelo Buratti de Freitas Filho**, BlitzCS explores how legacy BASIC programs can move to a managed runtime while integrating modern C# and native libraries. Compatibility research uses the original Blitz3D compiler and real legacy projects, including SCP: Containment Breach. These are reference targets, not a restriction on what BlitzCS can be used to build.

BlitzCS is an independent project. It is not an official Blitz Research, Microsoft, FMOD, or SCP project.

`blitzcs build Main.bb` now defaults to a self-contained Windows x64 package at `dist/Main/Main.exe`, including when compiling on Linux. See [Windows application builds](docs/windows-build.md) for tool installation, assets, explicit Linux builds and validation, and [the modern Windows runtime](docs/windows-runtime-modernization.md) for the Direct3D 11 and SDL3 runtime that these packages use. The earlier binary-recompilation path (BCSBRE, [native-library status](docs/windows-native-status.md)) is deprecated and kept only for reference.

## Quick start

For the verified full-solution build, use **Linux**, the **.NET 10 SDK**, and a **C compiler available as `cc`**. The native integration test project explicitly requires Linux. Window/input tests additionally require SDL3 and, on Linux, Xvfb; interactive samples require a desktop display. Run these commands from a checkout of this repository:

```bash
dotnet restore BlitzCS.slnx
dotnet build BlitzCS.slnx -c Release --nologo
dotnet run --project src/BlitzCS.CLI -c Release --no-build -- build samples/Hybrid/answer.bb -o artifacts/answer.dll
dotnet artifacts/answer.dll
```

The program prints `42`. Emitted assemblies enable JIT optimizations; see [compiler performance](docs/compiler-performance.md) for array indexing measurements. The compiler also writes generated C#, portable debugging symbols and runtime configuration alongside the assembly. Generated programs need .NET 10; compilation needs the SDK and Roslyn. External runtime libraries must accompany programs that use them.

This is a source distribution: no published NuGet package, global tool installation, or downloadable release is claimed here. Linux x64 is the verified development platform. Other platforms and native-library combinations need validation.

## Architecture

```text
.bb entry + Include files + embedded C#
                   |
       lexer / parser / semantic analysis
                   |
       generated C# + external bindings
                   |
                Roslyn
                   |
         one .NET program assembly
```

| Component | Responsibility |
| --- | --- |
| `BlitzCS.Compiler` | Language parsing, typing, compatibility semantics and C# generation |
| `BlitzCS.CLI` | Compilation, diagnostics, catalogs and deployment of dependencies |
| `BlitzCS.NativeInterop` | Generic native-library ownership and typed binding infrastructure |
| `BlitzCS.LegacyRuntime` | Optional legacy errors and checked memory-bank services |
| `Blitz3DCS.Runtime` | Engine-specific legacy file, string and system services |
| `Blitz3DCS.Audio` | FSOUND compatibility layer, SDL3 output, libsndfile decoding and managed PCM mixing |
| `Blitz3DCS.Windowing` | SDL3 window host and legacy keyboard/mouse bindings; the Windows renderer draws into its window |
| `Blitz3DCS.Engine` | Renderer-independent scene graph, B3D and DirectX X model loaders, animation, collision and picking |
| `Blitz3DCS.Engine.Windows` | Legacy Blitz3D graphics command adapter over the engine and the Direct3D 11 renderer |
| `Blitz3DCS.Graphics.D3D11` | Direct3D 11 renderer: MSAA, mipmaps, texture blends, per-flush object constants and instanced draws |

**BlitzCS is language infrastructure; Blitz3DCS is an engine integration built on it.** The compiler has no dependency on the Blitz3DCS projects. Other engines can register their own typed external functions without adopting Blitz3D APIs.

See [compiler architecture](docs/architecture.md), [external functions](docs/external-functions.md), [native interoperability](docs/native-interop-m13.md) and the [proposed Blitz3DCS architecture](docs/blitz3dcs-architecture.md).

Historical API [profiles](docs/api-profiles.md) compose declarations and real providers through `--externals`. Declaration validation does not imply runtime support; builds reject required unbound calls. See [measured original-source compatibility](docs/legacy-source-compatibility.md).

## BASIC and C# in one program

This is the checked-in [answer example](samples/Hybrid/answer.bb):

```blitzbasic
Global answer%=0

<?cs module
public static class MathBridge
{
    public static int Compute() => BB.add(19, 22);
}
?>

answer = CS::MathBridge.Compute()
<?cs
BB.answer += 1;
?>
Print answer
End

Function Add%(left%, right%)
    Return left + right
End Function
```

`CS::` calls C# from BASIC. `BB.` exposes BASIC globals and functions to C#. Statement blocks use `<?cs ... ?>`; module blocks use `<?cs module ... ?>`. Both languages share the same generated state and assembly. The primitive bridge supports Integer `%` (`System.Int32`), Float `#` (`System.Single`) and String `$` (`System.String`), with static signature and overload checking.

The [typed example](samples/Hybrid/typed.bb) prints `42`, `3.5` and `[Blitz] C#`:

```bash
dotnet run --project src/BlitzCS.CLI -c Release --no-build -- build samples/Hybrid/typed.bb -o artifacts/typed.dll
dotnet artifacts/typed.dll
```

The [multifile example](samples/Hybrid/MultiFile/main.bb) demonstrates the same bridge across `Include` files. See [hybrid syntax](docs/hybrid.md), [typed semantics](docs/typed-interop.md) and [Include resolution](docs/multifile.md). Embedded C# is executable application code, not a sandbox.

## Implemented language capabilities

- Typed globals, locals and functions; constant expressions, hexadecimal/binary literals and optional trailing parameters.
- `If`, `ElseIf`, `While`, `For`, `Repeat`, `Select`, nested control flow and `Exit`.
- `Dim` arrays with inclusive upper bounds and multiple dimensions; fixed vectors, including fields.
- `Type`, `Field`, `New`, `Delete`, ordered object lists, `For Each`, navigation and managed object handles.
- `Data`, `Read`, `Restore`, labels, `Goto` and `Gosub` within the implemented legacy control-flow contract.
- Multifile compilation into one assembly, preserving original `.bb` locations in diagnostics.
- Managed lifetime handling for deleted objects and explicit native resource ownership.

These are tested capabilities, not a claim of complete BlitzBasic compatibility. Detailed contracts and edge cases are in [legacy language](docs/legacy-language.md), [fixed vectors](docs/fixed-vectors.md), [objects](docs/legacy-objects.md), [handles](docs/handles.md), [Data](docs/data-M10.md) and [runtime errors](docs/runtime-errors.md).

## External and native libraries

Typed catalogs separate three stages: **semantic declaration**, **binding to an implementation**, and **runtime execution**. A declaration can be checked before a library runs; emitting a used declaration without an implementation fails explicitly. Registered managed functions compile to direct static calls. External commands may return `void`; typed trailing defaults can be declared without binding a method. `DebugLog` is implemented through host-provided .NET trace listeners. See the [external signature contract](docs/external-signatures-m14.md).

C/C++ integration uses explicit bindings, ABI contracts and resource ownership. It does not make arbitrary BASIC integers into native pointers. [Checked memory banks and synchronous native buffer borrowing](docs/legacy-memory-m18.md) preserve this boundary. The [native sample](samples/NativeInterop) and its [contract](docs/native-interop-m13.md) exercise integers, floats, UTF-8 strings, library errors and resource release. A Win32 FMOD DLL cannot be loaded into the verified Linux x64 .NET process; the original FMOD is used separately under Proton as a compatibility reference.

The [historical API inventory](docs/blitz3d-api-inventory.md) separates signatures, declarations, bindings and scoped validation. The complete 18-function [legacy string family](docs/historical-api-m19.md) is implemented with an explicit CP1252 boundary, historical byte casing/trimming and real local clock formatting.

The optional audio layer supports sample and stream playback, managed channel handles, volume, pan, frequency, looping, pause, stop and cleanup. SDL3 provides output and system libsndfile decodes supported assets. Streams currently decode into memory, with a 256 MiB decoded-file limit. See [audio implementation and differences](docs/audio-milestone15.md) and [audio requirements](src/Blitz3DCS.Audio/README.md). This is not complete binary or behavioral equivalence with FMOD 3.75.

## Build and validation

```bash
dotnet build BlitzCS.slnx -c Release --nologo
dotnet test BlitzCS.slnx -c Release --no-build --nologo
dotnet build BlitzCS.slnx -c Debug --nologo
dotnet test BlitzCS.slnx -c Debug --no-build --nologo
python3 -m unittest discover -s tests/differential -v
bash scripts/verify.sh
```

The complete verification script additionally needs **Python 3**, a **C compiler available as `cc`**, **SDL3** and **libsndfile**. On the verified Linux host, the native libraries are `libSDL3.so.0` and `libsndfile.so.1`. Install equivalent runtime packages from your distribution. The script exercises default, dummy and disk audio paths; a headless machine may lack a usable default device. Basic compiler examples do not require these audio libraries.

Last recorded run on the development machine (Linux x64, Release; skipped tests need Windows, an SDK or original assets):

| Verification | Recorded result |
| --- | --- |
| `BlitzCS.Compiler.Tests` | 377 passed |
| `BlitzCS.CLI.Tests` | 552 passed, 15 skipped |
| `Blitz3DCS.Engine.Tests` | 134 passed |
| `Blitz3DCS.Engine.Windows.Tests` | 178 passed, 10 skipped (native Direct3D smokes) |
| `Blitz3DCS.Graphics.D3D11.Tests` | 42 passed, 4 skipped (native Direct3D smokes) |
| `Blitz3DCS.Audio.Tests`, `Blitz3DCS.Windowing.Tests`, `BlitzCS.NativeInterop.Tests` | 69, 74 and 41 passed |
| Python harness and differential tests | 124 passing |
| Native Direct3D pixel smokes (Windows APIs under Proton) | 9 of 9 pass; their logs are compared byte for byte between builds |

The native smokes run only with a real display on Windows or under Wine/Proton, not on a virtual X server. `ArrayCompilerTests.RepeatedArrayAccessDoesNotAllocateIndexBuffers` is intermittently unstable (about 8 percent of runs, also on the unmerged `main`): it asserts zero allocations and can see a one-off allocation. The historical differential evidence is recorded in the [M19 report](docs/historical-api-m19.md).

The publication audit reruns the available suite; see [release validation and audit](docs/public-release-audit.md) for its actual results. Differential replay reuses recorded genuine original captures where stated; it is not a new run of the original compiler for every fixture. Local expectations alone do not prove equivalence. The known `upper-bound-error` difference retains useful nonzero error termination in BlitzCS. See [differential validation](docs/differential.md) and [M16 evidence](docs/evidence/m16-validation.json).

Audio evidence includes non-silent PCM measured after SDL's disk output and playback tests. It does not prove audible sound at a physical speaker. The FMOD comparison records deliberate and unresolved differences separately.

## Modern Windows runtime and SCP: Containment Breach

The Windows x64 and x86 packages use a managed runtime instead of translating the legacy engine: Direct3D 11 for graphics, SDL3 for window, input and audio output, and an FSOUND compatibility layer decoding through libsndfile (no FMOD binary is used). The command catalog is chosen from the compilation target and binds Blitz3D commands to managed implementations. Details, the compatibility boundary and the validation plan are in [the modern Windows runtime](docs/windows-runtime-modernization.md); the Direct3D renderer needs Windows, or Wine/Proton on Linux.

SCP: Containment Breach is the real-world compatibility case study. Its original sources and assets are **not distributed here**. With the unchanged sources, the runtime catalogs validate with no unresolved external call for Win64 and Win32, and `build` produces a self-contained Windows x64 package (about 19 seconds, a ReadyToRun game assembly). Under Proton on Linux the game loads, reaches the main menu, starts a new game and renders the world; the author's manual play found doors, interactions, NPCs, collisions and the options menu working. This is a manual validation, not a certified full playthrough, and some behaviours differ from the original (see below). Frame rates are development-machine observations, not controlled benchmarks: the author saw peaks near 490 FPS under Proton. The frame profiler and per-phase counters used to find bottlenecks are part of the runtime.

Building the package on a clean checkout needs the native Windows runtimes (not stored in git), the generated catalogs and the game's own sources and assets:

```bash
dotnet build BlitzCS.slnx -c Release --nologo
python3 tools/install_sdl3.py            # pinned official SDL3 release, SHA-256 checked, needs network
python3 tools/install_libsndfile.py      # pinned official libsndfile release, SHA-256 checked
dotnet run -c Release --project tools/GenerateWindowsRuntimeCatalog -- artifacts/windows-runtime-catalog artifacts/native
dotnet run -c Release --project src/BlitzCS.CLI --no-build -- build /path/to/SCP/Main.bb -o artifacts/scpcb \
  --externals artifacts/windows-runtime-catalog/scpcb.json --target windows-x64 --gui
```

Place the game's `GFX`, `SFX`, `Data` and `Loadingscreens` folders and its option files next to the produced `Main.exe`, then run it on Windows or with Proton. A smaller graphics example is [samples/ModernRuntime](samples/ModernRuntime/README.md). Measurement and diagnostic switches (`BLITZCS_ENGINE_PROFILE`, `BLITZCS_TRACE_*`) are described in the source of `FrameProfiler` and `EngineTrace`.

## Current limitations

- Native Windows (without Wine/Proton) has not been run. The Windows packages are validated on Linux under Proton with Direct3D 11 translation.
- The adapter is stricter than the original Blitz3D in some resource cases: locking the same buffer twice, unlocking an unlocked buffer, pixel access to a buffer that is not locked, drawing into the locked buffer, and closing files, directories or streams with an invalid handle raise errors where the original ignores them. `FreeImage`, `FreeFont`, `FreeTexture` and `FreeEntity(0)` already follow the original.
- Collisions follow the original per-object update order, but objects are visited in the engine's persistent root order, which differs from creation order after entities are freed. Picking uses a spatial index that is bit-identical to a full scan except for a ray exactly tangent to a sphere and moving away.
- Some commands fail explicitly instead of approximating, for example texture flags 64 and 128 and keyed or skinned animation in X models. Video playback uses Media Foundation and could not decode the game's AVI files under Proton; the game's startup videos are disabled in its options.
- Direct array/object interoperability is outside the public primitive `BB.` bridge. Floating-point boundaries, encodings and other historical corner cases still need broader differential coverage. `Print` targets the console on the language-only path, while original Blitz3D draws to a graphical surface.
- Experimental alternatives exist but are not the supported path: the [original-runtime provider](providers/OriginalBlitz3D/README.md) runs generated managed BASIC inside the unmodified Blitz3D `runtime.dll`, and the deprecated BCSBRE translates legacy PE32 libraries (see [Windows native status](docs/windows-native-status.md)).

## Roadmap

1. Run and validate the Windows packages on native Windows, and add the Direct3D smokes to automated verification on a machine with a display.
2. Continue the full SCP: Containment Breach validation (longer play sessions, audio, save and load) and document the measurement method for frame time and loading.
3. Close the remaining differences from the original runtime listed above, and broaden differential coverage of language and runtime corner cases without changing original application sources.
4. Document the clean-checkout build flow in the Windows build guide, and keep the diagnostic and measurement tools described.

These are future directions, not implemented features or delivery guarantees.

## Contributing and project history

Read [CONTRIBUTING.md](CONTRIBUTING.md). Existing commits and milestone tags are retained. Historical research notes and reports are point-in-time records; some are in Portuguese and contain development-machine paths.

## License and credits

Copyright 2026 Marcelo Buratti de Freitas Filho. Project created and led by Marcelo Buratti de Freitas Filho.

Original BlitzCS and Blitz3DCS contributions in this repository are licensed under the **Apache License 2.0**, subject to the explicit scope and exclusions in [LICENSING.md](LICENSING.md). See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Third-party code, dependencies, excerpts and separately licensed fixtures retain their own terms.

Blitz Research Ltd and the original Blitz3D authors provided the legacy compiler/runtime reference. Microsoft and .NET contributors provide .NET and Roslyn; SDL, libsndfile and xUnit contributors provide important runtime and test dependencies. SCP: Containment Breach and its contributors provide a separately licensed compatibility target. These acknowledgments do not imply endorsement or transfer of ownership.

## Legacy window and input host

The [M20 input example](samples/LegacyInput/window-input.bb) opens an SDL3 window and processes native keyboard/mouse events through typed external bindings, with shared BASIC/C# state. See [setup, contracts and verified limitations](docs/m20-windowing-input.md). On its own this sample supplies no 3D rendering; on Windows the Direct3D 11 runtime draws into the same SDL3 window.

Historical `.decls` discovery, catalog composition and optional binding traces are documented in [legacy dependency resolution](docs/legacy-dependency-resolution.md); [SCP:CB results](docs/scp-legacy-dependency-results.md) distinguish semantic acceptance from executable generation.
