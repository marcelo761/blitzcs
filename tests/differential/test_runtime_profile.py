import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from tools.generate_runtime_profile import build_profile


class RuntimeProfileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.base = self.write('base.json', {'functions': []})
        self.output = self.root / 'generated' / 'runtime.json'

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, path, value):
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(json.dumps(value), encoding='utf-8')
        return file

    def snapshot(self, *descriptors):
        return {'runtimeSHA256': 'ab' * 32, 'version': 311, 'descriptors': list(descriptors)}

    def test_recovers_all_delta_and_excludes_reserved_and_internal_symbols(self):
        self.write('base.json', {'functions': [{'name': 'Existing', 'returnType': 'Integer', 'parameters': ['Integer'], 'defaults': [7]}]})
        result = build_profile(self.snapshot('%Existing%value=7', '$ErrorLog', 'TextureLodBias#bias', '_bbAbs', 'Print$text', 'End'), self.base, self.output)
        self.assertEqual(['../base.json'], result['profiles'])
        self.assertEqual(['ErrorLog', 'TextureLodBias'], [f['name'] for f in result['functions']])
        self.assertEqual('String', result['functions'][0]['returnType'])
        self.assertEqual('Void', result['functions'][1]['returnType'])
        self.assertEqual(['Float'], result['functions'][1]['parameters'])
        self.assertEqual('ab' * 32, result['metadata']['runtimeSHA256'])
        self.assertEqual(311, result['metadata']['version'])
        for function in result['functions']:
            self.assertEqual('runtime-registration', function['contract']['resolution'])
            self.assertIsNone(function['contract']['callingConvention'])
            self.assertFalse({'assembly', 'method', 'binary'} & function.keys())

    def test_live_string_defaults_keep_backslashes_markers_and_literal_quotes(self):
        descriptor = r'Open$path="C:\Games\#cache%files$\x"$caption="say ""hello"""'
        result = build_profile(self.snapshot(descriptor), self.base, self.output, provider='custom.runtime')
        function = result['functions'][0]
        self.assertEqual([r'C:\Games\#cache%files$\x', 'say "hello"'], function['defaults'])
        self.assertEqual(descriptor, function['contract']['symbol'])
        self.assertEqual('custom.runtime', function['contract']['provider'])

    def test_recursive_base_import_and_case_insensitive_name_comparison(self):
        self.write('stock.json', {'functions': [{'name': 'Existing', 'returnType': 'Integer', 'parameters': []}]})
        self.write('base.json', {'profiles': ['stock.json'], 'functions': []})
        result = build_profile(self.snapshot('%EXISTING', 'NewCall'), self.base, self.output)
        self.assertEqual(['NewCall'], [f['name'] for f in result['functions']])

    def test_base_and_snapshot_conflicts_are_explicit(self):
        self.write('base.json', {'functions': [{'name': 'Existing', 'returnType': 'Integer', 'parameters': ['Integer'], 'defaults': [7]}]})
        for descriptor in ['#Existing%value=7', '%Existing#value=7', '%Existing%value=8', '%Existing%value']:
            with self.subTest(descriptor=descriptor), self.assertRaisesRegex(ValueError, 'conflict.*Existing'):
                build_profile(self.snapshot(descriptor), self.base, self.output)
        with self.assertRaisesRegex(ValueError, 'conflict.*Repeated'):
            build_profile(self.snapshot('%Repeated', '#Repeated'), self.base, self.output)

    def test_rejects_malformed_nonfinite_and_nontrailing_defaults(self):
        for descriptor in ['Broken%', 'Bad#x=NaN', 'Bad#x=1e999', 'Bad%x=2147483648',
                           'Bad%x=1%y', 'Bad$text="open', 'Bad$text=unquoted', 'Bad#x=1+2']:
            with self.subTest(descriptor=descriptor), self.assertRaises(ValueError):
                build_profile(self.snapshot(descriptor), self.base, self.output)

    def test_numeric_defaults_are_strict_typed_constants(self):
        result = build_profile(self.snapshot('Numbers%a=-3%b=0x10#c=1.25e2'), self.base, self.output)
        self.assertEqual([-3, 16, 125.0], result['functions'][0]['defaults'])

    def test_base_cycle_and_conflicting_imports_are_rejected(self):
        self.write('base.json', {'profiles': ['base.json']})
        with self.assertRaisesRegex(ValueError, 'cycle'):
            build_profile(self.snapshot('New'), self.base, self.output)
        self.write('a.json', {'functions': [{'name': 'Repeated', 'returnType': 'Void', 'parameters': []}]})
        self.write('b.json', {'functions': [{'name': 'Repeated', 'returnType': 'Integer', 'parameters': []}]})
        self.write('base.json', {'profiles': ['a.json', 'b.json']})
        with self.assertRaisesRegex(ValueError, 'conflict.*Repeated'):
            build_profile(self.snapshot('New'), self.base, self.output)

    def test_cli_is_deterministic_and_refuses_overwriting_inputs(self):
        snapshot = self.write('snapshot.json', self.snapshot('$ErrorLog', 'TextureLodBias#bias'))
        script = Path(__file__).resolve().parents[2] / 'tools/generate_runtime_profile.py'
        args = [sys.executable, str(script), '--snapshot', str(snapshot), '--base-profile', str(self.base), '--output', str(self.output)]
        subprocess.run(args, check=True, capture_output=True)
        first = self.output.read_bytes()
        subprocess.run(args, check=True, capture_output=True)
        self.assertEqual(first, self.output.read_bytes())
        original = self.base.read_bytes()
        result = subprocess.run(args[:-1] + [str(self.base)], capture_output=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, self.base.read_bytes())


if __name__ == '__main__':
    unittest.main()
