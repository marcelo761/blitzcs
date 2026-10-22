#!/usr/bin/env python3
"""Compare fresh local captures with preserved, source-hashed original evidence; no new oracle run."""
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
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('local_reports', nargs='+', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    baseline = json.loads(args.baseline.read_text())
    originals = {row['name']: row for row in baseline['differential']}
    rows = []
    seen = set()
    for report in args.local_reports:
        for fixture in json.loads(report.read_text())['fixtures']:
            name = fixture['name']
            if name in seen:
                raise ValueError(f'duplicate fixture {name}')
            seen.add(name)
            original = originals[name]
            source = Path(fixture['source'])
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            if digest != original['source_sha256']:
                raise ValueError(f'source hash differs from oracle evidence: {source}')
            status, reason = compare_capture(fixture['actual'], original['original_capture'])
            rows.append({'name': name, 'source': str(source.relative_to(root)), 'source_sha256': digest,
                         'local_report': str(report), 'local_status': fixture['local_status'],
                         'comparison_status': status, 'comparison_reason': reason})
    if seen != set(originals):
        raise ValueError(f'fixture sets differ: {seen.symmetric_difference(originals)}')
    result = {'oracle': str(args.baseline), 'oracle_sha256': hashlib.sha256(args.baseline.read_bytes()).hexdigest(),
              'fresh_original_captures': 0, 'reused_original_captures': len(rows),
              'totals': {key: sum(row['comparison_status'] == key for row in rows)
                         for key in ('match', 'mismatch', 'adapter_error')}, 'fixtures': rows}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result['totals']))
    # The known upper-bound-error exit-code divergence is preserved explicitly.
    unexpected = [row for row in rows if row['local_status'] != 'pass' or row['comparison_status'] == 'adapter_error'
                  or row['comparison_status'] == 'mismatch' and not (
                      row['name'] == 'upper-bound-error' and row['comparison_reason'] == 'success/failure exit status differs')]
    return int(bool(unexpected))


if __name__ == '__main__':
    raise SystemExit(main())
