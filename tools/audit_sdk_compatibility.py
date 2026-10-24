#!/usr/bin/env python3
"""Run bounded syntax and semantic checks over the original Blitz3D SDK corpus.

This audit reads the SDK in place and never invokes the original compiler or
runtime. It records source hashes and compiler diagnostics; a successful
semantic check is not evidence that a program can be emitted or executed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any

try:  # Support both direct script execution and package imports in tests.
    from tools.original_blitz3d_adapter import include_references
    from tools.scp_compile_probe import _case_insensitive_path
except ModuleNotFoundError:
    from original_blitz3d_adapter import include_references
    from scp_compile_probe import _case_insensitive_path


DEFAULT_CASES = (
    ("pure", "help/commands/2d_examples/Print.bb"),
    ("files", "help/commands/2d_examples/FileSize.bb"),
    ("input", "help/commands/2d_examples/GetKey.bb"),
    ("graphics", "help/commands/2d_examples/Graphics.bb"),
    ("graphics3d", "help/commands/3d_examples/Graphics3D.bb"),
    ("world", "help/commands/3d_examples/RenderWorld.bb"),
    ("entity", "help/commands/3d_examples/CreateCube.bb"),
    ("camera", "help/commands/3d_examples/CreateCamera.bb"),
    ("multifile", "Games/TunnelRun/tr.bb"),
)
MAX_CAPTURE = 64 * 1024
SOURCE_DIAGNOSTIC = re.compile(r"\(\d+,\d+\):")
PARSER_DIAGNOSTIC_MARKERS = (
    "expected an expression",
    "expected end-of-file",
    "unsupported syntax",
    "unexpected token",
    "unsupported keyword",
    "unsupported character",
    "unterminated string",
    "expected ",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def include_closure(sdk: Path, source: Path) -> list[dict[str, str]]:
    """Hash the SDK-local Include closure using classic Include path rules."""
    sdk = sdk.resolve()
    project_root = source.parent.resolve()
    pending = [source.resolve()]
    visited: set[Path] = set()
    while pending:
        current = pending.pop().resolve()
        if current in visited or not current.is_file() or not current.is_relative_to(sdk):
            continue
        visited.add(current)
        try:
            text = current.read_bytes().decode("cp1252")
        except UnicodeDecodeError:
            text = current.read_text(encoding="utf-8", errors="replace")
        for operand in include_references(text):
            # BlitzCS resolves every Include from the main source directory,
            # including Includes nested inside another source file.
            target = _case_insensitive_path(project_root, operand).resolve()
            if target.is_relative_to(sdk) and target.is_file():
                pending.append(target)
    return [
        {"path": path.relative_to(sdk).as_posix(), "sha256": sha256(path)}
        for path in sorted(visited)
    ]


def cli_prefix(path: Path) -> list[str]:
    if path.suffix.casefold() == ".dll":
        return ["dotnet", str(path)]
    return [str(path)]


def run_stage(command: list[str], cwd: Path, timeout: float) -> dict[str, Any]:
    started = time.monotonic()
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
        )
        stdout, stdout_truncated = bounded(result.stdout)
        stderr, stderr_truncated = bounded(result.stderr)
        return {
            "command": command,
            "exit_code": result.returncode,
            "timed_out": False,
            "duration_seconds": round(time.monotonic() - started, 3),
            "stdout": stdout,
            "stderr": stderr,
            "output_truncated": stdout_truncated or stderr_truncated,
        }
    except subprocess.TimeoutExpired as error:
        stdout, stdout_truncated = bounded(decode_output(error.stdout))
        stderr, stderr_truncated = bounded(decode_output(error.stderr))
        return {
            "command": command,
            "exit_code": None,
            "timed_out": True,
            "duration_seconds": round(time.monotonic() - started, 3),
            "stdout": stdout,
            "stderr": stderr,
            "output_truncated": stdout_truncated or stderr_truncated,
        }
    except OSError as error:
        return {
            "command": command,
            "exit_code": None,
            "timed_out": False,
            "duration_seconds": round(time.monotonic() - started, 3),
            "stdout": "",
            "stderr": str(error),
            "output_truncated": False,
        }


def decode_output(value: str | bytes | None) -> str:
    if value is None:
        return ""
    return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value


def bounded(value: str) -> tuple[str, bool]:
    if len(value) <= MAX_CAPTURE:
        return value, False
    return value[:MAX_CAPTURE] + "\n...[truncated]", True


def is_parser_diagnostic(output: str) -> bool:
    lowered = output.casefold()
    return bool(SOURCE_DIAGNOSTIC.search(output)) and any(
        marker in lowered for marker in PARSER_DIAGNOSTIC_MARKERS
    )


def has_missing_provider_diagnostic(output: str) -> bool:
    lowered = output.casefold()
    if "no implementation" in lowered:
        return True
    summary = re.search(r"\b(\d+)\s+external call\(s\) lack implementations\b", lowered)
    return bool(summary and int(summary.group(1)) > 0)


def classify_stage(stage: str, result: dict[str, Any]) -> str:
    """Classify only what the stage's own diagnostics establish."""
    if result["timed_out"]:
        return "timeout"
    if result["exit_code"] is None:
        return "infrastructure_error"
    output = result["stdout"] + "\n" + result["stderr"]
    if SOURCE_DIAGNOSTIC.search(output) and "unable to open include file" in output.casefold():
        return "include_resolution_error"
    if stage == "syntax":
        if result["exit_code"] == 0:
            return "syntax_validated"
        return "parser_syntax_error" if is_parser_diagnostic(output) else "syntax_check_error"
    if stage == "semantics":
        if result["exit_code"] == 0:
            if has_missing_provider_diagnostic(output):
                return "declared_provider_missing"
            return "contract_validation_succeeded"
        if "unknown basic function or external symbol" in output.casefold():
            return "unknown_symbol_missing_declaration"
        if is_parser_diagnostic(output):
            return "parser_syntax_error"
        return "semantic_error"
    if stage == "emit":
        lowered = output.casefold()
        if has_missing_provider_diagnostic(output) or "required runtime provider" in lowered:
            return "declared_provider_missing"
        if any(marker in lowered for marker in (
            "binary library", "pe32 x86 image", "has no export", "export is ambiguous",
            "forwarder to",
        )):
            return "native_linking_error"
        if result["exit_code"] != 0:
            if "unknown basic function or external symbol" in lowered:
                return "unknown_symbol_missing_declaration"
            if is_parser_diagnostic(output):
                return "parser_syntax_error"
            if SOURCE_DIAGNOSTIC.search(output):
                return "semantic_error"
            return "managed_emission_error"
        return "managed_emission_succeeded"
    raise ValueError(f"unknown audit stage: {stage}")


