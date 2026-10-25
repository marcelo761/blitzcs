import tempfile
import unittest
from pathlib import Path

from tools.probe_input_original import comparison_mismatches, managed_fixture, validate_outputs


class InputProbeTests(unittest.TestCase):
    def test_managed_fixture_preserves_input_expressions(self):
        root = Path(__file__).resolve().parents[2]
        original = (root/'samples/LegacyInput/reference-input.bb').read_text()
        managed = managed_fixture(original)
        self.assertIn('Blitz3DCSOpenWindow 320,240,"BlitzCS Original Input Probe"', managed)
        self.assertIn('Blitz3DCSCloseWindow\nEnd', managed)
        self.assertNotIn('Function Snapshot(', managed)
        self.assertNotIn('Graphics 320,240', managed)
        for line in original.split('\nFunction Snapshot(', 1)[0].splitlines():
            if line.startswith('Snapshot '):
                self.assertIn('CS::ProbeIO.Snapshot('+line[len('Snapshot '):]+')', managed)
            elif line.startswith('WaitSignal '):
                self.assertIn('CS::ProbeIO.WaitSignal('+line[len('WaitSignal '):]+')', managed)
        self.assertIn('LegacyInput.PumpEvents()', managed)
        self.assertIn('deadline.Elapsed.TotalSeconds>15', managed)

    def test_comparison_keeps_focus_divergence_and_missing_completion(self):
        self.assertEqual(comparison_mismatches({'focus':'1','done':'complete'}, {'focus':'0'}),
                         {'focus': {'expected':'1','actual':'0'},
                          'done': {'expected':'complete','actual':None}})
        self.assertEqual(comparison_mismatches({'x':'37'}, {'x':'37'}), {})

    def test_output_alias_and_hardlink_cannot_overwrite_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            reference = root/'reference.json'
            reference.write_text('original')
            alias = root/'alias.json'
            alias.hardlink_to(reference)
            for report in (reference, alias):
                with self.assertRaises(ValueError):
                    validate_outputs(report, root/'raw', [reference])
            self.assertEqual(reference.read_text(), 'original')
            validate_outputs(root/'report.json', root/'raw', [reference])

    def test_outputs_cannot_modify_original_sdk(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sdk = root/'sdk'
            with self.assertRaises(ValueError):
                validate_outputs(sdk/'report.json', root/'raw', [], sdk)
            with self.assertRaises(ValueError):
                validate_outputs(root/'report.json', sdk/'raw', [], sdk)


if __name__ == '__main__':
    unittest.main()
