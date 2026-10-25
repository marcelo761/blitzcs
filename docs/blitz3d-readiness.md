# Blitz3D SDK readiness survey

Question: what do the genuine Blitz3D V1.118 SDK binaries need from the binary compiler (BCSBRE) before any of them can be translated cleanly and started? This is a measurement
only. Nothing from the SDK was run, rebuilt or copied into the repository; no claim below says that something works unless it names the test that ran. Dates and numbers: survey of
2026-10-24 on the working tree of branch `codex/milestone-21-binary-compiler` (HEAD `49eaa1b` plus uncommitted work of other agents), target `windows-x64`.

Related: `docs/binary-seh.md` (exception machinery decision, built on the same survey), `docs/binary-abi-coverage.md`, `docs/windows-integration-audit.md`, `docs/binary-packed-images.md`.

## Method and reproduction

The SDK archive is treated as untrusted data: it is extracted into a new empty directory and only read. `tools/survey/BcSurvey` (a console project, not part of `BlitzCS.slnx`)
references the translator and the Win32 component and, for every file whose first two bytes are `MZ` (found by content, not by extension), does the following:

1. `PeReader.Read` plus raw header facts (subsystem, OS version, DllCharacteristics, data directories) and `PackedImageAnalyzer`.
2. `ModuleBaseAllocator.RequiredBase(..., windows-x64)` and `X64Translator.Translate(image, BinaryCompilerOptions(TargetOs: windows-x64){ LoadBase })`. Diagnostics are aggregated by code and severity.
   `BC_UNSUPPORTED_INSTRUCTION` is Error when the instruction is reachable by direct control flow from the entry point, an export or a TLS callback, and Warning when it was found only
   through a speculative root (relocation target, scanned data dword, code-address immediate). Only Errors block loading (`GuestModuleOptions.AllowTranslationErrors` is false by default).
3. `ControlFlowGraph.Build` on the unmodified image to find every `call/jmp dword ptr [iat_slot]` and every call to a one-instruction `jmp [slot]` thunk ("static call sites"), and the
   functions reachable from the entry point by direct calls ("entry closure"; indirect calls are not followed, so for MFC programs such as ide.exe the closure is a small part of the program).
4. `Win32ImportResolver.Analyze(PeImage)` for implemented/absent, and a grouping of the absent imports into contracts (by DLL; kernel32 is split by function family).

```
export PATH=<your .NET SDK dir>:$PATH
dotnet build tools/survey/BcSurvey/BcSurvey.csproj -c Release -o /tmp/bcsurvey
SDKDIR=$(mktemp -d /tmp/blitz3d-sdk.XXXXXX)            # a NEW EMPTY directory
(cd "$SDKDIR" && unzip -q "$HOME/Downloads/Blitz3D-V1.118.zip")
dotnet /tmp/bcsurvey/BcSurvey.dll sdk "$SDKDIR" --out /tmp/sdk-survey.md --json /tmp/sdk-survey.json   # ~16 s wall, ~1.5 GB peak RSS (translator and CFG run per image)
python3 -I tools/survey/dynamic_strings.py "$SDKDIR"/Blitz3D-V1.118/bin/runtime.dll                     # DLL names present as strings (LoadLibrary candidates)
dotnet /tmp/bcsurvey/BcSurvey.dll dump "$SDKDIR"/Blitz3D-V1.118/Blitz3D.exe 1D2C 30                      # read-only disassembly of the recovered stream
```

The tool never needs the SDK inside the repository; the markdown and JSON it writes contain every table below (and the complete list of absent imports, which is not repeated here).
Limits of the method: reachability follows direct calls and tail jumps only; "function" is the span between recovered function entries; the translator was run per image at its own
rebased address (`0x50000000`, independent of the other images), not as a linked set; translation diagnostics describe what the translator rejects, not what would execute.

## 1. The files

Seven files start with `MZ` out of 2061 in the archive; six are PE32 x86, and `cfg/Blitz.fon` is a Windows font resource (`PE000: Missing PE signature`, not an executable image).
None is packed (`PackedImageAnalyzer`: None), none has delay imports, none is a .NET image, all are `/DYNAMICBASE` + NX with a load-config directory (SafeSEH/CFG data), and all
declare minimum OS and subsystem version 6.0 (Windows Vista), so they were built for a newer system than the Win32 component's default identity (5.1.2600, XP SP3).

| file | kind / subsystem | size | preferred base | relocs | TLS | imports | exports | entry RVA | windows-x64 base | decoded instr. (speculative) | functions | errors | warnings | translate ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Blitz3D.exe | exe / gui | 96,768 | 0x00400000 | 1,989 | no | 74 | 0 | 0x1D00 | 0x50000000 | 19,044 (7,041) | 554 | 30 | 5 | 466 |
| bin/blitzcc.exe | exe / console | 512,000 | 0x00400000 | 9,654 | no | 82 | 0 | 0x3602F | 0x50000000 | 121,113 (54,116) | 3,035 | 55 | 94 | 1,529 |
| bin/linker.dll | dll / console | 181,760 | 0x10000000 | 3,552 | no | 81 | 1 | 0x8A87 | 0x50000000 | 42,540 (31,793) | 1,110 | 22 | 15 | 355 |
| bin/runtime.dll | dll / gui | 1,126,400 | 0x10000000 | 16,406 | yes | 210 | 2 | 0xADAF5 | 0x50000000 | 278,326 (264,919) | 5,261 | 22 | 395 | 1,879 |
| bin/debugger.dll | dll / console | 2,101,248 | 0x10000000 | 68,596 | yes | 593 | 1 | 0x14E822 | 0x50000000 | 534,134 (509,639) | 13,535 | 26 | 139 | 4,413 |
| bin/ide.exe | exe / gui | 2,299,904 | 0x00400000 | 73,021 | yes | 634 | 0 | 0x16C485 | 0x50000000 | 577,355 (566,355) | 14,693 | 22 | 174 | 4,704 |

* **EXEs can move.** All three EXEs carry base relocations (the usual "EXE without relocations cannot move" does not apply), so `ModuleBaseAllocator` places each at `0x50000000` for
  windows-x64; a preferred base of `0x00400000` is unusable there (`BC_BASE_UNUSABLE_ON_TARGET` would be raised otherwise) and also falls inside the allocator's default low reservation on Linux.
* **TLS is handled.** ide.exe, debugger.dll and runtime.dll have a TLS directory; none got `BC_TLS_UNSUPPORTED` (the translator reports `TLS_HANDLED`). The "Static TLS: Unsupported" row in
  `docs/binary-abi-coverage.md` is stale (owned elsewhere; reported, not edited).
