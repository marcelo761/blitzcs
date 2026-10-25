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
DESCRIPTOR = re.compile(r"^(?P<result>[%#$]?)(?P<name>[A-Za-z_][A-Za-z0-9_]*)(?P<args>.*)$")
ARGUMENT = re.compile(r"(?P<marker>[%#$])(?P<name>[A-Za-z_][A-Za-z0-9_]*)(?P<default>=[^%#$]*)?")
FUNCTION = re.compile(
    r"^\s*Function\s+([A-Za-z_][A-Za-z0-9_]*)([%#$]?)\s*\(([^)]*)\)", re.IGNORECASE
)
DECLARATION = re.compile(
    r'^\s*([A-Za-z_][A-Za-z0-9_]*)([%#$]?)\s*\(([^)]*)\)\s*(?::\s*"([^"]+)")?',
    re.IGNORECASE,
)
MARKER_TYPES = {"%": "integer", "#": "float", "$": "string"}
CALL_PREFIX = re.compile(r"(?:^|\b(?:Then|Else|Call|While|Until|If)\b|:)\s*", re.IGNORECASE)
DECLARATION_PREFIX = re.compile(r"^\s*(?:Field|Global|Local|Const|Dim)\b", re.IGNORECASE)
IMPLEMENTED_SYMBOLS = {
    "filesize": ("src/Blitz3DCS.Runtime/LegacyFiles.cs", "Core behavior, NUL truncation, missing directory, and unique wildcard cases verified by native-first tests; ordering for multiple wildcard matches and host Unicode differences remain unverified."),
    "filetype": ("src/Blitz3DCS.Runtime/LegacyFiles.cs", "Core behavior, NUL truncation, missing directory, and unique wildcard cases verified by native-first tests; ordering for multiple wildcard matches and host Unicode differences remain unverified."),
    "currentdir": ("src/Blitz3DCS.Runtime/LegacyFiles.cs", "Core behavior verified by native-first tests; wildcard ordering and host Unicode differences remain unverified."),
    "getenv": ("src/Blitz3DCS.Runtime/LegacySystem.cs", "Core behavior and NUL truncation verified by native-first tests."),
    "chr": ("src/Blitz3DCS.Runtime/LegacyStrings.cs", "Native capture verified ASCII and CP1252 low-byte behavior; unrepresentable Unicode input raises an explicit encoding error."),
    "len": ("src/Blitz3DCS.Runtime/LegacyStrings.cs", "Native capture verified byte count for ASCII and CP1252; unrepresentable Unicode input raises an explicit encoding error."),
    "runtimeerror": ("src/BlitzCS.LegacyRuntime/LegacyErrors.cs", "Existing implementation reported by parent agent."),
}
PENDING_SYMBOLS = {"changedir", "setenv"}
COMPILER_INTRINSICS = [
    {
        "name": "Abs", "status": "implemented", "kind": "language_intrinsic",
        "external_dependency": False, "native_registration": False,
        "source_files": ["src/BlitzCS.Compiler/Syntax.cs", "src/BlitzCS.Compiler/CSharpEmitter.cs", "src/BlitzCS.Compiler/CompatibilityRuntime.cs"],
        "evidence": "Parsed as a unary language token and emitted through __BlitzAbs; this is not an external native call.",
    },
    {
        "name": "Sgn", "status": "implemented", "kind": "language_intrinsic",
        "external_dependency": False, "native_registration": False,
        "source_files": ["src/BlitzCS.Compiler/Syntax.cs", "src/BlitzCS.Compiler/CSharpEmitter.cs", "src/BlitzCS.Compiler/CompatibilityRuntime.cs"],
        "evidence": "Parsed as a unary language token and emitted through __BlitzSgn; this is not an external native call.",
    },
]
FILE_GROUPS = {
    "bbaudio.cpp": "audio", "bbfilesystem.cpp": "files", "bbstream.cpp": "files", "bbgraphics.cpp": "graphics",
    "bbinput.cpp": "input", "bbmath.cpp": "math", "bbstring.cpp": "strings",
    "bbruntime.cpp": "system", "bbsys.cpp": "system", "runtime.cpp": "system",
    "std.cpp": "system", "bbblitz3d.cpp": "entities",
}


