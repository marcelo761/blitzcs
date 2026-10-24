# M20 original input contracts

The genuine Blitz3D 1.118 Win32 compiler and runtime were executed under
Wine/Proton on an isolated Xvfb display. The original runtime created a mode-2
320×240 graphics window solely to receive real input. `xdotool` injected XTest
events into that owned display. The fixture wrote closed stage files; the driver
waited for each stage before injecting events and advancing the fixture. No OCR,
rendered-text extraction, or replacement input runtime was involved.

The [machine-readable observations](evidence/m20-original-input.json) identify the
fixture and SDK binaries by SHA-256. The reproducible sources are
[`reference-input.bb`](../samples/LegacyInput/reference-input.bb) and
[`probe_input_original.py`](../tools/probe_input_original.py). The probe has bounded
stage, compiler and process waits. Its `WaitKey` case receives a scheduled F1
followed by `b`; it never relies on an unbounded negative wait test.

## Executed original observations

| Operation | Observed original result |
|---|---|
| `MoveMouse 37,53`; read X,Y and each speed twice | `37,53,0,0,0,0` |
| XTest move to 73,91; read X,Y and each speed twice | `73,91,36,38,0,0` |
| Held left mouse; read Down,Hit,Hit; flush; read Down,Hit,GetMouse | `1,1,0,1,0,0` |
| Release then three left clicks; read Down,Hit,Hit and four GetMouse calls | `0,3,0,1,1,1,0` |
| Held `a`; read Down,Hit,Hit; flush; read Down,Hit,GetKey | `1,1,0,1,0,0` |
| Release then three `a` presses; read Down,Hit,Hit and four GetKey calls | `0,3,0,97,97,97,0` |
| Home,End,Insert,Delete,PageUp,PageDown,Up,Down,Right,Left | `1,2,3,4,5,6,28,29,30,31` |
| Queue unshifted `a`, then hold Shift before GetKey | `65`; following Shift FIFO entry translates to `0`; Shift remains down |
| Queue `a` while Shift held, release Shift before GetKey | `97`; Shift is up |
| Hold `a` for 1.2 seconds | Down `1`, Hit `1`, GetKey `97`, next GetKey `0` |
| Lose focus while `a` held in mode 2 | Down remains `1`; X,Y remain `73,91` |
| Regain focus and release `a` | Down `0`, Hit `0`; X,Y remain `73,91` |
| WaitKey after F1 then `b` | `98` |
| Two upward wheel events; read Z,ZSpeed,ZSpeed; flush; read Z | `2,2,0,2` |

The complete fixture exited with code 0 and no mismatches. These observations
cover a US-compatible keyboard layout in this captured environment, windowed
mode 2, and a positive wheel direction. They do not establish all OS keyboard
layouts, dead-key behavior, DirectInput fullscreen acquisition, scaled windows,
autosuspend modes, or rendering compatibility.

## Managed execution and SCP compilation

The [managed execution](evidence/m20-managed-input.json) used a real SDL X11
window on another owned Xvfb display and the same staged XTest injections. The
driver derives its program from the original fixture: it preserves the input
expressions and replaces only window creation and stage-file I/O. It opens the
explicit Blitz3DCS window host rather than implementing the original `Graphics`
command. The C# stage helper pumps events while awaiting each external signal.

There are **15 output records**, comprising **14 behavioral observations and one
completion marker**. Managed execution matched **13/14 behavioral observations**
and the completion marker, and exited with code 0. One difference is retained in
the comparison: on focus loss with `a` held, original mode 2 reports down `1`,
while SDL reports `0`; both preserve cursor position `73,91`. SDL generates a key
release during focus loss. The result is not claimed as complete input parity,
and the comparison command returns nonzero for this recorded difference.

The [SCP recompilation](evidence/m20-scp.json) verifies that all 30 audited game
source hashes remain unchanged. Standalone compilation remains **5/30**; integrated
syntax validation passes through Main and its 23 Include targets. Integrated
semantic/build progress passes the previous `MoveMouse` blocker at
`fullscreen_window_fix.bb:84:2` and stops at **`RenderWorld` at line 95, column 2**
in the same file. No integrated game assembly was emitted or executed. These
results establish input binding progress, not a renderer or a playable game.

Reproduce managed comparison after a Release solution build:

```sh
python tools/probe_input_original.py \
  --managed-cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --report /tmp/blitz-managed-input-report.json \
  --evidence-dir /tmp/blitz-managed-input-raw
python tools/scp_compile_probe.py --source-dir "$SCP_SOURCE" \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Audio/scp.json --check-syntax \
  --report /tmp/blitz-scp-input-report.json
```

The managed driver compares against the committed original observation file and
records its hash. `--reference` can select a separately captured original report.
It rejects report paths that alias its fixture, reference, executable or catalog,
including hardlinks, and keeps output outside the original SDK.

## Source-derived contracts