* **Every image translates and none translates clean**: the six images produce 177 Error and 822 Warning diagnostics in total (176 `BC_UNSUPPORTED_INSTRUCTION` errors plus 1 `BC_SEGMENT_OVERRIDE`
  error; warnings are `BC_UNSUPPORTED_INSTRUCTION` 747, `BC_SEGMENT_OVERRIDE` 18, `BC_UNTRANSLATED_TARGET` 29, `CFG_DECODE_FAILURE` 28). No translator exception occurred.
* Volume: 1,572,512 decoded instructions of which 1,433,863 (91%) are speculative, i.e. found only through pointer-like values. The speculative share is why most unsupported sites are Warnings.

## 2. Unsupported instructions

923 `BC_UNSUPPORTED_INSTRUCTION` sites: **176 reachable (Errors, blocking)** and 747 speculative (Warnings). Counts are sites; "files" is out of the 6 images.

Ranked by reachable (blocking) sites, then total (the 12 groups with a reachable site, then the largest speculative-only groups):

| # | mnemonic | translator reason | sites | reachable | speculative | files | examples (image RVA bytes) | class |
|---|---|---|---|---|---|---|---|---|
| 1 | `mov r/m16, sreg` | segment register operand | 150 | **115** | 35 | 6 | Blitz3D.exe 0x1D91 `668C15F07A4100`, 0x1D98 `668C0DE47A4100` | valid in user mode, unlowered |
| 2 | `int 29h` | system / interrupt | 36 | **25** | 11 | 6 | Blitz3D.exe 0x1D6C `CD29`, 0x21D2 `CD29` | valid in user mode, unlowered (`__fastfail`) |
| 3 | `push sreg` | segment register operand | 195 | **10** | 185 | 4 | blitzcc.exe 0x1647C `06`, 0x19263 `06` | all 10 sit in blitzcc's data-in-code regions (see below) |
| 4 | `vzeroupper` | VEX encoding | 19 | **5** | 14 | 6 | Blitz3D.exe 0x9484 `C5F877` | valid, unlowered (AVX2 CRT path) |
| 5 | `vpmovmskb` | VEX encoding | 17 | **5** | 12 | 6 | Blitz3D.exe 0x9462 `C5FDD7C0` | same |
| 6 | `vpxor` | VEX encoding | 15 | **5** | 10 | 6 | Blitz3D.exe 0x9456 `C5F1EFC9` | same |
| 7 | `vpcmpeqb` | VEX encoding | 9 | **3** | 6 | 5 | Blitz3D.exe 0x945E `C5F57401` | same |
| 8 | `vpcmpeqw` | VEX encoding | 8 | **2** | 6 | 6 | Blitz3D.exe 0x95B6 `C5F57507` | same |
| 9 | `arpl` | no lowering in 64-bit mode | 6 | **2** | 4 | 4 | blitzcc.exe 0x1645D, 0x18958 | data decoded as code (blitzcc) |
| 10 | `pop sreg` | segment register operand | 202 | **1** | 201 | 4 | blitzcc.exe 0x1647D `07` | data decoded as code |
| 11 | `lds` | no lowering | 15 | **1** | 14 | 4 | blitzcc.exe 0x19244 `C58F41000090` | data decoded as code |
| 12 | `int1`, `bound` | system / no lowering | 6 + 2 | **1 + 1** | 5 + 1 | 3 + 2 | blitzcc.exe 0x18964; blitzcc.exe 0x16445 `624100` | data decoded as code |
| - | `pop sreg` / `push sreg` (speculative) | segment register operand | 201 / 185 | 0 | 201 / 185 | 4 | blitzcc.exe, ide.exe, runtime.dll | data decoded as code from speculative roots |
| - | `in`, `out`, `insb/insd`, `outsb`, `cli`, `sti`, `hlt`, `iretd`, `into` | privileged / I-O | 134 | 1 | 133 | 4 | blitzcc.exe 0x1EEF5 `EC` (`in al,dx`), 0x1F151 `EF` | ring-0 opcodes: data decoded as code |
| - | `aam`, `aaa`, `aas`, `das`, `daa`, `aad`, `salc`, `les` | no lowering in 64-bit mode | 13+8+5+9+1+1+4+11 | 0 | all | 2-4 | blitzcc.exe 0x3D6A6 `D443`, debugger.dll 0x8D4A0 | BCD / legacy: data decoded as code |
| - | `vmovdqu`, `vpshufb`, `vmovd`, `vpermq`, `vpsllvd`, `vinsertf128` ... | VEX encoding | 105 VEX sites in all | 20 | 85 | 2-6 | debugger.dll 0x1501C4 `C5FE6F09`, runtime.dll 0x88190 | AVX2 memcpy/memchr in the CRT |
| - | far `call`/`jmp`, `retf` | far or 16-bit branch | 10 + 4 | 0 | all | 1-4 | debugger.dll 0x19C3B `9A0110739A0110` | data decoded as code |

Top 10 mnemonics by total sites: `pop sreg` 202 (1 reachable), `push sreg` 195 (10), `mov r/m16,sreg` 150 (115), `in` 57 (0), `out` 41 (0), `int 29h` 36 (25), `vzeroupper` 19 (5), `vpmovmskb` 17 (5), `lds` 15 (1), `vpxor` 15 (5).
By blocking weight the order is `mov sreg` 115, `int 29h` 25, `push sreg` 10, the five AVX2 mnemonics 20 together, everything else 6.

Why they are there (disassembly of the recovered stream):

* **Segment-register stores and `int 29h`: one and the same CRT routine.** Blitz3D.exe 0x1D54 starts with `push 17h; call [IsProcessorFeaturePresent]` (slot 0x410038, 0x17 = PF_FASTFAIL_AVAILABLE), `test eax,eax; je ...; push 2; pop ecx; int 29h` and then
  stores `eax, ecx, edx, ebx, esi, edi` and `ss, cs, ds, es, fs, gs` into a global `CONTEXT` image (`mov [417AF0h], ss` is `66 8C 15 ...`) before calling `UnhandledExceptionFilter` with `0xC0000409`:
  this is the MSVC `__report_gsfailure` / `__scrt_fastfail` path. Each of the five newer images has the same 5 functions (blitzcc.exe has 6, three of them with 7 sites): two of 7 sites (6 `mov sreg` + 1 `int 29h`), one of 6 (the segment stores alone, the capture helper)
  and two that are a bare `int 29h` (`__fastfail` intrinsic); 22 blocking sites per image (4 `int 29h`, 18 `mov sreg`). ide.exe, linker.dll and runtime.dll have **only** these 22 blocking sites.
* **AVX2 CRT paths.** Blitz3D.exe 0x9401 opens with `mov eax,[__isa_available]; cmp eax,5; jl <SSE path>` and its VEX body is a 32-byte `vpcmpeqb ymm0,ymm1,[ecx]` / `vpmovmskb` / `vzeroupper` string scan
  (`strnlen`/`memchr` flavour). It is dormant if `__isa_available < 5`. **Hazard**: `cpuid` and `xgetbv` are not lowered by the translator (they are emitted as the host instruction, and the unpacker
  refuses them as anti-debug probes), so on a modern host the CRT's own feature detection would select this path at run time.
  Blocking VEX sites: Blitz3D.exe 8, blitzcc.exe 8, debugger.dll 4; none in ide.exe, linker.dll, runtime.dll.
