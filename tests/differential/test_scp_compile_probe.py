import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from tools.scp_compile_probe import (
    classify_result,
    diagnostic_include_location,
    external_symbols,
    include_closure,
    load_native_symbols,
    parse_diagnostic_location,
    probe,
    summarize_integrated,
)


class ScpCompileProbeClassificationTests(unittest.TestCase):
    def test_extracts_first_diagnostic_file_and_line(self):
        self.assertEqual(
            {"file": "StrictLoads.bb", "line": 59, "column": 12},
            parse_diagnostic_location("/snapshot/StrictLoads.bb(59,12): Unknown function 'Foo'"),
        )
        self.assertIsNone(parse_diagnostic_location("compiler failed without a source location"))

    def test_integrated_summary_keeps_first_error_as_lower_bound(self):
        summary = summarize_integrated({
            "phase": "compiler_error",
            "first_diagnostic": "/snapshot/StrictLoads.bb(59,12): Unknown function 'Foo'",
            "category": "native_unregistered",
        }, diagnostic_depth=None)

        self.assertEqual(1, summary["observed_errors_lower_bound"])
        self.assertIsNone(summary["additional_errors"])
        self.assertEqual("StrictLoads.bb", summary["first_error"]["file"])
        self.assertEqual(59, summary["first_error"]["line"])
        self.assertEqual("native_unregistered", summary["first_error"]["category"])
        self.assertIsNone(summary["diagnostic_include_depth"])

    def test_static_include_closure_tracks_order_depth_and_missing_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "Main.bb").write_text('Include "One.bb"\nInclude "missing.bb"\n')
            (root / "One.bb").write_text('Include "Two.bb"\n')
            (root / "Two.bb").write_text('Print 1\n')

            closure = include_closure(root / "Main.bb")

        self.assertEqual(["One.bb", "Two.bb"], closure["files"])
        self.assertEqual(2, closure["max_depth"])
        self.assertEqual(["missing.bb"], closure["missing"])

    def test_ambiguous_diagnostic_paths_report_depth_range_and_witnesses(self):
        closure = {"depth_witnesses": {
            1: [["Main.bb", "StrictLoads.bb"]],
            2: [["Main.bb", "sub/Shared.bb", "StrictLoads.bb"]],
        }}

        depth, witnesses = diagnostic_include_location(
            closure, "/snapshot/StrictLoads.bb(59,20): Unsupported keyword 'object'")

        self.assertEqual([1, 2], depth)
        self.assertEqual(2, len(witnesses))

    def test_all_ten_m9_reserved_keyword_diagnostics_are_syntactic(self):
        keywords = ["Abs"] * 4 + ["Object"] * 3 + ["Handle", "Restore", "Wend"]

        categories = [classify_result(
            "compiler_error", f"file.bb(1,2): Unsupported keyword '{keyword}' cannot be used as a variable.")
            for keyword in keywords]

        self.assertEqual(10, len(categories))
        self.assertEqual(["syntactic"] * 10, categories)

    def test_distinguishes_front_end_categories_from_missing_symbols(self):
        declared = {"fsound_init"}
        natives = {"graphics", "right"}
        cases = [
            ("compiled", None, "compiled"),
            ("compiler_error", "file.bb(1,2): Unexpected character '@'", "lexical"),
            ("compiler_error", "file.bb(1,2): Expected Close; found '='", "syntactic"),
            ("compiler_error", "file.bb(1,2): Unsupported keyword 'Object' cannot be used as a variable.", "syntactic"),
            ("compiler_error", "file.bb(1,2): Unsupported keyword 'Handle' cannot be used as a variable.", "syntactic"),
            ("compiler_error", "file.bb(1,2): Unsupported keyword 'Abs' cannot be used as a variable.", "syntactic"),
            ("compiler_error", "file.bb(1,2): Unsupported keyword 'Restore' cannot be used as a variable.", "syntactic"),
            ("compiler_error", "file.bb(1,2): Unsupported keyword 'Wend' cannot be used as a variable.", "syntactic"),
            ("compiler_error", "file.bb(1,2): Unknown function 'Graphics'", "native_unregistered"),
            ("compiler_error", "file.bb(1,2): Unknown BASIC function or external symbol 'Graphics'", "native_unregistered"),
            ("compiler_error", "file.bb(1,2): Unknown BASIC function or external symbol 'FSOUND_Init'", "external_missing"),
            ("compiler_error", "file.bb(1,2): Unknown function 'FSOUND_Init'", "external_missing"),
            ("compiler_error", "file.bb(1,2): Unknown BASIC function or external symbol 'NotDeclared'", "semantic_type"),
            ("compiler_error", "file.bb(1,2): Unknown BASIC function or external symbol 'GetIniInt'", "semantic_type"),
            ("compiler_error", "file.bb(1,2): Cannot convert String to Integer", "semantic_type"),
            ("compiler_error", "file.bb(1,2): Unable to open include file 'BlitzAL.bb': not found", "external_missing"),
            ("infrastructure_timeout", "probe exceeded 30s timeout", "infrastructure"),
        ]

        for phase, diagnostic, expected in cases:
            with self.subTest(phase=phase, diagnostic=diagnostic):
                self.assertEqual(expected, classify_result(phase, diagnostic, declared, natives))

    def test_reads_external_symbols_from_sidecar_declarations(self):
        with tempfile.TemporaryDirectory() as temp:
            source_dir = Path(temp)
            (source_dir / "fmod.decls").write_text(
                'FSOUND_Init%(freq%,channels%,flags%):"_FSOUND_Init@12"\n', encoding="cp1252")
            (source_dir / "kernel32.decls").write_text(
                'api_GetTickCount%():"GetTickCount"\n', encoding="cp1252")

            self.assertEqual({"fsound_init", "api_gettickcount"}, external_symbols(source_dir))

    def test_loads_native_names_from_catalog_projection(self):
        with tempfile.TemporaryDirectory() as temp:
            catalog = Path(temp) / "native.json"
            catalog.write_text('{"symbols":["Graphics", "Right"]}', encoding="utf-8")

            self.assertEqual({"graphics", "right"}, load_native_symbols(catalog))

    def test_passes_optional_external_catalog_to_each_cli_build(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "input.bb"
            cli = root / "BlitzCS.CLI.dll"
            externals = root / "external.json"
            source.write_text("Print 1\n", encoding="utf-8")
            cli.touch()
            externals.write_text('{"functions":[]}', encoding="utf-8")

            def successful_build(command, **_):
                Path(command[command.index("-o") + 1]).touch()
                return SimpleNamespace(returncode=0, stdout="", stderr="")

            with patch("tools.scp_compile_probe.subprocess.run", side_effect=successful_build) as run:
                result = probe(source, "dotnet", cli, 1, externals=externals)

            self.assertEqual("compiled", result["phase"])
            self.assertEqual(["--externals", str(externals.resolve())], run.call_args.args[0][-2:])


if __name__ == "__main__":
    unittest.main()
