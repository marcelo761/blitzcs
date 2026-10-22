#!/usr/bin/env python3
"""Compile each .bb file independently and record first diagnostics; never run game code."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import subprocess
import tempfile
import time
from pathlib import Path
import re

try:  # Support both `python tools/scp_compile_probe.py` and package imports in tests.
    from tools.original_blitz3d_adapter import include_references
except ModuleNotFoundError:
    from original_blitz3d_adapter import include_references

DECLARED_COMMIT = "edb8fe0840b78f14d1aef3a0bf6174630e7be296"

EXTERNAL_DECLARATION = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_]*)[%#$]?\s*\(", re.M)
UNKNOWN_SYMBOL = re.compile(
    r"unknown\s+(?:basic\s+)?(?:function|external\s+symbol)(?:\s+or\s+external\s+symbol)?\s+['\"]?([A-Za-z_][A-Za-z0-9_]*)",
    re.IGNORECASE,
)
DIAGNOSTIC_LOCATION = re.compile(r"^(.*)\((\d+),(\d+)\):")


def external_symbols(source_dir: Path) -> set[str]:
    """Names declared in Blitz .decls files alongside this pinned source set."""
    symbols: set[str] = set()
    for declaration_file in source_dir.glob("*.decls"):
        try:
            text = declaration_file.read_bytes().decode("cp1252")
        except (OSError, UnicodeError):
            continue
        symbols.update(match.group(1).casefold() for match in EXTERNAL_DECLARATION.finditer(text))
    return symbols


def load_native_symbols(catalog: Path) -> set[str]:
    """Load the checked-in projection of original Blitz3D rtSym registrations."""
    payload = json.loads(catalog.read_text(encoding="utf-8"))
    symbols = payload.get("symbols") if isinstance(payload, dict) else None
    if not isinstance(symbols, list) or not all(isinstance(name, str) for name in symbols):
        raise ValueError(f"native catalog must contain a string array named 'symbols': {catalog}")
    return {name.casefold() for name in symbols}


def parse_diagnostic_location(diagnostic: str | None) -> dict[str, object] | None:
    """Return the source location from the compiler's first diagnostic, when present."""
    if not diagnostic:
        return None
    match = DIAGNOSTIC_LOCATION.match(diagnostic)
    if not match:
        return None
    return {"file": Path(match.group(1)).name, "line": int(match.group(2)),
            "column": int(match.group(3))}


def _case_insensitive_path(root: Path, operand: str) -> Path:
    """Resolve legacy Windows-cased relative Include paths on case-sensitive hosts."""
    current = root
    for component in Path(operand.replace("\\", "/")).parts:
        if component in ("", "."):
            continue
        if component == "..":
            current = current.parent
            continue
        exact = current / component
        if exact.exists():
            current = exact
            continue
        try:
            matches = [entry for entry in current.iterdir()
                       if entry.name.casefold() == component.casefold()]
        except OSError:
            return current / component
        if len(matches) != 1:
            return current / component
        current = matches[0]
    return current


def include_closure(entry: Path) -> dict[str, object]:
    """Describe the static Include graph; this is not evidence of parser progress."""
    root = entry.parent.resolve()
    files: list[str] = []
    missing: list[str] = []
    edges: dict[Path, list[Path]] = {}

    def scan(path: Path, active: set[Path]) -> None:
        path = path.resolve()
        if path in active or not path.is_file():
            return
        active = {*active, path}
        try:
            source = path.read_bytes().decode("cp1252")
        except (OSError, UnicodeError):
            edges[path] = []
            return
        children: list[Path] = []
        for operand in include_references(source):
            child = _case_insensitive_path(root, operand).resolve()
            children.append(child)
            if not child.is_file():
                if operand not in missing:
                    missing.append(operand)
                continue
            if child != entry.resolve() and child not in files_seen:
                files_seen.add(child)
                files.append(child.relative_to(root).as_posix())
            scan(child, active)
        edges[path] = children

    files_seen: set[Path] = set()
    scan(entry, set())

    target = entry.resolve()
    depth_paths: dict[int, list[list[str]]] = {}

    def walk(path: Path, depth: int, active: set[Path], witness: list[str]) -> None:
        path = path.resolve()
        depth_paths.setdefault(depth, []).append(witness)
        if path in active:
            return
        active = {*active, path}
        for child in edges.get(path, []):
            if child.is_file():
                try:
                    label = child.relative_to(root).as_posix()
                except ValueError:
                    label = str(child)
                walk(child, depth + 1, active, [*witness, label])

    walk(target, 0, set(), [target.name])
    return {
        "files": files,
        "file_count": len(files),
        "missing": missing,
        "max_depth": max(depth_paths, default=0),
        "depth_witnesses": {depth: paths for depth, paths in depth_paths.items()},
    }


