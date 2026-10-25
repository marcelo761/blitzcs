"""Evidence validation tests; synthetic local errors exercise comparison mechanics only."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from tools.compare_m19_evidence import ERRORS, compare_reports, main

ROOT = Path(__file__).resolve().parents[2]


class M19EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.original = json.loads((ROOT / 'docs/evidence/m19-original.json').read_text())
        manifest = json.loads((ROOT / 'tests/Compatibility/milestone19.json').read_text())
        self.local = {'fixtures': []}
        for entry in manifest['fixtures']:
            name = entry['name'].removeprefix('m19-')
            actual = copy.deepcopy(self.original['capture']) if name == 'strings' else {
                'phase': 'runtime_error', 'stdout': '', 'stderr': 'Runtime error: ' + ERRORS[name], 'exit_code': 1}
            self.local['fixtures'].append({'name': entry['name'], 'source': str((ROOT / 'tests/Compatibility' / entry['source']).resolve()),
                                           'local_status': 'pass', 'actual': actual})

    def test_exact_capture_and_six_explicit_termination_divergences(self):
        result = compare_reports(self.local, self.original)
        self.assertTrue(result['deterministic_capture_matches'])
        self.assertEqual((1, 6, 6, 0), tuple(result[k] for k in ('strict_match', 'known_termination_divergence', 'phase_diagnostic_agreement', 'unexpected_mismatch')))
        for row in self.local['fixtures']:
            name = row['name'].removeprefix('m19-')
            expected = self.original['source_sha256'] if name == 'strings' else self.original['cases'][name]['source_sha256']
            self.assertEqual(expected, hashlib.sha256(Path(row['source']).read_bytes()).hexdigest())

    def test_positive_output_mismatch_is_not_waived(self):
        self.local['fixtures'][0]['actual']['stdout'] += 'wrong\n'
        result = compare_reports(self.local, self.original)
        self.assertFalse(result['deterministic_capture_matches'])
        self.assertEqual(1, result['unexpected_mismatch'])
        self.assertEqual(0, result['strict_match'])

    def test_wrong_diagnostic_phase_stdout_or_exit_rejected(self):
        for field, value in [('stderr', 'another failure'), ('phase', 'compile_error'), ('stdout', 'unexpected'), ('exit_code', 0)]:
            with self.subTest(field=field):
                local = copy.deepcopy(self.local)
                local['fixtures'][1]['actual'][field] = value
                with self.assertRaises(ValueError):
                    compare_reports(local, self.original)

    def test_tampered_hash_rejected_for_positive_and_negative(self):
        for name in ('strings', 'left-negative'):
            with self.subTest(name=name):
                original = copy.deepcopy(self.original)
                if name == 'strings':
                    original['source_sha256'] = '0' * 64
                else:
                    original['cases'][name]['source_sha256'] = '0' * 64
                with self.assertRaises(ValueError):
                    compare_reports(self.local, original)

    def test_timeouts_rejected_even_when_phase_and_diagnostic_agree(self):
        for index in (0, 1):
            local = copy.deepcopy(self.local)
            local['fixtures'][index]['actual']['timed_out'] = True
            with self.assertRaises(ValueError):
                compare_reports(local, self.original)

    def test_duplicate_and_missing_cases_rejected(self):
        for rows in (self.local['fixtures'][:-1], self.local['fixtures'][:-1] + [self.local['fixtures'][0]]):
            with self.assertRaises(ValueError):
                compare_reports({'fixtures': rows}, self.original)

    def test_original_wrong_cause_rejected(self):
        self.original['cases']['left-negative']['diagnostic'] = 'unrelated failure'
        with self.assertRaises(ValueError):
            compare_reports(self.local, self.original)

    def test_output_cannot_overwrite_evidence_or_fixture(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            local, original = root / 'local.json', root / 'original.json'
            local.write_text(json.dumps(self.local))
            original.write_text(json.dumps(self.original))
            for output in (local, original, Path(self.local['fixtures'][0]['source'])):
                before = output.read_bytes()
                with self.subTest(output=output.name), patch('sys.stderr'), self.assertRaises(SystemExit):
                    main(['--local-report', str(local), '--original', str(original), '--report', str(output)])
                self.assertEqual(before, output.read_bytes())

    def test_symlink_and_hardlink_output_aliases_cannot_modify_input_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            local, original = root / 'local.json', root / 'original.json'
            local.write_text(json.dumps(self.local))
            original.write_text(json.dumps(self.original))
            for kind in ('symlink', 'hardlink'):
                alias = root / (kind + '.json')
                if kind == 'symlink': alias.symlink_to(original)
                else: alias.hardlink_to(original)
                before = original.read_bytes()
                with self.subTest(kind=kind), patch('sys.stderr'), self.assertRaises(SystemExit):
                    main(['--local-report', str(local), '--original', str(original), '--report', str(alias)])
                self.assertEqual(before, original.read_bytes())

    def test_cli_returns_failure_for_unexpected_mismatch(self):
        self.local['fixtures'][0]['actual']['stdout'] = 'wrong\n'
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            local, original, output = root / 'local.json', root / 'original.json', root / 'result.json'
            local.write_text(json.dumps(self.local))
            original.write_text(json.dumps(self.original))
            with patch('builtins.print'):
                self.assertEqual(1, main(['--local-report', str(local), '--original', str(original), '--report', str(output)]))
            self.assertEqual(1, json.loads(output.read_text())['unexpected_mismatch'])
