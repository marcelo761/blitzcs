#!/usr/bin/env python3
"""Compare owned M19 programs with recorded genuine captures; never run the original."""
import argparse
import hashlib
import json
import tempfile
from pathlib import Path

try:
    from tools.differential import compare_capture
except ModuleNotFoundError:
    from differential import compare_capture

ERRORS = {
    'left-negative': 'parameter must be positive',
    'right-negative': 'parameter must be positive',
    'lset-negative': 'parameter must be positive',
    'rset-negative': 'parameter must be positive',
    'mid-zero': 'parameter must be greater than 0',
    'instr-zero': 'parameter must be greater than 0',
}


def compare_reports(local, original):
    rows = local['fixtures']
    names = ['m19-strings'] + ['m19-' + name for name in ERRORS]
    if len(rows) != len(names) or {row['name'] for row in rows} != set(names):
        raise ValueError('expected exactly the seven owned M19 fixtures')
    if set(original['cases']) != set(ERRORS):
        raise ValueError('original negative fixture set differs')
    results = []
    for row in rows:
        name = row['name'].removeprefix('m19-')
        positive = name == 'strings'
        oracle = original['capture'] if positive else original['cases'][name]
        digest = original['source_sha256'] if positive else oracle['source_sha256']
        if hashlib.sha256(Path(row['source']).read_bytes()).hexdigest() != digest:
            raise ValueError('source differs from original capture: ' + name)
        actual = row['actual']
        if actual.get('timed_out') or oracle.get('timed_out'):
            raise ValueError('timeout is not a comparable observation: ' + name)
        if row['local_status'] != 'pass':
            raise ValueError('local harness failed: ' + name)
        if positive:
            if oracle['phase'] != 'run' or oracle['exit_code'] != 0:
                raise ValueError('original deterministic capture did not complete')
            status, reason = compare_capture(actual, oracle)
        else:
            diagnostic = ERRORS[name]
            if (oracle['phase'] != 'runtime_error' or oracle['exit_code'] != 0
                    or oracle.get('stdout', '') != '' or oracle.get('diagnostic') != diagnostic):
                raise ValueError('original error has the wrong cause or termination: ' + name)
            if (actual['phase'] != 'runtime_error' or type(actual['exit_code']) is not int
                    or actual['exit_code'] == 0 or actual.get('stdout') != ''
                    or diagnostic not in actual.get('stderr', '')):
                raise ValueError('local runtime error has the wrong cause or termination: ' + name)
            status = 'known_termination_divergence'
            reason = 'phase and diagnostic agree; original exits 0 after dialog, managed runtime exits nonzero'
        results.append({'name': name, 'source_sha256': digest, 'status': status, 'reason': reason})
    strict = sum(row['status'] == 'match' for row in results)
    known = sum(row['status'] == 'known_termination_divergence' for row in results)
    unexpected = sum(row['status'] not in ('match', 'known_termination_divergence') for row in results)
    return {'fresh_original_runs_by_this_command': 0,
            'deterministic_capture_matches': strict == 1,
            'source_sha256': original['source_sha256'], 'strict_match': strict,
            'known_termination_divergence': known, 'phase_diagnostic_agreement': known,
            'unexpected_mismatch': unexpected, 'fixtures': results,
            'scope': 'One deterministic program and six errors. Termination divergences are explicitly excluded from strict matches.'}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--local-report', type=Path, default=Path('artifacts/m19/local-differential.json'))
    parser.add_argument('--original', type=Path, default=Path('docs/evidence/m19-original.json'))
    parser.add_argument('--report', type=Path, default=Path('artifacts/m19/new-comparison.json'))
    args = parser.parse_args(argv)
    local = json.loads(args.local_report.read_text())
    protected = {args.local_report.resolve(), args.original.resolve(), Path(__file__).resolve()}
    protected.update(Path(row['source']).resolve() for row in local['fixtures'])
    same_input = args.report.exists() and any(path.exists() and args.report.samefile(path) for path in protected)
    if args.report.resolve() in protected or same_input:
        parser.error('output must not overwrite input evidence, source fixtures or the comparison tool')
    result = compare_reports(local, json.loads(args.original.read_text()))
    args.report.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=args.report.parent,
                                         prefix='.m19-comparison-', suffix='.tmp', delete=False) as output:
            temporary = Path(output.name)
            output.write(json.dumps(result, indent=2) + '\n')
        temporary.replace(args.report)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    print(json.dumps({key: result[key] for key in ('strict_match', 'known_termination_divergence', 'unexpected_mismatch')}))
    return int(result['strict_match'] != 1 or result['known_termination_divergence'] != 6 or bool(result['unexpected_mismatch']))


if __name__ == '__main__':
    raise SystemExit(main())
