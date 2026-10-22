# Blitz3DCS.Audio

This standalone `net10.0` assembly provides BASIC-callable `FSOUND.Init(int, int, int)` and `FSOUND.Close()` entry points backed by SDL3. It has no Compiler or CLI project dependency. `Init` returns `1` after opening SDL's default stereo playback device, and `0` on validation, SDL, or library-load failure; `FSOUND.LastError` contains the failure detail. `Close` is idempotent and returns `0` as an acknowledgment for the current BASIC external-call catalog.

The tested runtime target is Linux x64 with SDL3 installed as the system library `libSDL3.so.0`. Install SDL3 through the host OS package manager; this assembly does not bundle SDL3. Other operating systems and library names are not implemented yet. The `SDL_AUDIODRIVER=dummy` backend can verify lifecycle behavior in headless environments, but it is a test driver and does not produce sound.

The implementation supports `flags=0`; nonzero flags fail explicitly. A repeated successful `Init` with identical settings returns success and keeps the current device open; changed settings fail until `Close` is called. The `maxSoftwareChannels` value is validated for nonnegative input but is not yet used to create FMOD channels. This project implements initialization and shutdown only and does not claim FMOD mixer or playback equivalence.
