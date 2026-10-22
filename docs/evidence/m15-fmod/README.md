# M15 FMOD 3.75 semantic oracle

This evidence comes from the pinned PE32 FMOD 3.75 DLL (`1e08da161cf78746304a834973971ee47a24817ee4e28df74e3f37dbbb383b22`) loaded by the original Blitz3D 1.118 Win32 runtime under Proton 11, with FMOD output mode 1 (WinMM). The probe generates a 3 second PCM mono WAV and uses an isolated SDK copy. It leaves `/tmp/scpcb-audit-source` and the original SDK untouched. `probe.bb` is the exact captured source; `probe.json` contains the command output, DLL/WAV/source hashes, and declarations.

The `Stream_Open` declaration intentionally follows the FMOD 3.75 header and the `_FSOUND_Stream_Open@16` export: `(filename, mode, offset, length)`. Both `length=0` and `length=-1` returned a non-null stream handle for the WAV, and playback worked. The SCP declaration and active call use three arguments (`FSOUND_Stream_Open%(filename$,mode%,memlength%)`, call at `StrictLoads.bb:163`); they do not express the fourth ABI argument. The probe does not claim the old call is safe.

Observed results from the captured run:

- `FSOUND_Init(44100,64,0)` succeeds; version is `3.75`.
- `FSOUND_Stream_Play(FSOUND_FREE, stream)` returns a nonzero channel value (8192 in this capture; earlier captures returned 4096). Passing the returned value to channel operations works. Treat it as a returned handle value; its observed value is not stable across runs.
- SetFrequency to 11025 reports 11025; setting 0 then -1 returns success and the getter reports 0 then -1. The probe restores 22050 Hz and confirms the getter before the loop-duration wait.
- With playback restored to the WAV's native 22050 Hz, loop mode `2` remained playing after 3300 ms with a 3000 ms WAV. Pause returns success, `GetPaused` reports 1, and `IsPlaying` remains 1 while paused. Resume returns success and reports 0.
- SetVolume returns success for 0, 128, 255, 256, and -1. GetVolume reported 0, 128, 255, 0, and 0 respectively. `FSOUND_SetVolume(FSOUND_ALL=-3,64)` returned 0 (error 18 in this capture).
- SetPan to 0 or 255 returns success. This run does not measure audible output or pan position.
- Channel 0 reports not playing, while volume/pause/stop calls on it return success. `IsPlaying(999999)` returned 0 and error 14 without crashing. No broad invalid-channel mutators were run.
- SetVolume returns success for -1 and 256, and GetVolume reports 0 for both. This establishes the observed result only; it does not establish a general clamp/wrap rule.
- `StopSound` on the playing channel succeeds and `IsPlaying` then returns 0. `Stream_Stop` and `Stream_Close` on the owned stream succeed. `IsPlaying` on its former channel after stream close (device still initialized) returns 0/error 14.
- After `FSOUND_Close`, `IsPlaying` on the former integer channel value safely returns 0/error 2; a second `GetError` still reports 2. Reinitializing with `(44100,64,0)` succeeds/error 0, and closing again completes. The probe performs no native-pointer operation after close.
- Opening a missing file returns NULL/error 9. After a successful open, `GetError` reports 14 repeatedly in this capture; repeated reads do not clear that value. Successful playback/channel operations subsequently report error 0. Preserve the observed return/error pairs without treating the pointer result as proof that the error is cleared.

The integer result from `SetOutput` and the native signed-char functions contains unstable upper bits under Blitz's Integer declaration. Read their low byte only in the probe. Do not use captured full 32-bit values as expected results.

## SCP audio usage audit

The audit target is the read-only `/tmp/scpcb-audit-source` snapshot. File hashes for the central declarations/wrappers and high-traffic callers are in `usage-audit.json`. The snapshot was not changed.

SCP directly calls the FMOD declarations at 14 active call sites: `FSOUND_Init` (one site), `FSOUND_Stream_Open`, `FSOUND_Stream_Play`, `FSOUND_SetVolume`, `FSOUND_SetPaused`, `FSOUND_StopSound`, `FSOUND_Stream_Stop`, `FSOUND_Stream_Close`, `FSOUND_IsPlaying`, and `FSOUND_SetPan`. `FSOUND_GetVolume` and `FSOUND_SetVolumeAbsolute` are declared but have no active call sites. `FSOUND_Close` only appears in a commented-out call. Their signatures and return-width mismatches remain recorded in the M14 `usage-audit.json`.

The native Blitz sound surface used by SCP also includes `LoadSound`, `PlaySound`, `FreeSound`, `ChannelPlaying`, `ChannelVolume`, `PauseChannel`, `ResumeChannel`, and `StopChannel`, plus spatial `PlaySound2` and `LoopSound2`. SCP wrappers layer strict resource handles, auto-release, streamed sound controls, and spatial updates over these calls; they are not extra native exports. `MusicPlayer.bb` also uses its own `alSource*` music-player path and should not be conflated with this FSOUND stream API.