def functional_group(source_file: str, name: str) -> str:
    if source_file == "bbblitz3d.cpp" and name.casefold().startswith("collision"):
        return "physics"
    return FILE_GROUPS.get(source_file, "others")


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


def decode_descriptor(descriptor: str) -> dict[str, object] | None:
    """Decode Blitz's rtSym syntax without guessing types omitted from it."""
    match = DESCRIPTOR.fullmatch(descriptor)
    if match is None:
        return None
    if not match.group("result") and match.group("name").startswith("_") and not match.group("args"):
        return {
            "name": match.group("name"), "return_type": None, "arguments": None,
            "kind": "internal_runtime_symbol",
        }
    arguments = match.group("args")
    parsed: list[dict[str, object]] = []
    offset = 0
    while offset < len(arguments):
        argument = ARGUMENT.match(arguments, offset)
        if argument is None:
            return None
        marker = argument.group("marker")
        parsed.append({
            "name": argument.group("name"),
            "type": MARKER_TYPES[marker],
            "default": argument.group("default")[1:] if argument.group("default") else None,
        })
        offset = argument.end()
    result = match.group("result")
    return {
        "name": match.group("name"),
        "return_type": MARKER_TYPES.get(result, "void"),
        "arguments": parsed,
        "kind": "typed_runtime_callable",
    }


def suffix_type(suffix: str) -> str:
    return MARKER_TYPES.get(suffix, "integer")


