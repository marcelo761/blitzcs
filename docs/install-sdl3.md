# SDL3 Windows runtime files

`Blitz3DCS.Audio` and `Blitz3DCS.Windowing` use SDL3. Install the official
Windows runtime binaries with:

```sh
python3 tools/install_sdl3.py
```

The script downloads SDL 3.4.18 release archives from the
[official SDL GitHub release](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.18),
checks their pinned SHA-256 values, validates each DLL's PE architecture, and
copies only `SDL3.dll` and `LICENSE.txt` into `artifacts/native/sdl3/`. The
default download cache is outside the repository; `--cache-dir` and
`--output-dir` can override the locations. The generated per-target manifest
records source and installed-file hashes. `artifacts/` is ignored by Git, so
these redistributable binaries are not included in source commits.

The 3.4.18 archive SHA-256 pins are:

| Target | Archive | SHA-256 |
|---|---|---|
| Windows x64 | `SDL3-3.4.18-win32-x64.zip` | `75c2c0fc74e7d1206aaedc22893a35887332f4f85f5c8b74fddc10d177985f2c` |
| Windows x86 | `SDL3-3.4.18-win32-x86.zip` | `99f2ee6e126d828c5e5b3d20201930f3d16a8c6e147d1de90a59f4ae6df138c9` |

To install only one target, pass `--target win-x64` or `--target win-x86`.
Linux distributions should use their package manager to install SDL3 for the
required process architectures.

The Windows audio decoder also uses libsndfile. Install its official x86 and
x64 runtimes with:

```sh
python3 tools/install_libsndfile.py
```

This pins the official [libsndfile 1.2.2 release](https://github.com/libsndfile/libsndfile/releases/tag/1.2.2),
verifies each archive checksum and PE architecture, and installs
`sndfile.dll`, the upstream `COPYING` license, and verified ALAC and Opus
license texts under
`artifacts/native/libsndfile/win-x86/` and `win-x64/`. The library is
LGPL-2.1-or-later; each installed manifest records archive, runtime, and
license hashes and labels the overall codec-notice inventory as incomplete.
Both release archives have detached signatures from the
release signer; the installer uses pinned SHA-256 digests for reproducible
verification.

### Codec notice status

The `COPYING` file above is the libsndfile license, not a complete inventory of
all code in the published DLLs. The inspected x86 and x64 release archives each
contain only `sndfile.dll`; their PE import tables name only `KERNEL32.dll`.
The DLLs' embedded strings indicate code for ALAC, FLAC, GSM, G.72x, Vorbis,
Opus (including an Opus 1.4 source path), mpg123, and LAME 3.100. This is
consistent with codec implementations being built into the DLL. However, the
archives provide no codec source manifest, dependency versions, configuration,
or per-codec notices. The release README describes Windows build machinery,
while the DLL strings also retain vcpkg build paths; the upstream 1.2.2 source
manifest is not sufficient to identify the exact build of these binaries.

Therefore the installed libsndfile `manifest.json` records the libsndfile
license and two verified embedded-code licenses, but does not claim complete
third-party notice coverage. The current Windows packages are not ready for
redistribution as a fully audited codec bundle. Close this gap with upstream
build provenance or a reproducible build whose codec versions, options,
license texts, and hashes are captured in the package output. See the upstream
[1.2.2 README](https://github.com/libsndfile/libsndfile/blob/1.2.2/README),
[1.2.2 CMake configuration](https://github.com/libsndfile/libsndfile/blob/1.2.2/CMakeLists.txt),
and [1.2.2 vcpkg manifest](https://github.com/libsndfile/libsndfile/blob/1.2.2/vcpkg.json).
