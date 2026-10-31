#!/usr/bin/env python3
"""Bounded original Blitz3D input probe on an owned Xvfb display.

Requires BLITZ3D_HOME, BLITZ3D_WINE, BLITZ3D_WINEPREFIX; optional XVFB.
The compiler runs against a temporary SDK copy; input uses xdotool/XTest.
"""
from __future__ import annotations
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import select
import shutil
import subprocess
import tempfile
import time
try:
    from tools.original_blitz3d_adapter import windows_path
except ModuleNotFoundError:
    from original_blitz3d_adapter import windows_path

KEYPAD = [('KP_Enter',156), ('KP_Divide',181), ('KP_Multiply',55),
          ('KP_Add',78), ('KP_Subtract',74), ('KP_1',79), ('KP_2',80),
          ('KP_3',81), ('KP_4',75), ('KP_5',76), ('KP_6',77), ('KP_7',71),
          ('KP_8',72), ('KP_9',73), ('KP_0',82), ('KP_Decimal',83)]


def comparison_mismatches(expected, actual):
    return {key: {'expected': value, 'actual': actual.get(key)}
            for key, value in expected.items() if actual.get(key) != value}


def keyboard_led_mask(display):
    class State(ctypes.Structure):
        _fields_ = [('key_click_percent', ctypes.c_int), ('bell_percent', ctypes.c_int),
                    ('bell_pitch', ctypes.c_uint), ('bell_duration', ctypes.c_uint),
                    ('led_mask', ctypes.c_ulong), ('global_auto_repeat', ctypes.c_int),
                    ('auto_repeats', ctypes.c_char * 32)]
    x11 = ctypes.CDLL('libX11.so.6')
    x11.XOpenDisplay.argtypes, x11.XOpenDisplay.restype = [ctypes.c_char_p], ctypes.c_void_p
    x11.XGetKeyboardControl.argtypes = [ctypes.c_void_p, ctypes.POINTER(State)]
    x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
    connection = x11.XOpenDisplay(display.encode())
    if not connection:
        raise RuntimeError('cannot read owned X display keyboard state')
    try:
        state = State()
        x11.XGetKeyboardControl(connection, ctypes.byref(state))
        return f'{state.led_mask:08x}'
    finally:
        x11.XCloseDisplay(connection)


def validate_outputs(report, evidence, protected, sdk=None):
    report, evidence = report.resolve(), evidence.resolve()
    for path in protected:
        path = path.resolve()
        if report == path or (report.exists() and path.exists() and report.samefile(path)):
            raise ValueError('report must not overwrite an input or reference file')
        if evidence == path:
            raise ValueError('evidence directory must not overwrite an input file')
    if sdk and (report.is_relative_to(sdk.resolve()) or evidence.is_relative_to(sdk.resolve())):
        raise ValueError('outputs must remain outside the original SDK')


