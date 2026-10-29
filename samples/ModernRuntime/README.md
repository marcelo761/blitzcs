# Modern Windows runtime sample

`cube.bb` is a small Windows-targeted Blitz BASIC program using the managed
SDL3 window host and the Direct3D 11 graphics runtime. It exercises the current
camera, cube mesh, render, and frame-present path. It copies a mesh, frees the
original, changes from perspective to orthographic projection, and reports
elapsed milliseconds. The target catalogs
bind implemented managed commands directly; this path does not require the
binary-translated runtime.

Install the matching SDL3 and libsndfile DLLs for both Windows architectures,
then build the catalog's base managed assemblies, generate the target catalogs,
and compile the sample for Win32 and Win64:

```sh
python3 tools/install_sdl3.py
python3 tools/install_libsndfile.py
dotnet build src/Blitz3DCS.Runtime/Blitz3DCS.Runtime.csproj -c Release
dotnet build src/BlitzCS.LegacyRuntime/BlitzCS.LegacyRuntime.csproj -c Release
dotnet run -c Release --project tools/GenerateWindowsRuntimeCatalog \
  -- artifacts/windows-runtime-catalog artifacts/native
dotnet run -c Release --project src/BlitzCS.CLI -- build \
  samples/ModernRuntime/cube.bb -o artifacts/modern-cube-win32 \
  --externals artifacts/windows-runtime-catalog/runtime.json --target windows-x86
dotnet run -c Release --project src/BlitzCS.CLI -- build \
  samples/ModernRuntime/cube.bb -o artifacts/modern-cube-win64 \
  --externals artifacts/windows-runtime-catalog/runtime.json --target windows-x64
```

The generated manifest selects the Win32 or Win64 command catalog and matching
native dependencies. The package build stages the matching `SDL3.dll` and
`sndfile.dll`; include copies of their licenses beside each distributed build:
The libsndfile release's codec-notice inventory is incomplete; see
[`docs/install-sdl3.md`](../../docs/install-sdl3.md#codec-notice-status) before
redistributing a package that includes `sndfile.dll`.

```sh
cp artifacts/native/sdl3/win-x86/LICENSE.txt artifacts/modern-cube-win32/SDL3-LICENSE.txt
cp artifacts/native/libsndfile/win-x86/COPYING artifacts/modern-cube-win32/libsndfile-COPYING.txt
cp artifacts/native/libsndfile/win-x86/ALAC-LICENSE.txt artifacts/modern-cube-win32/ALAC-LICENSE.txt
cp artifacts/native/libsndfile/win-x86/OPUS-COPYING.txt artifacts/modern-cube-win32/OPUS-COPYING.txt
cp artifacts/native/sdl3/win-x64/LICENSE.txt artifacts/modern-cube-win64/SDL3-LICENSE.txt
cp artifacts/native/libsndfile/win-x64/COPYING artifacts/modern-cube-win64/libsndfile-COPYING.txt
cp artifacts/native/libsndfile/win-x64/ALAC-LICENSE.txt artifacts/modern-cube-win64/ALAC-LICENSE.txt
cp artifacts/native/libsndfile/win-x64/OPUS-COPYING.txt artifacts/modern-cube-win64/OPUS-COPYING.txt
```

The generator also emits `scpcb.json` and target leaves `Win32.scpcb.json` and
`Win64.scpcb.json`. These leaves omit only `EntityScaleX`, `EntityScaleY`, and
`EntityScaleZ`, because SCP defines source helpers with those names. This keeps
the normal runtime catalogs unchanged while allowing the SCP sources to retain
their own implementations. Select that profile explicitly when compiling SCP:

```sh
dotnet run -c Release --project src/BlitzCS.CLI -- build \
  /path/to/SCP/SourceOriginal/Main.bb -o artifacts/scpcb-win64 \
  --externals artifacts/windows-runtime-catalog/scpcb.json --target windows-x64
```

The SCP profile resolves the same managed assemblies and native dependencies as
the general profile; it is a command-binding compatibility selection, not a
claim that all SCP APIs or behavior are implemented.

`CopyEntity` preserves hierarchy names, local transforms, and child order.
Mesh copies share geometry and the surface list, matching the legacy mesh
representation: vertex edits and newly created surfaces are visible through
both copies. Entity transforms remain independent. Geometry is released after
the final mesh reference is freed. Copying a camera-containing hierarchy is
explicitly unsupported until camera-copy semantics are validated.

`CameraProjMode` disables a camera with mode 0, selects orthographic projection
with mode 2, and uses perspective for other nonzero modes. `CameraZoom` defaults
to 1 and follows the legacy frustum scale. Multiple camera entities are permitted
when at most one is enabled; multiple-camera composition remains unsupported.
`MilliSecs` returns system uptime as a wrapping 32-bit BASIC integer, using the
legacy Windows timer on Windows targets.

This is an initial graphics slice, not a complete replacement for the legacy
Blitz3D command set. It currently supports one active camera and opaque, unlit
vertex-colored surfaces. Legacy lights, brush/material state, transparency
ordering, multiple-camera composition, and other unimplemented APIs are not
provided by this sample runtime.

The Win32 and Win64 target builds ran under Wine/Proton, and backbuffer
readback plus visible presentation were verified for both. The rotating cube
appeared in the SDL client under Wine/XWayland after rendering and presentation.
A capture taken immediately when the window first appears can show the
pre-render black frame, so visual checks should wait until after the first
`Flip`.
