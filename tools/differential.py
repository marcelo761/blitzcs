#!/usr/bin/env python3
"""Run compatibility fixtures against BlitzCS and an optional original adapter."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any, Callable


def normalize_newlines(value: str) -> str:
    return value.replace("\r\n", "\n").replace("\r", "\n")


def load_manifest(path: Path) -> list[dict[str, Any]]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError("manifest must be a JSON object")
    if "fixtures" not in document:
        raise ValueError("manifest needs a 'fixtures' list")
    fixtures = document["fixtures"]
    if not isinstance(fixtures, list):
        raise ValueError("manifest 'fixtures' must be a list")
    names: set[str] = set()
    for item in fixtures:
        if not isinstance(item, dict) or not all(k in item for k in ("name", "source", "expected_phase")):
            raise ValueError("each fixture needs name, source, and expected_phase")
        name = item["name"]
        if not isinstance(name, str) or not name or Path(name).name != name or name in (".", ".."):
            raise ValueError("fixture names must be non-empty single path components")
        if name in names:
            raise ValueError(f"duplicate fixture name: {name}")
        names.add(name)
        source = item["source"]
        if not isinstance(source, str) or not source or Path(source).is_absolute():
            raise ValueError(f"source for {name} must be a non-empty relative path")
        if item["expected_phase"] not in ("run", "compile_error", "runtime_error"):
            raise ValueError(f"invalid expected_phase for {name}")
        if "expected_stdout" in item and not isinstance(item["expected_stdout"], str):
            raise ValueError(f"expected_stdout for {name} must be a string")
    return fixtures


def _valid_capture(value: Any) -> bool:
    return (isinstance(value, dict) and set(value) == {"phase", "stdout", "exit_code"}
            and value.get("phase") in ("run", "compile_error", "runtime_error")
            and isinstance(value.get("stdout"), str)
            and isinstance(value.get("exit_code"), int) and not isinstance(value.get("exit_code"), bool))


def compare_capture(actual: dict[str, Any], original: Any) -> tuple[str, str | None]:
    if isinstance(original, str):
        try:
            original = json.loads(original)
        except (json.JSONDecodeError, TypeError):
            return "adapter_error", "adapter stdout is not valid JSON"
    if not _valid_capture(original):
        return "adapter_error", "adapter response must contain phase, stdout, and integer exit_code"
    if actual["phase"] != original["phase"]:
        return "mismatch", f"phase differs: local={actual['phase']} original={original['phase']}"
    if (actual["exit_code"] == 0) != (original["exit_code"] == 0):
        return "mismatch", "success/failure exit status differs"
    if actual["phase"] != "compile_error" and normalize_newlines(actual["stdout"]) != normalize_newlines(original["stdout"]):
        return "mismatch", "stdout differs"
    return "match", None


def _capture_process(command: list[str], timeout: float, runner: Callable[..., Any]) -> Any:
    return runner(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def run_harness(dotnet: str, blitz_cli: str, manifest: Path, report_path: Path,
                original_runner: str | None = None, timeout: float = 20,
                require_original: bool = False, runner: Callable[..., Any] = subprocess.run,
                externals: Path | None = None) -> dict[str, Any]:
    entries = load_manifest(Path(manifest))
    rows: list[dict[str, Any]] = []
    manifest_dir = Path(manifest).resolve().parent
    for entry in entries:
        source = (manifest_dir / entry["source"]).resolve()
        if not source.is_file():
            raise ValueError(f"fixture source does not exist: {source}")
        row: dict[str, Any] = {"name": entry["name"], "source": str(source)}
        with tempfile.TemporaryDirectory(prefix="blitzcs-diff-") as temp:
            output = Path(temp) / f"{entry['name']}.dll"
            build_command = [dotnet, blitz_cli, "build", str(source), "-o", str(output)]
            if externals is not None:
                build_command.extend(["--externals", str(Path(externals).resolve())])
            stage = "compile"
            try:
                built = _capture_process(build_command, timeout, runner)
                if built.returncode != 0:
                    actual = {"phase": "compile_error", "stdout": normalize_newlines(built.stdout),
                              "stderr": normalize_newlines(built.stderr), "exit_code": built.returncode}
                else:
                    stage = "run"
                    execution = _capture_process([dotnet, str(output)], timeout, runner)
                    actual = {"phase": "run" if execution.returncode == 0 else "runtime_error",
                              "stdout": normalize_newlines(execution.stdout),
                              "stderr": normalize_newlines(execution.stderr), "exit_code": execution.returncode}
            except subprocess.TimeoutExpired as exc:
                stdout = exc.stdout or ""
                stderr = exc.stderr or ""
                actual = {"phase": "compile_error" if stage == "compile" else "runtime_error", "stdout": normalize_newlines(
                    stdout.decode("utf-8", "replace") if isinstance(stdout, bytes) else stdout),
                    "stderr": normalize_newlines(stderr.decode("utf-8", "replace") if isinstance(stderr, bytes) else stderr),
                    "exit_code": -1, "timed_out": True}
            row["actual"] = actual
            expected_ok = not actual.get("timed_out") and actual["phase"] == entry["expected_phase"]
            if "expected_stdout" in entry:
                expected_ok = expected_ok and normalize_newlines(actual["stdout"]) == normalize_newlines(entry["expected_stdout"])
            row["local_status"] = "pass" if expected_ok else "fail"
            if expected_ok:
                row["local_reason"] = None
            elif actual.get("timed_out"):
                row["local_reason"] = "local process timed out"
            else:
                row["local_reason"] = "actual phase or expected stdout differs from manifest"

            if not original_runner:
                row["comparison_status"] = "pending"
                row["comparison_reason"] = "original runner was not provided"
            else:
                try:
                    original_process = _capture_process([original_runner, str(source)], timeout, runner)
                    if original_process.returncode != 0:
                        row["comparison_status"] = "adapter_error"
                        row["comparison_reason"] = f"adapter exited with status {original_process.returncode}"
                    else:
                        status, reason = compare_capture(
                            {"phase": actual["phase"], "stdout": actual["stdout"], "exit_code": actual["exit_code"]},
                            original_process.stdout)
                        if actual.get("timed_out"):
                            status, reason = "mismatch", "local process timed out; its phase cannot be compared"
                        row["comparison_status"] = status
                        row["comparison_reason"] = reason
                        try:
                            row["original_capture"] = json.loads(original_process.stdout)
                        except (json.JSONDecodeError, TypeError):
                            row["original_capture"] = {"raw_stdout": original_process.stdout}
                except subprocess.TimeoutExpired:
                    row["comparison_status"] = "adapter_error"
                    row["comparison_reason"] = f"original adapter timed out after {timeout:g}s"
                except OSError as exc:
                    row["comparison_status"] = "adapter_error"
                    row["comparison_reason"] = f"could not start original adapter: {exc}"
        rows.append(row)

    totals = {
        "fixtures": len(rows),
        "local_pass": sum(r["local_status"] == "pass" for r in rows),
        "local_fail": sum(r["local_status"] == "fail" for r in rows),
        "comparison_match": sum(r["comparison_status"] == "match" for r in rows),
        "comparison_mismatch": sum(r["comparison_status"] == "mismatch" for r in rows),
        "comparison_adapter_error": sum(r["comparison_status"] == "adapter_error" for r in rows),
        "comparison_pending": sum(r["comparison_status"] == "pending" for r in rows),
    }
    report = {"totals": totals, "fixtures": rows}
    report_path = Path(report_path)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


def main(argv: list[str] | None = None) -> int:
    repo = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--blitz-cli", default=str(repo / "src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll"))
    parser.add_argument("--manifest", default=str(repo / "tests/Compatibility/fixtures.json"))
    parser.add_argument("--report", default=str(repo / "artifacts/differential.json"))
    parser.add_argument("--original-runner", help="optional executable implementing the JSON capture protocol")
    parser.add_argument("--timeout", type=float, default=20)
    parser.add_argument("--require-original", action="store_true")
    parser.add_argument("--externals", type=Path, help="optional typed external catalog for the BlitzCS build")
    args = parser.parse_args(argv)
    try:
        report = run_harness(args.dotnet, args.blitz_cli, Path(args.manifest), Path(args.report),
                             args.original_runner, args.timeout, args.require_original, externals=args.externals)
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"differential harness error: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(report["totals"], sort_keys=True))
    totals = report["totals"]
    if totals["local_fail"] or totals["comparison_mismatch"] or totals["comparison_adapter_error"]:
        return 1
    if args.require_original and totals["comparison_pending"]:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