def diagnostic_include_location(static_closure: dict[str, object], diagnostic: str | None
                                ) -> tuple[int | list[int] | None, list[list[str]]]:
    """Find static Include path depth(s) that witness the diagnostic's source file."""
    location = parse_diagnostic_location(diagnostic)
    if location is None:
        return None, []
    target = str(location["file"]).casefold()
    depth_witnesses = static_closure.get("depth_witnesses", {})
    matches_by_depth: dict[int, list[list[str]]] = {}
    if isinstance(depth_witnesses, dict):
        for raw_depth, paths in depth_witnesses.items():
            matches = [path for path in paths if path[-1].casefold() == target]
            if matches:
                matches_by_depth[int(raw_depth)] = matches
    depths = sorted(matches_by_depth)
    depth: int | list[int] | None = (
        depths[0] if len(depths) == 1 else [depths[0], depths[-1]] if depths else None)
    paths = [path for value in depths for path in matches_by_depth[value]]
    return depth, paths


def summarize_integrated(result: dict[str, object], diagnostic_depth: int | list[int] | None,
                         diagnostic_paths: list[list[str]] | None = None) -> dict[str, object]:
    """Report only the first observed compiler error; later errors are unknown."""
    failed = result.get("phase") == "compiler_error"
    location = parse_diagnostic_location(result.get("first_diagnostic"))
    first_error = None
    if failed and location is not None:
        first_error = {**location, "category": result.get("category"),
                       "diagnostic": result.get("first_diagnostic")}
    return {
        "phase": result.get("phase"),
        "first_error": first_error,
        "observed_errors_lower_bound": 1 if failed else 0 if result.get("phase") == "compiled" else None,
        "additional_errors": None if failed or result.get("phase") != "compiled" else 0,
        "diagnostic_include_depth": diagnostic_depth,
        "diagnostic_include_paths": diagnostic_paths or [],
    }


def classify_result(phase: str, diagnostic: str | None,
                    declared_externals: set[str] | None = None,
                    known_natives: set[str] | None = None) -> str:
    """Group the first compiler result without treating every failure as syntax."""
    if phase == "compiled":
        return "compiled"
    if phase.startswith("infrastructure_"):
        return "infrastructure"
    message = (diagnostic or "").casefold()
    if not message:
        return "unclassified_compiler_error"
    if any(marker in message for marker in ("unexpected character", "unterminated string", "invalid numeric literal")):
        return "lexical"
    if any(marker in message for marker in ("expected ", "unexpected token", "unsupported syntax",
                                             "unsupported keyword", "expected an expression")):
        return "syntactic"
    if "unable to open include file" in message or "include file" in message and "not found" in message:
        return "external_missing"
    unknown = UNKNOWN_SYMBOL.search(message)
    if unknown:
        symbol = unknown.group(1).casefold()
        if declared_externals is not None and symbol in declared_externals:
            return "external_missing"
        if known_natives is not None and symbol in known_natives:
            return "native_unregistered"
        return "semantic_type"
    if any(marker in message for marker in ("type", "cannot convert", "incompatible", "undefined variable")):
        return "semantic_type"
    return "semantic_type"


def probe(source: Path, dotnet: str, cli: Path, timeout: float,
          declared_externals: set[str] | None = None,
          known_natives: set[str] | None = None,
          externals: Path | None = None) -> dict[str, object]:
    content = source.read_bytes()
    record: dict[str, object] = {
        "source": source.name,
        "sha256": hashlib.sha256(content).hexdigest(),
        "phase": "infrastructure_error",
        "returncode": None,
        "first_diagnostic": None,
        "assembly_emitted": False,
    }
    with tempfile.TemporaryDirectory(prefix="scp-blitzcs-probe-") as temp:
        assembly = Path(temp) / f"{source.stem}.dll"
        command = [dotnet, str(cli), "build", str(source), "-o", str(assembly)]
        if externals is not None:
            command.extend(["--externals", str(externals.resolve())])
        started = time.monotonic()
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            record["phase"] = "infrastructure_timeout"
            record["first_diagnostic"] = f"probe exceeded {timeout:g}s timeout"
        except OSError as error:
            record["first_diagnostic"] = f"could not start CLI: {error}"
        else:
            record["returncode"] = result.returncode
            record["assembly_emitted"] = assembly.is_file()
            diagnostic = next((line.strip() for text in (result.stderr, result.stdout)
                               for line in text.splitlines() if line.strip()), None)
            if result.returncode == 0 and assembly.is_file():
                record["first_diagnostic"] = None
                record["phase"] = "compiled"
            elif result.returncode == 0:
                record["first_diagnostic"] = "CLI returned success without emitting the requested assembly"
                record["phase"] = "infrastructure_error"
            else:
                record["first_diagnostic"] = diagnostic
                record["phase"] = "compiler_error"
        record["duration_ms"] = round((time.monotonic() - started) * 1000)
    record["category"] = classify_result(str(record["phase"]), record["first_diagnostic"],
                                          declared_externals, known_natives)
    return record


