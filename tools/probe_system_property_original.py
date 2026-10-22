#!/usr/bin/env python3
"""Probe classic SystemProperty behavior in the genuine Blitz3D Win32 runtime."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
try:
    from tools.original_blitz3d_adapter import AdapterError, capture
except ModuleNotFoundError:
    from original_blitz3d_adapter import AdapterError, capture


SOURCE = '''Local cpu$=SystemProperty("cpu")
Local os$=SystemProperty("os")
Local appdir$=SystemProperty("appdir")
Local apphwnd$=SystemProperty("apphwnd")
Local apphinstance$=SystemProperty("apphinstance")
Local windowsdir$=SystemProperty("windowsdir")
Local systemdir$=SystemProperty("systemdir")
Local tempdir$=SystemProperty("tempdir")
Local unknown$=SystemProperty("m16_unknown_property")
Local uppercpu$=SystemProperty("CPU")
Print "unknown-empty="+(unknown$="")
Print "cpu-case-insensitive="+(uppercpu$=cpu$)
Print "tempdir-nonempty="+(Len(tempdir$)>0)
Print "tempdir-trailing-separator="+(Right$(tempdir$,1)=Chr$(92) Or Right$(tempdir$,1)=Chr$(47))
Print "apphwnd-nonzero="+(apphwnd$<>"" And apphwnd$<>"0")
Print "cpu="+cpu$
Print "os="+os$
Print "appdir="+appdir$
Print "apphwnd="+apphwnd$
Print "apphinstance="+apphinstance$
Print "windowsdir="+windowsdir$
Print "systemdir="+systemdir$
Print "tempdir="+tempdir$
'''


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    args = parser.parse_args()
    required = ['BLITZ3D_HOME', 'BLITZ3D_WINE', 'BLITZ3D_WINEPREFIX', 'BLITZ3D_DISPLAY']
    if any(not os.environ.get(key) for key in required):
        parser.error('set ' + ', '.join(required))
    home = Path(os.environ['BLITZ3D_HOME']).resolve()
    compiler = home / 'bin' / 'blitzcc.exe'
    if not compiler.is_file():
        parser.error(f'original Blitz3D compiler not found: {compiler}')
    args.evidence_dir.mkdir(parents=True, exist_ok=True)
    source = args.evidence_dir / 'probe.bb'
    source.write_text(SOURCE, encoding='cp1252')
    report = {'runtime': 'original Blitz3D Win32 under Proton/Wine',
              'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
              'capture': None}
    os.environ['BLITZ3D_EVIDENCE_DIR'] = str(args.evidence_dir.resolve())
    try:
        report['capture'] = capture(source, home=home, wine=os.environ['BLITZ3D_WINE'],
            prefix=Path(os.environ['BLITZ3D_WINEPREFIX']), display=os.environ['BLITZ3D_DISPLAY'], timeout=10)
        result = report['capture']
        status = 0 if result.get('phase') == 'run' and result.get('exit_code') == 0 else 1
    except (AdapterError, OSError, ValueError) as error:
        report['adapter_error'] = str(error)
        status = 2
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(args.report.resolve())
    return status


if __name__ == '__main__':
    raise SystemExit(main())
