# Blitz3DCS.Audio

This standalone `net10.0` assembly provides BASIC-callable `FSOUND` and `NativeAudio` functions backed by a managed PCM mixer and SDL3 playback. It has no Compiler or CLI project dependency. Audio is decoded from WAV and OGG files through the Linux `libsndfile.so.1` runtime. The mixer produces stereo float PCM; a bounded SDL audio stream queue feeds the device without a managed callback crossing the native ABI.

The tested runtime target is Linux x64 with SDL3 installed as `libSDL3.so.0`. Install SDL3 through the host OS package manager; this assembly does not bundle SDL3. The `SDL_AUDIODRIVER=dummy` backend supports headless lifecycle and playback tests but does not produce sound.

`FSOUND.Init` supports `flags=0`, returns `1` on success and `0` on failure, and preserves the failure detail in `FSOUND.LastError`. Repeating `Init` with identical settings succeeds; changing settings requires `Close`. `FSOUND.GetError` returns the FMOD-style integer error code and leaves it available until a later operation updates it. The project intentionally implements a subset of FMOD and does not claim full FMOD compatibility.

Audio asset and channel handles are managed positive integer tokens. Channel controls accept handles returned by `StreamPlay` or `PlaySample`; they do not treat token `0` as physical channel zero. `-1` requests automatic stream-channel allocation. `FSOUND_ALL` (`-3`) channel controls are unsupported. These functions report failure through their return value and the runtime error state.

Streams are currently decoded in full before playback. `StreamOpen` accepts modes `0`, `1`, or `2`, and only the full file (`offset=0`, `length=0` or `-1`). The software mixer supports mono and stereo samples, looping, per-channel volume, pan, frequency and pause controls. Sample volume and pan use normalized values (`0..1` and `-1..1` respectively); `NativeAudio.ChannelVolume` and `ChannelPan` translate those ranges to the FSOUND byte controls.

`FSOUND_SetVolumeAbsolute` maps to the per-channel `FSOUND.SetVolume` operation. There is no independent master-volume control, and its default scale is 255. Nonzero `FSOUND.Init` flags remain unsupported; this is a known difference from native FMOD behavior.

Native `SoundVolume`/`SoundPan` defaults and channel float adapters truncate their
scaled values like `gxSound`/`SoundChannel` in the original C++ runtime. Direct
FSOUND controls preserve their Integer arguments. Streams preserve the previous
error on successful open/stop; successful close/channel controls clear it, matching
the captured original sequence. File/decode failures use codes 9/10, invalid handles
14 and unsupported operations 18. Original behavior for every error is not proven.

Supported initialization bounds are 1..384000 Hz and 0..4096 software channels;
these resource limits are deliberate and are validated before opening a device.
Decoder output is limited to 256 MiB per file. Buffered streams are not incremental
file IO, and frequency zero/reverse playback are currently rejected explicitly.
The SDL queue can contain a short tail of already rendered PCM after a control
change; no instantaneous cancellation of device buffers is claimed.