def parse_application_declarations(source_dir: Path) -> tuple[list[dict[str, object]], dict[str, list[dict[str, object]]], list[dict[str, object]]]:
    functions: list[dict[str, object]] = []
    by_name: dict[str, list[dict[str, object]]] = {}
    external: list[dict[str, object]] = []
    for path in sorted(source_dir.rglob("*.bb"), key=lambda item: item.as_posix().casefold()):
        text = path.read_bytes().decode("cp1252")
        clean = erase_comments_and_strings(text)
        for line_number, line in enumerate(clean.splitlines(), 1):
            match = FUNCTION.match(line)
            if match is None:
                continue
            name, result_suffix, raw_args = match.groups()
            args = []
            for raw_arg in raw_args.split(","):
                arg = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)([%#$]?)", raw_arg)
                if arg:
                    args.append({"name": arg.group(1), "type": suffix_type(arg.group(2))})
            declaration = {
                "name": name, "return_type": suffix_type(result_suffix), "arguments": args,
                "file": path.relative_to(source_dir).as_posix(), "line": line_number,
            }
            functions.append(declaration)
            by_name.setdefault(name.casefold(), []).append(declaration)
    for path in sorted(source_dir.rglob("*.decls"), key=lambda item: item.as_posix().casefold()):
        text = path.read_bytes().decode("cp1252")
        for line_number, line in enumerate(text.splitlines(), 1):
            match = DECLARATION.match(line)
            if match is None:
                continue
            name, result_suffix, raw_args, target = match.groups()
            args = []
            for raw_arg in raw_args.split(","):
                arg = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)([%#$]?)(\*)?", raw_arg)
                if arg:
                    args.append({"name": arg.group(1), "type": "pointer" if arg.group(3) else suffix_type(arg.group(2))})
            external.append({
                "name": name, "return_type": MARKER_TYPES.get(result_suffix, "void"), "arguments": args,
                "target": target, "file": path.relative_to(source_dir).as_posix(), "line": line_number,
                "raw_signature": line.strip(),
            })
    return functions, by_name, external


def find_call_candidates(clean: str, names: set[str] | None = None) -> list[dict[str, object]]:
    """Find call-shaped uses; this is syntax evidence, not parser/binder resolution."""
    candidates: dict[tuple[int, str], str] = {}
    lines = clean.splitlines()
    for line_number, line in enumerate(lines, 1):
        if FUNCTION.match(line):
            continue
        declaration = DECLARATION_PREFIX.match(line)
        declared_name = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)", line[declaration.end():]) if declaration else None
        declared_name_start = declaration.end() + declared_name.start(1) if declared_name else None
        for match in re.finditer(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", line):
            name = match.group(1)
            if match.start(1) == declared_name_start:
                continue
            if names is None or name.casefold() in names:
                candidates[(line_number, name.casefold())] = "parenthesized"
        for prefix in CALL_PREFIX.finditer(line):
            match = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)(?=\s|$)(.*)$", line[prefix.end():])
            if match is None:
                continue
            name, tail = match.groups()
            remainder = tail.strip()
            is_block_terminator = name.casefold() == "end" and bool(re.match(r"(?:If|Function|Select|Type|While)\b", remainder, re.IGNORECASE))
            if ((names is None or name.casefold() in names)
                    and not is_block_terminator
                    and (not remainder or not remainder.startswith(("=", "(", ".")))):
                candidates.setdefault((line_number, name.casefold()), "statement")
    return [{"line": line, "name": name, "form": form}
            for (line, name), form in sorted(candidates.items())]


def summarize_call_sites(sites: list[dict[str, object]]) -> dict[str, object]:
    by_file: Counter[str] = Counter(str(site["file"]) for site in sites)
    return {
        "frequency": len(sites),
        "call_counts_by_file": [
            {"file": file, "frequency": count} for file, count in sorted(by_file.items(), key=lambda item: item[0].casefold())
        ],
        "sample_call_sites": sites[:3],
    }


def summarize_native_calls(sites: list[dict[str, object]]) -> dict[str, object]:
    summary = summarize_call_sites(sites)
    return {
        "call_frequency": summary["frequency"],
        "call_counts_by_file": summary["call_counts_by_file"],
        "sample_call_sites": summary["sample_call_sites"],
    }


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

    source_paths = sorted(source_dir.rglob("*.bb"), key=lambda item: item.as_posix().casefold())
    functions, app_functions, external_declarations = parse_application_declarations(source_dir)
    for path in source_paths:
        raw = path.read_bytes()
        text = raw.decode("cp1252")
        clean = erase_comments_and_strings(text)
        counts = Counter(name.casefold() for name in IDENTIFIER.findall(clean))
        evidence[path.name] = counts
        source_files.append({"file": path.relative_to(source_dir).as_posix(), "sha256": hashlib.sha256(raw).hexdigest()})
    if not source_files:
        raise ValueError(f"no .bb files found in source directory: {source_dir}")

    groups: list[dict[str, object]] = []
    registrations_by_name: dict[str, dict[str, object]] = {}
    native_names: set[str] = set()
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
            decoded = decode_descriptor(descriptor)
            if decoded is None:
                continue
            name = str(decoded["name"])
            folded = name.casefold()
            native_names.add(folded)
            observed_in = [file_name for file_name, counts in evidence.items() if folded in counts]
            registration: list[object] = [
                name,
                descriptor,
                content.count("\n", 0, match.start()) + 1,
            ]
            if observed_in:
                registration.append(observed_in)
            registrations.append(registration)
            registrations_by_name[folded] = {
                "name": name, "descriptor": descriptor, "signature": decoded,
                "registration_file": source_file,
                "functional_group": functional_group(source_file, name),
                "registration_line": content.count("\n", 0, match.start()) + 1,
                "lexical_files": observed_in,
            }
        groups.append({
            "file": source_file,
            "functional_group": FILE_GROUPS.get(source_file, "others"),
            "count": len(registrations),
        })

    candidate_names = native_names | set(app_functions) | {str(item["name"]).casefold() for item in external_declarations}
    call_sites: dict[str, list[dict[str, object]]] = {}
    for path in source_paths:
        clean = erase_comments_and_strings(path.read_bytes().decode("cp1252"))
        relative = path.relative_to(source_dir).as_posix()
        for candidate in find_call_candidates(clean, candidate_names):
            call_sites.setdefault(str(candidate["name"]), []).append({
                "file": relative, "line": candidate["line"], "form": candidate["form"],
            })
    external_by_name: dict[str, list[dict[str, object]]] = {}
    for declaration in external_declarations:
        external_by_name.setdefault(str(declaration["name"]).casefold(), []).append(declaration)
    resolved_candidates: list[dict[str, object]] = []
    for folded in sorted(call_sites):
        native = registrations_by_name.get(folded)
        if folded in app_functions:
            binding = "application_function"
            status = "unverified"
        elif folded in external_by_name:
            binding = "external_declaration"
            status = "externaldependency"
        elif native:
            binding = "native_registration"
            status = "implemented" if folded in IMPLEMENTED_SYMBOLS else ("pending" if folded in PENDING_SYMBOLS else "unverified")
        else:
            continue
        if binding == "native_registration":
            continue
        summary = summarize_call_sites(call_sites[folded])
        resolved_candidates.append({
            "name": native["name"] if native else folded,
            "binding_candidate": binding,
            "status": status,
            "shadowed_native": folded in app_functions and folded in registrations_by_name,
            "external_declaration_count": len(external_by_name.get(folded, [])),
            "application_function_count": len(app_functions.get(folded, [])),
            **summary,
        })

    native_candidate_names = {
        name for name in call_sites
        if name in registrations_by_name and name not in app_functions
    }
    mapped_candidate_occurrences = sum(
        len(call_sites[name]) for name in native_candidate_names
    ) + sum(int(candidate["frequency"]) for candidate in resolved_candidates)

    packed_functions = [
        [
            item["name"], item["return_type"],
            [[argument["name"], argument["type"]] for argument in item["arguments"]],
            item["file"], item["line"],
        ]
        for item in functions
    ]
    packed_external_declarations = [
        [
            item["name"], item["return_type"],
            [[argument["name"], argument["type"]] for argument in item["arguments"]],
            item["target"], item["file"], item["line"],
        ]
        for item in external_declarations
    ]
    packed_call_candidates = [
        [
            item["name"], item["binding_candidate"], item["status"], item["shadowed_native"],
            item["external_declaration_count"], item["application_function_count"], item["frequency"],
            [[row["file"], row["frequency"]] for row in item["call_counts_by_file"]],
            [[row["file"], row["line"], row["form"]] for row in item["sample_call_sites"]],
        ]
        for item in resolved_candidates
    ]
    packed_native_inventory = []
    for name, registration in sorted(registrations_by_name.items()):
        app_shadowed = name in app_functions
        native_summary = summarize_native_calls([] if app_shadowed else call_sites.get(name, []))
        shadow_summary = summarize_native_calls(call_sites.get(name, []) if app_shadowed else [])
        signature = registration["signature"]
        packed_native_inventory.append([
            registration["name"], registration["descriptor"],
            [
                signature["return_type"],
                [[arg["name"], arg["type"], arg["default"]] for arg in signature["arguments"]]
                if signature["arguments"] is not None else None,
                signature["kind"],
            ],
            registration["registration_file"], registration["registration_line"],
            registration["functional_group"], registration["lexical_files"],
            "unverified" if app_shadowed else (
                "implemented" if name in IMPLEMENTED_SYMBOLS else ("pending" if name in PENDING_SYMBOLS else "unverified")
            ),
            native_summary["call_frequency"],
            [[row["file"], row["frequency"]] for row in native_summary["call_counts_by_file"]],
            [[row["file"], row["line"], row["form"]] for row in native_summary["sample_call_sites"]],
            app_shadowed, shadow_summary["call_frequency"],
            [[row["file"], row["frequency"]] for row in shadow_summary["call_counts_by_file"]],
            [[row["file"], row["line"], row["form"]] for row in shadow_summary["sample_call_sites"]],
            len(app_functions.get(name, [])),
        ])

    return {
        "schema_version": 3,
        "source_catalog": catalog_label,
        "source": catalog["source"],
        "source_directory": str(original_root),
        "scope": "Static original rtSym metadata, SCP call-shaped candidates, application functions, and external declarations.",
        "descriptor_note": "Descriptors beginning with an untyped underscore symbol, such as _bbAbs, are retained as internal_runtime_symbol with unknown callable signature; they are not exposed Blitz command signatures.",
        "subsystem_source_row_schema": ["original runtime source file", "functional group", "registration count"],
        "application_function_row_schema": ["name", "return type", "[[argument name, type], ...]", "file", "line"],
        "external_declaration_row_schema": ["name", "return type", "[[argument name, type], ...]", "target", "file", "line"],
        "native_inventory_row_schema": [
            "name", "rtSym descriptor", "[return type, [[argument, type, default], ...], descriptor kind]",
            "registration file", "registration line", "functional group", "lexical files", "status",
            "call frequency", "[[file, frequency], ...]", "up to 3 [file, line, form] samples",
            "shadowed by app Function", "shadowed candidate frequency", "shadow candidate file counts",
            "up to 3 shadow samples", "same-name app Function count",
        ],
        "call_candidate_row_schema": [
            "name", "binding candidate", "status", "shadowed native", "external declaration count",
            "app Function count", "frequency", "[[file, frequency], ...]", "up to 3 [file, line, form] samples",
        ],
        "identifier_evidence_rule": "Case-insensitive token occurrence after BASIC comments and strings are blanked; not call resolution.",
        "call_candidate_rule": "Call-shaped identifiers followed by '(' or a statement-shaped identifier after a small set of BASIC statement prefixes. These candidates are not resolved parser/binder callsites; arrays and ambiguous statements may be included.",
        "status_rule": "A syntactic candidate mapped to a parsed .decls row is externaldependency unless shadowed by an app Function. Native statuses come from the explicit support map below; language intrinsics are listed separately. No status is inferred from original registration or lexical mention alone.",
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
        "application_functions": packed_functions,
        "external_declarations": packed_external_declarations,
        "compiler_intrinsics": COMPILER_INTRINSICS,
        "implementation_status_evidence": {
            name: {"source": source, "scope": scope}
            for name, (source, scope) in IMPLEMENTED_SYMBOLS.items()
        },
        "pending_symbols": sorted(PENDING_SYMBOLS),
        "native_inventory": packed_native_inventory,
        "call_candidates": packed_call_candidates,
        "dependency_evidence": [
            {
                "subsystem": "filesystem",
                "depends_on": ["gx_filesys", "gx_runtime.openFileSystem"],
                "commands": ["FileSize", "FileType", "CurrentDir", "ChangeDir"],
                "evidence": "Source-backed: original bbruntime/bbfilesystem.cpp uses gx_filesys for these operations; filesystem_create obtains it from gx_runtime->openFileSystem(0); bbruntime_link calls filesystem_link.",
            },
            {
                "subsystem": "process_environment",
                "depends_on": ["C runtime getenv", "C runtime putenv"],
                "commands": ["GetEnv", "SetEnv"],
                "evidence": "Source-backed: original bbruntime/bbruntime.cpp implements GetEnv using getenv and SetEnv using putenv.",
            },
            {
                "subsystem": "legacy_strings",
                "depends_on": ["CodePagesEncodingProvider.GetEncoding(1252)"],
                "commands": ["Chr", "Len"],
                "evidence": "Source-backed current implementation: LegacyStrings uses the .NET code pages provider to encode and decode CP1252 bytes; these commands have no app .decls dependency.",
            },
        ],
        "subsystem_sources": groups,
        "totals": {
            "source_files": len(groups),
            "registrations": sum(int(group["count"]) for group in groups),
            "scp_files": len(source_files),
            "registrations_with_lexical_identifier_occurrence": sum(
                bool(registration["lexical_files"])
                for registration in registrations_by_name.values()
            ),
            "call_candidate_names": len(native_candidate_names) + len(resolved_candidates),
            "call_candidate_occurrences": mapped_candidate_occurrences,
            "application_functions": len(functions),
            "external_declarations": len(external_declarations),
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
