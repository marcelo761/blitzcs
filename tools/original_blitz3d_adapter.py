#!/usr/bin/env python3
"""Capture genuine Blitz3D Print output through its own WriteLine implementation.

Requires BLITZ3D_HOME, BLITZ3D_WINE, BLITZ3D_WINEPREFIX and BLITZ3D_DISPLAY.
See docs/original-adapter.md for provenance, supported syntax and limitations.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time


class AdapterError(Exception):
    """The original result could not be established; never a language result."""


# Strings and semicolon comments are opaque. Blitz identifiers cannot contain
# quotes; Print/End are reserved words. Expressions are copied byte for byte.
TOKENS = re.compile(r'"[^"\r\n]*"|;[^\r\n]*|[A-Za-z_][A-Za-z_0-9]*|[^A-Za-z_";]+')
RUNTIME_DIAGNOSTIC = re.compile(
    r'trace:msgbox:MSGBOX_OnInit L"(Array index out of bounds|Integer divide by zero|'
    r'Memory access violation|Illegal instruction|Stack overflow!|Object does not exist)"')


def instrument(source: str, output: str, completion: str, *, project: bool = False,
               root: bool = True) -> str:
    tokens = list(TOKENS.finditer(source))
    edits = []
    for token in tokens:
        word = token.group().lower()
        if not word.startswith(('"', ';')) and ('/*' in word or '*/' in word):
            raise AdapterError('block comments are outside the supported fixture grammar')
        if not re.fullmatch(r'[a-z_][a-z_0-9]*', word):
            continue
        if word.startswith('blitzcscapture'):
            raise AdapterError('source collides with reserved BlitzCSCapture names')
        if word == 'include' and project:
            continue
        if word in ('include', 'dialect'):
            raise AdapterError(f'{word} is outside the supported standalone classic fixture grammar')
        if word == 'print':
            tail = source[token.end():]
            empty = not tail or re.match(r'[ \t]*(?:[\r\n:;]|Else\b|$)', tail, re.I)
            edits.append((token.start(), token.end(), 'WriteLine BlitzCSCaptureFile,' + (' ""' if empty else '')))
        elif word == 'end':
            following = re.match(r'[ \t]+([a-z]+)', source[token.end():], re.I)
            if following and following[1].lower() in ('function', 'type', 'if', 'select'):
                continue
            edits.append((token.start(), token.end(), 'BlitzCSCaptureFinish():End'))
    for start, end, replacement in reversed(edits):
        source = source[:start] + replacement + source[end:]
    if not root:
        return source
    header = f'Global BlitzCSCaptureFile=WriteFile("{output}")\n'
    footer = ('\nBlitzCSCaptureFinish()\n'
              'Function BlitzCSCaptureFinish()\n'
              'CloseFile BlitzCSCaptureFile\n'
              f'Local BlitzCSCaptureDone=WriteFile("{completion}")\n'
              'WriteLine BlitzCSCaptureDone,"complete"\n'
              'CloseFile BlitzCSCaptureDone\n'
              'End Function\n')
    return header + source + footer


def include_references(source: str) -> list[str]:
    """Read classic quoted Include operands without changing their spelling."""
    tokens = list(TOKENS.finditer(source))
    references = []
    for index, token in enumerate(tokens):
        if token.group().lower() != 'include':
            continue
        following = index + 1
        while following < len(tokens) and not tokens[following].group().strip():
            following += 1
        if following >= len(tokens) or not tokens[following].group().startswith('"'):
            continue  # Let the original compiler produce the syntax diagnostic.
        references.append(tokens[following].group()[1:-1])
    return references


def stage_project(source: Path, destination: Path) -> tuple[Path, dict[str, str]]:
    """Copy only the transitive Include closure, keeping relative path topology.

    Original Blitz3D resolves every Include against the compiler process CWD.
    The adapter uses the entry file's directory as that controlled CWD and
    mirrors all included files beneath it. Lowercased staged names reproduce
    the original compiler's lowercased canonical paths on a case-sensitive host.
    """
    project_root = source.parent.resolve()
    if source.resolve().parent != project_root:
        raise AdapterError('entry source must be directly beneath the project working directory')
    staged: dict[str, str] = {}
    pending = [(source.resolve(), True)]
    original_names: dict[str, Path] = {}
    while pending:
        current, is_root = pending.pop()
        try:
            relative = current.relative_to(project_root)
        except ValueError as exc:
            raise AdapterError(f'Include escapes project working directory: {current}') from exc
        key = relative.as_posix().lower()
        prior = original_names.get(key)
        if prior is not None:
            if prior != current:
                raise AdapterError(f'case-insensitive Include path collision: {prior} and {current}')
            continue
        if not current.is_file():
            if is_root:
                raise AdapterError(f'entry source does not exist: {current}')
            # Leave a missing target absent so blitzcc emits its own diagnostic.
            continue
        original_names[key] = current
        try:
            content = current.read_bytes().decode('cp1252')
        except (OSError, UnicodeError) as exc:
            raise AdapterError(f'cannot read BlitzBasic source {current}: {exc}') from exc
        staged[key] = content
        for operand in include_references(content):
            normalized = operand.replace('\\', '/')
            if normalized.startswith('/') or re.match(r'^[A-Za-z]:', normalized):
                raise AdapterError(f'absolute Include path is outside the staged project: {operand}')
            candidate = (project_root / normalized).resolve()
            if not candidate.exists():
                # Windows resolves path components case-insensitively; recover
                # that behavior for Linux-hosted Wine staging.
                candidate = _case_insensitive_path(project_root, normalized)
            pending.append((candidate, False))

    destination.mkdir(parents=True, exist_ok=True)
    original_by_key = {key: path for key, path in original_names.items()}
    for key, content in staged.items():
        target = destination / key
        target.parent.mkdir(parents=True, exist_ok=True)
        original = original_by_key[key]
        is_root = original == source.resolve()
        transformed = instrument(content, '', '', project=True, root=False)
        if is_root:
            helper = 'BlitzCSCaptureHelper.bb'
            if helper.lower() in original_names:
                raise AdapterError('project collides with reserved capture helper name')
            transformed = ('Include "BlitzCSCaptureHelper.bb"\n'
                           'BlitzCSCaptureDepth=BlitzCSCaptureDepth+1\n' + transformed +
                           '\nBlitzCSCaptureDepth=BlitzCSCaptureDepth-1\n'
                           'If BlitzCSCaptureDepth=0 Then BlitzCSCaptureFinish()\n')
        target.write_bytes(transformed.encode('cp1252'))
    helper_path = destination / 'blitzcscapturehelper.bb'
    helper_path.write_bytes((
        f'Global BlitzCSCaptureFile=WriteFile("{windows_path(destination.parent / "stdout.txt")}")\n'
        'Global BlitzCSCaptureDepth=0\n'
        'Function BlitzCSCaptureFinish()\n'
        'CloseFile BlitzCSCaptureFile\n'
        f'Local BlitzCSCaptureDone=WriteFile("{windows_path(destination.parent / "complete.txt")}")\n'
        'WriteLine BlitzCSCaptureDone,"complete"\n'
        'CloseFile BlitzCSCaptureDone\n'
        'End Function\n').encode('cp1252'))
    root_key = source.name.lower()
    return destination / root_key, {
        key: hashlib.sha256(original_by_key[key].read_bytes()).hexdigest()
        for key in sorted(original_by_key)
    }


def _case_insensitive_path(root: Path, relative: str) -> Path:
    current = root
    for component in Path(relative).parts:
        if component in ('', '.'):
            continue
        if component == '..':
            current = current.parent
            if current != root and root not in current.parents:
                raise AdapterError('Include escapes project working directory')
            continue
        if not current.is_dir():
            return current / component
        matches = [entry for entry in current.iterdir() if entry.name.lower() == component.lower()]
        if len(matches) > 1:
            raise AdapterError(f'ambiguous case-insensitive Include component: {component}')
        current = matches[0] if matches else current / component
    return current.resolve()


def windows_path(path: Path) -> str:
    # A standard Wine prefix maps Unix / to Z:. No shell interpolation.
    return 'Z:' + str(path.resolve()).replace('/', '\\')


def _decode_process_output(value):
    if isinstance(value, bytes):
        return value.decode('cp1252', 'replace')
    return value


def _instrumented_file_evidence(path: Path) -> dict:
    try:
        data = path.read_bytes()
    except FileNotFoundError:
        return {'exists': False}
    return {
        'exists': True,
        'size_bytes': len(data),
        'text': data.decode('cp1252', 'replace'),
        'hex': data.hex(),
    }


def run_process(command, *, env, timeout, cwd=None):
    """Bound processes, including Wine descendants inheriting capture pipes."""
    process = subprocess.Popen(command, env=env, cwd=cwd, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, start_new_session=True)
    deadline = time.monotonic() + timeout
    dismissed = False
    try:
        while True:
            try:
                stdout, stderr = process.communicate(timeout=max(.01, min(.25, deadline - time.monotonic())))
                break
            except subprocess.TimeoutExpired as exc:
                diagnostic = (exc.stderr or b'').decode('cp1252', 'replace')
                if not dismissed and RUNTIME_DIAGNOSTIC.search(diagnostic):
                    # Only the documented runtime error caption on the dedicated
                    # X display is dismissed. Text comes from Wine, never OCR.
                    found = subprocess.run(['xdotool', 'search', '--onlyvisible', '--name', '^Error!$'],
                                           env=env, capture_output=True, text=True, timeout=1)
                    identifiers = found.stdout.split()
                    if len(identifiers) == 1:
                        subprocess.run(['xdotool', 'windowfocus', identifiers[0], 'key', 'Return'],
                                       env=env, capture_output=True, timeout=1)
                        dismissed = True
                if time.monotonic() >= deadline:
                    raise
    except (subprocess.TimeoutExpired, OSError) as exc:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        # A detached Wine service may still own the capture pipe. Never wait
        # without a bound while recovering an infrastructure timeout.
        try:
            process.communicate(timeout=1)
        except subprocess.TimeoutExpired:
            process.stdout.close()
            process.stderr.close()
        raise exc
    return subprocess.CompletedProcess(command, process.returncode,
                                       stdout.decode('cp1252', 'replace'),
                                       stderr.decode('cp1252', 'replace'))


def capture(source: Path, *, home: Path, wine: str, prefix: Path, display: str,
            timeout: float, runner=run_process) -> dict:
    if not math.isfinite(timeout) or timeout <= 0:
        raise AdapterError('timeout must be a positive finite number')
    env = dict(os.environ, WINEPREFIX=str(prefix.resolve()), WINEDEBUG='-all,+msgbox',
               DISPLAY=display, blitzpath=windows_path(home))
    with tempfile.TemporaryDirectory(prefix='blitz-original-capture-') as directory:
        work = Path(directory)
        output, completion, program = (work / n for n in ('stdout.txt', 'complete.txt', 'program.exe'))
        transformed = work / 'input.bb'
        source_text = source.read_bytes().decode('cp1252')
        has_include = any(token.group().lower() == 'include' for token in TOKENS.finditer(source_text))
        working = source.parent.resolve()
        compile_source = transformed
        source_hashes = {source.name: hashlib.sha256(source.read_bytes()).hexdigest()}
        if has_include:
            project_dir = work / 'project'
            compile_source, source_hashes = stage_project(source, project_dir)
            working = project_dir
        evidence = {'source': str(source), 'source_sha256': source_hashes.get(source.name.lower(),
                    hashlib.sha256(source.read_bytes()).hexdigest()),
                    'project_sources_sha256': source_hashes, 'processes': []}
        stage = 'compile'
        command = None
        try:
            if not has_include:
                transformed.write_bytes(instrument(source_text, windows_path(output),
                                                   windows_path(completion)).encode('cp1252'))
            compiler = home / 'bin' / 'blitzcc.exe'
            command = [wine, str(compiler), '+q', '-d', '-o', windows_path(program),
                       windows_path(compile_source)]
            built = runner(command, env=env, timeout=timeout, cwd=working)
            evidence['processes'].append({'stage': stage, 'command': command,
                                          'cwd': str(working), 'exit_code': built.returncode,
                                          'stdout': built.stdout, 'stderr': built.stderr})
            if built.returncode:
                # Source diagnostics use the original compiler's documented
                # "file":row:col:row:col:message format. DLL/startup errors don't.
                if re.search(r'^"[^"\r\n]+":\d+:\d+:\d+:\d+:.+', built.stdout, re.M):
                    return {'phase': 'compile_error', 'stdout': '', 'exit_code': built.returncode}
                raise AdapterError(f'original compiler infrastructure failed: {built.stdout.strip()} {built.stderr.strip()}')
            if not program.is_file():
                raise AdapterError('original compiler returned success without creating an executable')
            stage = 'run'
            command = [wine, str(program)]
            ran = runner(command, env=env, timeout=timeout, cwd=working)
            evidence['processes'].append({'stage': stage, 'command': command,
                                          'cwd': str(working), 'exit_code': ran.returncode,
                                          'stdout': ran.stdout, 'stderr': ran.stderr})
            if RUNTIME_DIAGNOSTIC.search(ran.stderr):
                stdout = output.read_bytes().decode('cp1252').replace('\r\n', '\n').replace('\r', '\n') if output.exists() else ''
                return {'phase': 'runtime_error', 'stdout': stdout, 'exit_code': ran.returncode}
            if ran.returncode or not completion.is_file() or completion.read_bytes() != b'complete\r\n':
                raise AdapterError(f'original process did not produce verified normal completion (exit {ran.returncode}); '
                                   'runtime dialogs/crashes are not inferred as language errors')
            if not output.is_file():
                raise AdapterError('original output capture file was not created')
            stdout = output.read_bytes().decode('cp1252').replace('\r\n', '\n').replace('\r', '\n')
            return {'phase': 'run', 'stdout': stdout, 'exit_code': 0}
        except subprocess.TimeoutExpired as exc:
            stdout = exc.stdout if exc.stdout is not None else exc.output
            evidence['processes'].append({
                'stage': stage,
                'command': command,
                'cwd': str(working),
                'timeout_seconds': timeout,
                'timed_out': True,
                'stdout': _decode_process_output(stdout),
                'stderr': _decode_process_output(exc.stderr),
            })
            raise AdapterError(f'original process timed out after {timeout:g}s; runtime result unavailable') from exc
        finally:
            if os.environ.get('BLITZ3D_EVIDENCE_DIR'):
                # Preserve raw instrumentation artifacts for abnormal exits too.
                # They are evidence only and do not change phase classification.
                evidence['instrumented_stdout_file'] = _instrumented_file_evidence(output)
                evidence['completion_marker_file'] = _instrumented_file_evidence(completion)
                target = Path(os.environ['BLITZ3D_EVIDENCE_DIR'])
                target.mkdir(parents=True, exist_ok=True)
                (target / (source.stem + '.json')).write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    args = parser.parse_args(argv)
    try:
        required = ['BLITZ3D_HOME', 'BLITZ3D_WINE', 'BLITZ3D_WINEPREFIX', 'BLITZ3D_DISPLAY']
        missing = [key for key in required if not os.environ.get(key)]
        if missing:
            raise AdapterError('set ' + ', '.join(missing) + '; see docs/original-adapter.md')
        result = capture(args.source.resolve(), home=Path(os.environ['BLITZ3D_HOME']),
                         wine=os.environ['BLITZ3D_WINE'], prefix=Path(os.environ['BLITZ3D_WINEPREFIX']),
                         display=os.environ['BLITZ3D_DISPLAY'], timeout=float(os.environ.get('BLITZ3D_TIMEOUT', '6')))
        print(json.dumps(result, ensure_ascii=True))
        return 0
    except (AdapterError, OSError, ValueError) as exc:
        print(f'original Blitz3D adapter error: {exc}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
