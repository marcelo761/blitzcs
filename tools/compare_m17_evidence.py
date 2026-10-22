#!/usr/bin/env python3
"""Compare three bounded local cases with genuine M17 captures, not a new original run."""
import argparse
import hashlib
import json
from pathlib import Path

try:
    from tools.differential import compare_capture
except ModuleNotFoundError:
    from differential import compare_capture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--local-report', type=Path, default=Path('artifacts/m17/local-differential.json'))
    parser.add_argument('--original', type=Path, default=Path('docs/evidence/m17-original.json'))
    parser.add_argument('--report', type=Path, default=Path('artifacts/m17/new-comparison.json'))
    args = parser.parse_args()
    if args.report.resolve() in {args.local_report.resolve(), args.original.resolve()}:
        parser.error('comparison output must not overwrite either input')
    root = Path(__file__).resolve().parents[1]
    original = json.loads(args.original.read_text())['cases']
    local = {row['name'].removeprefix('m17-'): row for row in json.loads(args.local_report.read_text())['fixtures']}
    expected = {'windowed3d-argument': 'accepts at most 0 arguments',
                'debuglog-expression': 'Void external commands', 'debuglog-release': None}
    if set(local) != set(expected):
        raise ValueError('local fixture set differs from the three comparable original cases')
    comparisons = []
    for name, diagnostic in expected.items():
        row, oracle = local[name], original[name]
        source = Path(row['source'])
        if hashlib.sha256(source.read_bytes()).hexdigest() != oracle['source_sha256']:
            raise ValueError('source differs from original capture: ' + source.name)
        actual = row['actual']
        if row['local_status'] != 'pass' or actual.get('timed_out'):
            raise ValueError('local execution failed or timed out: ' + name)
        if diagnostic and diagnostic not in actual['stderr']:
            raise ValueError('local compile error has the wrong cause: ' + name)
        status, reason = compare_capture(actual, {'phase': oracle['phase'],
                                                 'stdout': oracle.get('stdout', ''), 'exit_code': oracle['exit_code']})
        comparisons.append({'name': name, 'source': str(source.relative_to(root)),
                            'source_sha256': oracle['source_sha256'], 'status': status, 'reason': reason,
                            'scope': 'phase and exit success class; stdout for successful execution; compile diagnostic cause checked separately'})
    result = {'fresh_original_runs_by_this_command': 0,
              'original_capture': str(args.original), 'fixtures': comparisons,
              'match': sum(x['status'] == 'match' for x in comparisons),
              'mismatch': sum(x['status'] != 'match' for x in comparisons),
              'graphics_lifecycle_comparison': 'pending: no managed graphics backend'}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'match': result['match'], 'mismatch': result['mismatch']}))
    return int(bool(result['mismatch']))


if __name__ == '__main__':
    raise SystemExit(main())
