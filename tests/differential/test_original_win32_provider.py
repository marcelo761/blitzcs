import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


class OriginalWin32ProviderTests(unittest.TestCase):
    def test_catalog_preserves_requested_win32_contracts(self):
        catalog = json.loads((ROOT / 'providers/OriginalBlitz3D/win32.json').read_text())
        signatures = {
            'api_GetClientRect': 2,
            'api_GetDesktopWindow': 0,
            'api_SetWindowLong': 3,
            'api_SetWindowPos': 7,
            'api_GetFocus': 0,
            'api_GetModuleFileName': 3,
            'GlobalMemoryStatus': 1,
        }
        self.assertEqual(set(signatures), {f['name'] for f in catalog['functions']})
        for function in catalog['functions']:
            self.assertEqual(['Integer'] * signatures[function['name']], function['parameters'])
            self.assertEqual('Void' if function['name']=='GlobalMemoryStatus' else 'Integer', function['returnType'])
            self.assertEqual('BlitzCS.OriginalRuntime.Win32', function['type'])
            self.assertEqual(function['name'][4:] if function['name'].startswith('api_') else function['name'], function['method'])
            self.assertNotIn('binary', function)
        rect = next(f for f in catalog['functions'] if f['name'] == 'api_GetClientRect')
        self.assertTrue(rect['legacyAdapter'])
        memory=next(f for f in catalog['functions'] if f['name']=='GlobalMemoryStatus')
        self.assertEqual([0],memory['objectAdapterParameters'])
        module=next(f for f in catalog['functions'] if f['name']=='api_GetModuleFileName')
        self.assertEqual(['Integer']*3,module['legacySignatureOverride']['parameters'])
        self.assertTrue(module['legacySignatureOverride']['reason'])


if __name__ == '__main__':
    unittest.main()
