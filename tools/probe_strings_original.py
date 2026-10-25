#!/usr/bin/env python3
"""Capture deterministic string semantics from an isolated genuine Blitz3D SDK and Xvfb.

Raw paths/processes stay in evidence-dir; report contains portable captures.
Never probes empty Replace search strings: the original does not terminate.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import tempfile
try:
    from tools.original_blitz3d_adapter import capture
    from tools.probe_windowed3d_original import capture_case
except ModuleNotFoundError:
    from original_blitz3d_adapter import capture
    from probe_windowed3d_original import capture_case

SOURCE = (Path(__file__).resolve().parents[1] / 'samples/LegacyStrings/reference.bb').read_text()

CASES = {
    'left-negative': ('Print Left("abc",-1)\n', 'parameter must be positive'),
    'right-negative': ('Print Right("abc",-1)\n', 'parameter must be positive'),
    'lset-negative': ('Print LSet("abc",-1)\n', 'parameter must be positive'),
    'rset-negative': ('Print RSet("abc",-1)\n', 'parameter must be positive'),
    'mid-zero': ('Print Mid("abc",0)\n', 'parameter must be greater than 0'),
    'instr-zero': ('Print Instr("abc","a",0)\n', 'parameter must be greater than 0'),
}

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--invalid-cases', action='store_true', help='Opt into isolated native invalid-parameter observations; results may vary or crash')
    parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--evidence-dir',type=Path,required=True)
    args=parser.parse_args()
    for key in ('BLITZ3D_HOME','BLITZ3D_WINE','BLITZ3D_WINEPREFIX'):
        if not os.environ.get(key): parser.error('set '+key)
    evidence=args.evidence_dir.resolve()
    evidence.mkdir(parents=True,exist_ok=True)
    report={'runtime':'genuine Blitz3D Win32 under Wine with owned Xvfb','cases':{},
            'sdk_sha256': {name: hashlib.sha256((Path(os.environ['BLITZ3D_HOME']) / 'bin' / name).read_bytes()).hexdigest()
                           for name in ('blitzcc.exe', 'runtime.dll', 'linker.dll')},
            'limitations': ['One deterministic program, not one independent comparison per output row.',
                            'CurrentDate and CurrentTime are excluded from deterministic capture.',
                            'Empty Replace search excluded because native source does not terminate.',
                            'Byte ctype observations apply to the captured SDK runtime locale.']}
    with tempfile.TemporaryDirectory(prefix='blitz-strings-') as directory:
        home=Path(directory)/'sdk'
        shutil.copytree(Path(os.environ['BLITZ3D_HOME']),home)
        with (evidence/'xvfb.log').open('w') as log:
            rd,wr=os.pipe()
            xvfb=subprocess.Popen(['Xvfb','-displayfd',str(wr),'-screen','0','800x600x24','-nolisten','tcp'],pass_fds=(wr,),stdout=log,stderr=log)
            os.close(wr)
            try:
                if not select.select([rd],[],[],10)[0]: raise RuntimeError('Xvfb display unavailable')
                display=':'+os.read(rd,64).decode().strip()
                os.environ['BLITZ3D_EVIDENCE_DIR']=str(evidence)
                common=dict(home=home,wine=os.environ['BLITZ3D_WINE'],prefix=Path(os.environ['BLITZ3D_WINEPREFIX']),display=display)
                source=evidence/'strings.bb'
                source.write_text(SOURCE)
                report['source_sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
                report['capture']=capture(source,timeout=30,**common)
                for name,(text,diagnostic) in (CASES.items() if args.invalid_cases else []):
                    source=evidence/(name+'.bb')
                    source.write_text(text)
                    result=capture_case(source,debug=True,evidence_dir=evidence,**common)
                    # Shared graphics helper preserves exact msgbox evidence but only
                    # recognizes its own graphics error. Classify this string diagnostic
                    # from the captured Wine msgbox and absent completion marker.
                    raw=json.loads((evidence/(name+'.json')).read_text())
                    stderr=raw.get('run',{}).get('stderr','')
                    if ('L"'+diagnostic+'"') in stderr and not raw.get('completion_marker'):
                        result['phase']='runtime_error'
                        result['diagnostic']=diagnostic
                    elif raw.get('run',{}).get('exit_code') == 5 and not raw.get('completion_marker'):
                        result['phase']='unclassified_abnormal_exit'
                        result['source_expected_diagnostic']=diagnostic
                    result['source_sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
                    report['cases'][name]=result
            finally:
                os.close(rd)
                xvfb.terminate()
                xvfb.wait(timeout=5)
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,indent=2)+'\n')
    return 0 if report['capture']['phase']=='run' and all(v['phase']=='runtime_error' for v in report['cases'].values()) else 1

if __name__=='__main__': raise SystemExit(main())
