#!/usr/bin/env python3
"""Capture the existing SDL playback probe through SDL3's disk audio driver.

This verifies PCM submitted through the SDL device backend without opening a
host audio device or recording a system sink. It stores only a JSON summary by
default; the temporary raw PCM output is removed when the probe exits.
"""
from __future__ import annotations

import argparse
import array
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import subprocess
import tempfile


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--playback", type=Path, required=True, help="built playback.dll")
    parser.add_argument("--asset", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    dotnet, playback, asset = (p.resolve() for p in (args.dotnet, args.playback, args.asset))
    for path in (dotnet, playback, asset):
        if not path.is_file():
            parser.error(f"file does not exist: {path}")

    with tempfile.TemporaryDirectory(prefix="blitzcs-sdl-disk-") as temp:
        raw = Path(temp) / "post-sdl.raw"
        env = os.environ.copy()
        env.update({
            "SDL_AUDIO_DRIVER": "disk",
            "SDL_AUDIODRIVER": "disk",
            "SDL_AUDIO_DISK_OUTPUT_FILE": str(raw),
            "BLITZCS_AUDIO_ASSET": str(asset),
        })
        env.pop("BLITZCS_AUDIO_DEVICE", None)
        env.pop("PULSE_SINK", None)
        result = subprocess.run([str(dotnet), str(playback)], env=env, text=True,
                                capture_output=True, timeout=30)
        snapshots = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
        stderr_match = re.search(r"format=(\w+) channels=(\d+) freq=(\d+)", result.stderr)
        report = {
            "exit_code": result.returncode,
            "audio_driver": "disk",
            "device_routing": "SDL disk output file; no Pulse sink/device selected",
            "asset_sha256": hashlib.sha256(asset.read_bytes()).hexdigest(),
            "playback_sha256": hashlib.sha256(playback.read_bytes()).hexdigest(),
            "stdout": result.stdout,
            "stderr": result.stderr,
            "snapshots": snapshots,
            "raw_pcm": None,
        }
        if raw.is_file() and raw.stat().st_size:
            pcm = raw.read_bytes()
            raw_format = stderr_match.group(1) if stderr_match else None
            channels = int(stderr_match.group(2)) if stderr_match else None
            rate = int(stderr_match.group(3)) if stderr_match else None
            stats = {"bytes": len(pcm), "sha256": hashlib.sha256(pcm).hexdigest(),
                     "format": raw_format, "channels": channels, "sample_rate": rate}
            if raw_format == "F32LE" and len(pcm) % 4 == 0:
                samples = struct.unpack(f"<{len(pcm) // 4}f", pcm)
                stats.update({
                    "samples": len(samples),
                    "frames": len(samples) // channels if channels else None,
                    "finite_samples": sum(math.isfinite(value) for value in samples),
                    "nonzero_samples": sum(value != 0 for value in samples),
                    "rms": math.sqrt(sum(value * value for value in samples) / len(samples)),
                    "peak": max(abs(value) for value in samples),
                })
            report["raw_pcm"] = stats
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"exit_code": report["exit_code"], "snapshots": len(snapshots),
                          "raw_pcm": report["raw_pcm"]}))
        return 0 if result.returncode == 0 and report["raw_pcm"] is not None else 1


if __name__ == "__main__":
    raise SystemExit(main())