These details come from the public [Blitz3D source revision](https://github.com/blitz-research/blitz3d/tree/6beb288cb5962393684a59a4a44ac11524894939),
particularly `bbruntime/bbinput.cpp`, `bbruntime/bbgraphics.cpp`,
`gxruntime/gxdevice.cpp`, `gxruntime/gxdevice.h`, `gxruntime/gxinput.cpp`,
`gxruntime/gxinput.h`, and `gxruntime/gxruntime.cpp`.

- Each device read calls `update`. Windowed devices are unacquired, so their
  update calls `runtime->idle`, which pumps the Win32 message queue. Polling input
  is sufficient to receive events without drawing or flipping.
- Down state, hit counters and the FIFO are separate. A down event sets down,
  increments that key's counter and enqueues its scan or mouse-button number.
  An up event clears down only. Hit reads clear only that key's hit counter.
  FIFO reads do not consume hit counts. The FIFO holds 32 entries and discards
  new entries when full while retaining their hit/down effects.
- Flush pumps first, clears every hit counter and the FIFO, and preserves down
  and axes. It does not reset the language runtime's mouse-speed baselines.
- Mouse speed uses absolute position minus the last speed-read baseline for
  that axis. Reading position does not consume speed. `MoveMouse` sets X/Y
  position and their speed baselines before requesting a system cursor warp.
  Wheel position accumulates raw 120-unit wheel deltas; `MouseZ` divides by 120.
- Windowed motion events clamp coordinates to `[0,width-1]` and
  `[0,height-1]`. A direct `MoveMouse` assignment happens before any subsequent
  clamping motion event. Physical button numbering is left 1, right 2, middle 3;
  SDL's right/middle numeric values require translation.
- Win32 repeated keydown messages are ignored using the previous-state bit.
  `downEvent` itself does not deduplicate events. Legacy keyboard scan codes are
  PC/DirectInput set-1 codes, including the high bit for extended keys; SDL
  scancode enum values cannot be returned directly.
- `GetKey` pops one scan and translates it when read, using current Shift,
  Control and Alt state. A non-character scan can return zero even when more
  FIFO entries remain. Navigation keys have the fixed values shown above.
  Other keys use `scan & 127`, Win32 `MapVirtualKey` and `ToAscii`, retaining the
  low byte when exactly one character is produced. The translation matrix
  supplies modifier high bits but no CapsLock toggle state.
- `WaitKey` pumps, pops and translates; it ignores zero translations and retries
  after a 20 ms delay. `WaitMouse` uses the equivalent FIFO loop without ASCII
  translation. `MouseWait` aliases `WaitMouse`.
- Focus handling depends on graphics mode. Modes 2/3 do not autosuspend, so
  `WM_ACTIVATEAPP` does not reset their input. Modes 6/7 autosuspend: suspend and
  resume each reset device down, hit, FIFO and axis state. The language runtime's
  speed baselines are not part of that device reset.

## Reproduction

Provide a genuine SDK and an existing Wine prefix using `BLITZ3D_HOME`,
`BLITZ3D_WINE`, and `BLITZ3D_WINEPREFIX`. Install Xvfb, xdotool and xwininfo;
`XVFB` optionally selects a non-default Xvfb executable. Run from the repository:

```sh
python tools/probe_input_original.py \
  --report /tmp/blitz-input-report.json \
  --evidence-dir /tmp/blitz-input-raw
```

The driver copies the SDK into a temporary directory, allocates its own display,
and records compiler/process logs and raw stage files in the selected evidence
directory. It does not interact with the user's desktop display.

## Additional keypad probes

The same driver accepts `--keypad` to add 16 independently flushed stages. Each
records `KeyHit` for the expected DirectInput scan followed by eight `GetKey`
reads. This avoids treating extra modifier FIFO entries generated by XTest's
keysym mapping as the tested key's character. Every expected keypad scan recorded
exactly one hit in both original runs.

The original [fresh-display capture](evidence/m20-original-keypad-off.json) and
[NumLock-toggle capture](evidence/m20-original-keypad-on.json) recorded identical
keypad results. Xlib keyboard-state queries verified LED masks `00000000` before
and after the off run, and `00000000` → `00000002` when NumLock was toggled on:

| Key | Nonzero GetKey values in its independently flushed stage |
|---|---|
| Keypad Enter | `13` |
| Keypad Divide | `47` |
| Keypad Add | `43` |
| Keypad Subtract | `45` |
| Keypad Multiply | None; all eight reads returned `0` |
| Keypad digits 0–9 and Decimal | None; all eight reads returned `0` |

These are original-runtime observations, including its surprising Multiply and
digit behavior; SDL keypad keycodes must not simply be converted to their
apparent printable characters. The source's `MapVirtualKey(scan & 127,1)` route
and modifier-only translation matrix differ from direct SDL keypad translation.
The extended navigation scan cases remain separate from the nonextended keypad
scan cases. Operators can include leading zero FIFO entries; the reports retain
every read rather than filtering those entries away.

To reproduce, add `--keypad` to the original probe command; add `--num-lock` for
the toggle run. Both extra captures retain the original fixture hash and record
the generated variant's hash. The generated BASIC source is retained in the raw
evidence directory. A managed keypad comparison can select the corresponding
original report with `--reference`.

The managed [keypad-off](evidence/m20-managed-keypad-off.json) and
[keypad-on](evidence/m20-managed-keypad-on.json) executions match all 16 original
keypad stage records, including their non-character FIFO entries and scan hit
counts. Each variant retains the same focus-loss difference as the base program.
These are two variants of the same staged program, not independent differential
cases per output record. No additional keyboard-layout or rendering coverage is
implied.
