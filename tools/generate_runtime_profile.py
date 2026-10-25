#!/usr/bin/env python3
"""Recover advisory API additions from an actual runtime-registration snapshot."""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import re
import struct

try:
    from tools.generate_scp_native_manifest import decode_descriptor
    from tools.generate_blitz3d_profile import RESERVED, TYPE_NAMES
except ModuleNotFoundError:
    from generate_scp_native_manifest import decode_descriptor
    from generate_blitz3d_profile import RESERVED, TYPE_NAMES


def _constant(value, kind):
    if kind == 'Integer':
        if isinstance(value, str):
            if not re.fullmatch(r'[+-]?(?:[0-9]+|0[xX][0-9a-fA-F]+)', value):
                raise ValueError(f'invalid Integer default {value!r}')
            value = int(value, 16 if 'x' in value.lower() else 10)
        if type(value) is not int or not -(2**31) <= value < 2**31:
            raise ValueError(f'Integer default out of range: {value!r}')
        return value
    if kind == 'Float':
        if isinstance(value, str) and not re.fullmatch(r'[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?', value):
            raise ValueError(f'invalid Float default {value!r}')
        if isinstance(value, bool) or not isinstance(value, (str, int, float)):
            raise ValueError(f'invalid Float default {value!r}')
        try:
            result = struct.unpack('f', struct.pack('f', float(value)))[0]
        except (OverflowError, struct.error) as error:
            raise ValueError(f'Float default out of range: {value!r}') from error
        if not math.isfinite(result):
            raise ValueError(f'nonfinite Float default: {value!r}')
        return result
    if kind == 'String' and isinstance(value, str):
        return value
    raise ValueError(f'invalid {kind} default {value!r}')


def _signature(function):
    returned = function.get('returnType')
    parameters = function.get('parameters')
    defaults = function.get('defaults', [])
    if returned not in TYPE_NAMES.values() or not isinstance(parameters, list) or any(p not in ('Integer', 'Float', 'String') for p in parameters):
        raise ValueError(f'invalid explicit signature for {function.get("name")}')
    if not isinstance(defaults, list) or len(defaults) > len(parameters):
        raise ValueError(f'invalid trailing defaults for {function.get("name")}')
    normalized = [_constant(value, kind) for value, kind in zip(defaults, parameters[len(parameters)-len(defaults):])]
    return returned, parameters, normalized


def _base_functions(path):
    functions, inputs, active, visited = {}, [], set(), set()

    def visit(file):
        file = file.resolve()
        if file in active:
            raise ValueError(f'base profile cycle at {file}')
        if file in visited:
            return
        if len(visited) >= 256:
            raise ValueError('base profile exceeds 256 inputs')
        active.add(file)
        visited.add(file)
        inputs.append(file)
        root = json.loads(file.read_text(encoding='utf-8'))
        if not isinstance(root, dict) or not isinstance(root.get('profiles', []), list) or not isinstance(root.get('functions', []), list):
            raise ValueError(f'invalid base profile {file}')
        for profile in root.get('profiles', []):
            if not isinstance(profile, str) or not profile.strip():
                raise ValueError(f'invalid base profile import at {file}')
            visit(file.parent / profile.replace('\\', os.sep))
        for function in root.get('functions', []):
            if not isinstance(function, dict) or not isinstance(function.get('name'), str):
                raise ValueError(f'invalid base function at {file}')
            key = function['name'].casefold()
            signature = _signature(function)
            if key in functions and functions[key] != signature:
                raise ValueError(f'base signature conflict for {function["name"]} at {file}')
            functions[key] = signature
        active.remove(file)

    visit(Path(path))
    return functions, inputs


