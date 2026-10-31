"""Contract and safety guards for the original DLL adapter; native execution is separately required."""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]

class NativeZipContractTests(unittest.TestCase):
    def test_catalog_covers_required_original_contracts(self):
        rows = json.loads((ROOT/'providers/OriginalBlitz3D/native-zip.json').read_text())['functions']
        self.assertEqual(23, len(rows))
        self.assertEqual(23, len({r['name'].casefold() for r in rows}))
        for row in rows:
            self.assertEqual('BlitzCS.OriginalRuntime.NativeZip', row['type'])
            self.assertEqual('Integer', row['returnType'])
            self.assertTrue(row.get('legacyAdapter'))
            for index in row.get('objectAdapterParameters', []):
                self.assertEqual('Integer', row['parameters'][index])
            method = re.search(r'public static int '+re.escape(row['method'])+r'\((.*?)\)', (ROOT/'providers/OriginalBlitz3D/Bridge/NativeZip.cs').read_text())
            self.assertIsNotNone(method)
            self.assertEqual([i for i,p in enumerate(method[1].split(', ')) if p.startswith('object ')], row['objectAdapterParameters'])

    def test_adapter_uses_real_banks_and_explicit_native_abi(self):
        source = (ROOT/'providers/OriginalBlitz3D/Bridge/NativeZip.cs').read_text()
        for required in ['Commands.BankSize(', 'Commands.PeekByte(', 'Commands.PokeByte(', 'CallingConvention.StdCall', 'BLITZCS_ORIGINAL_NATIVE_ROOT', 'IntPtr.Size != 4', 'OperatingSystem.IsWindows()', 'NativeLibrary.GetExport', 'Encoding.GetEncoding(1252', 'Dictionary<int,']:
            self.assertIn(required, source)
        self.assertNotIn('ZLibStream', source)
        self.assertNotIn('DeflateStream', source)
        self.assertNotIn('Int32ToPtr', source)
        self.assertIn('zipWriteInFileInZip', source)

if __name__ == '__main__': unittest.main()
