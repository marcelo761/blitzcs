# BlitzCS research baseline

Research snapshot: 2026-10-20. The checkout was empty when this note was prepared; the upstream repositories below were inspected at the listed shallow-clone commits. No upstream source was copied.

## First executable scope

The immediate vertical slice is a source-to-source compiler: `.bb` text → tokenizer/parser/typed subset → generated C# → .NET 10 executable. Keep source syntax and Blitz semantics in the compiler/runtime boundary; generated C# is an implementation target, not the compatibility definition. The language level can be pinned to C# 14 on .NET 10 (Microsoft documents C# 14 as supported by .NET 10). [C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14) · [.NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview) · [Roslyn](https://github.com/dotnet/roslyn)

Implemented initial modules: `BlitzCS.Compiler` owns source locations, lexer, parser, typed IR and C# emission; the CLI owns input/output paths and hosts Roslyn compilation. No runtime project is needed by the console validation host yet. See architecture.md for the current contracts; these are not a commitment to future graphics or interop APIs.

## Primary-source observations

| Reference at inspected commit | Evidence and relevance | License evidence |
|---|---|---|
| [blitz-research/blitz3d `6beb288`](https://github.com/blitz-research/blitz3d/tree/6beb288cb5962393684a59a4a44ac11524894939) | Main semantic reference. `compiler/toker.cpp` defines case-insensitive keywords, `;` line comments, newline/colon statement boundaries, decimal integer/float, `%` binary and `$` hexadecimal literals, and string tokenization. `compiler/parser.cpp` implements precedence and parses integer/float/hex/binary constants. `bbruntime/bbruntime.cpp` maps `End` to `bbEnd`; `bbruntime/bbgraphics.cpp` registers `Print$string=""` and implements it by drawing text at the current cursor, resetting X and advancing Y by font height. Thus original `Print` is graphics text, not inherently console output. | Root `LICENSE.TXT` says zlib/libpng. |
| [blitz3d-ng/package `d8d5d1b`](https://github.com/blitz3d-ng/package/tree/d8d5d1be34dfac328e15d9e9b831d9572cde9d0f) | README describes LLVM-powered code generation added to the original compiler; test suite is `test/all.bb`. Its license text says zlib/libpng. Useful modernization and regression-test reference, not a semantic authority over original Blitz3D. | Root `LICENSE.TXT` says zlib/libpng. |
| [Team-X3D/BlitzX3D `1e0fd1f`](https://github.com/Team-X3D/BlitzX3D/tree/1e0fd1f53dc3a781941650b22cf28f6000b9c134) | README describes a community-maintained Blitz3D fork with DirectX 9, newer editor/debugger, profiling, scene and rendering changes. Treat its additions as fork-specific until checked against the original. | Root `LICENSE` inspected; it contains the zlib/libpng notice. Additional `LICENSE-ZLIB.txt` exists. Bundled dependencies have separate notices. |
| [RydeTec/blitz-forge `bdbfd9f`](https://github.com/RydeTec/blitz-forge/tree/bdbfd9ff154b2a65b8c458b52bec524b23e4e81e) | README claims a cross-platform compiler/runtime, and explicitly marks macOS runtime execution incomplete/alpha-stub while compile/translation/assembly pipeline can be exercised. `LICENSES.md` is a map rather than one root license; it points inherited source to `src/blitzrc/LICENSE.TXT` (zlib/libpng) and lists separate third-party licenses. Do not infer completeness from project descriptions. | License must be evaluated per component; the map identifies core and bundled notices. |
| [Roslyn](https://github.com/dotnet/roslyn) and [Microsoft C# guide](https://learn.microsoft.com/en-us/dotnet/csharp/) | Roslyn is the C#/VB compiler platform and analysis API. It is an optional route for parsing/validating generated C# or hosting compilation; invoking the installed `dotnet` SDK is a simpler first executable path. C# specification/reference is authoritative for generated C# syntax and behavior, not Blitz behavior. | The repository's [`License.txt`](https://github.com/dotnet/roslyn/blob/main/License.txt) is MIT. Check package versions and transitive dependencies before adding it. |
| [.NET 10 docs](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview), [C# 14 docs](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14) | .NET 10 is documented as LTS; C# 14 is supported on .NET 10. Pin target framework and language version explicitly in project configuration once implementation begins. | Microsoft docs are documentation sources, not code dependencies. |
| [SDL3 GPU category](https://wiki.libsdl.org/SDL3/CategoryGPU) | SDL GPU provides device, buffers, textures, shaders, pipelines, command buffers and render passes over Vulkan, D3D12 and Metal-style backends. The docs state backend-specific shader formats and list platform requirements. It is lower level than a scene/entity renderer but offers a portable GPU interface. | License and bundled dependency notices were not verified in this pass; inspect the exact SDL release before adoption. |

## Preliminary graphics options (no selection)

SDL GPU is a portable low-level interface and could reduce platform-specific device/pipeline setup, while leaving Blitz3D entity, material, animation and draw ordering semantics to BlitzCS. Raw Vulkan exposes a platform graphics API directly and would require more explicit resource, synchronization, shader and platform work; it offers control but creates a larger initial implementation burden. Blitz3D's own existing GX abstraction is a source reference for what the legacy engine organized (not a current cross-platform .NET package); Blitz3D NG's README reports multiple renderer/platform goals, not proof of a drop-in abstraction. SDL's simpler Render API is a different, higher-level option and may be insufficient for the later 3D contract. Evaluate on supported targets, shader pipeline, integration cost, licensing and compatibility needs before choosing. [SDL GPU](https://wiki.libsdl.org/SDL3/CategoryGPU) · [Blitz3D NG README](https://github.com/blitz3d-ng/package/blob/d8d5d1be34dfac328e15d9e9b831d9572cde9d0f/README.md)

## Scope findings

Scope implementation was checked against the same original commit:
[`compiler/environ.cpp`](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/compiler/environ.cpp)
separates local lookup from enclosing globals;
[`compiler/declnode.cpp`](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/compiler/declnode.cpp)
binds declaration initializers before inserting the declaration and creates
function parameters/return defaults;
[`compiler/parser.cpp`](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/compiler/parser.cpp)
parses `Function` / `End Function` and `Return`. The bounded integer implementation
follows these observed rules; this does not replace differential execution.

## Research not yet completed

This baseline does not document the complete grammar, type system, arrays, memory model, entity/animation/collision behavior, or all engine APIs. The `Print` observation comes from the original runtime registration and implementation; the initial executable uses a documented console validation host, not a graphical compatibility implementation. Establish differential fixtures against the original compiler/runtime before making broader equivalence claims. No original executable was run here.
