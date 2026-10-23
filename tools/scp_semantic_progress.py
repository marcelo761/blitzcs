#!/usr/bin/env python3
"""Advance semantic analysis using verified original declarations, never fake implementations."""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import subprocess
import tempfile

try:
    from tools.generate_scp_native_manifest import RTSYM, decode_descriptor, parse_application_declarations
except ModuleNotFoundError:
    from generate_scp_native_manifest import RTSYM, decode_descriptor, parse_application_declarations

TYPES = {'integer': 'Integer', 'float': 'Float', 'string': 'String', 'void': 'Void'}
UNKNOWN = re.compile(r"Unknown BASIC function or external symbol '([^']+)'")
CPP_TOKEN = re.compile(r'''"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|//[^\n]*|/\*[\s\S]*?\*/''')


def default_value(text: str, kind: str):
    if kind == 'integer':
        value = int(text, 10)
        if not -(2**31) <= value < 2**31:
            raise ValueError('Integer default out of range')
        return value
    if kind == 'float':
        value = struct.unpack('f', struct.pack('f', float(text)))[0]
        if not math.isfinite(value):
            raise ValueError('Float default must be finite')
        return value
    if len(text) < 2 or text[0] != '"' or text[-1] != '"' or '"' in text[1:-1]:
        raise ValueError('String default must be quoted')
    return text[1:-1]  # BASIC backslashes are literal characters, not Python escapes.


def extract_signatures(native_catalog: Path, runtime_source: Path):
    """Require the inventoried source hashes; preserve void and optional arguments."""
    result, rejected, blocked = {}, [], set()
    for item in json.loads(native_catalog.read_text())['source_files']:
        relative = Path(item['file'])
        file = (runtime_source / relative).resolve()
        if not file.is_relative_to(runtime_source.resolve()):
            raise ValueError('runtime source path escapes its root')
        raw = file.read_bytes()
        if hashlib.sha256(raw).hexdigest() != item['sha256']:
            raise ValueError('original source hash differs: ' + item['file'])
        content = CPP_TOKEN.sub(lambda match: re.sub(r'[^\n]', ' ', match[0]) if match[0].startswith(('//', '/*')) else match[0], raw.decode('cp1252'))
        for match in RTSYM.finditer(content):
            descriptor = ast.literal_eval('"' + match.group(1) + '"')
            decoded = decode_descriptor(descriptor)
            if not decoded:
                identifiable = re.match(r'^[%#$]?([A-Za-z_][A-Za-z0-9_]*)', descriptor)
                if identifiable:
                    name = identifiable[1]
                    result.pop(name.casefold(), None)
                    blocked.add(name.casefold())
                    rejected.append({'name': name, 'source': item['file'], 'reason': 'unsupported original descriptor'})
                continue
            if decoded['kind'] != 'typed_runtime_callable':
                continue
            name = decoded['name']
            key = name.casefold()
            if key in blocked:
                continue
            try:
                arguments = decoded['arguments']
                parameters = [TYPES[x['type']] for x in arguments]
                first = next((i for i, arg in enumerate(arguments) if arg['default'] is not None), len(arguments))
                if any(arg['default'] is None for arg in arguments[first:]):
                    raise ValueError('non-trailing optional parameters')
                defaults = [default_value(arg['default'], arg['type']) for arg in arguments[first:]]
                signature = {'name': name, 'returnType': TYPES[decoded['return_type']],
                             'parameters': parameters, 'defaults': defaults}
                if key in result and result[key] != signature:
                    raise ValueError('conflicting original declarations')
                result[key] = signature
            except (KeyError, ValueError, SyntaxError, OverflowError, struct.error) as error:
                result.pop(key, None)
                blocked.add(key)
                rejected.append({'name': name, 'source': item['file'], 'reason': str(error)})
    return result, rejected



