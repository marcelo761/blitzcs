# Original Blitz3D runtime provider (experimental Windows x86)

This provider runs BlitzCS-generated managed BASIC inside the **unmodified original Blitz3D runtime.dll**. Both the compiler host and the application execute in .NET 10 x86, including under Proton. There is no IPC, replacement renderer, or BCSBRE translation. The normal compiler's Windows x64 default is unchanged.

The SDK is supplied separately; no original binary or BASIC source is included here. Build on Linux or Windows with the .NET 10 SDK:

```sh
python3 providers/OriginalBlitz3D/generate.py
dotnet publish providers/OriginalBlitz3D/Host/Host.csproj -c Release -r win-x86 -p:PlatformTarget=x86 --self-contained -o artifacts/original-host-x86
```

On Windows, use the published host to compile an unchanged source into a managed assembly, then execute it inside the original runtime's initialization callback:

```text
BlitzCS.OriginalRuntime.Host.exe build C:\SDK\help\commands\3d_examples\CreateCube.bb -o C:\output\CreateCube.dll --externals C:\BlitzCS\providers\OriginalBlitz3D\runtime.json
BlitzCS.OriginalRuntime.Host.exe --run C:\output\CreateCube.dll C:\SDK\bin\runtime.dll --capture C:\output\CreateCube.bmp
```

Press Escape to end this particular original sample. The optional capture reads the actual original front buffer after the BASIC entry point returns, before native shutdown. It uses LockBuffer/ReadPixelFast/UnlockBuffer and writes a BMP. `--log <file>` before either command captures managed diagnostics/output directly, useful under Proton.

On Linux, run those same Windows commands through `proton run`, with an experiment-owned `STEAM_COMPAT_DATA_PATH` and the Steam installation in `STEAM_COMPAT_CLIENT_INSTALL_PATH`. Pass filesystem arguments as Windows `Z:\...` paths. Use a real display or an owned Xvfb display; the original motor requires working 32-bit DirectDraw/Direct3D/DirectInput dependencies. Do not use a shared Proton prefix for the automated test.

Reproduce the original SDK rendering test (host already published, Proton prefix already initialized):

```sh
python3 tools/probe_original_runtime.py --sdk /path/to/Blitz3D-V1.118 --host artifacts/original-host-x86/BlitzCS.OriginalRuntime.Host.exe --proton /path/to/proton --compatdata /path/to/owned/compatdata --steam /path/to/Steam --display :101 --out artifacts/original-runtime-test
```

The test compiles the original `CreateCube.bb` using the x86 BlitzCS host, runs it with the original DLL, sends Escape to its native window, captures the front buffer, verifies nonblack pixels against the background, and checks source/runtime hashes. A nonzero exit, missing frame, or black frame fails the test. `xdotool` is required; start Xvfb separately when using a virtual display. The runner terminates only processes in its own test process groups on failure.

## Scope and native ABI

The generated catalog composes the existing historical profile and provides 532 bindings, including strings. Blitz3D strings are owned C++ objects, not C strings: the adapter uses the original engine's construction, copying, concatenation, conversion and release helpers, including for embedded NULs. Missing registrations and signature disagreements fail explicitly before the native call. Generation establishes bindings, not execution coverage for every command.

The factory/lifecycle interface is defined in the pinned original [runtime header](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/bbruntime_dll/bbruntime_dll.h) and [runtime implementation](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/bbruntime_dll/bbruntime_dll.cpp). The provider resolves `runtimeGetRuntime` (cdecl), follows the original `Runtime` vtable (thiscall), enumerates registrations using each original symbol pointer, initializes with `startup`, and invokes the BASIC entry inside `execute` through a cdecl callback. Registered commands and string helpers use **stdcall**, with single-precision float arguments/returns and opaque 32-bit handles. The original [runtime project](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/bbruntime_dll/bbruntime_dll.dsp) specifies `/Gz`; declarations without an explicit convention therefore are not cdecl. Runtime signatures are checked before constructing a cached native call. No C++ object layout is reimplemented. Managed exceptions return through the callback before being rethrown after native cleanup.

Native stack measurements confirmed callee cleanup in both the stock SDK and SCP runtime: `Graphics3D` removes 16 argument bytes, `PositionEntity` removes 20, and the one-argument string helpers remove four. A cdecl binding can appear to work yet corrupt execution state and fail later during GC. Pass `--gc-regression` to `tools/probe_original_runtime.py` to exercise native string/bank calls under allocation pressure and verify a rendered frame, in addition to the unchanged SDK sample.

Separate catalogs provide the original SCP runtime's additional registrations, Win32 bank/object adapters, and calls to the real FMOD, BlitzMovie and zlib DLLs. The unchanged SCP:CB 1.3.11 source compiled with these bindings and reached the rendered initial room after New Game; interactive operation was also confirmed by the user. This is Windows .NET x86 execution using the original engine, not x64 BCSBRE execution or exhaustive SCP compatibility.

Native runtime errors can unwind C++ exceptions; crossing the CLR/native boundary is not established as safe. Startup movie decoding still depends on native codecs; the demonstrated SCP run disabled startup videos only in its isolated configuration. Nested runtime execution remains unsupported. Original code is trusted native code and is not sandboxed.

## Measured execution

With Blitz3D V1.118 runtime SHA-256 `ba120a4a0f15f2ac48e1e86f6465137bd480d0b4a288cae2534a6fe0ecad1a4e`, Proton 11.0 and .NET 10.0.12 x86:

- A BlitzCS-compiled numeric camera/light/colored-cube probe ran and returned center pixel `-8445936`, background `-16777216`, exit 0.
- The unchanged original `help/commands/3d_examples/CreateCube.bb` (SHA-256 `3a7a80b91580deff8f0d6d69ebdb9f7e0250972d03a85bab1002a5c0f4bdd2be`) compiled and executed with exit 0. Its real 640×480 front buffer had 25,600 nonblack pixels, center RGB `(127,127,127)` and corner RGB `(0,0,0)`. The front-facing cube appears square in this original sample.
- The runtime reported version `132190`, 578 registrations and a 32-bit process. Original source and DLL hashes were unchanged.

These are real original-engine x86 executions of BlitzCS-generated BASIC. They do not demonstrate a BCSBRE-recompiled x64 engine, original IDE execution, or full SCP:CB compatibility.

`--inspect-runtime <runtime.dll> <new-snapshot.json>` reads the actual registration table without executing a BASIC program. The output must be new, and native dependencies must be available. Use `tools/generate_runtime_profile.py` to compare this snapshot with an existing historical profile; descriptors are declarations, not inferred executable bindings.
