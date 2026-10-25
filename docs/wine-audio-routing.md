# Wine audio routing and the capture guard

## Status

No Wine build and driver combination is marked `audio-safe` yet. The isolation is proven (below), but
audible capture is blocked: a private server without a session manager cannot carry audio (see Limits).
Until that is solved, use only silent programs with `scripts/wine/audio-capture.sh --dry-check`.

## Design (scripts/wine/audio-capture.sh, default mode)

- A private `pipewire` and `pipewire-pulse` run in a new `/tmp/blitz-audio.XXXXXX` (mode 700), started with
  an empty environment (`env -i`), `XDG_RUNTIME_DIR`/`PIPEWIRE_RUNTIME_DIR`/`PULSE_RUNTIME_PATH` set to that
  directory, the XDG config/state/cache dirs inside it, and no D-Bus session. No session manager (no WirePlumber).
- The script verifies, before the run, that the private server lists no card and no sink or source other than
  its own null sink `blitz_test` (and the built-in `auto_null`), and that PipeWire has no ALSA device object.
- The command gets `PULSE_SERVER`, `PULSE_RUNTIME_PATH`, `PIPEWIRE_REMOTE` and `PIPEWIRE_RUNTIME_DIR` pointing
  at the private server, plus `PULSE_SINK=blitz_test`. `XDG_RUNTIME_DIR` stays the user's (Wine and X need it).
- The output is recorded from `blitz_test.monitor` on the private server (ffmpeg).
- Observe-only cross-check against the real server, polled during the run and during the tail: a new sink
  input there that is named like `--exe`, or owned by the command's process tree or by a process carrying this
  run's `PULSE_RUNTIME_PATH`, stops the run with status 4. The real server is never changed.
- Teardown (EXIT/INT/TERM): stop the run's processes, the recorder and both private servers, remove the directory.
- A Wine command is refused (status 3) unless its prefix has `Audio=pulse` (see below).
- `--legacy-guard` keeps the previous behaviour (default sink changed, streams moved). It is UNSAFE on a
  multi-application desktop: it hijacks other applications' streams.

## Safety argument

A stream can reach speakers only through an audio server that reaches a sink with hardware. The private
server has no hardware: no ALSA card is loaded, the check above fails the run otherwise, and the command's
audio variables name only the private server. The real server is observed, never written to.
The remaining path is Wine's own driver fallback: with `Audio` unset, Wine falls back to `winealsa`, which
opens the hardware directly when the audio server is unreachable. `Audio=pulse` loads only `winepulse`, which
uses `PULSE_SERVER` alone, so an unreachable server fails instead of reaching hardware.

## Measurements (silent program: 2 s of zeros through waveOut, no audible output)

| Wine build | Driver setting | Stream on private server | New stream on real server | Unreachable private server |
|---|---|---|---|---|
| A: Proton 11.0 | unset (winepulse) | yes, 2 streams, unlinked | none | ALSA fallback: writes to `HDA Intel PCH` |
| A: Proton 11.0 | `Audio=pulse` | not re-run | - | `waveOutOpen` rc=2, no fallback |
| B1: Proton-CachyOS | unset | yes, 2 streams, unlinked | none | ALSA fallback: writes to `HDA Intel PCH` |
| B2: Proton-CachyOS | `Audio=pulse` | not run (matrix stopped) | - | `waveOutOpen` rc=2, no fallback |

B1 maps both `winepipewire.so` and `winepulse.so`; its stream is a PipeWire-pulse sink input
(`client.api = pipewire-pulse`), so the pulse path was used. `winepipewire` ignores `PULSE_SINK`, but it did not
reach the real server either. The real default sink was `alsa_output.pci-0000_00_1f.3.analog-stereo` before and
after every run, and the real server's other streams were not touched.

## Limits

- No session manager, no links: the null sink's monitor stays SUSPENDED and streams stay unlinked
  (`sink = 4294967295`). The Wine program therefore blocks in `waveOutOpen`/write and never finishes,
  and `ffmpeg` cannot record the monitor, so default mode records nothing (status 3).
- The private server has no `default` metadata: `pactl set-default-sink` fails with "Not supported". The
  command gets `PULSE_SINK` instead.
- The cross-check sees a stream on the real server only after it appears, so sound may already be out when the
  run is stopped (only silent tests were ever run).
- A wineserver started by a run keeps that run's environment; stop it with `wineserver -k` for its prefix.

## Rules

- Audible tests only through `scripts/wine/audio-capture.sh` default mode, and only with a prefix set to
  `Audio=pulse` (`BLITZCS_WINE_AUDIO_DRIVER=pulse . scripts/wine/env.sh`).
- Before any audible run, `--dry-check` must report the stream on the private server and the real server
  unchanged, for that build and driver.
- `--legacy-guard` is not used for audible tests.
