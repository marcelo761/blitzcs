# BlitzCS

BlitzCS is an experimental, general-purpose BlitzBasic language compiler and interoperability infrastructure for .NET 10. It compiles BASIC and embedded C# into one assembly through Roslyn, with typed calls and shared state in both directions.

Created and led by **Marcelo Buratti de Freitas Filho**, the project explores how legacy BASIC programs can move to a managed runtime while integrating modern C# and native libraries. Compatibility research uses the original Blitz3D compiler and real legacy projects, including SCP: Containment Breach. These are reference targets, not a restriction on what BlitzCS can be used to build.

BlitzCS is an independent project. It is not an official Blitz Research, Microsoft, FMOD, or SCP project.

## Quick start

For the verified full-solution build, use **Linux**, the **.NET 10 SDK**, and a **C compiler available as `cc`**. The native integration test project explicitly requires Linux. Run these commands from a checkout of this repository:

```bash
dotnet restore BlitzCS.slnx
dotnet build BlitzCS.slnx -c Release --nologo
dotnet run --project src/BlitzCS.CLI -c Release --no-build -- build samples/Hybrid/answer.bb -o artifacts/answer.dll
dotnet artifacts/answer.dll
```

The program prints `42`. The compiler also writes generated C#, portable debugging symbols and runtime configuration alongside the assembly. Generated programs need .NET 10; compilation needs the SDK and Roslyn. External runtime libraries must accompany programs that use them.

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
| `BlitzCS.LegacyRuntime` | Optional legacy error service |
| `Blitz3DCS.Runtime` | Engine-specific legacy file, string and system services |
| `Blitz3DCS.Audio` | FSOUND compatibility layer, SDL3 output and managed PCM mixing |

**BlitzCS is language infrastructure; Blitz3DCS is an engine integration built on it.** The compiler has no dependency on the Blitz3DCS projects. Other engines can register their own typed external functions without adopting Blitz3D APIs.

See [compiler architecture](docs/architecture.md), [external functions](docs/external-functions.md), [native interoperability](docs/native-interop-m13.md) and the [proposed Blitz3DCS architecture](docs/blitz3dcs-architecture.md).

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

Typed catalogs separate three stages: **semantic declaration**, **binding to an implementation**, and **runtime execution**. A declaration can be checked before a library runs; emitting a used declaration without an implementation fails explicitly. Registered managed functions compile to direct static calls.

C/C++ integration uses explicit bindings, ABI contracts and resource ownership. It does not make arbitrary BASIC integers into native pointers. The [native sample](samples/NativeInterop) and its [contract](docs/native-interop-m13.md) exercise integers, floats, UTF-8 strings, library errors and resource release. A Win32 FMOD DLL cannot be loaded into the verified Linux x64 .NET process; the original FMOD is used separately under Proton as a compatibility reference.

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

At the M16 checkpoint (`216c025`, `blitzcs-m16-checkpoint`):

| Verification | Recorded result |
| --- | --- |
| .NET tests, Release and Debug | 611 passing in each configuration |
| Python harness tests | 40 passing |
| Local language fixtures | 86 passing |
| Differential language comparisons | 85 of 86 match captured original results |

The publication audit reruns the available suite; see [release validation and audit](docs/public-release-audit.md) for its actual results. Differential replay reuses recorded genuine original captures where stated; it is not a new run of the original compiler for every fixture. Local expectations alone do not prove equivalence. The known `upper-bound-error` difference retains useful nonzero error termination in BlitzCS. See [differential validation](docs/differential.md) and [M16 evidence](docs/evidence/m16-validation.json).

Audio evidence includes non-silent PCM measured after SDL's disk output and playback tests. It does not prove audible sound at a physical speaker. The FMOD comparison records deliberate and unresolved differences separately.

## Current limitations and SCP status

SCP: Containment Breach is a real-world compatibility case study. Its original sources and assets are **not distributed here**. The M16 audit records integrated syntax validation of `Main.bb` and its Include tree, with **3 of 30 files compiling independently**. Integrated semantic compilation stops at **`fullscreen_window_fix.bb:47:8`, `Windowed3D`**. The game assembly has not been emitted and the game does not run on BlitzCS. See [latest SCP audit](docs/system-runtime-m16.md).

There is no renderer, Vulkan backend, physics implementation or editor. Some native commands and external dependencies remain unavailable. `SystemProperty("AppHWND")` reports an unsupported capability without a real window host. Direct array/object interoperability is outside the public primitive `BB.` bridge. Floating-point boundaries, encodings and other historical corner cases still need broader differential coverage. `Print` targets the console here, while original Blitz3D draws to a graphical surface.

## Roadmap

1. Expand differential coverage and resolve remaining language and runtime gaps without changing original application sources.
2. Continue real external runtime implementations, dependency packaging and platform validation.
3. Develop Blitz3DCS independently, starting with a reviewed graphical vertical slice. Vulkan 1.4 is planned; Direct3D 12 and Metal are future possibilities.
4. Evaluate modern graphics, physics and other engine facilities in Blitz3DCS, without making them compiler dependencies.

These are future directions, not implemented features or delivery guarantees.

## Contributing and project history

Read [CONTRIBUTING.md](CONTRIBUTING.md). Existing commits and milestone tags are retained. Historical research notes and reports are point-in-time records; some are in Portuguese and contain development-machine paths. Trail-Code is used for optional repository context; its generated cache is not shipped as source. No Trail installation is required to build the compiler.

## License and credits

Copyright 2026 Marcelo Buratti de Freitas Filho. Project created and led by Marcelo Buratti de Freitas Filho.

Original BlitzCS and Blitz3DCS contributions in this repository are licensed under the **Apache License 2.0**, subject to the explicit scope and exclusions in [LICENSING.md](LICENSING.md). See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Third-party code, dependencies, excerpts and separately licensed fixtures retain their own terms.

Blitz Research Ltd and the original Blitz3D authors provided the legacy compiler/runtime reference. Microsoft and .NET contributors provide .NET and Roslyn; SDL, libsndfile and xUnit contributors provide important runtime and test dependencies. SCP: Containment Breach and its contributors provide a separately licensed compatibility target. These acknowledgments do not imply endorsement or transfer of ownership.