def managed_fixture(source):
    """Preserve input expressions; replace only bootstrap and stage-file I/O."""
    body = source.split('\nFunction Snapshot(', 1)[0]
    body = body.replace('AppTitle "BlitzCS Original Input Probe"\nGraphics 320,240,0,2',
                        'Blitz3DCSOpenWindow 320,240,"BlitzCS Original Input Probe"')
    lines = []
    for line in body.splitlines():
        if line.startswith('Snapshot '):
            line = 'CS::ProbeIO.Snapshot('+line[len('Snapshot '):]+')'
        elif line.startswith('WaitSignal '):
            line = 'CS::ProbeIO.WaitSignal('+line[len('WaitSignal '):]+')'
        elif line == 'End':
            line = 'Blitz3DCSCloseWindow\nEnd'
        lines.append(line)
    return '\n'.join(lines)+'''\n<?cs module
public static class ProbeIO {
 public static int Snapshot(string stage, string value) {
  System.IO.File.WriteAllText(stage+".txt",value+"\\n"); return 0;
 }
 public static int Snapshot(string stage, int value) => Snapshot(stage,value.ToString());
 public static int WaitSignal(string stage) {
  Snapshot(stage+"-ready","ready");
  var deadline=System.Diagnostics.Stopwatch.StartNew();
  while (!System.IO.File.Exists(stage+".go")) {
   if (deadline.Elapsed.TotalSeconds>15) throw new System.TimeoutException(stage);
   Blitz3DCS.Windowing.LegacyInput.PumpEvents();
   System.Threading.Thread.Sleep(5);
  }
  return 0;
 }
}
?>
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--managed-cli', type=Path,
                        help='run the same input expressions through this BlitzCS CLI instead')
    parser.add_argument('--managed-externals', type=Path,
                        default=Path(__file__).resolve().parents[1]/'samples/LegacyInput/runtime.json')
    parser.add_argument('--reference', type=Path,
                        default=Path(__file__).resolve().parents[1]/'docs/evidence/m20-original-input.json')
    parser.add_argument('--keypad', action='store_true', help='add one independently flushed stage per keypad key')
    parser.add_argument('--num-lock', action='store_true', help='toggle NumLock on the fresh owned display')
    args = parser.parse_args()
    for name in (() if args.managed_cli else ('BLITZ3D_HOME', 'BLITZ3D_WINE', 'BLITZ3D_WINEPREFIX')):
        if not os.environ.get(name):
            parser.error('set '+name)
    evidence = args.evidence_dir.resolve()
    fixture = Path(__file__).resolve().parents[1]/'samples/LegacyInput/reference-input.bb'
    try:
        validate_outputs(args.report, evidence,
                         [fixture, args.reference, args.managed_externals, Path(__file__)]+
                         ([args.managed_cli] if args.managed_cli else []),
                         None if args.managed_cli else Path(os.environ['BLITZ3D_HOME']))
    except ValueError as error:
        parser.error(str(error))
    evidence.mkdir(parents=True, exist_ok=True)
    fixture_text = fixture.read_text()
    if args.keypad:
        extra = ''
        for key, scan in KEYPAD:
            values = '+","+'.join([f'KeyHit({scan})']+['GetKey()']*8)
            extra += f'WaitSignal "{key}"\nSnapshot "{key}",{values}\nFlushKeys\n'
        fixture_text = fixture_text.replace('WaitSignal "modifier-held"', extra+'WaitSignal "modifier-held"')
    report = {'runtime': 'original Blitz3D 1.118 Win32 under Wine/Proton',
              'fixture_sha256': hashlib.sha256(fixture.read_bytes()).hexdigest(),
              'events': [], 'observations': {}}
    if args.managed_cli:
        report['runtime'] = 'BlitzCS managed input with real SDL X11 window'
        report['reference_sha256'] = hashlib.sha256(args.reference.read_bytes()).hexdigest()
    else:
        report['sdk_sha256'] = {name: hashlib.sha256((Path(os.environ['BLITZ3D_HOME'])/'bin'/name).read_bytes()).hexdigest()
                                for name in ('blitzcc.exe','runtime.dll','linker.dll')}
    expected = {'warp': '37,53,0,0,0,0', 'motion': '73,91,36,38,0,0',
                'mouse-held': '1,1,0,1,0,0', 'mouse-three': '0,3,0,1,1,1,0',
                'key-held': '1,1,0,1,0,0', 'key-three': '0,3,0,97,97,97,0',
                'arrows': '1,2,3,4,5,6,28,29,30,31', 'wait-key': '98',
                'modifier-held': '65,0,1', 'modifier-released': '97,0',
                'repeat-held': '1,1,97,0', 'focus-lost': '1,73,91',
                'focus-gained': '0,0,73,91',
                'wheel': '2,2,0,2', 'done': 'complete'}
    if args.managed_cli:
        expected = json.loads(args.reference.read_text())['observations']
    process = xvfb = None
    status = 2
    with tempfile.TemporaryDirectory(prefix='blitz-input-original-') as directory:
        work = Path(directory)
        home = work/'sdk'
        if not args.managed_cli:
            shutil.copytree(Path(os.environ['BLITZ3D_HOME']), home)
        source = work/'input.bb'
        if args.managed_cli:
            source.write_text(managed_fixture(fixture_text))
            env = dict(os.environ, SDL_VIDEODRIVER='x11')
            report['managed_fixture_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
            shutil.copyfile(source, evidence/'managed-input.bb')
        else:
            source.write_text(fixture_text)
            if args.keypad:
                report['executed_fixture_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
                shutil.copyfile(source, evidence/'original-keypad-input.bb')
            env = dict(os.environ, WINEPREFIX=str(Path(os.environ['BLITZ3D_WINEPREFIX']).resolve()),
                       WINEDEBUG='-all', blitzpath=windows_path(home))
        with (evidence/'xvfb.log').open('w') as log, (evidence/'run.log').open('w') as runlog:
            read_fd, write_fd = os.pipe()
            try:
                xvfb = subprocess.Popen([os.environ.get('XVFB', 'Xvfb'), '-displayfd', str(write_fd),
                    '-screen', '0', '800x600x24', '-nolisten', 'tcp'], pass_fds=(write_fd,), stdout=log, stderr=log)
                os.close(write_fd)
                if not select.select([read_fd], [], [], 10)[0]:
                    raise RuntimeError('Xvfb display allocation timed out')
                env['DISPLAY'] = ':'+os.read(read_fd,64).decode().strip()
                os.close(read_fd)
                program = work/('program.dll' if args.managed_cli else 'program.exe')
                command = (['dotnet',str(args.managed_cli.resolve()),'build',str(source),'-o',str(program),
                            '--externals',str(args.managed_externals.resolve())] if args.managed_cli else
                           [env['BLITZ3D_WINE'], str(home/'bin/blitzcc.exe'), '+q', '-o',
                            windows_path(program), windows_path(source)])
                built = subprocess.run(command, cwd=work, env=env, text=True, capture_output=True, timeout=40)
                (evidence/'compile.log').write_text(built.stdout+built.stderr)
                if built.returncode or not program.exists():
                    raise RuntimeError('compiler failed; see compile.log')
                process = subprocess.Popen(['dotnet' if args.managed_cli else env['BLITZ3D_WINE'], str(program)], cwd=work,
                                           env=env, stdout=runlog, stderr=runlog)

                def wait_file(name):
                    deadline = time.monotonic()+15
                    path = work/(name+'.txt')
                    while not path.exists() or path.stat().st_size == 0:
                        if time.monotonic()>deadline or process.poll() is not None:
                            raise RuntimeError('missing original stage '+name)
                        time.sleep(.025)
                    value = path.read_text().strip()
                    shutil.copyfile(path, evidence/path.name)
                    return value

                def xtest(*words):
                    subprocess.run(['xdotool', *map(str,words)], env=env, check=True,
                                   capture_output=True, text=True, timeout=5)
                    report['events'].append(list(map(str,words)))

                wait_file('warp-ready')
                found = subprocess.run(['xdotool','search','--onlyvisible','--name','^BlitzCS Original Input Probe$'],
                    env=env, check=True, capture_output=True, text=True, timeout=5).stdout.split()
                if len(found)!=1:
                    raise RuntimeError('expected one owned original window')
                window = found[0]
                root_info = subprocess.run(['xwininfo','-root'], env=env, check=True,
                    capture_output=True, text=True, timeout=5).stdout
                root_window = re.search(r'Window id: (0x[0-9a-f]+)', root_info)[1]
                xtest('windowfocus', '--sync', window)
                if args.keypad:
                    report['keyboard_led_mask_before'] = keyboard_led_mask(env['DISPLAY'])
                if args.num_lock:
                    xtest('key','Num_Lock')
                if args.keypad:
                    report['keyboard_led_mask_after'] = keyboard_led_mask(env['DISPLAY'])

                def stage(name, action=lambda: None):
                    wait_file(name+'-ready')
                    action()
                    time.sleep(.1)
                    (work/(name+'.go')).touch()
                    report['observations'][name] = wait_file(name)

                stage('warp')
                stage('motion', lambda: xtest('mousemove','--window',window,73,91))
                stage('mouse-held', lambda: xtest('mousedown',1))
                stage('mouse-three', lambda: xtest('mouseup',1,'click','--repeat',3,'--delay',80,1))
                stage('key-held', lambda: xtest('keydown','a'))
                stage('key-three', lambda: xtest('keyup','a','key','--repeat',3,'--delay',80,'a'))
                stage('arrows', lambda: xtest('key','--delay',50,'Home','End','Insert','Delete','Prior','Next','Up','Down','Right','Left'))
                if args.keypad:
                    for key, _ in KEYPAD:
                        stage(key, lambda key=key: xtest('key',key))
                stage('modifier-held', lambda: xtest('key','a','keydown','Shift_L'))
                stage('modifier-released', lambda: xtest('key','a','keyup','Shift_L'))
                def hold_repeat():
                    xtest('keydown','a')
                    time.sleep(1.2)
                stage('repeat-held', hold_repeat)
                # Focusing the owned display root creates real focus loss while
                # mode 2 keeps executing. Release after regaining focus.
                stage('focus-lost', lambda: xtest('windowfocus','--sync',root_window))
                stage('focus-gained', lambda: xtest('windowfocus','--sync',window,'keyup','a'))
                wait_file('wait-ready')
                xtest('key','F1')
                time.sleep(.1)
                xtest('key','b')
                report['observations']['wait-key'] = wait_file('wait-key')
                stage('wheel', lambda: xtest('click','--repeat',2,'--delay',80,4))
                report['observations']['done'] = wait_file('done')
                process.wait(timeout=10)
                report['exit_code'] = process.returncode
                report['mismatches'] = comparison_mismatches(expected, report['observations'])
                status = int(bool(report['mismatches']) or process.returncode!=0)
            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                report['infrastructure_error'] = str(error)
            finally:
                if process and process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                if xvfb:
                    xvfb.terminate()
                    xvfb.wait(timeout=5)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2)+'\n')
    print(args.report.resolve())
    return status


if __name__ == '__main__':
    raise SystemExit(main())