def _live_function(descriptor):
    # The shared decoder splits arguments at type markers. Mask quoted literals
    # first so markers inside real runtime strings remain data, not arguments.
    literals, masked, index = [], [], 0
    while index < len(descriptor):
        if descriptor[index] != '"':
            masked.append(descriptor[index])
            index += 1
            continue
        index += 1
        literal = []
        while index < len(descriptor):
            char = descriptor[index]
            index += 1
            if char != '"':
                literal.append(char)
            elif index < len(descriptor) and descriptor[index] == '"':
                literal.append('"')
                index += 1
            else:
                break
        else:
            raise ValueError(f'unterminated runtime string default: {descriptor!r}')
        literals.append(''.join(literal))
        masked.append(f'"runtime_literal_{len(literals)-1}"')
    signature = decode_descriptor(''.join(masked))
    if signature is None:
        raise ValueError(f'unsupported runtime descriptor: {descriptor!r}')
    name = signature['name']
    if name.startswith('_') or name.casefold() in RESERVED:
        return None
    parameters = signature['arguments']
    function = {'name': name, 'returnType': TYPE_NAMES[signature['return_type']],
                'parameters': [TYPE_NAMES[p['type']] for p in parameters], 'defaults': []}
    optional = False
    for parameter in parameters:
        value = parameter['default']
        if value is None:
            if optional:
                raise ValueError(f'non-trailing runtime defaults in {descriptor!r}')
            continue
        optional = True
        kind = TYPE_NAMES[parameter['type']]
        if kind == 'String':
            match = re.fullmatch(r'"runtime_literal_([0-9]+)"', value)
            if match is None:
                raise ValueError(f'invalid quoted runtime String default in {descriptor!r}')
            value = literals[int(match[1])]
        function['defaults'].append(_constant(value, kind))
    return function


def build_profile(snapshot: dict, base_profile: Path, output: Path, provider: str = 'blitz3d.runtime') -> dict:
    digest = snapshot.get('runtimeSHA256')
    if not isinstance(digest, str) or not re.fullmatch('[0-9a-fA-F]{64}', digest):
        raise ValueError('snapshot runtimeSHA256 must be a SHA-256 hex digest')
    if 'version' not in snapshot or not isinstance(snapshot['version'], (str, int)) or isinstance(snapshot['version'], bool):
        raise ValueError('snapshot must contain a runtime version')
    descriptors = snapshot.get('descriptors')
    if not isinstance(descriptors, list) or any(not isinstance(d, str) for d in descriptors):
        raise ValueError('snapshot descriptors must be runtime strings')
    if not isinstance(provider, str) or not provider.strip():
        raise ValueError('provider must be nonempty')
    base, _ = _base_functions(base_profile)
    seen, additions, excluded = {}, [], []
    for descriptor in descriptors:
        function = _live_function(descriptor)
        if function is None:
            excluded.append(descriptor)
            continue
        key, signature = function['name'].casefold(), _signature(function)
        if key in seen and seen[key] != signature or key in base and base[key] != signature:
            raise ValueError(f'runtime signature/default conflict for {function["name"]}: {descriptor!r}')
        if key in seen:
            continue
        seen[key] = signature
        if key in base:
            continue
        function['contract'] = {'provider': provider, 'resolution': 'runtime-registration',
                                'symbol': descriptor, 'callingConvention': None,
                                'source': f'sha256:{digest.lower()}!registration:{function["name"]}'}
        additions.append(function)
    return {
        'metadata': {'runtimeSHA256': digest.lower(), 'version': snapshot['version'],
                     'descriptorEncoding': 'Actual runtime registration text; no C++ source-string unescaping.',
                     'description': 'Advisory declarations only; native ABI and executable implementations are not inferred.',
                     'excludedDescriptors': sorted(set(excluded))},
        'profiles': [os.path.relpath(Path(base_profile).resolve(), Path(output).resolve().parent).replace(os.sep, '/')],
        'functions': sorted(additions, key=lambda f: (f['name'].casefold(), f['name']))
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--base-profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--provider', default='blitz3d.runtime')
    args = parser.parse_args()
    try:
        _, inputs = _base_functions(args.base_profile)
        output = args.output.resolve()
        for protected in [args.snapshot.resolve(), *inputs]:
            if output == protected or output.exists() and output.samefile(protected):
                raise ValueError('output cannot overwrite a snapshot or base-profile input')
        profile = build_profile(json.loads(args.snapshot.read_text(encoding='utf-8')), args.base_profile, args.output, args.provider)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(profile, indent=2, ensure_ascii=False, allow_nan=False) + '\n', encoding='utf-8')
    except (ValueError, OSError) as error:
        parser.exit(1, f'{error}\n')


if __name__ == '__main__':
    main()