def external_signatures(source_dir: Path):
    """Read only fully typed scalar .decls contracts; pointer ABI work stays pending."""
    result, rejected, blocked = {}, [], set()
    _, _, declarations = parse_application_declarations(source_dir)
    for declaration in declarations:
        name = declaration['name']
        key = name.casefold()
        if key in blocked:
            continue
        arguments = declaration['arguments']
        # Inventory parsing is intentionally permissive; require the whole argument
        # list here before treating it as a compiler contract.
        raw_args = declaration['raw_signature'].split('(', 1)[1].split(')', 1)[0]
        parts = [] if not raw_args.strip() else raw_args.split(',')
        if len(parts) != len(arguments) or any(not re.fullmatch(r'\s*[A-Za-z_][A-Za-z0-9_]*[%#$]\s*', p) for p in parts):
            rejected.append({'name': name, 'source': declaration['file'],
                             'reason': 'pointer or unsupported .decls parameter requires an explicit ABI bridge'})
            blocked.add(key)
            result.pop(key, None)
            continue
        signature = {'name': name, 'returnType': TYPES[declaration['return_type']],
                     'parameters': [TYPES[a['type']] for a in arguments], 'defaults': []}
        if key in result and result[key] != signature:
            rejected.append({'name': name, 'source': declaration['file'], 'reason': 'conflicting .decls contracts'})
            blocked.add(key)
            result.pop(key, None)
        else:
            result[key] = signature
    return result, rejected


def validate_outputs(entry: Path, cli: Path, bindings: Path, native_catalog: Path,
                     runtime_source: Path, outputs: list[Path]):
    protected = {p.resolve() for p in [entry, cli, bindings, native_catalog]}
    # Directory exclusions also cover aliases to secondary BASIC/.decls inputs and original runtime files.
    for source_root in [entry.parent.resolve(), runtime_source.resolve()]:
        protected.update(p.resolve() for p in source_root.rglob('*') if p.is_file())
    # Runtime configuration and adjacent compiler/native dependencies are inputs too.
    if cli.parent.is_dir():
        protected.update(p.resolve() for p in cli.parent.iterdir()
                         if p.is_file() and (p.suffix.lower() in {'.dll', '.exe', '.pdb', '.so', '.dylib'}
                                             or p.name.endswith(('.deps.json', '.runtimeconfig.json'))))
    protected.update(cli.with_suffix(suffix).resolve() for suffix in ['.deps.json', '.runtimeconfig.json'])
    catalog = json.loads(bindings.read_text())
    for function in catalog['functions']:
        if 'assembly' in function:
            assembly = (bindings.parent / function['assembly']).resolve()
            protected.update([assembly, assembly.with_suffix('.deps.json').resolve(), assembly.with_suffix('.runtimeconfig.json').resolve()])
    protected.update((bindings.parent / name).resolve() for name in catalog.get('nativeLibraries', []))
    resolved = [p.resolve() for p in outputs]
    if len(set(resolved)) != len(resolved) or any(
            a.exists() and b.exists() and a.samefile(b)
            for index, a in enumerate(resolved) for b in resolved[index + 1:]):
        raise ValueError('output files must be distinct')
    # Preflight assumes stable paths, not concurrent hostile filesystem changes.
    for output in resolved:
        same_input = output.exists() and any(p.exists() and output.samefile(p) for p in protected)
        if same_input or output in protected or output.suffix.lower() != '.json' or output.is_relative_to(entry.parent.resolve()) or output.is_relative_to(runtime_source.resolve()):
            raise ValueError('output must be a JSON file outside inputs and original source directories')


