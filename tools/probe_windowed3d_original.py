#!/usr/bin/env python3
"""Probe genuine Windowed3D capability and a guarded graphics lifecycle.

Set BLITZ3D_HOME, BLITZ3D_WINE and BLITZ3D_WINEPREFIX. Uses a temporary SDK
copy and a dedicated Xvfb display; raw process evidence stays in evidence-dir.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
import select
import re
import threading
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
try:
    from tools.original_blitz3d_adapter import AdapterError, capture, instrument, windows_path, run_process
except ModuleNotFoundError:
    from original_blitz3d_adapter import AdapterError, capture, instrument, windows_path, run_process

SOURCE = '''Local supported=Windowed3D()
Print "windowed3d="+supported
Print "windowed3d-repeat="+(Windowed3D()=supported)
If supported Then
 Graphics3D 160,120,0,2
 Print "graphics3d-size="+GraphicsWidth()+"x"+GraphicsHeight()
 Print "graphics3d-backbuffer="+(GraphicsBuffer()=BackBuffer())
 Flip 0
 Print "flip-zero-completed=1"
 Flip
 Print "flip-default-completed=1"
 EndGraphics
 Print "endgraphics-size="+GraphicsWidth()+"x"+GraphicsHeight()
 Print "endgraphics-frontbuffer="+(GraphicsBuffer()=FrontBuffer())
Else
 Print "graphics3d-lifecycle=skipped-capability-unavailable"
EndIf
Print "windowed3d-after="+Windowed3D()
'''


CASES = {
    'windowed3d-argument': ('Print Windowed3D(1)\n', 'compile_error', True),
    'debuglog-expression': ('Local result=DebugLog("probe")\n', 'compile_error', True),
    'graphics3d-invalid-mode': ('Print "before-invalid-mode"\nGraphics3D 160,120,0,4\nPrint "unexpected-after"\n', 'runtime_error', True),
    'debuglog-release': ('Print "before-debuglog"\nDebugLog "m17 original DebugLog statement"\nPrint "after-debuglog"\n', 'run', False),
}


def lifecycle_errors(capture_result):
    values = dict(line.split('=', 1) for line in capture_result.get('stdout', '').splitlines() if '=' in line)
    expected = {'windowed3d-repeat': '1'}
    capability = values.get('windowed3d')
    if capability not in ('0', '1'):
        return ['missing or invalid capability result']
    expected['windowed3d-after'] = capability
    if capability == '1':
        expected.update({'graphics3d-size': '160x120', 'graphics3d-backbuffer': '1',
                         'flip-zero-completed': '1', 'flip-default-completed': '1',
                         'endgraphics-size': '400x300', 'endgraphics-frontbuffer': '1'})
    else:
        expected['graphics3d-lifecycle'] = 'skipped-capability-unavailable'
    return [key for key, value in expected.items() if values.get(key) != value]


def capture_case(source, *, home, wine, prefix, display, debug, evidence_dir):
    """Classify diagnostics and completion markers independently of legacy exits."""
    env = dict(os.environ, WINEPREFIX=str(prefix.resolve()), WINEDEBUG='-all,+msgbox',
               DISPLAY=display, blitzpath=windows_path(home))
    evidence = {'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'debug': debug}
    with tempfile.TemporaryDirectory(prefix='blitz-windowed3d-case-') as directory:
        work = Path(directory)
        output, done, program, transformed = [work / name for name in ('stdout.txt', 'complete.txt', 'program.exe', 'input.bb')]
        transformed.write_bytes(instrument(source.read_text(), windows_path(output), windows_path(done)).encode('cp1252'))
        command = [wine, str(home / 'bin/blitzcc.exe'), '+q'] + (['-d'] if debug else []) + ['-o', windows_path(program), windows_path(transformed)]
        built = run_process(command, env=env, timeout=30, cwd=work)
        evidence['compile'] = {'command': command, 'exit_code': built.returncode, 'stdout': built.stdout, 'stderr': built.stderr}
        diagnostic = re.search(r'^"[^"\r\n]+":\d+:\d+:\d+:\d+:(.+)', built.stdout, re.M)
        if diagnostic:
            result = {'phase': 'compile_error', 'diagnostic': diagnostic[1].strip(), 'exit_code': built.returncode}
        elif built.returncode or not program.is_file():
            result = {'phase': 'infrastructure_error', 'diagnostic': 'compiler did not create executable'}
        else:
            # Only dismiss the known Error! dialog on this owned display.
            stop = threading.Event()
            def dismiss():
                while not stop.wait(.2):
                    found = subprocess.run(['xdotool', 'search', '--onlyvisible', '--name', '^Error!$'], env=env, capture_output=True, text=True, timeout=2)
                    ids = found.stdout.split()
                    if len(ids) == 1:
                        subprocess.run(['xdotool', 'windowfocus', ids[0], 'key', 'Return'], env=env, capture_output=True, timeout=2)
            watcher = threading.Thread(target=dismiss, daemon=True)
            watcher.start()
            try:
                ran = run_process([wine, str(program)], env=env, timeout=30, cwd=work)
            finally:
                stop.set()
                watcher.join(timeout=3)
            evidence['run'] = {'command': [wine, str(program)], 'exit_code': ran.returncode, 'stdout': ran.stdout, 'stderr': ran.stderr}
            stdout = output.read_text().replace('\r\n', '\n') if output.exists() else ''
            completed = done.exists() and done.read_bytes() == b'complete\r\n'
            evidence['completion_marker'] = completed
            if re.search(r'trace:msgbox:MSGBOX_OnInit L"Illegal Graphics3D mode"', ran.stderr) and not completed and 'unexpected-after' not in stdout:
                result = {'phase': 'runtime_error', 'diagnostic': 'Illegal Graphics3D mode', 'exit_code': ran.returncode, 'stdout': stdout}
            elif completed and ran.returncode == 0:
                result = {'phase': 'run', 'exit_code': 0, 'stdout': stdout}
            else:
                result = {'phase': 'infrastructure_error', 'exit_code': ran.returncode, 'stdout': stdout}
        (evidence_dir / (source.stem+'.json')).write_text(json.dumps(evidence, indent=2)+'\n')
        return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--extended-only', action='store_true')
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    args = parser.parse_args()
    for key in ('BLITZ3D_HOME', 'BLITZ3D_WINE', 'BLITZ3D_WINEPREFIX'):
        if not os.environ.get(key):
            parser.error('set '+key)
    args.evidence_dir.mkdir(parents=True, exist_ok=True)
    source = args.evidence_dir.resolve() / 'windowed3d.bb'
    source.write_bytes(SOURCE.encode('cp1252'))
    report = {'runtime': 'original Blitz3D Win32 under Wine/Proton with Xvfb',
              'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest()}
    status = 2
    with tempfile.TemporaryDirectory(prefix='blitz-windowed3d-') as directory:
        work = Path(directory)
        home = work / 'sdk'
        shutil.copytree(Path(os.environ['BLITZ3D_HOME']), home)
        with (args.evidence_dir / 'xvfb.log').open('w') as log:
            read_fd, write_fd = os.pipe()
            xvfb = subprocess.Popen(['Xvfb', '-displayfd', str(write_fd), '-screen', '0', '800x600x24', '-nolisten', 'tcp'], pass_fds=(write_fd,), stdout=log, stderr=log)
            os.close(write_fd)
            try:
                if not select.select([read_fd], [], [], 10)[0]:
                    raise AdapterError('Xvfb did not allocate a display')
                display = ':'+os.read(read_fd, 64).decode().strip()
                time.sleep(.3)
                os.environ['BLITZ3D_EVIDENCE_DIR'] = str(args.evidence_dir.resolve())
                status = 0
                if not args.extended_only:
                    report['capture'] = capture(source, home=home, wine=os.environ['BLITZ3D_WINE'], prefix=Path(os.environ['BLITZ3D_WINEPREFIX']), display=display, timeout=30)
                    status = 0 if report['capture']['phase'] == 'run' else 1
                    report['lifecycle_errors'] = lifecycle_errors(report['capture'])
                    if report['lifecycle_errors']:
                        status = 1
                report['cases'] = {}
                for name, (text, expected, debug) in CASES.items():
                    case_source = args.evidence_dir.resolve() / (name+'.bb')
                    case_source.write_text(text)
                    result = capture_case(case_source, home=home, wine=os.environ['BLITZ3D_WINE'], prefix=Path(os.environ['BLITZ3D_WINEPREFIX']), display=display, debug=debug, evidence_dir=args.evidence_dir)
                    result['source_sha256'] = hashlib.sha256(case_source.read_bytes()).hexdigest()
                    report['cases'][name] = result
                    expected_diagnostic = {'windowed3d-argument': 'Too many parameters',
                                           'debuglog-expression': 'Illegal type conversion',
                                           'graphics3d-invalid-mode': 'Illegal Graphics3D mode'}.get(name)
                    if result['phase'] != expected or (expected_diagnostic and result.get('diagnostic') != expected_diagnostic):
                        status = 1
                    if name == 'debuglog-release' and result.get('stdout') != 'before-debuglog\nafter-debuglog\n':
                        status = 1
            except (AdapterError, OSError, ValueError, subprocess.TimeoutExpired) as error:
                report['adapter_error'] = str(error)
                status = 2
            finally:
                os.close(read_fd)
                xvfb.terminate()
                xvfb.wait(timeout=5)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2)+'\n')
    print(args.report.resolve())
    return status

if __name__ == '__main__':
    raise SystemExit(main())
