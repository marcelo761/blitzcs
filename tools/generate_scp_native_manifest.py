#!/usr/bin/env python3
"""Generate the metadata-only SCP/native identifier inventory."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path

RTSYM = re.compile(r'rtSym\s*\(\s*"((?:\\.|[^"\\])*)"\s*,')
IDENTIFIER = re.compile(r"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_])")


def erase_comments_and_strings(text: str) -> str:
    """Blank Blitz string literals and semicolon comments while preserving lines."""
    output: list[str] = []
    index = 0
    in_string = False
    in_comment = False
    while index < len(text):
        char = text[index]
        following = text[index + 1] if index + 1 < len(text) else ""
        if in_comment:
            if char == "\n":
                in_comment = False
                output.append("\n")
            else:
                output.append(" ")
            index += 1
        elif in_string:
            if char == '"' and following == '"':
                output.extend((" ", " "))
                index += 2
            else:
                if char == '"':
                    in_string = False
                output.append("\n" if char == "\n" else " ")
                index += 1
        elif char == '"':
            in_string = True
            output.append(" ")
            index += 1
        elif char == ";":
            in_comment = True
            output.append(" ")
            index += 1
        else:
            output.append(char)
            index += 1
    return "".join(output)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build_manifest(catalog_path: Path, source_dir: Path) -> dict[str, object]:
    repo_root = Path(__file__).resolve().parents[1]
    catalog_location = catalog_path.resolve()
    try:
        catalog_label = catalog_location.relative_to(repo_root).as_posix()
    except ValueError:
        catalog_label = str(catalog_location)
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    original_root = Path(catalog["source_directory"])
    source_files: list[dict[str, str]] = []
    evidence: dict[str, Counter[str]] = {}

    for path in sorted(source_dir.glob("*.bb"), key=lambda item: item.name.casefold()):
        raw = path.read_bytes()
        text = raw.decode("cp1252")
        clean = erase_comments_and_strings(text)
        counts = Counter(name.casefold() for name in IDENTIFIER.findall(clean))
        evidence[path.name] = counts
        source_files.append({"file": path.name, "sha256": hashlib.sha256(raw).hexdigest()})
    if not source_files:
        raise ValueError(f"no .bb files found in source directory: {source_dir}")

    groups: list[dict[str, object]] = []
    for metadata in catalog["source_files"]:
        source_file = str(metadata["file"])
        path = original_root / source_file
        if sha256(path) != metadata["sha256"]:
            raise ValueError(f"source hash differs from catalog metadata: {path}")
        content = path.read_text(encoding="cp1252", errors="replace")
        registrations: list[list[object]] = []
        for match in RTSYM.finditer(content):
            descriptor = match.group(1)
            parts = re.match(r"^[%#$]?([A-Za-z_][A-Za-z0-9_]*)(.*)$", descriptor)
            if parts is None:
                continue
            name = parts.group(1)
            folded = name.casefold()
            observed_in = [file_name for file_name, counts in evidence.items() if folded in counts]
            registration: list[object] = [
                name,
                descriptor,
                content.count("\n", 0, match.start()) + 1,
            ]
            if observed_in:
                registration.append(observed_in)
            registrations.append(registration)
        groups.append({
            "file": source_file,
            "count": len(registrations),
            "registrations": registrations,
        })

    return {
        "schema_version": 1,
        "source_catalog": catalog_label,
        "source": catalog["source"],
        "source_directory": str(original_root),
        "scope": "Static original rtSym registration metadata and lexical SCP identifier observations.",
        "registration_row_schema": [
            "name", "original rtSym descriptor", "original source line",
            "optional list of SCP files with a lexical identifier occurrence",
        ],
        "identifier_evidence_rule": "Case-insensitive token occurrence after BASIC comments and strings are blanked; not call resolution.",
        "original_source_files": catalog["source_files"],
        "limitations": {
            "call_resolution_performed": False,
            "argument_binding_performed": False,
            "engine_implementation_support_inferred": False,
            "lexical_identifier_evidence_is_not_a_callsite": True,
        },
        "scp_source_set": {
            "directory": str(source_dir),
            "files_checked": len(source_files),
            "files": source_files,
        },
        "subsystems": groups,
        "totals": {
            "source_files": len(groups),
            "registrations": sum(int(group["count"]) for group in groups),
            "scp_files": len(source_files),
            "registrations_with_lexical_identifier_occurrence": sum(
                len(registration) > 3
                for group in groups for registration in group["registrations"]
            ),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=Path(__file__).with_name("scp_native_catalog.json"))
    parser.add_argument("--source-dir", type=Path, required=True,
                        help="directory containing the pinned SCP .bb source files")
    parser.add_argument("--output", type=Path, default=Path(__file__).with_name("scp_native_manifest.json"))
    args = parser.parse_args()
    if not args.catalog.is_file():
        parser.error(f"native catalog does not exist: {args.catalog}")
    if not args.source_dir.is_dir():
        parser.error(f"source directory does not exist: {args.source_dir}")
    try:
        manifest = build_manifest(args.catalog, args.source_dir)
    except (OSError, UnicodeError, KeyError, ValueError, json.JSONDecodeError) as error:
        parser.error(str(error))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"{manifest['totals']['registrations']} registrations; manifest: {args.output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
