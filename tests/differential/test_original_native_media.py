import json
from pathlib import Path
import unittest
import shutil
import subprocess
import tempfile
ROOT = Path(__file__).resolve().parents[2]

class NativeMediaTests(unittest.TestCase):
    def test_real_exports_and_explicit_abi_adapters(self):
        c = json.loads((ROOT/'providers/OriginalBlitz3D/native-media.json').read_text())
        fs = {f['name']:f for f in c['functions']}
        expected = {'FSOUND_Init','FSOUND_Close','FSOUND_SetVolume','FSOUND_SetPaused','FSOUND_StopSound','FSOUND_Stream_Play','FSOUND_Stream_Stop','FSOUND_Stream_Close','FSOUND_Stream_Open','FSOUND_IsPlaying','FSOUND_SetPan'}
        self.assertTrue(expected <= fs.keys())
        self.assertEqual('Void', fs['FSOUND_Close']['returnType'])
        self.assertEqual('Void', fs['FSOUND_Close']['legacySignatureOverride']['returnType'])
        self.assertTrue(fs['FSOUND_Close']['legacyAdapter'])
        self.assertTrue(fs['FSOUND_Stream_Open']['legacyAdapter'])
        self.assertEqual(['String','Integer','Integer'], fs['FSOUND_Stream_Open']['parameters'])
        for f in fs.values():
            self.assertEqual('BlitzCS.OriginalRuntime.NativeMedia',f['type'])
            self.assertNotIn('binary',f)
            self.assertIn('nativeContract',f)
        self.assertEqual(7, sum(n.startswith('BlitzMovie_') for n in fs))

    @unittest.skipUnless(shutil.which('dotnet'), 'requires .NET SDK')
    def test_filename_validation_rejects_truncation_and_replacement(self):
        with tempfile.TemporaryDirectory(prefix='blitzcs-media-') as directory:
            directory = Path(directory)
            shutil.copy(ROOT/'providers/OriginalBlitz3D/Bridge/NativeMedia.cs', directory/'NativeMedia.cs')
            (directory/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
            (directory/'Program.cs').write_text(r'''using BlitzCS.OriginalRuntime;
using System.Text;
static void Reject<T>(Action action) where T : Exception {
    try { action(); } catch(T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
Reject<ArgumentException>(() => NativeMedia.FSOUND_Stream_Open("a\0b",0,0));
Reject<EncoderFallbackException>(() => NativeMedia.FSOUND_Stream_Open("\u4e2d",0,0));
Reject<ArgumentException>(() => NativeMedia.BlitzMovie_Open("a\0b"));
Reject<EncoderFallbackException>(() => NativeMedia.BlitzMovie_OpenDecodeToImage("\u4e2d",123,0));
Reject<NotSupportedException>(() => NativeMedia.FSOUND_Stream_Open("file",0x8000,0));
Console.WriteLine("filename validation passed");
''')
            result = subprocess.run(['dotnet','run','--project',str(directory/'Test.csproj'),'--configuration','Release'],capture_output=True,text=True,timeout=90)
            self.assertEqual(0,result.returncode,result.stdout+result.stderr)
            self.assertIn('filename validation passed',result.stdout)

if __name__=='__main__':unittest.main()
