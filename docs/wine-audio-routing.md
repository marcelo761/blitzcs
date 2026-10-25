# Wine audio routing and the capture guard

## Finding

`PULSE_SINK` only routes a Wine program's audio when Wine uses its PulseAudio driver (`winepulse.drv`).
Proton-CachyOS loads `winepipewire.drv` by default, which ignores `PULSE_SINK`: the stream lands on the real
default device. Proton 11.0 loads `winepulse.drv` and honours the variable. The driver in use is recorded in
the prefix at `HKCU\Software\Wine\Drivers` (value `Audio`; the subkey `winepulse.drv` or `winepipewire.drv`
shows which one was loaded).

## Measurements (silent program: zero samples through waveOut, 3 s; no audible output)

| Wine root | Driver | `PULSE_SINK` only | `pactl move-sink-input` | guarded `--dry-check` |
|---|---|---|---|---|
| Proton 11.0 | winepulse.drv | null sink (honoured) | not needed | verified, exit 0 |
| Proton-CachyOS (default) | winepipewire.drv | real sink, index 56 (ignored) | client test: moved 56 -> null, OK | verified, exit 0 (needs `--exe`) |
| Proton-CachyOS + `Audio=pulse` | winepulse.drv | null sink (honoured) | not needed | verified, exit 0 |
| Guard abort test | unattributable same-named stream on a real sink | - | - | aborted, exit 4 |

The CachyOS default is safe with the guard because the null sink is the default sink during the run, so the
new stream lands there even though `PULSE_SINK` is ignored. A Wine stream was never moved by the guard in
these runs; the move was exercised with a non-Wine client aimed at the real sink.

WirePlumber keeps per-application routing `target`s in its `stream-properties` state. None existed for the test
program, so they did not cause the finding, but they can redirect applications that share a name.

## audio-capture.sh

- Sets the null sink as the default sink for the run and restores the previous default on every exit path.
- Polls `pactl list sink-inputs` about every 50-100 ms while the command and its tail run.
- A stream is ours when its `application.process.id` is in the command's process tree, or its process has this
  run's unique `PULSE_SINK` in its environment (Wine re-parents its clients to `systemd --user`), or, without a
  pid, its `application.name` equals the `.exe` name (`--exe` or a `*.exe` argument).
- Streams present before the start, and streams of other processes, are never touched.
- Ours on a real sink: moved with `pactl move-sink-input`. A failed move, a stream of unknown owner on a real
  sink, or a sink still real after three moves kills the run's processes and exits 4 with
  `audio-capture: a stream reached a real device (sink N); aborted`.
- Limit: a stream is moved within one poll interval, so its first ~100 ms can reach a real device. The default
  sink covers this for streams that honour the default. An unattributable foreign stream is reported, not moved.
- Side effect: other applications' new streams during the run also go to the null sink and are recorded.

## --dry-check

    scripts/wine/audio-capture.sh --dry-check [--exe name.exe] -- <silent command>

Runs the same guard without recording and prints one line per new stream: application, owner kind, sink at
first sight, moved, final sink. Exit status: the command's, or 4 on abort. Use a program that writes silence.

Rule: never run an audible test through a Wine/driver combination until `--dry-check` reports the stream on
the null sink ("routing verified").

## Opt-in driver

`BLITZCS_WINE_AUDIO_DRIVER=pulse . scripts/wine/env.sh` sets `Audio=pulse` in the selected prefix. The default
is unchanged.