def classified(stage: str, result: dict[str, Any]) -> dict[str, Any]:
    return {**result, "classification": classify_stage(stage, result)}


def cases(sdk: Path, include_all: bool) -> list[tuple[str, Path]]:
    if include_all:
        return [("all", p) for p in sorted(sdk.rglob("*.bb")) if p.is_file()]
    selected = [(category, sdk / relative) for category, relative in DEFAULT_CASES]
    missing = [str(path.relative_to(sdk)) for _, path in selected if not path.is_file()]
    if missing:
        raise ValueError("SDK is missing curated source(s): " + ", ".join(missing))
    return selected


def profile_hash(path: Path | None) -> str | None:
    return sha256(path) if path else None


def audit_case(
    sdk: Path,
    category: str,
    source: Path,
    cli: Path,
    baseline_cli: Path | None,
    profile: Path | None,
    timeout: float,
    emit: bool = False,
) -> dict[str, Any]:
    inputs = include_closure(sdk, source)
    case: dict[str, Any] = {
        "category": category,
        "source": source.relative_to(sdk).as_posix(),
        "source_sha256": sha256(source),
        "include_closure": inputs,
        "runs": {},
        "runtimeExecution": "not_attempted",
        "interpretation": "CLI diagnostics only; no translated application is executed",
    }
    run_specs = [("current", cli)]
    if baseline_cli:
        run_specs.insert(0, ("baseline", baseline_cli))
    with tempfile.TemporaryDirectory(prefix="blitzcs-sdk-audit-") as temporary:
        cwd = Path(temporary)
        for label, executable in run_specs:
            prefix = cli_prefix(executable)
            syntax = classified("syntax", run_stage(prefix + ["check-syntax", str(source)], cwd, timeout))
            semantics_args = prefix + ["check-semantics", str(source)]
            if profile:
                semantics_args += ["--externals", str(profile)]
            semantics = classified("semantics", run_stage(semantics_args, cwd, timeout))
            run = {
                "cli": str(executable),
                "stages": {"syntax": syntax, "semantics": semantics},
            }
            if emit:
                output = cwd / f"{label}.dll"
                emit_args = prefix + ["build", str(source), "-o", str(output)]
                if profile:
                    emit_args += ["--externals", str(profile)]
                emitted = run_stage(emit_args, cwd, timeout)
                artifact = {
                    "file": output.name,
                    "exists": output.is_file(),
                    "size_bytes": output.stat().st_size if output.is_file() else None,
                }
                emit_result = {**emitted, "artifact": artifact}
                emit_result["classification"] = (
                    "managed_emission_missing_output"
                    if emitted["exit_code"] == 0 and not artifact["exists"]
                    else classify_stage("emit", emit_result)
                )
                run["stages"]["emit"] = emit_result
            case["runs"][label] = run
    return case


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", required=True, type=Path, help="root of an extracted Blitz3D V1.118 SDK")
    parser.add_argument("--cli", required=True, type=Path, help="blitzcs executable or CLI .dll (run with dotnet)")
    parser.add_argument("--baseline-cli", type=Path, help="optional comparison compiler executable or CLI .dll")
    parser.add_argument("--profile", type=Path, help="optional external catalog passed to check-semantics")
    parser.add_argument("--out", type=Path, help="write JSON here; defaults to standard output")
    parser.add_argument("--all", action="store_true", help="audit every .bb source instead of the bounded curated corpus")
    parser.add_argument("--emit", action="store_true", help="also run CLI build to a temporary DLL and record artifact presence; never execute it")
    parser.add_argument("--timeout", type=float, default=30.0, help="seconds allowed per compiler stage (default: 30)")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    sdk = args.sdk.resolve()
    cli = args.cli.resolve()
    baseline = args.baseline_cli.resolve() if args.baseline_cli else None
    profile = args.profile.resolve() if args.profile else None
    for label, path in (("SDK", sdk), ("CLI", cli), ("baseline CLI", baseline), ("profile", profile)):
        if path is None:
            continue
        if not path.exists() or (label == "SDK" and not path.is_dir()) or (label != "SDK" and not path.is_file()):
            raise SystemExit(f"{label} path does not exist or has the wrong kind: {path}")
    if args.timeout <= 0:
        raise SystemExit("--timeout must be greater than zero")
    if args.out and args.out.resolve().is_relative_to(sdk):
        raise SystemExit("--out must be outside the read-only SDK tree")
    selected = cases(sdk, args.all)
    document = {
        "schema_version": 1,
        "sdk": str(sdk),
        "sdk_version_scope": "caller-supplied extracted SDK; version is not inferred",
        "cli": str(cli),
        "cli_sha256": sha256(cli),
        "baseline_cli": str(baseline) if baseline else None,
        "baseline_cli_sha256": sha256(baseline) if baseline else None,
        "profile": str(profile) if profile else None,
        "profile_sha256": profile_hash(profile),
        "mode": "all" if args.all else "curated",
        "emit_requested": args.emit,
        "timeout_seconds_per_stage": args.timeout,
        "case_count": len(selected),
        "cases": [audit_case(sdk, category, source, cli, baseline, profile, args.timeout, args.emit)
                  for category, source in selected],
    }
    payload = json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(payload, encoding="utf-8")
    else:
        print(payload, end="")
    return 0


if __name__ == "__main__":
    main()