* **blitzcc.exe's other blocking sites (17 + the `gs` error) are data in `.text`.** Three regions reached by fall-through, RVAs 0x1642C (`bound`, `arpl`, `push es`, `pop es`), 0x1894C (`arpl`, `int1`, and
  the one blocking `BC_SEGMENT_OVERRIDE` at 0x1895C, a `gs`-prefixed `jae`) and 0x19244 (`lds ecx,[edi-6FFFFFBFh]` ... 9 x `push es`): the bytes (for example `C5 8F 41 00 00 90 ...`) look like a table of
  `0x0041xxxx` dwords rather than code (interpretation from the byte pattern and the surrounding `jmp near`, not verified against symbols). They are reachable only because the sweep falls out of the preceding function (0x1638C ends in a `jmp near` with the table behind it) or is reached from an
  imprecise root; they would never run.

By class (all 923 sites): segment-register move/push/pop 547 (126 reachable), ring-0/I-O/trap opcodes 134 (1), VEX 105 (20), `int 29h` 36 (25), legacy BCD/`lds`/`les`/`arpl`/`bound`/`salc` and 64-bit-mode-invalid aliases 85 (4),
far/16-bit forms 10 (0), other 6 (0). All unsupported sites are in `.text`.

**Projection (derived from the counts above, not run):** lowering the segment-register capture and `int 29h` would remove every blocking diagnostic from ide.exe, linker.dll and runtime.dll;
adding a policy for the 20 AVX2 sites clears Blitz3D.exe and debugger.dll; blitzcc.exe also needs its three data regions (17 `BC_UNSUPPORTED_INSTRUCTION` + the 1 `BC_SEGMENT_OVERRIDE` error)
dropped from the reachable set. Whether further errors appear after those are lowered cannot be known without trying: the translator reports an unsupported instruction at its first use.

## 3. Imports

693 distinct `dll!function` imports over 20 DLLs. The Win32 component implements **106** of them (15%), **587 are absent**, 0 are satisfied by another file of the SDK (the DLLs of the SDK are not
imported by each other: blitzcc.exe carries only kernel32 imports; the strings `runtime.dll`, `linker.dll`, `debugger.dll` in it are LoadLibrary candidates, not traced).

| DLL | distinct functions | implemented | absent | static call sites | of which absent |
|---|---|---|---|---|---|
| user32.dll | 236 | 4 | 232 | 13,641 | 13,115 |
| kernel32.dll | 179 | 75 | 104 | 2,652 | 1,023 |
| gdi32.dll | 108 | 0 | 108 | 1,935 | 1,935 |
| uxtheme.dll | 12 | 0 | 12 | 258 | 258 |
| oleaut32.dll | 22 | 0 | 22 | 171 | 171 |
| ole32.dll | 35 | 7 | 28 | 129 | 66 |
| advapi32.dll | 11 | 11 | 0 | 110 | 0 |
| gdiplus.dll | 22 | 0 | 22 | 98 | 98 |
| shell32.dll | 10 | 0 | 10 | 79 | 79 |
| ws2_32.dll | 23 | 0 | 23 | 72 | 72 |
| winmm.dll | 13 | 9 | 4 | 30 | 8 |
| shlwapi.dll, oleacc.dll, imm32.dll, winspool.drv, msimg32.dll, oledlg.dll | 6+3+3+3+2+1 | 0 | 18 | 24+19+18+14+12+2 | all |
| ddraw.dll (`DirectDrawCreateEx`, `DirectDrawEnumerateExA`) | 2 | 0 | 2 | 7 | 7 |
| dinput8.dll (`DirectInput8Create`), d3dxof.dll (`DirectXFileCreate`) | 1 + 1 | 0 | 2 | 2 + 2 | all |

Absent imports grouped into contracts, with the entry-point closure count (functions with at least one call site in a function reachable from the entry point or DllMain of one of the six images):

| contract | absent functions | absent call sites | with a call site in an entry closure | images |
|---|---|---|---|---|
| user32 window/message pump | 232 | 13,115 | 5 | 4 |
| gdi32 / gdiplus / printing | 135 | 2,059 | 2 | 4 |
| shell / theme / IME / accessibility UI (shell32, uxtheme, shlwapi, imm32, oleacc, oledlg) | 35 | 400 | 2 | 3 |
| kernel32: resources and global/local memory | 14 | 297 | 9 | 3 |
| kernel32: MSVC 2010+ CRT start-up | 13 | 281 | 11 | 6 |
| ole32 / oleaut32 COM and automation | 50 | 237 | 0 | 2 |
| kernel32: wide-char file / console / locale | 21 | 198 | 13 | 6 |
| kernel32: other | 51 | 189 | 8 | 6 |
| network (ws2_32) | 23 | 72 | 0 | 1 |
| kernel32: SEH / diagnostics (`UnhandledExceptionFilter`, `RaiseException`, `RtlUnwind`, `IsDebuggerPresent`) | 5 | 58 | 3 | 6 |
| winmm multimedia (`PlaySoundA`, `timeGetDevCaps`, `timeBeginPeriod`, `timeEndPeriod`) | 4 | 8 | 0 | 3 |
| DirectX/COM creation (ddraw 2, dinput8 1, d3dxof 1) | 4 | 11 | 0 | 1 (runtime.dll) |

Per image (absent functions / static call sites of them; implemented functions of the same contract in brackets). The columns that matter for a first run are blitzcc.exe, linker.dll and runtime.dll:

| contract | Blitz3D.exe | blitzcc.exe | linker.dll | runtime.dll | debugger.dll | ide.exe |
|---|---|---|---|---|---|---|
| kernel32 CRT start-up | 12 / 22 [6] | 12 / 24 [6] | 11 / 21 [6] | 12 / 25 [6] | 13 / 92 [6] | 13 / 97 [6] |
| kernel32 wide-char file / console / locale | 8 / 9 [2] | 13 / 34 [2] | 12 / 33 [2] | 16 / 39 [2] | 17 / 41 [2] | 18 / 42 [2] |
| kernel32 SEH / diagnostics | 4 / 8 [1] | 4 / 9 [1] | 4 / 8 [1] | 4 / 11 [2] | 5 / 11 [2] | 5 / 11 [2] |
| kernel32 other | 3 / 3 [33] | 6 / 8 [38] | 6 / 8 [37] | 20 / 29 [50] | 42 / 67 [55] | 44 / 74 [55] |
| kernel32 resources, global memory | - | - | - | 3 / 3 [0] | 14 / 138 [5] | 14 / 156 [5] |
| user32 | 2 / 2 [2] | - | 0 / 0 [2] | 35 / 74 [2] | 221 / 6,413 [4] | 228 / 6,626 [4] |
| gdi32 / gdiplus | 1 / 1 | - | - | 14 / 19 | 128 / 1,005 | 134 / 1,034 |
| network (ws2_32) | - | - | - | 23 / 72 | - | - |
| DirectX creation (ddraw, dinput8, d3dxof) | - | - | - | 4 / 11 | - | - |
| winmm | - | - | - | 3 / 4 [9] | 1 / 2 | 1 / 2 |
| ole32 / oleaut32 | - | - | - | 0 / 0 [4] | 28 / 75 [6] | 50 / 162 [7] |
| shell / theme / IME | - | - | - | 1 / 1 | 33 / 196 | 35 / 203 |
| total absent of imports | 30 of 74 | 35 of 82 | 33 of 81 | 135 of 210 | 502 of 593 | 542 of 634 |

