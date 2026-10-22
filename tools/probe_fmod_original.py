#!/usr/bin/env python3
"""Execute a trusted historical FMOD DLL inside original Blitz3D's Win32 process.

This headless NOSOUND ABI probe is not a functional audio or .NET FMOD binding.
The supplied Blitz3D installation and game sources are never modified.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
try:
    from tools.original_blitz3d_adapter import capture, windows_path, AdapterError
except ModuleNotFoundError:
    from original_blitz3d_adapter import capture, windows_path, AdapterError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True, help='trusted FMOD 3 DLL; never downloaded by this tool')
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--output', choices=['nosound', 'default'], default='nosound')
    args = parser.parse_args()
    required = ['BLITZ3D_HOME', 'BLITZ3D_WINE', 'BLITZ3D_WINEPREFIX', 'BLITZ3D_DISPLAY']
    if any(not os.environ.get(key) for key in required):
        parser.error('set ' + ', '.join(required))
    dll = args.dll.resolve()
    home = Path(os.environ['BLITZ3D_HOME']).resolve()
    if not dll.is_file() or not (home / 'bin' / 'blitzcc.exe').is_file():
        parser.error('DLL and original compiler must exist')
    report = {'dll_sha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
              'profile': 'original Win32 runtime; ' + ('explicit FMOD_OUTPUT_NOSOUND=0' if args.output == 'nosound' else 'default output, as in SCP startup') + '; no playback requested',
              'native_return_note': 'FMOD signed-char results are declared Integer by historical .decls; upper EAX bits are not stable ABI results.'}
    os.environ['BLITZ3D_EVIDENCE_DIR'] = str(args.evidence_dir.resolve())
    with tempfile.TemporaryDirectory(prefix='blitzcs-fmod-probe-') as temp:
        shadow = Path(temp)
        shutil.copytree(home / 'bin', shadow / 'bin')
        (shadow / 'userlibs').mkdir()
        declarations = [f'.lib "{windows_path(dll)}"',
            'M13Version#():"_FSOUND_GetVersion@0"',
            'M13Init%(freq%,channels%,flags%):"_FSOUND_Init@12"',
            'M13Error%():"_FSOUND_GetError@0"',
            'M13Output%(mode%):"_FSOUND_SetOutput@4"',
            'M13Close():"_FSOUND_Close@0"']
        (shadow / 'userlibs' / 'm13.decls').write_text('\n'.join(declarations) + '\n', encoding='cp1252')
        source = shadow / 'probe.bb'
        output_setup = 'Local output%=M13Output(0)\nPrint output\nPrint output And 255\n' if args.output == 'nosound' else ''
        source.write_text('Print M13Version()\n' + output_setup + 'Local initialized%=M13Init(44100,64,0)\nPrint initialized\nPrint initialized And 255\nPrint M13Error()\nM13Close\nPrint "closed"\n', encoding='cp1252')
        report['probe_source_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
        report['declarations'] = declarations
        try:
            result = capture(source, home=shadow, wine=os.environ['BLITZ3D_WINE'],
                prefix=Path(os.environ['BLITZ3D_WINEPREFIX']), display=os.environ['BLITZ3D_DISPLAY'], timeout=8)
            report['capture'] = result
            status = 0 if result['phase'] == 'run' and result['exit_code'] == 0 else 1
        except (AdapterError, OSError, ValueError) as error:
            report['adapter_error'] = str(error)
            status = 2
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(args.report.resolve())
    return status


if __name__ == '__main__':
    raise SystemExit(main())
