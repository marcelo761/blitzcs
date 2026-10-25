# FMOD 3.75 from a BlitzBasic program

`main.bb` is plain BlitzBasic. It calls the **original, unmodified FMOD 3.75 `fmod.dll`** (a 32-bit x86 DLL) through the catalog `fmod.json`; the compiler translates the
DLL to x64 at build time and the generated Windows application runs the translated code in its own process. No FMOD source, no wrapper DLL and no FMOD binary is in this
repository: you supply your own `fmod.dll`.

## Files

| File | Purpose |
| --- | --- |
| `main.bb` | the program: version, output selection, `FSOUND_Init`, load a WAV, play / stop / play to the end, `FSOUND_Close` |
| `fmod.json` | the catalog: one `binaryLibraries` entry and 17 `FSOUND_*` declarations bound to the DLL's decorated stdcall exports |
| `make_tone.py` | writes `assets/tone.wav` (440 Hz sine, 16-bit mono, 44.1 kHz, 1.0 s), the file `main.bb` loads |

## Build and run

Put your own `fmod.dll` (FMOD 3.75, the UPX-packed build is supported) next to `fmod.json`, then:

```bash
python3 make_tone.py                                        # assets/tone.wav
blitzcs build main.bb --externals fmod.json --assets assets # -> dist/main/main.exe (Windows x64, self-contained)
```

The result is the directory `dist/main/` (copy all of it). It contains the .NET runtime, the generated assembly, `fmod.dll` byte for byte, its translation `fmod.dll.bcx`, the
unpacked image `fmod.dll.derived` with its `fmod.dll.lineage.json`, the runtime and Win32 components, and `tone.wav`. On Windows run `dist\main\main.exe`. On Linux, under Wine:

```bash
. scripts/wine/env.sh && (cd dist/main && wine main.exe)    # from the repository root; see docs/wine-testing.md
```

Expected output (NOSOUND, the default):

```
version 3.75
mixer before init 4
set output 1
output 0
drivers 1
set driver 1
init 1
error 0
mixer resolved 1
sample 1
samples 44100
mode 305
channel 1
playing 1
volume 1
playing 1
stop 1
playing 0
finished 0
error 0
closed
```

`--target linux-x64` builds the same program as a Linux x64 package instead (`dist/main/main`); the Linux test run builds it that way.
The DLL path in `fmod.json` is relative to the catalog. The test suite does not copy your DLL: it writes a copy of `fmod.json` into a temporary directory with the absolute path of the
file named by `BLITZCS_PACKED_SAMPLE`.

## Output type: one constant

```basic
Const OUTPUT_TYPE = FSOUND_OUTPUT_NOSOUND   ; 0: FMOD's silent mixer. FSOUND_OUTPUT_WINMM (1): waveOut
```

NOSOUND runs FMOD's mixer in real time without opening a device (`finished 0` after 1.3 s shows the mixer consumed the one-second sample). `FSOUND_OUTPUT_WINMM` makes FMOD open a
waveOut device; the Win32 component forwards it according to the library's `audio` option (see `docs/binary-audio.md`):

| catalog `binaryLibraries` key | effect |
| --- | --- |
| `audio` absent | the Win32 component's default: the host's sound device on Windows (and Wine), no devices elsewhere |
| `"audio": "host"` | waveOut through the host's `winmm.dll` (Windows only) |
| `"audio": "virtual"` | an in-process sink that consumes audio in real time; no sound device is touched (headless runs, tests) |
| `"audio": "none"` | waveOut reports zero devices |
| `"audioRecord": "name.wav"` | also write what the library plays to `name.wav` beside the application |
| `"fileRoot": "app"` | the library's file APIs see the application directory as `C:\` (needed to read `tone.wav`); the default is no file access |

`BLITZCS_AUDIO=host|virtual|none` in the environment overrides `audio` at run time.

Known limits of the WINMM path today (details and evidence in `docs/windows-integration-audit.md`, F-11 and F-12): `FSOUND_Close` with WINMM output reaches a code address the
translator does not discover (the program ends without calling it in the tests), and the virtual sink's recording of FMOD's looped buffer is silent although the program plays it.
Neither affects NOSOUND.

