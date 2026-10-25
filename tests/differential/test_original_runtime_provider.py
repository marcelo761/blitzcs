import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('original_runtime_probe', ROOT/'tools/probe_original_runtime.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class OriginalRuntimeProviderTests(unittest.TestCase):
    def test_generated_bindings_are_current(self):
        result = subprocess.run([sys.executable, str(ROOT/"providers/OriginalBlitz3D/generate.py"), "--check"], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_bindings_preserve_historical_numeric_contracts(self):
        historical = {f['name']: f for f in json.loads((ROOT/'profiles/blitz3d.json').read_text())['functions']}
        catalog = json.loads((ROOT/'providers/OriginalBlitz3D/runtime.json').read_text())
        self.assertEqual(['../../profiles/blitz3d.json'], catalog['profiles'])
        for binding in catalog['functions']:
            contract = historical[binding['name']]
            self.assertEqual(contract['parameters'], binding['parameters'])
            self.assertEqual(contract['returnType'], binding['returnType'])
            self.assertNotIn('String', binding['parameters'] + [binding['returnType']])
        self.assertTrue({'Graphics3D', 'CreateCamera', 'CreateCube', 'RenderWorld', 'ReadPixelFast'} <= {f['name'] for f in catalog['functions']})

    def test_capture_verifier_distinguishes_rendered_pixels_from_background(self):
        pixels = struct.pack('<IIII', 0, 0xFF7F7F7F, 0, 0)
        header = b'BM' + struct.pack('<III', 54+len(pixels), 0, 54)
        header += struct.pack('<IiiHHIIIIII', 40, 2, 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'frame.bmp'
            path.write_bytes(header+pixels)
            actual = probe.bitmap_metrics(path)
            self.assertEqual(1, actual['nonBlackPixels'])
            self.assertEqual([127, 127, 127], actual['center'])
            path.write_bytes(header+pixels[:-1])
            with self.assertRaisesRegex(ValueError, 'Incomplete'):
                probe.bitmap_metrics(path)

    def test_capture_verifier_rejects_other_formats(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'not-a-frame.bmp'
            path.write_bytes(b'not an image')
            with self.assertRaisesRegex(ValueError, 'BMP header'):
                probe.bitmap_metrics(path)


if __name__ == '__main__':
    unittest.main()
