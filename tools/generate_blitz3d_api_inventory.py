#!/usr/bin/env python3
"""Inventory every active rtSym registration in the pinned historical runtime.

This is metadata extraction, never a claim that a declaration executes.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import os
import tempfile
from pathlib import Path
import re
import subprocess

from tools.generate_scp_native_manifest import decode_descriptor, erase_comments_and_strings, IDENTIFIER

COMMIT = '6beb288cb5962393684a59a4a44ac11524894939'
CATEGORIES = ['graphics/window', 'input', '2D/buffers', '3D/entities',
              'meshes/materials/textures', 'cameras/light/animation',
              'collisions/movement', 'audio', 'files/memory/timing/system', 'remaining']
REGISTRATION = re.compile(r'\brtSym\s*\(\s*"((?:\\.|[^"\\])*)"\s*,\s*([^;)]+)\)')


def strip_cpp_comments(text: str) -> str:
    """Preserve strings and positions; remove both C++ comment forms."""
    pattern = re.compile(r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*[\s\S]*?\*/')
    return pattern.sub(lambda m: re.sub(r'[^\n]', ' ', m[0]) if m[0].startswith(('//', '/*')) else m[0], text)


def category(module: str, name: str) -> str:
    n = name.casefold()
    if module == 'bbinput.cpp': return 'input'
    if module == 'bbaudio.cpp': return 'audio'
    if module == 'bbgraphics.cpp':
        if any(word in n for word in ('graphics', 'gfx', 'driver', 'vidmem', 'gamma')) or n in {'vwait', 'scanline'}:
            return 'graphics/window'
        return '2D/buffers'
    if module == 'bbblitz3d.cpp':
        if any(word in n for word in ('collision', 'pick', 'move', 'translate', 'rotate', 'turn', 'position', 'pointentity', 'alignto')):
            return 'collisions/movement'
        if any(word in n for word in ('camera', 'light', 'anim', 'ambient')): return 'cameras/light/animation'
        if any(word in n for word in ('mesh', 'surface', 'vertex', 'triangle', 'texture', 'brush', 'terrain', 'sphere', 'cube', 'cylinder', 'cone', 'plane', 'md2', 'bsp')):
            return 'meshes/materials/textures'
        if n in {'graphics3d', 'windowed3d', 'gfxdrivercaps3d', 'countgfxmodes3d', 'gfxmode3d', 'gfxmode3dexists'}:
            return 'graphics/window'
        return '3D/entities'
    if module in {'bbfilesystem.cpp', 'bbstream.cpp', 'bbbank.cpp', 'bbsys.cpp', 'bbruntime.cpp'}:
        return 'files/memory/timing/system'
    return 'remaining'


# Deliberately narrow: these names have explicit public captured comparisons.
STRING_FUNCTIONS = 'String Left Right Mid Instr Replace Upper Lower Trim LSet RSet Chr Asc Len Hex Bin CurrentDate CurrentTime'.lower().split()

VALIDATION = {
    **dict.fromkeys(STRING_FUNCTIONS[:-2],
                    ('docs/evidence/m19-original.json', 'docs/evidence/m19-new-comparison.json', 'one deterministic string program, including all 256 byte values for case/trim; six error diagnostics have known process-termination divergence; no clock differential')),
    **dict.fromkeys('CreateBank FreeBank BankSize ResizeBank CopyBank PeekByte PeekShort PeekInt PeekFloat PokeByte PokeShort PokeInt PokeFloat'.lower().split(),
                    ('docs/evidence/m18-original.json', 'docs/evidence/m18-new-comparison.json', 'one bank program, valid operations and specified edge cases; not all invalid handles or host ABIs')),
    **dict.fromkeys('Windowed3D Graphics3D GraphicsWidth GraphicsHeight BackBuffer FrontBuffer Flip EndGraphics'.lower().split(),
                    ('docs/evidence/m17-original.json', 'docs/evidence/m17-new-comparison.json', 'mode 2 lifecycle and selected contracts; no pixel presentation or vsync timing guarantee')),
    'systemproperty': ('docs/evidence/m16-original/system-property.json', 'docs/evidence/m16-comparison.json', 'normalized selected properties only; AppHWND explicitly unsupported'),
}


def extract(root: Path) -> tuple[list[dict], list[dict]]:
    rows, files = [], []
    for path in sorted((root / 'bbruntime').rglob('*')):
        if not path.is_file() or path.suffix not in {'.cpp', '.h', '.c'}: continue
        raw = path.read_bytes()
        clean = strip_cpp_comments(raw.decode('cp1252'))
        matches = list(REGISTRATION.finditer(clean))
        relative = path.relative_to(root).as_posix()
        includes = re.findall(r'^\s*#\s*include\s*[<"]([^>"\n]+)', clean, re.M)
        files.append({'file': relative, 'sha256': hashlib.sha256(raw).hexdigest(), 'registrations': len(matches)})
        for match in matches:
            descriptor = match[1]
            decoded = decode_descriptor(descriptor)
            rows.append({'descriptor': descriptor, 'signature': decoded,
                         'name': decoded['name'] if decoded else None,
                         'source': relative, 'line': clean.count('\n', 0, match.start()) + 1,
                         'implementation_symbol': match[2].strip(),
                         'subsystem': category(path.name, decoded['name'] if decoded else descriptor),
                         'dependencies': {'registration_module': relative, 'direct_includes': includes,
                                          'runtime_transitive_dependencies': 'not verified; includes are source evidence, not a runtime dependency graph'}})
    return rows, files


def validation_evidence(repo: Path, key: str) -> list[str]:
    validation = VALIDATION.get(key)
    evidence = list(validation[:2]) if validation and all((repo / p).is_file() for p in validation[:2]) else []
    if key in STRING_FUNCTIONS[:-2] and evidence:
        original = json.loads((repo / evidence[0]).read_text())
        comparison = json.loads((repo / evidence[1]).read_text())
        if not (comparison.get('deterministic_capture_matches') is True
                and comparison.get('unexpected_mismatch') == 0
                and original.get('source_sha256')
                and comparison.get('source_sha256') == original['source_sha256']):
            return []
    return evidence


def verify_source(root: Path) -> str:
    revision = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
    if revision != COMMIT: raise ValueError(f'expected source revision {COMMIT}, got {revision}')
    if subprocess.run(['git', '-C', str(root), 'diff', '--quiet', 'HEAD', '--', 'bbruntime']).returncode:
        raise ValueError('historical bbruntime differs from pinned HEAD')
    if subprocess.check_output(['git', '-C', str(root), 'ls-files', '--others', '--exclude-standard', '--', 'bbruntime'], text=True).strip():
        raise ValueError('historical bbruntime contains untracked source inputs')
    return revision


def validate_outputs(output: Path, original: Path, repo: Path, scp: Path | None = None) -> tuple[Path, Path]:
    if output.suffix != '.json': raise ValueError('--output must have a .json suffix')
    outputs = (output, output.with_suffix('.md'))
    roots = [original] + ([scp] if scp else [])
    inputs = [repo / 'samples/Audio/scp.json', Path(__file__).resolve(),
              repo / 'tools/generate_scp_native_manifest.py', repo / 'tools/scp_native_catalog.json']
    inputs += [p for root in roots for p in root.rglob('*') if p.is_file()]
    inputs += [p for p in (repo / 'docs/evidence').rglob('*') if p.is_file()]
    def aliases(left: Path, right: Path) -> bool:
        return left.resolve() == right.resolve() or (left.exists() and right.exists() and left.samefile(right))
    if aliases(*outputs): raise ValueError('JSON and Markdown outputs alias each other')
    for destination in outputs:
        if any(destination.resolve().is_relative_to(root.resolve()) for root in roots):
            raise ValueError('output lies within historical or application source inputs')
        if any(aliases(destination, source) for source in inputs):
            raise ValueError('output aliases a protected input')
    return outputs


def write_outputs(outputs: tuple[Path, Path], data: dict) -> None:
    contents = (json.dumps(data, indent=2) + '\n', markdown(data))
    staged = []
    try:
        for destination, content in zip(outputs, contents):
            with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=destination.parent,
                                             prefix='.' + destination.name + '.', delete=False) as stream:
                staged.append((Path(stream.name), destination))
                stream.write(content)
        for temporary, destination in staged:
            os.replace(temporary, destination)
    finally:
        for temporary, _ in staged:
            temporary.unlink(missing_ok=True)


def build(root: Path, repo: Path, scp: Path | None = None) -> dict:
    revision = verify_source(root)
    rows, files = extract(root)
    declarations = json.loads((repo / 'samples/Audio/scp.json').read_text())['functions']
    declared = defaultdict(list)
    for item in declarations: declared[item['name'].casefold()].append(item)
    occurrences = Counter()
    source_manifest = []
    if scp:
        for path in sorted(scp.rglob('*.bb')):
            raw = path.read_bytes()
            source_manifest.append({'file': path.relative_to(scp).as_posix(), 'sha256': hashlib.sha256(raw).hexdigest()})
            occurrences.update(n.casefold() for n in IDENTIFIER.findall(erase_comments_and_strings(raw.decode('cp1252'))))
    by_name = defaultdict(set)
    for row in rows:
        if row['name']: by_name[row['name'].casefold()].add(row['descriptor'])
    for row in rows:
        key = (row['name'] or '').casefold()
        candidates = declared[key]
        bindings = [d for d in candidates if all(d.get(k) for k in ('assembly', 'type', 'method'))]
        managed_sources = []
        for binding in bindings:
            for path in sorted((repo / 'src').rglob(binding['type'].split('.')[-1] + '.cs')):
                if re.search(r'\b' + re.escape(binding['method']) + r'\s*\(', path.read_text()):
                    managed_sources.append(path.relative_to(repo).as_posix())
        validation = VALIDATION.get(key)
        evidence = validation_evidence(repo, key)
        row['states'] = {'signature_known': row['signature'] is not None and row['signature']['kind'] == 'typed_runtime_callable',
                         'typed_declaration': bool(candidates),
                         'executable_binding': bool(bindings and managed_sources),
                         'functional': 'managed implementation present; behavior scope requires cited tests' if managed_sources else 'not established',
                         'validated': 'scoped historical comparison' if evidence and managed_sources else 'not established'}
        row['managed_binding'] = bindings
        row['managed_sources'] = sorted(set(managed_sources))
        row['functional_evidence'] = []
        if key in STRING_FUNCTIONS and managed_sources:
            tests = 'tests/BlitzCS.CLI.Tests/LegacyStringFamilyTests.cs'
            row['functional_evidence'] = sorted(set(managed_sources)) + ([tests] if (repo / tests).is_file() else [])
            row['states']['functional'] = 'implemented; focused managed tests cited' if (repo / tests).is_file() else 'managed implementation present; tests not established'
            if key in {'currentdate', 'currenttime'}:
                row['functional_scope'] = 'real local host clock formatting; no historical clock differential claimed'
            else:
                row['functional_scope'] = 'managed CP1252 string operations and explicit encoding failures; see cited focused tests'
        row['validation_evidence'] = evidence
        row['validation_scope'] = validation[2] if evidence else None
        row['descriptor_conflict'] = len(by_name[key]) > 1
        row['scp_lexical_occurrences'] = occurrences[key] if scp else None
    return {'schema_version': 1, 'source': {'repository': 'https://github.com/blitz-research/blitz3d', 'commit': revision,
            'scope': 'all active literal rtSym registrations recursively under bbruntime; internal symbols retained separately by kind',
            'files': files},
            'state_definitions': {'signature_known': 'decoded public rtSym descriptor; internal symbols have unknown ABI',
              'typed_declaration': 'name present in samples/Audio/scp.json, including declaration-only entries',
              'executable_binding': 'assembly/type/method configured and matching managed source method present; does not guarantee assembly built or runtime host available',
              'functional': 'source implementation evidence only; no universal correctness claim',
              'validated': 'explicit scoped published comparison; each program is one experiment, not one per function'},
            'limitations': ['Subsystem grouping is a navigation taxonomy inferred from registration module/name.',
              'No C++ preprocessor configuration evaluated; active means outside comments, including conditional registrations.',
              'Literal registration descriptors only; unsupported descriptors and same-name conflicts are retained explicitly.',
              'SCP frequency is lexical identifier occurrence after strings/comments removal, not AST-resolved calls.',
              'Source declarations are historical reference evidence; published captures use the documented original runtime binary version.',
              'Language keywords/intrinsics without rtSym registration are outside this runtime API inventory.'],
            'excluded_domains': {
                'compiler_language_intrinsics': {
                    'examples_not_exhaustive': ['Abs', 'Sgn', 'Int', 'Float', 'Str', 'Object', 'Handle', 'Mod', 'Pi'],
                    'source_evidence': [{'file': 'compiler/toker.cpp', 'lines': '52-72'},
                                        {'file': 'compiler/parser.cpp', 'lines': '620-660'},
                                        {'file': 'compiler/exprnode.cpp', 'lines': '295-327'}],
                    'status': 'not inventoried here; existing compiler core unchanged; an exhaustive language inventory requires a separate phase'},
                'third_party_external_declarations': {
                    'examples_not_exhaustive': ['FSOUND_*', 'Win32 API adapters'],
                    'status': 'separate external integrations, not historical built-in rtSym APIs; the 17 audio rows are built-in Blitz3D audio only'}},
            'scp_source_files': source_manifest, 'totals': {'registrations': len(rows), 'unique_names': len(by_name),
              'unsupported_descriptors': sum(r['signature'] is None for r in rows),
              'internal_symbols': sum(bool(r['signature'] and r['signature']['kind'] == 'internal_runtime_symbol') for r in rows),
              'conflicting_names': sum(len(v) > 1 for v in by_name.values()),
              'by_subsystem': dict(Counter(r['subsystem'] for r in rows))}, 'registrations': rows}


def markdown(data: dict) -> str:
    out = ['# Historical Blitz3D runtime API inventory', '',
           'Generated by `python -m tools.generate_blitz3d_api_inventory --original-root <checkout>`.', '',
           f"Reference: [upstream source](https://github.com/blitz-research/blitz3d/tree/{COMMIT}), commit `{COMMIT}`.", '',
           f"{data['totals']['registrations']} registrations; {data['totals']['unique_names']} unique names; "
           f"{data['totals']['internal_symbols']} internal ABI symbols; {data['totals']['unsupported_descriptors']} unsupported descriptors; "
           f"{data['totals']['conflicting_names']} conflicting names.", '',
           'Complete machine-readable signatures, defaults, returns, source lines, direct includes, binding states and evidence: [JSON inventory](blitz3d-api-inventory.json).', '',
           'Baseline: ' + ', '.join(f"{sum(bool(r['states'][state]) for r in data['registrations'])} {state}" for state in ('signature_known', 'typed_declaration', 'executable_binding')) + '.', '',
           'A configured managed binding is distinct from a known signature. Functional source presence and historical comparisons are separate fields. No inventory entry implies complete semantic parity.', '',
           'Descriptors describe BASIC calls, not native C/C++ ABI signatures or pointer-marshalling contracts.', '']
    out += ['- ' + item for item in data['limitations']]
    out += ['', '## Scope boundaries', '',
            'This inventory is complete for the 593 literal runtime registrations in the pinned source, not the whole Blitz3D language. Compiler intrinsics and language operations such as Abs, Sgn, Int, Float, Str, Object and Handle require a separate exhaustive language inventory; the existing compiler core is unchanged.', '',
            f'Original evidence: [keyword tokens](https://github.com/blitz-research/blitz3d/blob/{COMMIT}/compiler/toker.cpp#L52-L72), [expression parser](https://github.com/blitz-research/blitz3d/blob/{COMMIT}/compiler/parser.cpp#L620-L660), [intrinsic lowering](https://github.com/blitz-research/blitz3d/blob/{COMMIT}/compiler/exprnode.cpp#L295-L327).', '',
            'The 17 audio registrations are built-in Blitz3D audio. FSOUND external declarations and Win32 adapters are separate integrations and are excluded. The remaining category includes internal ABI registrations, strings, math, networking and other modules that do not fit the named engine subsystems.']
    for group in CATEGORIES:
        rows = [r for r in data['registrations'] if r['subsystem'] == group]
        out += ['', '## ' + group, '', f'{len(rows)} registrations.', '', '| Descriptor (return prefix and argument defaults) | Original module | Known signature | Declaration | Binding | Functional | Validation |', '| --- | --- | --- | --- | --- | --- | --- |']
        for row in rows:
            out.append(f"| `{row['descriptor']}` | `{Path(row['source']).name}:{row['line']}` | {'yes' if row['states']['signature_known'] else 'internal ABI only'} | {'typed' if row['states']['typed_declaration'] else 'none'} | {'managed source' if row['states']['executable_binding'] else 'none established'} | {row['states']['functional']} | {row['states']['validated']} |")
    out += ['', 'Descriptor markers: `%` integer, `#` float, `$` string; no return marker means void. Internal `_bb…` registrations omit their ABI and are not public callable signatures.', '',
            'The full 18-function string family has managed implementations and focused test evidence; historical validation is limited to the 16 deterministic functions with a completed comparison. CurrentDate and CurrentTime use the real local host clock and have no historical clock differential. Next family candidate: scalar math, followed by stream/file I/O. SCP lexical counts help rank candidates but do not prove resolved call frequency.', '']
    return '\n'.join(out)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original-root', type=Path, required=True)
    parser.add_argument('--scp-source', type=Path)
    parser.add_argument('--output', type=Path, default=Path('docs/blitz3d-api-inventory.json'))
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    outputs = validate_outputs(args.output, args.original_root, repo, args.scp_source)
    data = build(args.original_root, repo, args.scp_source)
    write_outputs(outputs, data)
    print(json.dumps(data['totals'], indent=2))

if __name__ == '__main__': main()
