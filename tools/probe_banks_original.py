#!/usr/bin/env python3
"""Capture safe bank semantics from an isolated genuine Blitz3D SDK and Xvfb.

Raw paths/processes stay in evidence-dir; report contains portable captures.
Default probes valid operations only. Optional invalid cases may crash.
Never probes negative sizes, offsets, counts, or userlib arbitrary pointers.
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

SOURCE = '''Local empty=CreateBank()
Print "default-size="+BankSize(empty)
Local zero=CreateBank(0)
Print "zero-size="+BankSize(zero)
Local b=CreateBank(16)
Print "size="+BankSize(b)
Print "zero-init="+PeekInt(b,0)+","+PeekInt(b,12)
PokeByte b,0,-1
PokeByte b,1,258
Print "byte-wrap="+PeekByte(b,0)+","+PeekByte(b,1)
PokeShort b,1,-2
Print "short-unsigned="+PeekShort(b,1)
Print "short-little-endian="+PeekByte(b,1)+","+PeekByte(b,2)
PokeShort b,1,65537
Print "short-wrap="+PeekShort(b,1)
PokeInt b,1,$89abcdef
Print "int-signed="+PeekInt(b,1)
Print "int-little-endian="+PeekByte(b,1)+","+PeekByte(b,2)+","+PeekByte(b,3)+","+PeekByte(b,4)
PokeFloat b,3,1.5
Print "float-roundtrip="+(PeekFloat(b,3)=1.5)
Print "float-bits="+PeekInt(b,3)
PokeInt b,0,123456
ResizeBank b,8
Print "shrink="+BankSize(b)+","+PeekInt(b,0)
ResizeBank b,16
Print "regrow-in-capacity-zero="+PeekInt(b,8)+","+PeekInt(b,12)
ResizeBank b,64
Print "reallocate-preserve="+BankSize(b)+","+PeekInt(b,0)
; Reallocation tail is intentionally not inspected: native source leaves it uninitialized.
Local c=CreateBank(8)
For i=0 To 7
 PokeByte c,i,i+1
Next
CopyBank c,0,c,2,6
Print "overlap-forward="+PeekByte(c,0)+","+PeekByte(c,1)+","+PeekByte(c,2)+","+PeekByte(c,3)+","+PeekByte(c,6)+","+PeekByte(c,7)
CopyBank c,2,c,0,6
Print "overlap-backward="+PeekByte(c,0)+","+PeekByte(c,1)+","+PeekByte(c,4)+","+PeekByte(c,5)
Local d=CreateBank(8)
CopyBank c,0,d,1,6
Print "cross-copy="+PeekByte(d,0)+","+PeekByte(d,1)+","+PeekByte(d,6)+","+PeekByte(d,7)
CopyBank c,0,d,0,0
Print "zero-copy="+PeekByte(d,0)
FreeBank empty
FreeBank zero
FreeBank b
FreeBank c
FreeBank d
FreeBank d
FreeBank 0
FreeBank 123
Print "free-invalid-noop=1"
'''

CASES = {
    'null-size': ('Print BankSize(0)\n', 'bbBank does not exist'),
    'freed-size': ('Local b=CreateBank(4)\nFreeBank b\nPrint BankSize(b)\n', 'bbBank does not exist'),
    'byte-upper-bound': ('Local b=CreateBank(4)\nPrint PeekByte(b,4)\n', 'Offset out of range'),
    'short-upper-bound': ('Local b=CreateBank(4)\nPrint PeekShort(b,3)\n', 'Offset out of range'),
    'int-upper-bound': ('Local b=CreateBank(4)\nPrint PeekInt(b,1)\n', 'Offset out of range'),
    'float-upper-bound': ('Local b=CreateBank(4)\nPrint PeekFloat(b,1)\n', 'Offset out of range'),
}

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--invalid-cases', action='store_true', help='Opt into isolated native invalid-handle/bounds observations; results may vary or crash')
    parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--evidence-dir',type=Path,required=True)
    args=parser.parse_args()
    for key in ('BLITZ3D_HOME','BLITZ3D_WINE','BLITZ3D_WINEPREFIX'):
        if not os.environ.get(key): parser.error('set '+key)
    evidence=args.evidence_dir.resolve()
    evidence.mkdir(parents=True,exist_ok=True)
    report={'runtime':'genuine Blitz3D Win32 under Wine with owned Xvfb','cases':{}}
    with tempfile.TemporaryDirectory(prefix='blitz-banks-') as directory:
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
                source=evidence/'banks.bb'
                source.write_text(SOURCE)
                report['source_sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
                report['capture']=capture(source,timeout=30,**common)
                for name,(text,diagnostic) in (CASES.items() if args.invalid_cases else []):
                    source=evidence/(name+'.bb')
                    source.write_text(text)
                    result=capture_case(source,debug=True,evidence_dir=evidence,**common)
                    # Shared graphics helper preserves exact msgbox evidence but only
                    # recognizes its own graphics error. Classify this bank diagnostic
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
    return 0 if report['capture']['phase']=='run' and all(v['phase'] in ('run','runtime_error','unclassified_abnormal_exit') for v in report['cases'].values()) else 1

if __name__=='__main__': raise SystemExit(main())
