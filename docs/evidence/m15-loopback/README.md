# M15 loopback investigation

The SDL disk driver gives a sink-free capture of the PCM written by SDL. The
local SDL3 build enumerated `pipewire`, `pulseaudio`, `alsa`, `jack`, `disk`,
and `dummy`. The disk probe uses `SDL_AUDIO_DRIVER=disk` and writes the driver
output to an isolated temporary file selected with
`SDL_AUDIO_DISK_OUTPUT_FILE`; it does not open or modify a Pulse sink. The
capture is raw `F32LE` stereo at 44100 Hz, as SDL reports on stderr. See
`sdl-disk.json` and the reproducer in `tools/probe_sdl_disk_playback.py`.

The current built playback probe exited successfully and showed non-silent
PCM in both runtime statistics and SDL's post-device output: the first
snapshot had 45,056 non-silent frames and energy 8.68; the final snapshot had
48,128 non-silent frames and energy 10.17. The captured disk file contained
48,128 stereo frames, RMS 0.010065, peak 0.071995, with 94,208 of 96,256 float
samples nonzero. This establishes non-silent samples reached SDL's real disk
backend. It does not establish that a physical speaker was audible.

Pulse capture tests ran against uniquely named null sinks and unloaded only
the module they created. A routing inspection found the `paplay` process's
Pulse sink-input listed at sink index 5959 (`mini_eq_sink`) while its
`target.object` property named the test sink and that sink's monitor remained
idle. The user's EQ/default routing was left untouched. This means those
captures do not yet establish an end-to-end Pulse null-sink monitor test, and
their silent/mostly-silent PCM cannot be used to judge SDL output.
The silence-only routing observation and scoped move attempt are preserved in
`pulse-routing.json`.

SDL3 renamed the disk driver's output file variable from `SDL_DISKAUDIOFILE`
to `SDL_AUDIO_DISK_OUTPUT_FILE`; the current hint is documented as taking
effect before opening the device. [SDL3 migration guide](https://wiki.libsdl.org/SDL3/README-migration),
[disk output file hint](https://wiki.libsdl.org/SDL3/SDL_HINT_AUDIO_DISK_OUTPUT_FILE).
