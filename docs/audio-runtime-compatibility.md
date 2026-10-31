# Audio runtime compatibility

The Windows replacement uses the same managed FSOUND compatibility engine on
Win32 and Win64. Each package must load SDL3 and libsndfile of its own native
architecture. SDL provides the output device; libsndfile decodes files. Neither
library supplies the legacy channel, scene or DSP semantics automatically.

## Compatibility contract

The following checks describe separate requirements. Passing a decoder or
headless output test does not establish the other requirements.

| Area | Required observable behavior | Verification |
|---|---|---|
| Decoding | WAV and OGG Vorbis retain their sample rate, channel count and non-silent PCM. Missing codecs report a failure. | Decode real files with the packaged Windows DLLs on both architectures. |
| Channel lifetime | Concurrent sounds have independent positions and controls. Exhaustion, stale handles, stop, free and close have explicit outcomes. | Runtime tests using managed handles, channel reuse and resource teardown. |
| Playback controls | Volume, pan, frequency, pause and looping preserve their declared ranges and defaults. Stereo content must not lose one source channel. | PCM assertions, including spatial stereo downmix and loop boundaries. |
| Spatial playback | Listener position/orientation and emitter position change the stereo image. Distance limits and rolloff affect gain; velocity affects Doppler pitch. | Spatial PCM tests plus comparison against the native reference before claiming equivalent FMOD behavior. |
| File streaming | Retained decoded PCM is bounded independently of file duration. The render path performs no file reads or decoder waits. Cache misses are observable and do not skip source frames. | Controlled blocked-decoder tests, retained-buffer accounting and playback beyond the initial cache. |
| BASIC bindings | Implemented behavior is reachable through typed compiler bindings, with the correct argument units and defaults. | Compile and execute a BASIC sample; testing a lower-level C# method alone is insufficient. |
| Device output | Native SDL opens a device, accepts mixed PCM and shuts down cleanly. | Native x86/x64 smoke tests. Dummy or disk output proves submission, not physical audibility. |
| Effects | DSP and master-bus operations used by the application produce their specified effect. | Explicit implementation and effect-specific PCM tests; unsupported calls must fail rather than succeed as no-ops. |

The spatial model currently uses equal-power panning, inverse-distance-clamped
attenuation and radial-velocity Doppler. These are explicit implementation
choices, not evidence of bitwise FMOD equivalence. Preserve a distinction between
this model's tests and differential validation against the legacy implementation.

## Evidence and remaining work

The current mixer/runtime tests cover channel controls, the spatial model and
public-path streaming. Deterministic blocked-decoder tests verify that cache
misses do not wait or advance positions, and runtime tests verify independent
positions for channels sharing one bounded stream cache. See the [audio
implementation status](../src/Blitz3DCS.Audio/README.md) for unsupported
operations.

The native smoke host and `tools/probe_audio_native.py` report decoding, SDL driver
selection, playback-device enumeration and output results separately. A Wine
prefix without playback devices must not turn successful decoding into a claimed
output pass. Likewise, selecting a headless SDL driver must be verified from the
driver actually activated inside the Windows process.

Applications using native scene listeners or emitters additionally need their
scene-to-audio bindings, and every application needs an inventory of its actual
effect calls. Replacing the dependency does not establish a performance gain;
loading and frame-time comparisons require the same scene, settings and workload.

## SCP application behavior

The SCP scripts use scripted stereo positioning for their ordinary spatial
effects: `PlaySound2` and `LoopSound2` in `Main.bb` derive channel gain from
`EntityDistance`, then set `ChannelPan` from `DeltaYaw`; `UpdateSoundOrigin`
repeats those channel updates as entities move, including from the game and
event update paths in `Main.bb`, `NPCs.bb`, and `UpdateEvents.bb`. Room ambience
uses the same pattern for per-room sound emitters in `MapSystem.bb`. The
`CreateListener(Camera)` line in `Main.bb` is commented out, and the source has
no active `EmitSound`, `Load3DSound`, or FMOD DSP calls. Keep this existing
distance/gain/pan behavior for those channels; the new runtime's optional 3D
listener/emitter API is not a replacement for it. `MusicPlayer.bb` is a separate
music path and should not be treated as an FSOUND stream or spatial effect.