Notes that change the picture:

* **blitzcc.exe and linker.dll need no window system at all**: their only absent imports are kernel32 (35 and 33 functions). runtime.dll is the Blitz3D program runtime (the code a compiled program links against: it
  imports ddraw `DirectDrawCreateEx`/`DirectDrawEnumerateExA`, dinput8 `DirectInput8Create`, d3dxof `DirectXFileCreate`, 37 user32, 14 gdi32, 12 winmm of which 9 are implemented, ws2_32 23, ole32 4 implemented
  `CoCreateInstance`/`CoInitialize*`); its strings also name `xaudio2_9.dll` (dynamic). The two IDEs (ide.exe, debugger.dll) are full MFC/GDI+ GUI programs and the worst candidates.
* runtime.dll's absent user32 set is a typical small window/pump contract: `CreateWindowExA`, `RegisterClassA`, `DefWindowProcA`, `PeekMessageA`, `GetMessageA`, `DispatchMessageA`, `PostMessageA`, `ShowWindow`, `DestroyWindow`,
  `MoveWindow`, `SetWindowPos`, `Get/SetWindowLongA`, `GetClientRect`, `GetWindowRect`, `Screen/ClientToScreen`, `Get/SetCursorPos`, `SetCursor`, `ShowCursor`, `LoadCursorA`, `SetCapture/ReleaseCapture`, `MapVirtualKeyA`, `ToAscii`,
  `SystemParametersInfoA`, `GetSystemMetrics`, `MessageBoxA`, `BeginPaint/EndPaint`, `UpdateWindow`, `InvalidateRect`, `SetForegroundWindow`, `SetWindowTextA`, `GetDesktopWindow`, `UnregisterClassA`; gdi32: font and DC calls
  (`CreateFontA`, `CreateCompatibleDC`, `SelectObject`, `GetTextMetricsA`, `ExtTextOutA`, ...). The frequency ranking over all six images (top 80, with counts) is in the generated report; the top five absent functions overall are
  `user32!SendMessageA` 1,922, `GetParent` 863, `InflateRect` 630, `GetWindowRect` 615, `SetRectEmpty` 553, all from the two IDEs.
* **The kernel32 gap is small and shared.** Twenty-one absent kernel32 functions have a call site in the entry closure of all six images (Blitz3D.exe, blitzcc.exe, linker.dll, runtime.dll, debugger.dll, ide.exe):
  `CreateFileW, DecodePointer, EncodePointer, FindFirstFileExW, FindNextFileW, GetConsoleMode, GetConsoleOutputCP, GetModuleFileNameW, GetModuleHandleExW, GetModuleHandleW, GetStartupInfoW, GetSystemTimeAsFileTime,
  InitializeSListHead, IsDebuggerPresent, IsProcessorFeaturePresent, IsValidCodePage, LoadLibraryExW, RaiseException, SetFilePointerEx, UnhandledExceptionFilter, WriteConsoleW`; eight more reach five of them:
  `CompareStringW, EnumSystemLocalesW, GetFileSizeEx, GetLocaleInfoW, GetUserDefaultLCID, IsValidLocale, ReadConsoleW, SetEnvironmentVariableW`. That is the statically linked VS2015+ UCRT/vcruntime start-up and stdio, i.e. a
  property of the toolchain, not of Blitz3D: any modern MSVC executable needs it.

## 4. What a PE32 EXE needs at start-up

What exists (read from the sources, not run on these images): `GuestModule.Load` maps the image, binds imports, runs TLS callbacks and, **only for a DLL** (`image.IsDll`), calls `DllMain(base, 1, 0)` through
the entry point (`GuestModule.cs:229-243`). The TEB/PEB model (`docs/binary-teb-contract.md`) provides `fs:[0]` (SEH chain), `fs:[4]/[8]` stack bounds, `fs:[0x18]` self, `fs:[0x20]/[0x24]` ids, `fs:[0x2C]` TLS, `fs:[0x30]` PEB
pointer, `fs:[0x34]` last error, and a zeroed 4 KiB PEB with only `BeingDebugged` (+0x02) and `ImageBaseAddress` (+0x08, the translated base). The Win32 component implements `GetCommandLineA`, `GetEnvironmentStrings(W)`,
`GetStartupInfoA` (a zeroed 68-byte structure with `cb` set), `GetModuleHandleA(NULL)`, `GetModuleFileNameA`, `GetStdHandle`, `GetFileType`, `WriteFile`, `GetVersion(Ex)`, `ExitProcess` (throws `Win32ExitProcessException`
and never ends the host).

What the six images need beyond that, in the order a start-up reaches it:

