#!/usr/bin/env python3
"""Generate an advisory runtime-registration profile from the historical API inventory."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INVENTORY = ROOT / "docs/blitz3d-api-inventory.json"
DEFAULT_OUTPUT = ROOT / "profiles/blitz3d.json"
RESERVED = {
    "dim", "goto", "gosub", "return", "exit", "if", "then", "else", "endif", "elseif",
    "while", "wend", "for", "to", "step", "next", "function", "endfunction", "type", "endtype",
    "each", "local", "global", "field", "const", "select", "case", "default", "endselect", "repeat",
    "until", "forever", "data", "read", "restore", "abs", "sgn", "mod", "pi", "true", "false",
    "int", "float", "str", "include", "new", "delete", "first", "last", "insert", "before", "after",
    "null", "object", "handle", "and", "or", "xor", "not", "shl", "shr", "sar", "print", "end", "cs",
}
TYPE_NAMES = {"integer": "Integer", "float": "Float", "string": "String", "void": "Void"}


def decode_cpp_string_fragment(value: str) -> str:
    """Decode the C++ escapes retained by the source inventory extractor.

    The inventory stores the characters inside the source string literal, not
    the runtime descriptor bytes. Only escapes found/needed in these
    descriptors are accepted, so unsupported or malformed input cannot leak
    into a generated declaration as a literal source escape.
    """
    decoded = []
    index = 0
    while index < len(value):
        char = value[index]
        if char != "\\":
            decoded.append(char)
            index += 1
            continue
        index += 1
        if index == len(value) or value[index] not in {'\\', '"'}:
            raise ValueError(f"unsupported or malformed C++ escape in descriptor fragment: {value!r}")
        decoded.append(value[index])
        index += 1
    return "".join(decoded)


def decode_string_default(value: str, name: str) -> str:
    runtime_literal = decode_cpp_string_fragment(value)
    if len(runtime_literal) < 2 or runtime_literal[0] != '"' or runtime_literal[-1] != '"':
        raise ValueError(f"malformed quoted string default for {name}: {value!r}")
    contents = runtime_literal[1:-1]
    if '"' in contents:
        raise ValueError(f"unescaped quote in string default for {name}: {value!r}")
    return contents


def build_profile(inventory: dict) -> dict:
    rows = inventory["registrations"]
    functions = []
    excluded_internal = []
    excluded_reserved = []
    for row in rows:
        signature = row.get("signature")
        if not signature:
            raise ValueError(f"registration has no decoded signature: {row.get('descriptor')}")
        kind = signature.get("kind")
        if kind == "internal_runtime_symbol":
            excluded_internal.append({"name": row["name"], "symbol": row["descriptor"], "source": f"{row['source']}:{row['line']}"})
            continue
        if kind != "typed_runtime_callable":
            raise ValueError(f"unsupported signature kind {kind!r} for {row['name']}")
        name = signature["name"]
        if name.casefold() in RESERVED:
            excluded_reserved.append({"name": name, "reason": "parser-reserved language statement/control token; cannot be registered as external callable", "source": f"{row['source']}:{row['line']}"})
            continue
        params = signature.get("arguments")
        if not isinstance(params, list):
            raise ValueError(f"typed signature has no arguments: {name}")
        fn = {
            "name": name,
            "returnType": TYPE_NAMES[signature["return_type"]],
            "parameters": [TYPE_NAMES[param["type"]] for param in params],
            "defaults": [],
            "contract": {
                "profile": "blitz3d-rtSym",
                "provider": "blitz3d.runtime",
                "resolution": "runtime-registration",
                "symbol": row["descriptor"],
                "callingConvention": None,
                "source": f"{row['source']}:{row['line']}",
            },
        }
        defaults = [param["default"] for param in params if param.get("default") is not None]
        if defaults:
            # The inventory encodes defaults as source literals. Parse them as typed constants.
            start = len(params) - len(defaults)
            converted = []
            for param, literal in zip(params[start:], defaults):
                if param["type"] == "integer":
                    converted.append(int(literal, 0) if literal.lower().startswith("0x") else int(literal, 10))
                elif param["type"] == "float":
                    converted.append(float(literal))
                elif param["type"] == "string":
                    converted.append(decode_string_default(literal, name))
                else:
                    raise ValueError(f"invalid default type for {name}")
            fn["defaults"] = converted
        functions.append(fn)

    source = inventory["source"]
    return {
        "metadata": {
            "id": "blitz3d",
            "sourceRepository": source["repository"],
            "sourceCommit": source["commit"],
            "source": "docs/blitz3d-api-inventory.json",
            "descriptorEncoding": "contract.symbol preserves the inventory's C++ source-string contents, including C++ escapes; it is provenance text, not a runtime registration lookup key.",
            "description": "Advisory historical declarations for Blitz3D runtime registrations. Runtime registration metadata does not establish binary ABI or string marshaling.",
            "exclusions": {
                "internalRuntimeSymbols": {
                    "count": len(excluded_internal),
                    "reason": "rtSym descriptors are internal symbols with unknown public callable signature/ABI; excluded from external declarations.",
                    "entries": excluded_internal,
                },
                "parserReservedNames": {
                    "count": len(excluded_reserved),
                    "reason": "Parser.IsReservedName prevents these names from being registered as external callables.",
                    "source": "src/BlitzCS.Compiler/Syntax.cs:199-208",
                    "entries": excluded_reserved,
                },
                "compilerLanguageIntrinsics": {
                    "names": ["Abs", "Sgn", "Mod", "Pi", "Int", "Float", "Str", "Object", "Handle"],
                    "reason": "Core syntax/compiler binding takes precedence; these language builtins are not external runtime registrations.",
                    "source": "src/BlitzCS.Compiler/Syntax.cs:145,199-208,664-679; src/BlitzCS.Compiler/Semantics.cs:501-504,574-600",
                },
            },
        },
        "functions": functions,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, default=DEFAULT_INVENTORY)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    profile = build_profile(json.loads(args.inventory.read_text(encoding="utf-8")))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(profile, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
