#!/usr/bin/env python3
"""Verify the owned bank probe against a genuine, recorded Blitz3D execution."""
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
    parser.add_argument('--local-report', type=Path, default=Path('artifacts/m18/local-differential.json'))
    parser.add_argument('--original', type=Path, default=Path('docs/evidence/m18-original.json'))
    parser.add_argument('--report', type=Path, default=Path('artifacts/m18/new-comparison.json'))
    args = parser.parse_args()
    if args.report.resolve() in {args.local_report.resolve(), args.original.resolve()}:
        parser.error('output must not overwrite input evidence')
    original = json.loads(args.original.read_text())
    rows = json.loads(args.local_report.read_text())['fixtures']
    if len(rows) != 1 or rows[0]['name'] != 'm18-banks':
        raise ValueError('expected the single owned bank fixture')
    row = rows[0]
    if hashlib.sha256(Path(row['source']).read_bytes()).hexdigest() != original['source_sha256']:
        raise ValueError('probe source differs from original execution')
    if row['local_status'] != 'pass' or row['actual'].get('timed_out'):
        raise ValueError('local execution failed')
    status, reason = compare_capture(row['actual'], original['capture'])
    result = {'fresh_original_runs_by_this_command': 0, 'match': int(status == 'match'),
              'mismatch': int(status != 'match'), 'source_sha256': original['source_sha256'],
              'status': status, 'reason': reason,
              'scope': 'valid bank operations; phase, stdout and exit success class only'}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
    return int(status != 'match')


if __name__ == '__main__':
    raise SystemExit(main())