## Where the signatures come from

Every declaration is checked at build time against the DLL's export table: the stdcall decoration `_FSOUND_X@N` fixes the argument bytes (N/4 arguments), and `exportConventions`
repeats the count so a wrong declaration fails the build.

| Function | Export | Evidence |
| --- | --- | --- |
| `FSOUND_GetVersion()` returns Float | `_FSOUND_GetVersion@0` | float in ST0 (`docs/binary-linking.md`); prints 3.75 |
| `FSOUND_SetOutput(type)`, `SetDriver(id)`, `Init(rate, channels, flags)` | `@4`, `@4`, `@12` | `docs/fmod-audit-m14.md`, `docs/native-original-m13.md`; return `signed char`, so the program masks with `And 255` |
| `FSOUND_Close()`, `Sample_Free(s)` return nothing | `@0`, `@4` | header `void` (`docs/fmod-audit-m14.md`) |
| `FSOUND_Sample_Load(index, name, mode, offset, length)` | `_FSOUND_Sample_Load@20` | five stack words (index, file name, mode, offset, length); the load succeeds and reports 44100 samples with this order. `index = -1` (`FSOUND_FREE`) as in `samples/Audio/playback.bb` (`FSOUND_Stream_Play(-1, ...)`) |
| `FSOUND_PlaySound(channel, sample)`, `StopSound(ch)`, `IsPlaying(ch)`, `SetVolume(ch, vol)` | `@8`, `@4`, `@4`, `@8` | `usage-audit.json` lists `SetVolume@8`, `IsPlaying@4`, `StopSound@4` |
| `GetError()`, `GetOutput()`, `GetMixer()`, `GetNumDrivers()` | `@0` | `docs/evidence/m14-fmod/init-semantics.json` (error codes), observed values below |
| `FSOUND_Sample_GetLength(s)`, `Sample_GetMode(s)` | `@4` | read back what FMOD parsed |
| mode `304` = `FSOUND_NORMAL` | | `GetMode` returned 305 for 304, 305 for 0, 306 for 2 and 306 for 306: bit 16 = 16-bit, 32 = mono, 256 = signed, 1 = loop off, 2 = loop normal |
| output `0` = NOSOUND, `1` = WINMM | | `docs/native-original-m13.md` (`verified-probe.json`) for 0; with 1 FMOD opens a waveOut device and the sink consumes its buffers in real time |
| `GetMixer()` is 4 before `Init`, 7 after | | observed; 4 is the "autodetect" value, so `mixer resolved` prints `GetMixer() <> 4` rather than a CPU-dependent number |

`Delay` is not part of the compiler's runtime; `main.bb` defines it with a three-line C# bridge (`<?cs module ... ?>`, see `docs/hybrid.md`).

## Tests

`tests/BlitzCS.Linking.E2E.Tests/FmodDemoTests.cs` builds this sample (the DLL named by `BLITZCS_PACKED_SAMPLE`, a WAV generated by the test), runs it and compares the exact output above.
It is skipped, with the reason shown, when the variable is unset. With `BLITZCS_TEST_PUBLISH=1` (and `BLITZCS_TEST_WINE=1` on Linux) it also builds the default Windows package and runs
`Main.exe`. See `docs/wine-testing.md`.

## DirectSound output (FSOUND_OUTPUT_DSOUND)

Set `Const OUTPUT_TYPE = FSOUND_OUTPUT_DSOUND` (value 2) in `main.bb`; the committed default stays NOSOUND. FMOD loads `dsound.dll` itself, so the translator gives its library a pool of
dynamic stubs automatically (it imports `LoadLibraryA`/`GetProcAddress`; `"com": true` or `"dynamicStubs": N` in the catalog entry force or size it), and the component answers
`GetProcAddress("dsound.dll", "DirectSoundCreate")` with a guest-callable stub that reaches the HOST's native dsound.dll (Windows, or Wine's built-in). On Linux there is no host dsound:
`FSOUND_Init` then fails with the real `DSERR_NODRIVER`. Expected output is the NOSOUND listing with `output 2`. See `docs/binary-com.md`.
