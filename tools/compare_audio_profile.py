#!/usr/bin/env python3
"""Compare normalized Init results; original NOSOUND and SDL dummy are distinct profiles."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def labeled_results(stdout):
    lines = stdout.splitlines()
    return {line: int(lines[index + 1]) for index, line in enumerate(lines[:-1])
            if line.startswith(("init-", "reinit-"))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--report", type=Path, default=Path("artifacts/m14-audio-comparison.json"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    original_path = root / "docs/evidence/m14-fmod/init-semantics.json"
    original = json.loads(original_path.read_text())
    source = root / "samples/Audio/init-profile.bb"
    output = root / "artifacts/m14-audio-profile/profile.dll"
    build = subprocess.run([args.dotnet, str(root / "src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll"),
                            "build", str(source), "-o", str(output), "--externals", str(root / "samples/Audio/scp.json")],
                           text=True, capture_output=True, timeout=30, check=True)
    capture = subprocess.run([args.dotnet, str(output)], text=True, capture_output=True,
                             env={**os.environ, "SDL_AUDIODRIVER": "dummy"}, timeout=20, check=True)
    expected = labeled_results(original["capture"]["stdout"])
    actual = labeled_results(capture.stdout)
    if set(actual) != set(expected):
        raise ValueError("Original and local profile cases differ")
    known = {"reinit-valid-flag", "init-unknown-flag-bit"}
    comparisons = [{"case": name, "original_low_byte": value, "local": actual[name],
                    "status": "match" if value == actual[name] else "known_divergence" if name in known and (value, actual[name]) == (1, 0) else "mismatch"}
                   for name, value in expected.items()]
    result = {"original_profile": original["profile"], "local_profile": "SDL3 Linux x64, dummy audio driver; no playback requested",
              "scope": "Init result normalized to 0/1 only; no comparison of error numbers, mixer or audio output",
              "original_evidence_sha256": hashlib.sha256(original_path.read_bytes()).hexdigest(),
              "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
              "build_exit_code": build.returncode, "run_exit_code": capture.returncode,
              "stdout": capture.stdout, "stderr": capture.stderr, "comparisons": comparisons}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"match": sum(row["status"] == "match" for row in comparisons),
                      "known_divergence": sum(row["status"] == "known_divergence" for row in comparisons)}))
    return int(any(row["status"] == "mismatch" for row in comparisons))


if __name__ == "__main__":
    raise SystemExit(main())
