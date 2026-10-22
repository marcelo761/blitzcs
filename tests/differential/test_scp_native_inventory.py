import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from tools.generate_scp_native_manifest import build_manifest, decode_descriptor


class ScpNativeInventoryTests(unittest.TestCase):
    def test_decodes_rt_sym_types_and_default_arguments(self):
        self.assertEqual(
            {
                "name": "CallDLL",
                "return_type": "integer",
                "kind": "typed_runtime_callable",
                "arguments": [
                    {"name": "dll_name", "type": "string", "default": None},
                    {"name": "func_name", "type": "string", "default": None},
                    {"name": "in_bank", "type": "integer", "default": "0"},
                    {"name": "out_bank", "type": "integer", "default": "0"},
                ],
            },
            decode_descriptor("%CallDLL$dll_name$func_name%in_bank=0%out_bank=0"),
        )
        self.assertEqual("void", decode_descriptor("SetEnv$env_var$value")["return_type"])
        self.assertIsNone(decode_descriptor("%Bad$unfinished%"))
        self.assertEqual("internal_runtime_symbol", decode_descriptor("_bbAbs")["kind"])

    def test_manifest_separates_native_calls_shadowing_and_external_declarations(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original = root / "original"
            source = root / "scp"
            original.mkdir()
            source.mkdir()
            native = original / "runtime.cpp"
            native.write_text(
                'rtSym("%FileSize$file",bbFileSize);\n'
                'rtSym("$GetEnv$env_var",bbGetEnv);\n'
                'rtSym("$Chr%ascii",bbChr);\n'
                'rtSym("%Len$string",bbLen);\n', encoding="cp1252"
            )
            native_catalog = {
                "source": "fixture original runtime",
                "source_directory": str(original),
                "source_files": [{"file": native.name, "sha256": hashlib.sha256(native.read_bytes()).hexdigest()}],
            }
            catalog = root / "catalog.json"
            catalog.write_text(json.dumps(native_catalog), encoding="utf-8")
            (source / "WinApi.decls").write_text(
                'MessageBox% (hwnd%, text$, caption$, flags%) : "MessageBoxA"\n', encoding="cp1252"
            )
            (source / "main.bb").write_text(
                'Function FileSize%(value$)\nReturn 3\nEnd Function\n'
                'Function HasNoSuffix()\nEnd Function\n'
                'Field Len(5)\n'
                'x% = FileSize("a")\n'
                'y$ = GetEnv("PATH")\n'
                'Global env2$ = GetEnv("PATH")\n'
                'Dim arr(FileSize("b"))\n'
                'MessageBox 0, "text", "title", 0\n'
                '; FileSize("comment only")\n', encoding="cp1252"
            )

            manifest = build_manifest(catalog, source)
            candidates = {row[0].casefold(): row for row in manifest["call_candidates"]}
            native_rows = {row[0].casefold(): row for row in manifest["native_inventory"]}
            self.assertEqual("application_function", candidates["filesize"][1])
            self.assertTrue(candidates["filesize"][3])
            self.assertEqual(2, candidates["filesize"][6])
            self.assertEqual("implemented", native_rows["getenv"][7])
            self.assertEqual(2, native_rows["getenv"][8])
            self.assertEqual([["main.bb", 2]], native_rows["getenv"][9])
            self.assertEqual("external_declaration", candidates["messagebox"][1])
            self.assertNotIn("hasnosuffix", candidates)
            self.assertNotIn("len", candidates)
            self.assertEqual("externaldependency", candidates["messagebox"][2])
            self.assertEqual(1, candidates["messagebox"][6])
            self.assertEqual([["main.bb", 1]], candidates["messagebox"][7])
            self.assertEqual([["main.bb", 11, "statement"]], candidates["messagebox"][8])
            self.assertEqual("integer", native_rows["filesize"][2][0])
            self.assertEqual("string", native_rows["filesize"][2][1][0][1])
            self.assertEqual("implemented", native_rows["chr"][7])
            self.assertEqual("string", native_rows["chr"][2][0])
            self.assertEqual("implemented", native_rows["len"][7])
            self.assertEqual("integer", native_rows["len"][2][0])
            self.assertEqual(0, native_rows["filesize"][8])
            self.assertEqual(2, native_rows["filesize"][12])
            self.assertFalse(manifest["limitations"]["call_resolution_performed"])
            self.assertEqual(
                {"abs", "sgn"},
                {item["name"].casefold() for item in manifest["compiler_intrinsics"]},
            )


if __name__ == "__main__":
    unittest.main()
