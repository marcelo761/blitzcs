import json
import os
import subprocess
from unittest.mock import patch
from pathlib import Path
import tempfile
import unittest

from tools.generate_blitz3d_api_inventory import extract, strip_cpp_comments, validation_evidence, validate_outputs, verify_source, write_outputs


class Blitz3DInventoryTests(unittest.TestCase):
    def test_comments_do_not_register_and_strings_preserve_comment_markers(self):
        source = ('// rtSym("Fake", ignored);\n'
                  '/* rtSym("Other", ignored); */\n'
                  r'rtSym("$Real$path=\"//\"", real);' + '\n')
        clean = strip_cpp_comments(source)
        self.assertNotIn('Fake', clean)
        self.assertNotIn('Other', clean)
        self.assertEqual(source.count('\n'), clean.count('\n'))
        self.assertIn('Real', clean)
        self.assertIn(r'\"//\"', clean)
        self.assertIn(', real);', clean)

    def test_recurses_retains_unsupported_conflicts_and_internal_symbols(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            runtime = root / 'bbruntime' / 'nested'
            runtime.mkdir(parents=True)
            (runtime / 'runtime.cpp').write_text(
                '#include "dependency.h"\n'
                '// rtSym("Fake", fake);\n'
                'rtSym("%Same%arg=4", bbSame);\n'
                'rtSym("$Same", bbSame2);\n'
                'rtSym("%Bad%", bad);\n'
                'rtSym("_bbInternal", internal);\n'
            )
            rows, files = extract(root)
            self.assertEqual(4, len(rows))
            self.assertEqual(1, len(files))
            self.assertEqual('4', rows[0]['signature']['arguments'][0]['default'])
            self.assertEqual('integer', rows[0]['signature']['return_type'])
            self.assertIsNone(rows[2]['signature'])
            self.assertEqual('internal_runtime_symbol', rows[3]['signature']['kind'])
            self.assertEqual(['dependency.h'], rows[0]['dependencies']['direct_includes'])
            self.assertEqual(3, rows[0]['line'])

    def test_strings_require_successful_comparison_and_identical_source_hash(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            evidence = root / 'docs/evidence'
            evidence.mkdir(parents=True)
            (evidence / 'm19-original.json').write_text(json.dumps({'source_sha256': 'abc'}))
            self.assertEqual([], validation_evidence(root, 'left'))
            comparison = evidence / 'm19-new-comparison.json'
            comparison.write_text(json.dumps({'source_sha256': 'wrong', 'deterministic_capture_matches': True, 'unexpected_mismatch': 0}))
            self.assertEqual([], validation_evidence(root, 'left'))
            comparison.write_text(json.dumps({'source_sha256': 'abc', 'deterministic_capture_matches': False, 'unexpected_mismatch': 0}))
            self.assertEqual([], validation_evidence(root, 'left'))
            comparison.write_text(json.dumps({'source_sha256': 'abc', 'deterministic_capture_matches': True, 'unexpected_mismatch': 0, 'known_termination_divergence': 6}))
            self.assertEqual(2, len(validation_evidence(root, 'left')))
            self.assertEqual([], validation_evidence(root, 'currentdate'))

    def test_output_guards_reject_sources_links_and_non_json(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original = root / 'original'
            original.mkdir()
            source = original / 'source.cpp'
            source.write_text('original')
            repo = root / 'repo'
            repo.mkdir()
            output = root / 'out.json'
            with self.assertRaises(ValueError): validate_outputs(root / 'out.md', original, repo)
            with self.assertRaises(ValueError): validate_outputs(original / 'out.json', original, repo)
            output.symlink_to(source)
            with self.assertRaises(ValueError): validate_outputs(output, original, repo)
            output.unlink()
            os.link(source, output)
            with self.assertRaises(ValueError): validate_outputs(output, original, repo)
            output.unlink()
            output.write_text('old')
            os.link(output, output.with_suffix('.md'))
            with self.assertRaises(ValueError): validate_outputs(output, original, repo)
            self.assertEqual('original', source.read_text())

    def test_atomic_output_replacement_preserves_unrelated_hardlink(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / 'out.json'
            unrelated = root / 'unrelated'
            unrelated.write_text('old')
            os.link(unrelated, output)
            with patch('tools.generate_blitz3d_api_inventory.markdown', return_value='markdown'):
                write_outputs((output, output.with_suffix('.md')), {'ok': True})
            self.assertEqual('old', unrelated.read_text())
            self.assertEqual({'ok': True}, json.loads(output.read_text()))

    def test_pinned_revision_does_not_accept_modified_runtime(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            subprocess.run(['git', 'init', '-q', str(root)], check=True)
            source = root / 'bbruntime/runtime.cpp'
            source.parent.mkdir()
            source.write_text('original')
            subprocess.run(['git', '-C', str(root), 'add', 'bbruntime'], check=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=Inventory Test', '-c', 'user.email=inventory@example.invalid', 'commit', '-qm', 'fixture'], check=True)
            revision = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
            with patch('tools.generate_blitz3d_api_inventory.COMMIT', revision):
                self.assertEqual(revision, verify_source(root))
                source.write_text('tampered')
                with self.assertRaisesRegex(ValueError, 'differs'): verify_source(root)

    def test_committed_inventory_is_complete_and_states_are_independent(self):
        root = Path(__file__).resolve().parents[2]
        data = json.loads((root / 'docs/blitz3d-api-inventory.json').read_text())
        rows = data['registrations']
        self.assertEqual(593, len(rows))
        self.assertEqual(57, data['totals']['internal_symbols'])
        self.assertEqual(len(rows), sum(f['registrations'] for f in data['source']['files']))
        names = {r['name'].lower(): r for r in rows}
        self.assertEqual('not established', names['createcube']['states']['validated'])
        self.assertFalse(names['createcube']['states']['executable_binding'])
        self.assertTrue(names['createcube']['states']['signature_known'])
        for clock in ('currentdate', 'currenttime'):
            self.assertEqual('not established', names[clock]['states']['validated'])
            self.assertIn('tests/BlitzCS.CLI.Tests/LegacyStringFamilyTests.cs', names[clock]['functional_evidence'])
            self.assertTrue(names[clock]['states']['executable_binding'])
        self.assertEqual('scoped historical comparison', names['createbank']['states']['validated'])
        for row in rows:
            if row['states']['validated'] != 'not established':
                self.assertTrue(row['validation_evidence'])
                self.assertTrue(row['validation_scope'])
                for evidence in row['validation_evidence']:
                    self.assertTrue((root / evidence).is_file())
        serialized = json.dumps(data)
        self.assertNotIn('/tmp/', serialized)
        self.assertNotIn('/home/', serialized)


if __name__ == '__main__':
    unittest.main()