| # | requirement | evidence | status |
|---|---|---|---|
| 1 | **An EXE entry path**: call the entry point (`WinMainCRTStartup` / `mainCRTStartup`, no arguments, `__stdcall`, never returns normally) and treat `Win32ExitProcessException` as the process exit code | EXEs: Blitz3D.exe 0x1D00, blitzcc.exe 0x3602F, ide.exe 0x16C485; `GuestModule` runs the entry only when `image.IsDll`; there is no exe runner or `LoadBase`-aware main module | **missing** |
| 2 | **PEB fields the CRT reads**: `PEB+0x10` (ProcessParameters) then `[ProcessParameters+8]`, and `PEB+0x68` (NtGlobalFlag) | disassembly of Blitz3D.exe: `mov eax,fs:[30h]; mov eax,[eax+10h]; cmp [eax+8],esi` (function 0x8CC9, called from 0x5AE5, non-speculative) and `mov ecx,fs:[30h]; mov ecx,[ecx+68h]; shr ecx,8; test cl,1` (0x5AEF); the PEB is a zeroed page, so `ProcessParameters` reads as 0 and the next load dereferences the guest null page | **missing, and fatal if executed** (see Findings) |
| 3 | `PEB+0x18` ProcessHeap, `+0xA4..0xAC` OS version, `+0x0C` Ldr | contract says "0 / left to the Win32 component"; only `GetProcessHeap` through kernel32 is served | missing (no image of these six reads them directly, they are the usual next probes) |
| 4 | **Main module identity**: `GetModuleHandle(NULL)` must equal the translated base | the Win32 component answers `Win32Options.MainModuleBase`, default `0x00400000`; the EXEs are translated at `0x50000000` for windows-x64 while the PEB says the real base | must be set from the loaded module, today a manual option |
| 5 | **OS identity for a Vista-minimum program** | headers say 6.0/6.0, default `Win32Version` is 5.1.2600 SP3 | policy: default should follow the image or be explicit |
| 6 | **Wide-character start-up**: `GetStartupInfoW`, `GetModuleFileNameW`, `GetModuleHandleW/ExW`, `GetCommandLineW` (6 of 6 images call the first three; `GetCommandLineW` is in the generated list), and the 21-function kernel32 set from section 3 | 6/6 images | missing (21 + 8 functions) |
| 7 | **Stdio**: console detection and output (`GetConsoleMode`, `GetConsoleOutputCP`, `WriteConsoleW`, `SetFilePointerEx`, `CreateFileW`); with redirected stdio the CRT uses `GetFileType` + `WriteFile`, which exist | 6/6 images call the console functions | missing for the console path |
| 8 | **`__scrt` security cookie and `__isa_available` detection**: `GetSystemTimeAsFileTime`, `IsProcessorFeaturePresent`, `cpuid`/`xgetbv` | 6/6 images; cpuid is passed through to the host (see section 2) | missing / needs a virtual CPU model |
| 9 | **Command line and environment**: `Win32Options.CommandLine` default `program.exe`; environment blocks are served empty by default and read from the host never | `ProcessService.cs` | present, defaults must be set per run |
| 10 | **Dynamic loading of guest DLLs** (`LoadLibraryExW`) | blitzcc.exe strings name `runtime.dll`, `linker.dll`, `debugger.dll` (not traced); Win32 `LoadLibraryA` serves emulated system DLLs and registered host modules only | missing for guest DLLs |
| 11 | `fs:` offsets used: `0x00`, `0x18`, `0x30` only (non-speculative code), all modelled; `gs:` used once, in the data region of blitzcc.exe | `BC_SEGMENT_OVERRIDE` | no gap |
| 12 | SEH: `__try` chains are built; software-exception dispatch (`RaiseException`, `RtlUnwind`) is implemented for frame-based SEH within one guest call (see `docs/binary-seh.md`, "Implementation") | `SehDifferentialTests`, `BlitzccDifferentialTests` | hardware faults and the cases listed there stay named faults |

## 5. Ranked next mechanisms and recommended order

Each item is reusable beyond Blitz3D (any MSVC-built PE32). Counts are from sections 2 to 4. "Unblocks" is a projection from the diagnostics, not a run.

