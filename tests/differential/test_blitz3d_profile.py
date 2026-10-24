import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from tools.generate_blitz3d_profile import build_profile

ROOT = Path(__file__).resolve().parents[2]
GENERATOR = ROOT / "tools/generate_blitz3d_profile.py"
PROFILE = ROOT / "profiles/blitz3d.json"


class Blitz3DProfileTests(unittest.TestCase):
    def test_generator_is_reproducible(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "first.json"
            second = Path(directory) / "second.json"
            subprocess.run([sys.executable, str(GENERATOR), "--output", str(output)], cwd=ROOT, check=True)
            subprocess.run([sys.executable, str(GENERATOR), "--output", str(second)], cwd=ROOT, check=True)
            self.assertEqual(output.read_bytes(), second.read_bytes())
            self.assertEqual(output.read_bytes(), PROFILE.read_bytes())

    def test_generator_emits_explicit_empty_defaults_for_required_only_functions(self):
        inventory = {
            "source": {"repository": "https://example.invalid/runtime", "commit": "abc123"},
            "registrations": [{
                "descriptor": "RuntimeStats",
                "signature": {
                    "name": "RuntimeStats",
                    "return_type": "void",
                    "arguments": [],
                    "kind": "typed_runtime_callable",
                },
                "source": "bbruntime/basic.cpp",
                "line": 1,
            }],
        }
        profile = build_profile(inventory)
        self.assertEqual([], profile["functions"][0]["defaults"])

    def test_cpp_escaped_empty_string_defaults_decode_to_empty_strings(self):
        profile = json.loads(PROFILE.read_text(encoding="utf-8"))
        functions = {item["name"]: item for item in profile["functions"]}
        self.assertEqual([""], functions["Input"]["defaults"])
        self.assertEqual([""], functions["AppTitle"]["defaults"])

    def test_cpp_escaped_windows_path_default_preserves_basic_backslashes(self):
        inventory = {
            "source": {"repository": "https://example.invalid/runtime", "commit": "abc123"},
            "registrations": [{
                "descriptor": "LoadFile$path=\\\"C:\\\\Games\\\\Blitz\\\"",
                "signature": {
                    "name": "LoadFile",
                    "return_type": "integer",
                    "arguments": [{"name": "path", "type": "string", "default": "\\\"C:\\\\Games\\\\Blitz\\\""}],
                    "kind": "typed_runtime_callable",
                },
                "source": "bbruntime/example.cpp",
                "line": 1,
            }],
        }
        profile = build_profile(inventory)
        self.assertEqual([r"C:\Games\Blitz"], profile["functions"][0]["defaults"])

    def test_malformed_cpp_string_default_is_rejected(self):
        inventory = {
            "source": {"repository": "https://example.invalid/runtime", "commit": "abc123"},
            "registrations": [{
                "descriptor": "Bad$string=\\\"open",
                "signature": {
                    "name": "Bad",
                    "return_type": "void",
                    "arguments": [{"name": "string", "type": "string", "default": "\\\"open"}],
                    "kind": "typed_runtime_callable",
                },
                "source": "bbruntime/example.cpp",
                "line": 1,
            }],
        }
        with self.assertRaises(ValueError):
            build_profile(inventory)

    def test_graphics3d_declaration_and_defaults(self):
        profile = json.loads(PROFILE.read_text(encoding="utf-8"))
        graphics = next(item for item in profile["functions"] if item["name"] == "Graphics3D")
        self.assertEqual("Void", graphics["returnType"])
        self.assertEqual(["Integer", "Integer", "Integer", "Integer"], graphics["parameters"])
        self.assertEqual([0, 0], graphics["defaults"])
        self.assertEqual("blitz3d-rtSym", graphics["contract"]["profile"])
        self.assertEqual("bbruntime/bbgraphics.cpp:1236", graphics["contract"]["source"])

    def test_internal_and_reserved_exclusions_are_explicit_and_not_functions(self):
        inventory = json.loads((ROOT / "docs/blitz3d-api-inventory.json").read_text(encoding="utf-8"))
        profile = json.loads(PROFILE.read_text(encoding="utf-8"))
        excluded = profile["metadata"]["exclusions"]
        internal = excluded["internalRuntimeSymbols"]
        self.assertEqual(57, internal["count"])
        self.assertEqual(57, len(internal["entries"]))
        self.assertTrue(all(row["signature"]["kind"] == "internal_runtime_symbol"
                            for row in inventory["registrations"] if row["name"] in {e["name"] for e in internal["entries"]}))
        reserved = excluded["parserReservedNames"]["entries"]
        self.assertEqual({"Print", "End"}, {item["name"] for item in reserved})
        function_names = {item["name"].casefold() for item in profile["functions"]}
        self.assertFalse(function_names & {item["name"].casefold() for item in reserved})
        self.assertEqual(534, len(profile["functions"]))

    def test_profile_carries_runtime_contract_without_binary_binding_claims(self):
        profile = json.loads(PROFILE.read_text(encoding="utf-8"))
        self.assertEqual("6beb288cb5962393684a59a4a44ac11524894939", profile["metadata"]["sourceCommit"])
        for function in profile["functions"]:
            contract = function["contract"]
            self.assertEqual("blitz3d-rtSym", contract["profile"])
            self.assertEqual("blitz3d.runtime", contract["provider"])
            self.assertEqual("runtime-registration", contract["resolution"])
            self.assertIsNone(contract["callingConvention"])
            self.assertIn(":", contract["source"])
            self.assertFalse({"binary", "library", "export", "callingConvention"} & function.keys())

    def test_scpcb_mavless_overlay_composes_stock_profile_and_only_fork_signatures(self):
        path = ROOT / "profiles/scpcb-mavless.json"
        profile = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(["blitz3d.json"], profile["profiles"])
        self.assertTrue((path.parent / profile["profiles"][0]).is_file())
        self.assertEqual(
            {
                "TextureBumpEnvMat": ("Void", ["Integer", "Integer", "Integer", "Float"], "TextureBumpEnvMat%texture%x%y#envmat", "mavless_bin.zip!bin/runtime.dll+0xEAD30"),
                "TextureBumpEnvOffset": ("Void", ["Integer", "Float"], "TextureBumpEnvOffset%texture#envoffset", "mavless_bin.zip!bin/runtime.dll+0xEACE4"),
                "TextureBumpEnvScale": ("Void", ["Integer", "Float"], "TextureBumpEnvScale%texture#envmat", "mavless_bin.zip!bin/runtime.dll+0xEAD0C"),
            },
            {
                function["name"]: (
                    function["returnType"],
                    function["parameters"],
                    function["contract"]["symbol"],
                    function["contract"]["source"],
                )
                for function in profile["functions"]
            },
        )
        artifact = profile["metadata"]["sourceArtifact"]
        self.assertEqual("ab1b66f0fca099f9bc5fbd13054ebe1bca009661598cde3c2091d7bdceef7749", artifact["archiveSha256"])
        self.assertEqual("ed61cd11cfae77f9f9c47c2ec2e5aa778ff79ddc5ee2f4f7353db4ce6006f39d", artifact["runtimeSha256"])
        self.assertNotIn("ignoredLocalArchive", artifact)
        self.assertNotIn("ignoredLocalRuntime", artifact)
        self.assertEqual(3, len(profile["functions"]))
        for function in profile["functions"]:
            contract = function["contract"]
            self.assertEqual("scpcb.mavless-runtime", contract["provider"])
            self.assertEqual("runtime-registration", contract["resolution"])
            self.assertIsNone(contract["callingConvention"])
            self.assertEqual([], function["defaults"])
            self.assertFalse({"binary", "library", "export"} & function.keys())


if __name__ == "__main__":
    unittest.main()