def advance(entry: Path, dotnet: str, cli: Path, bindings: Path, native_catalog: Path,
            runtime_source: Path, catalog_output: Path, max_additions: int, timeout: float, only_bound: bool = False):
    if max_additions < 0 or not math.isfinite(timeout) or timeout <= 0:
        raise ValueError('max-additions must be nonnegative and timeout finite and positive')
    validate_outputs(entry, cli, bindings, native_catalog, runtime_source, [catalog_output])
    signatures, rejected = extract_signatures(native_catalog, runtime_source)
    externals, external_rejected = external_signatures(entry.parent)
    for name, signature in externals.items():
        if name in signatures and signatures[name] != signature:
            signatures.pop(name)
            rejected.append({'name': name, 'reason': 'conflicting runtime and .decls contracts'})
        elif name not in signatures:
            signatures[name] = signature
    rejected.extend(external_rejected)
    catalog = json.loads(bindings.read_text())
    if only_bound:
        catalog['functions'] = [f for f in catalog['functions'] if f.get('method') is not None]
    initial_names = {x['name'].casefold() for x in catalog['functions']}
    for function in catalog['functions']:
        if 'assembly' in function:
            function['assembly'] = str((bindings.parent / function['assembly']).resolve())
    if 'nativeLibraries' in catalog:
        catalog['nativeLibraries'] = [str((bindings.parent / p).resolve()) for p in catalog['nativeLibraries']]
    names = {x['name'].casefold() for x in catalog['functions']}
    steps = []
    catalog_output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='blitzcs-semantic-', dir=catalog_output.parent) as work:
        working_catalog = Path(work) / 'catalog.json'
        for iteration in range(max_additions + 1):
            working_catalog.write_text(json.dumps(catalog, indent=2) + '\n')
            process = subprocess.run([dotnet, str(cli.resolve()), 'check-semantics', str(entry.resolve()),
                                      '--externals', str(working_catalog.resolve())],
                                     capture_output=True, text=True, timeout=timeout)
            diagnostic = (process.stderr or process.stdout).strip()
            # Public summaries refer to source-relative locations, not machine paths.
            diagnostic = diagnostic.replace(str(entry.parent.resolve()) + '/', '').replace(str(entry.parent.resolve()) + '\\', '')
            steps.append({'exit_code': process.returncode, 'diagnostic': diagnostic})
            if process.returncode == 0:
                catalog_output.write_text(working_catalog.read_text())
                return {'semantic_validated': True, 'assembly_emitted': False, 'steps': steps,
                        'added_declarations': sorted(names - initial_names),
                        'rejected_original_contracts': rejected}
            match = UNKNOWN.search(diagnostic)
            key = match.group(1).casefold() if match else None
            if key not in signatures or key in names or iteration == max_additions:
                break
            catalog['functions'].append(signatures[key])
            names.add(key)
        catalog_output.write_text(working_catalog.read_text())
    return {'semantic_validated': False, 'assembly_emitted': False, 'steps': steps,
            'added_declarations': sorted(names - initial_names),
            'rejected_original_contracts': rejected}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--entry', type=Path, required=True)
    parser.add_argument('--bindings', type=Path, required=True)
    parser.add_argument('--runtime-source-dir', type=Path, required=True)
    parser.add_argument('--native-catalog', type=Path, default=Path(__file__).with_name('scp_native_catalog.json'))
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--catalog-output', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--max-additions', type=int, default=64)
    parser.add_argument('--timeout', type=float, default=30)
    parser.add_argument('--only-bound', action='store_true', help='start from executable bindings to reproduce declaration discovery')
    args = parser.parse_args()
    if args.max_additions < 0 or not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error('max-additions must be nonnegative and timeout positive')
    try:
        validate_outputs(args.entry, args.cli, args.bindings, args.native_catalog,
                         args.runtime_source_dir, [args.catalog_output, args.report])
        result = advance(args.entry, args.dotnet, args.cli, args.bindings, args.native_catalog,
                         args.runtime_source_dir, args.catalog_output, args.max_additions, args.timeout, args.only_bound)
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        print('semantic progress infrastructure error: ' + str(error))
        return 2
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'semantic_validated': result['semantic_validated'],
                      'added_declarations': len(result['added_declarations']),
                      'assembly_emitted': False, 'last_diagnostic': result['steps'][-1]['diagnostic']}))
    return 0 if result['semantic_validated'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