def syntax_probe(source: Path, dotnet: str, cli: Path, timeout: float) -> dict[str, object]:
    """Ask the CLI to validate syntax without claiming semantic or native binding."""
    record: dict[str, object] = {
        "phase": "infrastructure_error",
        "syntax_validated": False,
        "semantics": "not_assessed",
        "binding": "not_assessed",
        "native_resolution": "not_assessed",
        "returncode": None,
        "first_diagnostic": None,
    }
    command = [dotnet, str(cli), "check-syntax", str(source)]
    started = time.monotonic()
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        record["phase"] = "infrastructure_timeout"
        record["first_diagnostic"] = f"syntax check exceeded {timeout:g}s timeout"
    except OSError as error:
        record["first_diagnostic"] = f"could not start CLI: {error}"
    else:
        record["returncode"] = result.returncode
        diagnostic = next((line.strip() for text in (result.stderr, result.stdout)
                           for line in text.splitlines() if line.strip()), None)
        if result.returncode == 0:
            record.update({
                "phase": "syntax_validated",
                "syntax_validated": True,
                "semantics": "unresolved",
                "binding": "unresolved",
                "native_resolution": "unresolved",
            })
        else:
            record["first_diagnostic"] = diagnostic
            record["phase"] = (
                "syntax_error" if parse_diagnostic_location(diagnostic) is not None
                else "infrastructure_error")
    record["duration_ms"] = round((time.monotonic() - started) * 1000)
    return record


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, required=True,
                        help="directory containing the pinned SCP .bb source files")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    parser.add_argument("--cli", type=Path, required=True,
                        help="BlitzCS.CLI.dll to probe")
    parser.add_argument("--externals", type=Path,
                        help="optional CLI external catalog JSON passed through to each build")
    parser.add_argument("--integrated-entry", default="Main.bb",
                        help="entry file to compile with its Include tree (default: Main.bb)")
    parser.add_argument("--native-catalog", type=Path,
                        default=Path(__file__).with_name("scp_native_catalog.json"),
                        help="JSON projection of original Blitz3D rtSym names")
    parser.add_argument("--report", type=Path, default=Path("artifacts/scp-compilation.json"))
    parser.add_argument("--timeout", type=float, default=30,
                        help="per-file timeout in seconds; timeouts are infrastructure results")
    parser.add_argument("--check-syntax", action="store_true",
                        help="also run syntax-only validation for each file and the integrated entry")
    args = parser.parse_args()

    source_dir = args.source_dir.resolve()
    cli = args.cli.resolve()
    if not source_dir.is_dir():
        parser.error(f"source directory does not exist: {source_dir}")
    if not cli.is_file():
        parser.error(f"CLI assembly does not exist: {cli}")
    if args.externals is not None and not args.externals.is_file():
        parser.error(f"external catalog does not exist: {args.externals}")
    if not args.native_catalog.is_file():
        parser.error(f"native catalog does not exist: {args.native_catalog}")
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error("timeout must be a finite positive number of seconds")
    sources = sorted(source_dir.glob("*.bb"), key=lambda path: path.name.casefold())
    if not sources:
        parser.error(f"no .bb files found in {source_dir}")

    declared = external_symbols(source_dir)
    natives = load_native_symbols(args.native_catalog)
    results = [probe(source, args.dotnet, cli, args.timeout, declared, natives, args.externals)
               for source in sources]
    if args.check_syntax:
        for source, result in zip(sources, results):
            result["syntax_check"] = syntax_probe(source, args.dotnet, cli, args.timeout)
    integrated_entry = source_dir / args.integrated_entry
    if not integrated_entry.is_file():
        parser.error(f"integrated entry source does not exist: {integrated_entry}")
    integrated_probe = probe(integrated_entry, args.dotnet, cli, args.timeout,
                             declared, natives, args.externals)
    if args.check_syntax:
        integrated_probe["syntax_check"] = syntax_probe(
            integrated_entry, args.dotnet, cli, args.timeout)
    static_closure = include_closure(integrated_entry)
    diagnostic_depth, diagnostic_paths = diagnostic_include_location(
        static_closure, integrated_probe["first_diagnostic"])
    integrated = summarize_integrated(integrated_probe, diagnostic_depth, diagnostic_paths)
    if args.check_syntax:
        integrated["syntax_check"] = integrated_probe["syntax_check"]
    integrated["entry"] = integrated_entry.name
    integrated["static_include_closure"] = {
        key: static_closure[key] for key in ("files", "file_count", "missing", "max_depth")
    }
    report = {
        "declared_repository": "Regalis11/scpcb",
        "declared_commit": DECLARED_COMMIT,
        "provenance_verified_by_probe": False,
        "source_dir": str(source_dir),
        "cli": str(cli),
        "timeout_seconds": args.timeout,
        "external_catalog": str(args.externals.resolve()) if args.externals else None,
        "native_catalog": str(args.native_catalog.resolve()),
        "files_checked": len(results),
        "compiled": sum(item["phase"] == "compiled" for item in results),
        "compiler_errors": sum(item["phase"] == "compiler_error" for item in results),
        "infrastructure_results": sum(str(item["phase"]).startswith("infrastructure_") for item in results),
        "categories": {category: sum(item["category"] == category for item in results)
                       for category in ("compiled", "lexical", "syntactic", "semantic_type",
                                        "native_unregistered", "external_missing",
                                        "infrastructure", "unclassified_compiler_error")},
        "integrated": integrated,
        "results": results,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{report['compiled']}/{report['files_checked']} compiled; report: {args.report.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