| rank | mechanism | evidence (exact counts) | unblocks |
|---|---|---|---|
| 1 | **Lower reads of segment registers** (`mov r/m16, sreg`, `push sreg`): store the flat-model selectors (the values expected for a WoW64 process on windows-x64 (`cs=0x23`, `ss=ds=es=gs=0x2B`, `fs=0x53`; documented values, NOT validated against real Windows hardware: the PE32 oracle under Wine reads `fs=0x63`, `gs=0x6B`, Wine's own GDT); linux-sysv reads `fs=gs=0`, as the ELF32 oracle does) and keep `pop sreg`/`mov sreg, r` as named faults | 115 + 10 + 1 reachable sites, 6/6 images, 5 CRT functions per image; 421 more speculative | with rank 2: ide.exe, linker.dll, runtime.dll become error-free by this survey's counts |
| 2 | **`int 29h` as a named fail-fast trap** (ECX = failure code), distinct from the generic trap kind | 25 reachable, 6/6 images; 11 speculative | same |
| 3 | **A virtual CPU model for `cpuid`/`xgetbv`/`IsProcessorFeaturePresent`** (no AVX/AVX2/OSXSAVE, SSE2 baseline, stable vendor/family) and a policy for VEX in code that is dormant under that model (a deferred run-time trap reported as a Warning, not a load-blocking Error) | 20 reachable VEX sites in 3 images (Blitz3D.exe 8, blitzcc.exe 8, debugger.dll 4); 85 speculative; cpuid is host-passthrough today, so detection would pick AVX2 on a modern host | Blitz3D.exe, debugger.dll; also removes a host-dependent result |
| 4 | **Data-in-code**: do not let a fall-through sweep or imprecise root turn address tables in `.text` into reachable code (stop at unconditional terminators; reject runs that decode as `arpl/bound/lds/les/in/out/push es`) | blitzcc.exe: 3 regions, 17 blocking `BC_UNSUPPORTED_INSTRUCTION` + 1 `BC_SEGMENT_OVERRIDE`; 747 speculative warnings overall, 134 of them ring-0 opcodes | blitzcc.exe |
| 5 | **EXE entry path** (`GuestModule`/`BinaryLibrary` run `mainCRTStartup`, map `Win32ExitProcessException` to an exit code, set `MainModuleBase` and the PEB image base from the loaded module, command line, stdio) | 3 EXEs; none runnable today | any EXE |
| 6 | **PEB completeness**: `ProcessParameters` (with `RTL_USER_PROCESS_PARAMETERS`: Flags, ImagePathName, CommandLine, Environment), `NtGlobalFlag`, `ProcessHeap`, OS version, `Ldr`; and make an unmodelled PEB read fail loudly instead of reading a zero page | 2 distinct PEB reads per image in non-speculative code (offsets +0x10 and +0x68) | any MSVC 2015+ image |
| 7 | **kernel32 modern-CRT set** (21 functions in the entry closure of all six images, 8 more in five) plus Unicode/locale/console variants | counts in section 3: absent 12-13 + 8-18 + 4-5 functions per image in the CRT/wide/SEH groups | the start-up of every image |
| 8 | **Software exceptions** (`RaiseException`, `RtlUnwind`): done, see `docs/binary-seh.md` ("Implementation"); an unhandled one is the named `GuestUnhandledException` | blitzcc.exe: 44 reachable `throw` sites, 33 in the entry closure | compile-error paths of blitzcc.exe (done, see below) |
| 9 | **Guest `LoadLibrary`/`GetProcAddress` for DLLs of the same set** (`ModuleLinker` exists; the Win32 `LoadLibrary` does not consult it) | blitzcc.exe's string table names runtime.dll/linker.dll/debugger.dll | blitzcc.exe driving linker.dll |
| 10 | **Window/message contract** (user32 + gdi32 + a headless window sink): 35 + 14 functions for runtime.dll, 232 + 108 for the IDEs | 74 + 19 call sites in runtime.dll; 6,413 + 1,005 in debugger.dll | runtime.dll start-up (it creates windows in its initialisation path, not traced) |
| 11 | **DirectX/COM creation contracts** (`DirectDrawCreateEx`, `DirectInput8Create`, `DirectXFileCreate` return COM objects) and winmm timers (`timeGetDevCaps/BeginPeriod/EndPeriod`, 4 functions) | runtime.dll only: 4 creation functions, 11 call sites; the DirectDraw/Direct3D surface behind them is the large part and is explicitly not started here | Blitz3D programs, graphics |
| 12 | ws2_32 (23 functions, ordinals), oleaut32 (22, ordinals), shell/theme/GDI+ for the IDEs | 72, 171, 400 + 98 call sites | IDEs only; not recommended |

Recommended order: **1, 2, 3, 4** (translator-only changes that make the six images load without Errors; independent of each other, 1 and 2 are small), then **5, 6, 7** together (loader plus the kernel32
start-up set; they are needed by the first EXE run), then **8, 9** for blitzcc.exe, and **10, 11** only when the goal moves from the compiler tool chain to the runtime library. The first candidate that needs no window system is
blitzcc.exe (kernel32 only; 35 absent functions), followed by linker.dll (33). Neither has been started; the survey does not show that either would run once the above exists.

## Findings and reproducers

1. **PEB fields read as zero while the contract promises no silent zeros for the TEB.** Reproducer (static, no run): disassemble Blitz3D.exe from RVA 0x8CC9 (`BcSurvey dump <Blitz3D.exe> 8CC9 20`): `mov eax,fs:[30h]` / `mov eax,[eax+10h]` / `cmp [eax+8],esi`. With `PEB+0x10 = 0` this reads guest address 8, an unmapped page, which is a native fault (docs: hardware faults in guest code end the host process). The function is non-speculative, called from the CRT start-up at 0x5AE5, so it is expected to run in start-up (not executed, since there is no EXE entry path to run it). Fix direction: rank 6.
2. **`docs/binary-abi-coverage.md` is stale on static TLS** ("Unsupported ... Refused at translation/load" vs `TLS_HANDLED`; the three SDK images with a TLS directory translate without `BC_TLS_UNSUPPORTED`). Not edited (owned by another agent).
3. **`cpuid` is host passthrough**, which makes the CRT feature detection, and therefore the executed code path, depend on the host CPU. Not a crash by itself, but a reproducibility hazard; rank 3.
4. **Survey tool memory**: translating and building the CFG for the 2 MB images peaks at about 1.5 GB RSS (two passes per image, both keep full instruction maps). Fine for a survey, relevant if the same CFG is ever built twice in production.

## Run log: blitzcc.exe under BCSBRE (S2)

Mechanisms added: `tools/BcRun`, `GuestModule.RunExecutable` (EXE entry path, `IGuestProcessExit`), PEB ProcessParameters/Ldr/OS fields (`docs/binary-teb-contract.md`), and the
kernel32 start-up/wide set (`docs/binary-win32.md`). The image was extracted into a fresh directory from `Blitz3D-V1.118.zip` and never executed directly; every run below is the
translated guest. Translation used `--allow-errors` (S1's removal of the reachable unsupported-instruction Errors was not yet in): 0 traps fired on the start-up path
(the Errors are `cli`, `bound`, `arpl`, segment-register push/pop, `in/out`, `int1`, `insb` in data-in-code regions that are never executed).

Observed runs (SDK root as sandbox, `--env blitzpath=C:\`; reported OS 6.0.6002 from the image's declared 6.0):

| target | args | stdout | exit code |
|---|---|---|---|
| linux-x64 (native, base 0x10000000) | none | `Can't find blitzpath environment variable` (without the env var) | 0xFFFFFFFF (255) |
| linux-x64 | none, `-h` | `Unable to open linker.dll` | 0xFFFFFFFF (255 as a process status) |
| linux-x64 | `+h` | `Usage error` | 255 |
| windows-x64 (win-x64 BcRun under Wine, Proton 11.0, base 0x50000000) | none, `-h` | `Unable to open linker.dll` | 255 |

The CRT start-up ran to completion, the program reached its own `main`, read `getenv("blitzpath")`, parsed its arguments and ended through ExitProcess.

**Remaining blocker (exact):** the usage text ("Usage: blitzcc [-h|-q|+q|-c|-d|-k|+k|-v|-o exefile] [sourcefile.bb]", string at file offset 0x5D9E8) is printed only after blitzcc has loaded
`<blitzpath>/bin/linker.dll` and `runtime.dll` and called their exports (`linkerGetLinker`, ...). The trace ends `KERNEL32.dll!LoadLibraryExW` (wide path of `C:\bin\linker.dll`) -> `GetLastError` ->
`GetModuleHandleExW` -> `ExitProcess(-1)`. This needs **guest DLL loading from LoadLibrary**: translate the DLL, load it as a second `GuestModule` with its own resolver, register it in the
module table with its export addresses, make those addresses callable from the EXE (bridge stubs, as `GuestModuleSet` does for static imports), and share memory visibility between the modules. The
other unbound imports of the image, `RaiseException` and `RtlUnwind`, were not called on this path.

### Guest DLL loading (S3)

Mechanism: `GuestLibraryLoader` (docs/binary-win32.md, "Guest DLLs"): LoadLibrary of `C:\bin\linker.dll` / `C:\bin\runtime.dll` translates them on first use (cached with `--cache-dir`), loads them into the same
`GuestModuleSet` as blitzcc.exe, runs DllMain and returns the guest base as HMODULE; GetProcAddress returns a stub of the EXE's dynamic pool that forwards to the export.

Observed (SDK root as sandbox, `--env blitzpath=C:\`, `--allow-errors`; the EXE still reports 8 translation Errors here: `cli`, `arpl`, a gs-prefixed `jae` and others in data-in-code regions; linker.dll and
runtime.dll translate with 0 Errors), `--verbose` log:

```
BcRun: loaded guest DLL C:\bin\linker.dll at 0x100A0000
BcRun: GetProcAddress(linker!linkerGetLinker) -> stub 0x10081580 (cdecl; inferred from the export's ret instructions)
BcRun: loaded guest DLL C:\bin\runtime.dll at 0x100F0000
BcRun: GetProcAddress(runtime!runtimeGetRuntime) -> stub 0x10081590 (cdecl; inferred from the export's ret instructions)
BcRun: GuestTrapException: Guest control flow reached untranslated code at 0x100A41D0 (indirect target not discovered at build time).
```

stdout: empty (0 bytes); BcRun exit code 125 (the run failed, not the guest). linux-x64: 4.3 s cold, 1.6 s with the translation cache. windows-x64 (win-x64 BcRun under Wine, Proton 11.0): the same log with
bases 0x50000000 (EXE), 0x500A0000, 0x500F0000 and the same outcome. The usage text is not reached (blitzcc calls the linker object before it prints), so no source file could be compiled and no output file exists.

**Next blocker (exact):** both exports return C++ interface objects (`Linker*`, `Runtime*`). blitzcc calls a virtual method through the vtable, whose slot holds a linker.dll code address
(vtable slot at linker RVA 0x2139c -> RVA 0x41D0, a `mov eax, 0x2045e; ret`). The EXE's dispatch table only covers the EXE image, so the indirect call lands in the trap routine. The cross-module
function pointer is the limit already documented in docs/binary-multimodule.md. A fix belongs in the code generator/runtime: a dispatch-miss fallback that recognises an address inside a peer module and
jumps into the peer's translated code (done in S4; GetProcAddress now returns real addresses as well). Then runtime.dll
start-up (imports DINPUT8, DDRAW, d3dxof, GDI32, SHELL32 are not provided) and the user32/gdi32 contracts follow.

### Cross-module dispatch and one Win32 state (S4)

Mechanism: the dispatch-miss routine consults the process-wide module registry and continues in the other module (docs/binary-multimodule.md section 3); the modules of the set share one TEB per thread and one `Win32Context` (docs/binary-win32.md).
Observed (extracted SDK as sandbox, `--env blitzpath=C:\`, `--allow-errors`, linux-x64, translation cache warm, 2-5 s):

| args | stdout | exit |
|---|---|---|
| `-h` | `Usage: blitzcc [-h|-q|+q|-c|-d|-k|+k|-v|-o exefile] [sourcefile.bb]` followed by the option list | 0 |
| `-v` | `Compiler version:11.18:SoLoud`, `Runtime version:11.18:SoLoud`, `Debugger version:0.0:`, `Linker version:11.18:SoLoud` | 0 |
| `-c C:\tmp\hello.bb` (`Print "hi"` / `WaitKey`) | `BlitzCC V1.118`, copyright line, `Compiling "C:\tmp\hello.bb"`, `Parsing...`, `Generating...`, `Translating...`, `Assembling...` | 0 |
| none | empty | 0 |
| `+h` | `Usage error` | 255 |
| `-o C:\tmp\hello.exe C:\tmp\hello.bb` | the lines above plus `Creating executable "C:\tmp\hello.exe"...` | 125 (BcRun failure) |

`-o` fails with `GuestImportException: The guest called unbound import KERNEL32.dll!CopyFileA, which no host service provides.` (blitzcc copies its runtime stub; the trace shows `GetProcAddress(runtime!_bbWinMain@0)` and `FreeLibrary(runtime)` just before).
**Next blocker (exact):** `KERNEL32!CopyFileA` (then writing the executable through the sandbox); the runtime.dll start-up (user32/gdi32/DirectDraw/DirectInput imports, `DINPUT8`, `DDRAW`, `d3dxof`, `GDI32`, `SHELL32` are reported as not provided) has not been reached because `-v`/`-c` only call the version/compile virtuals, which do not initialise it.

### Base independence of the code/data decision

For images with a base relocation table the control-flow graph no longer depends on the load base. A fixup can only sit inside the displacement or immediate field of an instruction, so a decoded instruction that overlaps a relocated dword anywhere else (opcode, prefix, ModRM or SIB bytes) is an absolute-address table inside `.text` and is treated as data; the data-run trial decode uses the same test. The older heuristics (function-start trial decode, data after a call, rejected-mnemonic list) remain the fallback for images without relocations. Measured on the six SDK images, translating at 0x10000000 and at 0x50000000 yields identical sets of translated instruction RVAs, and the 8 spurious `BC_UNSUPPORTED_INSTRUCTION` errors seen in `blitzcc.exe` at 0x10000000 are gone (a fixed base the target cannot use still reports `BC_BASE_UNUSABLE_ON_TARGET`, which is a target policy and independent of this rule).

### Compile and link finish (S6)

The only missing contract was `KERNEL32!CopyFileA` (new `FilePathService`, below); no window, DirectX or registry access was needed for `-o`. SDK copy as writable sandbox (`C:\` = sandbox), `--env blitzpath=C:\`, `--allow-errors`.
Verbatim stdout of `blitzcc -o C:\tmp\srcN.exe C:\tmp\srcN.bb` (BcRun exit 0 on linux-x64 and on windows-x64 under Wine/Proton 11.0):

```
BlitzCC V1.118
(C)opyright 2000-2003 Blitz Research Ltd
Compiling "C:\tmp\src1.bb"
Parsing...
Generating...
Translating...
Assembling...
Creating executable "C:\tmp\src1.exe"...
```

| source | output | size | head |
|---|---|---|---|
| src1: `Print "hello"` | src1.exe | 1126912 | `MZ`, `PE\0\0` at e_lfanew, machine 0x014C |
| src2: loop + function + Dim array + Type + While (20 lines) | src2.exe | 1127936 | same |
| src3: `Include "C:\tmp\inc.bb"` (second file in the sandbox) + call | src3.exe | 1126912 | same (content differs from src1) |

`cmp` of the linux-x64 and windows-x64 outputs: byte-identical for all three. No produced exe was executed. Next step: compare against the native Wine reference compile of the same sources.

## blitzcc compile-error path

`blitzcc -c` on a source with an error (StartNetGame.bb, JoyUDir.bb in the SDK) reports the error with a C++ `throw` deep in the parser/semantic phase that blitzcc's `main` catches. With guest SEH
(`docs/binary-seh.md`, "Implementation") BcRun prints exactly the native diagnostic and ends with `ExitProcess(0xFFFFFFFF)` (process status 255, as native under Wine): pinned by
`BlitzccDifferentialTests.ACompileErrorInTheParserPrintsTheNativeDiagnosticAndExitCode` and `AnUnknownFunctionPrintsTheNativeDiagnosticAndExitCode` (linux-x64, and windows-x64 under Wine). A translator defect that hid
this for JoyUDir (code before a throw helper call dropped by the data-after-call probe) is fixed. The corpus differential (`docs/blitz3d-reference.md`) now compares these two error runs too.

## Produced executable start-up

Static evidence only, measured at HEAD `bd30724`. The counts in sections 3 to 5 predate the current Win32 component (it now registers 156 of the 693 distinct imports, not 106) and are superseded here where they differ.

Method and limits:
* The SDK was extracted into a new empty directory under `/tmp/claude-1000`. Nothing from it was executed or copied into the repository.
* No produced exe exists on this machine (no `/tmp/blitzcc-corpus.*`). Producing one runs `blitzcc.exe` through BcRun, and its `-o` path loads `runtime.dll` (`GetProcAddress(runtime!_bbWinMain@0)`, `FreeLibrary`), which executes that DLL's `DllMain`. That is excluded, so `bin/runtime.dll` is the proxy. The produced exe is runtime-sized (1,126,912 bytes against 1,126,400), but its import table was not read and the proxy is unverified.
* Import tables: `tools/survey/pe_imports.py` (`python3 -I`, read-only).
* Implemented or absent: `BcSurvey sdk` (`Win32ImportResolver.Analyze`, the registry verdict per `dll!name`).
* Start-up set: `BcSurvey startup <pe> [export ...]` (`tools/survey/BcSurvey/Startup.cs`), the functions reached by direct calls and tail jumps from the entry or `DllMain` and from each export. Indirect calls are not followed. The `_bbWinMain@0` closure contains 253 indirect call sites, so the direct set is a lower bound.
* Join: `tools/survey/startup_table.py`. Full per-name table: `tools/survey/startup-runtime-table.tsv` (210 rows, columns `implemented`, `group`, `direct_sites`, `upper_sites`, `roots`).

| image | distinct imports | implemented | absent | absent in the direct start-up set |
|---|---|---|---|---|
| bin/runtime.dll (proxy for the produced exe) | 210 | 118 | 92 | 3 (from `_bbWinMain@0`); 0 from `DllMain` |
| bin/blitzcc.exe | 82 | 82 | 0 | 0 |
| Blitz3D.exe (IDE launcher, not on the path) | 74 | 70 | 4 | 4 (`CreateProcessA`, `GetDC`, `WaitForInputIdle`, `GetDeviceCaps`) |

runtime.dll, by subsystem (`implemented / absent`; "direct" = absent with a call site in the `_bbWinMain@0` start-up set):

| subsystem | imports | impl | absent | direct |
|---|---|---|---|---|
| user32 window/message | 37 | 2 | 35 | 0 |
| ws2_32 (ordinals only) | 23 | 0 | 23 | 0 |
| gdi32 / gdiplus | 14 | 0 | 14 | 0 |
| kernel32 memory/resources | 11 | 7 | 4 | 3 |
| kernel32 other / file / console / locale | 63 | 56 | 7 | 0 |
| kernel32 sync/threads/TLS | 15 | 14 | 1 | 0 |
| kernel32 CRT/startup, SEH, module loading | 26 | 26 | 0 | 0 |
| DirectX/COM creation (ddraw, dinput8, d3dxof) | 4 | 0 | 4 | 0 |
| winmm | 12 | 9 | 3 | 0 |
| ole32 | 4 | 4 | 0 | 0 |
| shell32 / shell-theme | 1 | 0 | 1 | 0 |
| **total** | **210** | **118** | **92** | **3** |

Top 10 blockers for the first frame, in start-up order:

| rank | import(s) | implemented | subsystem | needed at start (static evidence) or later | headless with truthful semantics? |
|---|---|---|---|---|---|
| 1 | kernel32 `FindResourceA`, `LoadResource`, `LockResource` | no | kernel32 resources | **start**: one direct call each from `_bbWinMain@0` | yes: read the loaded image's resource directory |
| 2 | user32 `RegisterClassA`, `CreateWindowExA`, `ShowWindow`, `UpdateWindow`, `DefWindowProcA`, `DestroyWindow`, `Get/SetWindowLongA`, `UnregisterClassA` | no | user32 window | later by direct closure; needed by any Graphics3D path (indirect calls only) | yes for handles and state (window sink), not for pixels |
| 3 | user32 `PeekMessageA`, `GetMessageA`, `DispatchMessageA`, `PostMessageA`, `SetForegroundWindow` | no | user32 message | the frame loop (indirect) | yes: queue semantics |
| 4 | DDRAW `DirectDrawCreateEx`, `DirectDrawEnumerateExA`; DINPUT8 `DirectInput8Create`; d3dxof `DirectXFileCreate` | no | COM creation | the Graphics3D path (indirect) | a failure HRESULT is truthful but ends Graphics3D; a frame needs a display host |
| 5 | gdi32 `CreateCompatibleDC`, `DeleteDC`, `CreateFontA`, `SelectObject`, `DeleteObject`, `GetStockObject`, `GetTextMetricsA`, `GetTextExtentPoint32A`, `GetCharABCWidthsA`, `ExtTextOutA`, `SetTextColor`, `SetBkColor`, `Add/RemoveFontResourceA` | no | gdi32 | the Print path (indirect) | DC and object bookkeeping yes; metrics and pixels need a font engine and a host |
| 6 | user32 `GetCursorPos`, `SetCursorPos`, `ShowCursor`, `SetCursor`, `LoadCursorA`, `SetCapture`, `ReleaseCapture`, `ClientToScreen`, `ScreenToClient`, `GetClientRect`, `GetWindowRect`, `MoveWindow`, `SetWindowPos`, `MapVirtualKeyA`, `ToAscii` | no | user32 input/geometry | the Graphics3D and input path (indirect) | from the sink's state yes; real pointer or keyboard state needs a host |
| 7 | user32 `BeginPaint`, `EndPaint`, `InvalidateRect` | no | user32 paint | the paint path (indirect) | only as no-op paint, which is truthful only if no pixels are expected |
| 8 | winmm `timeBeginPeriod`, `timeEndPeriod`, `timeGetDevCaps` | no | winmm | the timing path (indirect) | yes: documented fixed caps |
| 9 | kernel32 `AcquireSRWLockExclusive`, `ReleaseSRWLockExclusive`, `WakeAllConditionVariable`, `SleepConditionVariableSRW` | no | kernel32 sync | indirect only | yes for a single guest thread |
| 10 | kernel32 `GetTimeZoneInformation`, `SystemTimeToTzSpecificLocalTime`, `FileTimeToSystemTime`, `GetDateFormatW`, `GetTimeFormatW` | no | kernel32 time/locale | indirect only | yes: a documented fixed zone |

Not on the static start-up path: ws2_32 (23 ordinals, not reached even by address-taken functions), `GetSystemMetrics` and `SystemParametersInfoA` (no call site in any closure), `ShellExecuteA` (address-taken only). Rank 1 is the only blocker proven on the direct path. Ranks 2 to 10 are the names a Graphics3D or Print path needs by semantics; the static closure cannot prove that they run, because `runtime.dll`'s address-taken functions cover 5,258 of its 5,261 functions, so no upper bound follows without modelling vtables and object layouts.

Headless versus a real host: ranks 1, 3, 8, 9 and 10 (and the state side of 2 and 6) can be satisfied headlessly with truthful semantics. Pixel output (5, 7, the frame part of 4) and live pointer or keyboard state (6) need a real display host. The Windows target could host them with the platform's own user32, ddraw and GDI, but the Win32 component emulates these APIs in managed code and does not forward them, so this would be a new host service behind the registry. For the Linux target, the untracked `src/Blitz3DCS.Windowing` was not evaluated. Neither has been started, and nothing here shows that a produced exe runs once they exist.
