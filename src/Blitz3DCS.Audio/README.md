# Blitz3DCS.Audio

This standalone `net10.0` assembly provides BASIC-callable `FSOUND` and `NativeAudio` functions backed by a managed PCM mixer and SDL3 playback. It has no Compiler or CLI project dependency. Audio is decoded from WAV and OGG files through libsndfile. The mixer produces stereo float PCM; a bounded SDL audio stream queue feeds the device without a managed callback crossing the native ABI.

On Linux, install SDL3 and libsndfile through the host package manager (`libSDL3.so.0` and `libsndfile.so.1`). Windows x86 and x64 builds use the matching `SDL3.dll` and `sndfile.dll` beside the application; install those redistributables with `tools/install_sdl3.py` and `tools/install_libsndfile.py` as described in `docs/install-sdl3.md`. Set `SDL_AUDIO_DRIVER=dummy` to select SDL3's headless backend for lifecycle and playback tests; it does not produce sound.

`FSOUND.Init` supports `flags=0`, returns `1` on success and `0` on failure, and preserves the failure detail in `FSOUND.LastError`. Repeating `Init` with identical settings succeeds; changing settings requires `Close`. `FSOUND.GetError` returns the FMOD-style integer error code and leaves it available until a later operation updates it. The project intentionally implements a subset of FMOD and does not claim full FMOD compatibility.

Audio asset and channel handles are managed positive integer tokens. Channel controls accept handles returned by `StreamPlay` or `PlaySample`; they do not treat token `0` as physical channel zero. `-1` requests automatic stream-channel allocation. `FSOUND_ALL` (`-3`) channel controls are unsupported. These functions report failure through their return value and the runtime error state.

`StreamOpen` accepts modes `0`, `1`, or `2`, and only the full file (`offset=0`, `length=0` or `-1`). Streams decode asynchronously into a four-block cache plus one worker spare buffer; retained PCM is bounded by source channel count rather than file duration. Concurrent channels share that cache while keeping independent positions, loop state and controls. The render path only acquires nonblocking cache leases; a cache miss is counted in `AudioStatistics.StreamUnderruns` and leaves the source position unchanged. `AudioStatistics.StreamBufferBytes` reports retained stream PCM. The software mixer supports mono and stereo samples, looping, per-channel volume, pan, frequency and pause controls. Sample volume and pan use normalized values (`0..1` and `-1..1` respectively); `NativeAudio.ChannelVolume` and `ChannelPan` translate those ranges to the FSOUND byte controls.

## Implemented audio behavior and current gaps

| Capability | Current behavior |
|---|---|
| Simultaneous playback | Software-mixed channels up to `FSOUND.Init`'s configured channel count. Exhaustion reports an error. |
| Channel controls | The FSOUND facade exposes volume, stereo pan, frequency, pause/resume, playing state, and stop. The lower-level runtime also implements decoded-frame seek and loop-point controls. |
| Looping | Whole-sample loop mode is supported for streams and samples; loop-point control is available on the lower-level mixer API. |
| Input formats | WAV and Vorbis OGG decode to mono/stereo float samples through libsndfile. |
| File-backed streams | `StreamOpen` primes a bounded cache and a background decoder; no disk reads or waits occur in rendering. Cache misses are observable and do not advance channel positions. |
| 3D positioning | The runtime exposes listener and emitter state, plus `PlaySample3D` and channel 2D/3D mode controls. Spatial stereo sources are downmixed to mono; manual pan is rejected while a channel is spatial. |
| Distance attenuation and Doppler | Implemented in the software mixer using inverse-distance-clamped attenuation, equal-power panning, and velocity-based Doppler. The defaults use 343.3 world-units/second for speed of sound. This model has not been shown bit-for-bit equivalent to FMOD. |
| DSP/effects and master bus | Not implemented; there is no DSP chain or independent master-volume control. |

These gaps are explicit compatibility limits. Spatial behavior is implemented and covered by mixer/runtime PCM tests, but has not been differentially validated against native FMOD. The runtime does not silently ignore unsupported controls and should not be described as matching the full FMOD feature set.

`tools/AudioNativeSmoke` is a small native smoke host. `tools/probe_audio_native.py`
publishes and runs it for `win-x86` or `win-x64`, stages the pinned SDL3 and
libsndfile DLLs beside the app, and reads a caller-supplied OGG file without
copying it into the repository. The host generates its own short WAV and
checks concurrent channels, controls, decoded audio, and submitted non-silent
PCM. Use `--driver disk` to additionally capture SDL's PCM file, or `--driver
dummy` for a headless SDL device. The probe does not claim physical audibility.

`FSOUND_SetVolumeAbsolute` maps to the per-channel `FSOUND.SetVolume` operation. There is no independent master-volume control, and its default scale is 255. Nonzero `FSOUND.Init` flags remain unsupported; this is a known difference from native FMOD behavior.

Native `SoundVolume`/`SoundPan` defaults and channel float adapters truncate their
scaled values like `gxSound`/`SoundChannel` in the original C++ runtime. Direct
FSOUND controls preserve their Integer arguments. Streams preserve the previous
error on successful open/stop; successful close/channel controls clear it, matching
the captured original sequence. File/decode failures use codes 9/10, invalid handles
14 and unsupported operations 18. Original behavior for every error is not proven.

Supported initialization bounds are 1..384000 Hz and 0..4096 software channels;
these resource limits are deliberate and are validated before opening a device.
Sample decoder output is limited to 256 MiB per file; file-backed streams retain a
bounded cache and seek the decoder worker on demand. Frequency zero/reverse playback
are currently rejected explicitly.
The SDL queue can contain a short tail of already rendered PCM after a control
change; no instantaneous cancellation of device buffers is claimed.
