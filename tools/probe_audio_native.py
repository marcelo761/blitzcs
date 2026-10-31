#!/usr/bin/env python3
"""Publish and run the native Windows SDL3/libsndfile audio smoke harness."""

from __future__ import annotations

import argparse
import array
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, text=True, check=True, **kwargs)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", choices=("win-x86", "win-x64"), required=True)
    parser.add_argument("--ogg", type=Path, required=True, help="existing OGG asset; it is read, not copied")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--runner", default="wine", help="Windows executable runner, such as Wine or Proton")
    parser.add_argument("--runner-arg", action="append", default=[], help="extra runner argument; repeat as needed")
    parser.add_argument("--winepath", help="optional Wine path converter; otherwise use Wine's default Z: drive mapping")
    parser.add_argument("--driver", choices=("disk", "dummy", "default"), default="disk")
    parser.add_argument("--duration-ms", type=int, default=1200)
    parser.add_argument("--report", type=Path, help="optional JSON report path")
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    rid = args.target
    app_dir = root / "artifacts" / "native-audio-smoke" / rid
    app_dir.mkdir(parents=True, exist_ok=True)
    project = root / "tools" / "AudioNativeSmoke" / "AudioNativeSmoke.csproj"
    run([
        args.dotnet, "publish", str(project), "--configuration", "Release", "--runtime", rid,
        "--self-contained", "true", "--output", str(app_dir), "--verbosity", "minimal",
    ], cwd=root)

    shutil.copy2(root / "artifacts" / "native" / "sdl3" / rid / "SDL3.dll", app_dir / "SDL3.dll")
    shutil.copy2(root / "artifacts" / "native" / "libsndfile" / rid / "sndfile.dll", app_dir / "sndfile.dll")
    exe = app_dir / "AudioNativeSmoke.exe"
    if not exe.is_file():
        raise FileNotFoundError(f"Windows apphost was not produced: {exe}")

    def windows_path(path: Path) -> str:
        if os.name == "nt":
            return str(path.resolve())
        converter = args.winepath or shutil.which("winepath")
        if converter:
            result = run([converter, "-w", str(path.resolve())], capture_output=True)
            return result.stdout.strip()
        # Wine's standard Z: mapping points at the Unix root; Proton omits the
        # optional winepath utility but keeps this mapping in its prefixes.
        return "Z:\\" + str(path.resolve()).lstrip("/").replace("/", "\\")

    ogg = args.ogg.resolve(strict=True)
    disk_pcm = app_dir / "captured-f32le.pcm"
    driver_env = os.environ.copy()
    if args.driver == "default":
        driver_env.pop("SDL_AUDIODRIVER", None)
        driver_env.pop("SDL_AUDIO_DRIVER", None)
        driver_env.pop("SDL_AUDIO_DISK_OUTPUT_FILE", None)
    else:
        driver_env["SDL_AUDIODRIVER"] = args.driver
        driver_env["SDL_AUDIO_DRIVER"] = args.driver
        if args.driver == "disk":
            disk_pcm.unlink(missing_ok=True)
            driver_env["SDL_AUDIO_DISK_OUTPUT_FILE"] = windows_path(disk_pcm)

    smoke_command = [
        args.runner, *args.runner_arg, str(exe), "--ogg", windows_path(ogg),
        "--duration-ms", str(args.duration_ms),
        "--work-dir", windows_path(app_dir / "work"),
    ]
    if args.driver != "default":
        smoke_command.extend(["--sdl-driver", args.driver])
    process = subprocess.run(smoke_command, cwd=root, env=driver_env, capture_output=True, text=True, timeout=45, check=False)
    lines = [line for line in (process.stdout + "\n" + process.stderr).splitlines() if line.strip()]
    if not lines:
        raise RuntimeError(f"native smoke emitted no JSON; exit={process.returncode}; stderr={process.stderr[-4000:]}")
    json_lines = [line for line in lines if line.lstrip().startswith("{")]
    if not json_lines:
        raise RuntimeError(f"native smoke emitted no JSON; exit={process.returncode}; output={lines[-20:]}")
    result = json.loads(json_lines[-1])
    result["target"] = rid
    result["runner"] = args.runner
    result["driver"] = args.driver
    result["stderr"] = process.stderr
    result["processExitCode"] = process.returncode

    report = args.report or (app_dir / "report.json")
    report.parent.mkdir(parents=True, exist_ok=True)
    if result.get("result") != "pass":
        report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"result": result.get("result"), "target": rid, "driver": args.driver, "report": str(report)}))
        return 1
    if args.driver == "disk" and disk_pcm.is_file():
        raw = disk_pcm.read_bytes()
        samples = array.array("f")
        samples.frombytes(raw[:len(raw) - len(raw) % samples.itemsize])
        if sys.byteorder != "little":
            samples.byteswap()
        rms = math.sqrt(sum(sample * sample for sample in samples) / len(samples)) if samples else 0.0
        result["diskPcm"] = {
            "bytes": len(raw),
            "sha256": hashlib.sha256(raw).hexdigest(),
            "floatSamples": len(samples),
            "rms": rms,
            "nonzeroSamples": sum(sample != 0 for sample in samples),
        }
        if not samples or rms <= 0.0001:
            raise RuntimeError("SDL disk driver output was empty or silent")
    elif args.driver == "disk":
        raise RuntimeError("SDL disk driver did not create its PCM output file")

    report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"result": result.get("result"), "target": rid, "driver": args.driver, "report": str(report)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
