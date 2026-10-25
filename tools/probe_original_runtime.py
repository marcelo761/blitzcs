#!/usr/bin/env python3
"""Compile and render the unchanged SDK CreateCube example with .NET x86 and the original engine."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import struct
import subprocess
import time


def bitmap_metrics(path):
    data = Path(path).read_bytes()
    if len(data) < 54 or data[:2] != b'BM':
        raise ValueError('Missing BMP header')
    offset = struct.unpack_from('<I', data, 10)[0]
    header, width, height, planes, bits, compression = struct.unpack_from('<IiiHHI', data, 14)
    if header != 40 or not (0 < width <= 8192 and 0 < height <= 8192) or (planes, bits, compression) != (1, 32, 0):
        raise ValueError('Expected uncompressed positive-height 32-bit BMP')
    if len(data) != offset + width * height * 4:
        raise ValueError('Incomplete BMP pixels')
    def pixel(x, y):
        b, g, r = data[offset + ((height - 1 - y) * width + x) * 4:][:3]
        return [r, g, b]
    nonblack = sum(data[i:i+3] != b'\0\0\0' for i in range(offset, len(data), 4))
    return dict(width=width, height=height, center=pixel(width//2, height//2), corner=pixel(0, 0), nonBlackPixels=nonblack)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def windows(path):
    return 'Z:' + str(Path(path).resolve()).replace('/', '\\')


def stop(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)


def native_windows(env):
    result = subprocess.run(['xdotool', 'search', '--name', '^ $'], env=env, capture_output=True, text=True, timeout=5)
    if result.returncode not in (0, 1):
        raise RuntimeError('Cannot inspect the supplied X display: ' + result.stderr)
    return set(result.stdout.splitlines())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ('sdk', 'host', 'proton', 'compatdata', 'steam', 'out'):
        parser.add_argument('--' + argument, type=Path, required=True)
    parser.add_argument('--display', required=True, help='Use an isolated test display')
    parser.add_argument('--gc-regression', action='store_true', help='Also stress real native bindings and verify rendering after GC')
    options = parser.parse_args()
    sdk = options.sdk.resolve()
    source = sdk / 'help/commands/3d_examples/CreateCube.bb'
    runtime = sdk / 'bin/runtime.dll'
    host = options.host.resolve()
    out = options.out.resolve()
    if out == sdk or sdk in out.parents:
        parser.error('Output must be outside the original SDK')
    for path in (source, runtime, host, options.proton):
        if not path.is_file():
            parser.error('Missing input: ' + str(path))
    identities = dict(sourceSHA256=sha(source), runtimeSHA256=sha(runtime))
    out.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, STEAM_COMPAT_DATA_PATH=str(options.compatdata.resolve()),
               STEAM_COMPAT_CLIENT_INSTALL_PATH=str(options.steam.resolve()), SteamAppId='0', SteamGameId='0',
               DISPLAY=options.display, PROTON_USE_WINED3D='1', DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1')
    root = Path(__file__).resolve().parents[1]
    assembly = out / 'CreateCube.dll'
    frame = out / 'CreateCube.bmp'
    if frame.exists():
        frame.unlink()  # A stale image must never turn a failed run into a pass.
    commands = [
        ('compile', ['build', windows(source), '-o', windows(assembly), '--externals', windows(root/'providers/OriginalBlitz3D/runtime.json')]),
        ('execute', ['--run', windows(assembly), windows(runtime), '--capture', windows(frame)])]
    gc_frame = out / 'NativeGC.bmp'
    if options.gc_regression:
        if gc_frame.exists():
            gc_frame.unlink()
        fixture = root / 'tests/fixtures/original-runtime/native-gc-render.bb'
        gc_assembly = out / 'NativeGC.dll'
        commands.extend([
            ('gc-compile', ['build', windows(fixture), '-o', windows(gc_assembly), '--externals', windows(root/'providers/OriginalBlitz3D/runtime.json')]),
            ('gc-execute', ['--run', windows(gc_assembly), windows(runtime), '--capture', windows(gc_frame)])])
    results = []
    try:
        for stage, arguments in commands:
            managed = out / (stage + '-managed.log')
            if managed.exists():
                managed.unlink()
            existing_windows = native_windows(env) if stage == 'execute' else set()
            with (out / (stage + '-proton.log')).open('wb') as log:
                process = subprocess.Popen([str(options.proton.resolve()), 'run', str(host), '--log', windows(managed), *arguments],
                                           cwd=out, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    if stage == 'execute':
                        deadline = time.monotonic() + 30
                        while process.poll() is None and time.monotonic() < deadline:
                            candidates = native_windows(env) - existing_windows
                            if candidates:
                                window = sorted(candidates)[0]
                                time.sleep(3)
                                subprocess.run(['xdotool', 'windowfocus', '--sync', window], env=env, check=True, timeout=5)
                                subprocess.run(['xdotool', 'keydown', 'Escape'], env=env, check=True, timeout=5)
                                try:
                                    time.sleep(.3)
                                finally:
                                    subprocess.run(['xdotool', 'keyup', 'Escape'], env=env, check=True, timeout=5)
                                break
                            time.sleep(.2)
                        else:
                            raise RuntimeError('Original Blitz3D window was not observed')
                    code = process.wait(timeout=60)
                finally:
                    stop(process)
            output = managed.read_text(errors='replace') if managed.exists() else ''
            results.append(dict(stage=stage, exitCode=code, managedLog=managed.name))
            if code or 'X86' not in output or '.NET 10.' not in output:
                raise RuntimeError(stage + ' did not complete in .NET 10 x86: ' + output)
            if stage.endswith('execute') and 'Original Blitz3D runtime:' not in output:
                raise RuntimeError('Original engine initialization was not confirmed')
            if stage == 'gc-execute' and 'PASS original-runtime native ABI regression' not in output:
                raise RuntimeError('Native ABI regression did not reach its completion marker')
        metrics = bitmap_metrics(frame)
        if metrics['nonBlackPixels'] == 0 or metrics['center'] == metrics['corner']:
            raise RuntimeError('No cube rendering established: ' + json.dumps(metrics))
        if identities != dict(sourceSHA256=sha(source), runtimeSHA256=sha(runtime)):
            raise RuntimeError('Original input integrity check failed')
        report = dict(success=True, architecture='x86', compiler='BlitzCS', engine='original runtime.dll',
                      sourceUnchanged=True, runtimeUnchanged=True, stages=results, frame=metrics, **identities)
        if options.gc_regression:
            gc_metrics = bitmap_metrics(gc_frame)
            if gc_metrics['nonBlackPixels'] == 0 or gc_metrics['center'] == gc_metrics['corner']:
                raise RuntimeError('Native ABI regression did not establish rendering: ' + json.dumps(gc_metrics))
            report['nativeGCFrame'] = gc_metrics
    except Exception as error:
        report = dict(success=False, error=str(error), stages=results, **identities)
        raise
    finally:
        if 'report' in locals():
            (out/'result.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
