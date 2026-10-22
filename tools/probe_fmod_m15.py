#!/usr/bin/env python3
"""Probe FMOD 3.75 stream/channel behavior in genuine Blitz3D Win32.

Uses a generated WAV, a temporary Blitz3D SDK shadow and a caller-supplied
trusted FMOD DLL. The supplied SDK and game checkout are never modified.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import struct
import tempfile
import wave

try:
    from tools.original_blitz3d_adapter import capture, windows_path, AdapterError
except ModuleNotFoundError:
    from original_blitz3d_adapter import capture, windows_path, AdapterError


def make_wave(path: Path) -> None:
    rate, seconds = 22050, 3
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        frames = bytearray()
        for i in range(rate * seconds):
            sample = int(5000 * math.sin(2 * math.pi * 440 * i / rate))
            frames.extend(struct.pack("<h", sample))
        wav.writeframes(frames)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    required = ["BLITZ3D_HOME", "BLITZ3D_WINE", "BLITZ3D_WINEPREFIX", "BLITZ3D_DISPLAY"]
    if any(not os.environ.get(key) for key in required):
        parser.error("set " + ", ".join(required))
    dll, home = args.dll.resolve(), Path(os.environ["BLITZ3D_HOME"]).resolve()
    if not dll.is_file() or not (home / "bin" / "blitzcc.exe").is_file():
        parser.error("DLL and original compiler must exist")

    report = {
        "dll_sha256": hashlib.sha256(dll.read_bytes()).hexdigest(),
        "runtime": "original Blitz3D 1.118 Win32 under supplied Proton/Wine prefix",
        "safety": "only valid handles returned by Stream_Open/Stream_Play are used; no invalid pointers",
        "stream_open_abi": "header-correct four arguments: filename, mode, offset, length; @16",
        "wave": {"format": "PCM signed 16-bit mono 22050 Hz", "duration_seconds": 3,
                 "sha256": None},
        "cases": ["FSOUND_FREE=-1", "FSOUND_ALL=-3", "loop mode 2", "volume",
                  "pause/resume", "frequency: -1, 0, 11025, restore to 22050", "pan", "playing", "stop/close",
                  "after-stream-close status", "after-device-close status and reinit",
                  "missing file", "error codes"],
    }
    os.environ["BLITZ3D_EVIDENCE_DIR"] = str(args.evidence_dir.resolve())
    args.evidence_dir.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="blitzcs-m15-fmod-") as temp_name:
        temp = Path(temp_name)
        shadow = temp / "sdk"
        shutil.copytree(home / "bin", shadow / "bin")
        (shadow / "userlibs").mkdir()
        wav_path = args.evidence_dir / "tone.wav"
        make_wave(wav_path)
        report["wave"]["sha256"] = hashlib.sha256(wav_path.read_bytes()).hexdigest()
        wav_copy = temp / "tone.wav"
        shutil.copy2(wav_path, wav_copy)
        declarations = [f'.lib "{windows_path(dll)}"',
            'M15Version#():"_FSOUND_GetVersion@0"',
            'M15Output%(mode%):"_FSOUND_SetOutput@4"',
            'M15Init%(rate%,channels%,flags%):"_FSOUND_Init@12"',
            'M15Error%():"_FSOUND_GetError@0"',
            'M15Open%(file$,mode%,offset%,length%):"_FSOUND_Stream_Open@16"',
            'M15Play%(channel%,stream%):"_FSOUND_Stream_Play@8"',
            'M15IsPlaying%(channel%):"_FSOUND_IsPlaying@4"',
            'M15SetVolume%(channel%,volume%):"_FSOUND_SetVolume@8"',
            'M15GetVolume%(channel%):"_FSOUND_GetVolume@4"',
            'M15SetPaused%(channel%,paused%):"_FSOUND_SetPaused@8"',
            'M15GetPaused%(channel%):"_FSOUND_GetPaused@4"',
            'M15SetFrequency%(channel%,freq%):"_FSOUND_SetFrequency@8"',
            'M15GetFrequency%(channel%):"_FSOUND_GetFrequency@4"',
            'M15SetPan%(channel%,pan%):"_FSOUND_SetPan@8"',
            'M15StopChannel%(channel%):"_FSOUND_StopSound@4"',
            'M15StopStream%(stream%):"_FSOUND_Stream_Stop@4"',
            'M15CloseStream%(stream%):"_FSOUND_Stream_Close@4"',
            'M15Close() :"_FSOUND_Close@0"']
        (shadow / "userlibs" / "m15.decls").write_text("\n".join(declarations) + "\n", encoding="cp1252")
        win_wav = windows_path(wav_copy)
        source_text = f'''Print "version":Print M15Version()
Print "output":Print M15Output(1)
Print "init":Print M15Init(44100,64,0) And 255:Print "error":Print M15Error()
Print "channel-zero-is-playing":Print M15IsPlaying(0) And 255:Print M15Error()
Print "channel-zero-volume":Print M15SetVolume(0,32) And 255:Print M15Error()
Print "channel-zero-pause":Print M15SetPaused(0,1) And 255:Print M15Error()
Print "channel-zero-stop":Print M15StopChannel(0) And 255:Print M15Error()
Print "channel-large-is-playing":Print M15IsPlaying(999999) And 255:Print M15Error()
Print "open-length-minus-one":Local stream2%=M15Open("{win_wav}",2,0,-1):Print stream2%:Print M15Error():Print M15CloseStream(stream2%) And 255
Print "open-loop-valid":Local stream%=M15Open("{win_wav}",2,0,0):Print stream%:Print M15Error()
Print "open-error-read-again":Print M15Error()
Print "play-free":Local channel%=M15Play(-1,stream%):Print channel%:Print M15Error()
Print "playing-before-pause":Print M15IsPlaying(channel%) And 255
Print "volume-128":Print M15SetVolume(channel%,128) And 255:Print M15GetVolume(channel%)
Print "volume-zero":Print M15SetVolume(channel%,0) And 255:Print M15GetVolume(channel%)
Print "volume-255":Print M15SetVolume(channel%,255) And 255:Print M15GetVolume(channel%)
Print "volume-256":Print M15SetVolume(channel%,256) And 255:Print M15Error()
Print "volume-minus-one":Print M15SetVolume(channel%,-1) And 255:Print M15GetVolume(channel%):Print M15Error()
Print "restore-volume-128":Print M15SetVolume(channel%,128) And 255:Print M15GetVolume(channel%)
Print "frequency-11025":Print M15SetFrequency(channel%,11025) And 255:Print M15GetFrequency(channel%)
Print "frequency-zero":Print M15SetFrequency(channel%,0) And 255:Print M15GetFrequency(channel%):Print M15Error()
Print "frequency-minus-one":Print M15SetFrequency(channel%,-1) And 255:Print M15GetFrequency(channel%):Print M15Error()
Print "frequency-reset-22050":Print M15SetFrequency(channel%,22050) And 255:Print M15GetFrequency(channel%):Print M15Error()
Print "pan-left":Print M15SetPan(channel%,0) And 255
Print "pause":Print M15SetPaused(channel%,1) And 255:Print M15GetPaused(channel%)
Print "playing-paused":Print M15IsPlaying(channel%) And 255
Print "resume":Print M15SetPaused(channel%,0) And 255:Print M15GetPaused(channel%)
Delay 3300
Print "playing-after-source-duration-loop-mode-2":Print M15IsPlaying(channel%) And 255:Print M15Error()
Print "volume-all-64":Print M15SetVolume(-3,64) And 255:Print M15Error()
Print "pan-right":Print M15SetPan(channel%,255) And 255
Print "channel-stop":Print M15StopChannel(channel%) And 255:Print M15Error()
Print "playing-after-stop":Print M15IsPlaying(channel%) And 255
Print "stream-stop-after-channel-stop":Print M15StopStream(stream%) And 255:Print M15Error()
Print "stream-close":Print M15CloseStream(stream%) And 255:Print M15Error()
Print "channel-playing-after-stream-close":Print M15IsPlaying(channel%) And 255:Print M15Error()
Print "open-missing":Local missing%=M15Open("{windows_path(temp / 'missing.wav')}",1,0,0):Print missing%:Print M15Error()
Print "close":M15Close:Print "closed"
Print "channel-playing-after-device-close":Print M15IsPlaying(channel%) And 255:Print M15Error()
Print "device-close-error-read-again":Print M15Error()
Print "reinit-after-device-close":Print M15Init(44100,64,0) And 255:Print M15Error()
Print "close-after-reinit":M15Close:Print "closed-again"
'''
        source = temp / "probe.bb"
        source.write_text(source_text, encoding="cp1252")
        report["probe_source_sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
        report["declarations"] = declarations
        shutil.copy2(source, args.evidence_dir / "probe.bb")
        try:
            result = capture(source, home=shadow, wine=os.environ["BLITZ3D_WINE"],
                prefix=Path(os.environ["BLITZ3D_WINEPREFIX"]), display=os.environ["BLITZ3D_DISPLAY"], timeout=15)
            report["capture"] = result
            status = 0 if result["phase"] == "run" and result["exit_code"] == 0 else 1
        except (AdapterError, OSError, ValueError) as error:
            report["adapter_error"] = f"{type(error).__name__}: {error}"
            status = 2
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(args.report.resolve())
    return status


if __name__ == "__main__":
    raise SystemExit(main())
