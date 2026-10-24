# Windows x64 application builds

The normal application build selects Windows x64 regardless of the compiler host:

```bash
dotnet run --project src/BlitzCS.CLI -- build Main.bb
```

The output is `dist/Main/Main.exe` relative to the command's working directory, together with the .NET 10 Windows runtime, managed dependency closure, declared native dependencies and translated BCSBRE artifacts. Distribute the entire directory. No target option or installed .NET runtime is required on the destination machine. Publishing follows the [official .NET self-contained deployment mechanism](https://learn.microsoft.com/dotnet/core/deploying/#publish-as-self-contained), with the original Roslyn-emitted application assembly and an SDK-generated Windows apphost. The publisher uses a temporary SDK shell to resolve deployment assets, then installs the original application DLL and symbols; this preserves implementation-bound BCSBRE bridge metadata without recompiling it against reference-pack facades. Compilation on Linux needs the .NET 10 SDK and available `win-x64` runtime/apphost packs (NuGet access on first restore); Wine is used only for execution tests.

For the short `blitzcs` command, package and install the CLI as a local .NET tool:

```bash
dotnet pack src/BlitzCS.CLI -c Release -o artifacts/tools
dotnet tool install BlitzCS.Tool --tool-path artifacts/bin --add-source artifacts/tools
export PATH="$PWD/artifacts/bin:$PATH"
blitzcs build Main.bb
```

On Windows, add the corresponding `artifacts/bin` directory to `PATH`. The SDK must remain installed on the compiler host.

## Options and deployment contracts

* `-o <directory>` changes the package directory. A `.exe` output spelling selects a package subdirectory named after that executable. Existing directories are replaced only if they carry BlitzCS's package ownership marker; failed compilation/publishing preserves the old package. Keep application inputs and assets outside the output directory.
* `--target linux-x64` explicitly publishes a Linux x64 self-contained package. `--target win-x64` and `--target windows-x64` select Windows. The application's target also selects BCSBRE's ABI; catalog/environment binary defaults cannot silently change a Windows application into a Linux native artifact.
* `--externals <catalog.json>` uses the existing typed managed/native/BCSBRE catalog. Managed assemblies and their same-directory managed dependencies are included. Declare every required direct native dependency in the catalog; the publisher does not infer arbitrary DLL imports. Direct native libraries must match the selected platform and x64 architecture. Declare legacy x86 PE32 input in `binaryLibraries`, never as a direct native library.
* `--assets <directory>` copies that directory's contents into the package, preserving relative paths. Declare the asset root explicitly so a build never recursively packages the source repository or unrelated files. Case-insensitive Windows filename collisions with dependencies are errors. The application starts with its working directory set to its own package directory, including when launched from elsewhere; subsequent application `ChangeDir` behavior remains available.
* Console startup is the default. `--gui` requests a Windows GUI subsystem apphost; this selects the process subsystem and does not implement a renderer, window system or graphical APIs.
* Explicit `-o <file.dll>` preserves the existing low-level framework-dependent emission workflow for tooling and regression tests. It does not produce a deployable self-contained package. Asset and GUI options require package output.

The runtime is neither trimmed nor bundled into one file: native DLLs and `.bcx` guest artifacts retain stable paths. Original BlitzBasic input is never modified. Existing optional runtime/engine bindings remain catalog dependencies, not compiler dependencies.

## Verification

Run the package integration tests explicitly (they restore target runtime packs):

```bash
BLITZCS_TEST_PUBLISH=1 dotnet test tests/BlitzCS.CLI.Tests --filter ApplicationPackageTests
```

Use `scripts/wine/verify-application-package.sh` for the default build and Wine execution checks. The script requires the existing Wine/Proton test environment; it is not part of compiling applications. Its console/file/asset/native-call probe uses the supported hybrid C# bridge. It does not establish support for unimplemented historical BASIC APIs.

See [Windows native-library status](windows-native-status.md) for BCSBRE contracts and measured historical-library limits. Packaging does not imply FMOD playback, functional graphics/input or full SCP:CB compatibility.

On 2026-10-24 the full Linux solution test run completed without failures (1,456 passed; 25 explicit skips). The opt-in package/Wine suite subsequently passed all five cases without skips, covering Windows console/GUI apphosts, Linux execution, rebuild preservation, relative asset access from another launch directory, console input, file write/read, a kernel32 call and a catalog-managed dependency. The separate BCSBRE package regression also built and executed successfully under Wine. The I/O probe uses the hybrid bridge; historical library fixture coverage and missing fixtures are reported separately in the native status document.
