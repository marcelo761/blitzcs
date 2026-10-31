"""Focused, synthetic tests for the read-only original SDK auditor."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
from unittest import TestCase
from unittest.mock import patch

from tools import audit_sdk_compatibility as audit


class IncludeClosureTests(TestCase):
    def test_windows_case_and_slashes_and_quoted_semicolon_resolve_from_main_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            sdk = Path(temporary) / "SDK"
            root = sdk / "Demo"
            nested = root / "nested"
            nested.mkdir(parents=True)
            entry = root / "Main.bb"
            child = nested / "Child.bb"
            semi = root / "semi;colon.bb"
            base = root / "Base.bb"
            entry.write_text('Include "NESTED\\child.BB"\n', encoding="cp1252")
            # BlitzCS resolves nested Include operands from the original main
            # source directory, while quoted semicolons remain part of paths.
            child.write_text(
                'Include "semi;colon.BB"\nInclude "base.bb"\n', encoding="cp1252"
            )
            semi.write_text("Print 1\n", encoding="cp1252")
            base.write_text("Print 2\n", encoding="cp1252")

            result = audit.include_closure(sdk, entry)

            self.assertEqual(
                {"Demo/Main.bb", "Demo/nested/Child.bb", "Demo/semi;colon.bb", "Demo/Base.bb"},
                {item["path"] for item in result},
            )
            self.assertEqual(4, len({item["sha256"] for item in result}))


class EmitAuditTests(TestCase):
    def test_emit_uses_same_profile_and_original_source_without_executing_artifact(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sdk = root / "sdk"
            sdk.mkdir()
            source = sdk / "Example.bb"
            source.write_text('Print "unchanged"\n', encoding="cp1252")
            source_before = hashlib.sha256(source.read_bytes()).hexdigest()
            cli = root / "BlitzCS.CLI.dll"
            cli.write_bytes(b"fake cli")
            profile = root / "profile.json"
            profile.write_text("{}\n", encoding="utf-8")
            commands: list[list[str]] = []

            def fake_run(command, **kwargs):
                commands.append(command)
                if "-o" in command:
                    output = Path(command[command.index("-o") + 1])
                    output.write_bytes(b"managed assembly")
                return SimpleNamespace(returncode=0, stdout="", stderr="")

            with patch.object(audit.subprocess, "run", side_effect=fake_run):
                result = audit.audit_case(
                    sdk, "test", source, cli, None, profile, timeout=5, emit=True
                )

            self.assertEqual(3, len(commands))
            self.assertEqual("check-syntax", commands[0][2])
            self.assertEqual("check-semantics", commands[1][2])
            self.assertEqual("build", commands[2][2])
            self.assertEqual(str(profile), commands[1][commands[1].index("--externals") + 1])
            self.assertEqual(str(profile), commands[2][commands[2].index("--externals") + 1])
            self.assertEqual("not_attempted", result["runtimeExecution"])
            emitted = result["runs"]["current"]["stages"]["emit"]
            self.assertEqual("managed_emission_succeeded", emitted["classification"])
            self.assertTrue(emitted["artifact"]["exists"])
            self.assertEqual(len(b"managed assembly"), emitted["artifact"]["size_bytes"])
            self.assertEqual(source_before, hashlib.sha256(source.read_bytes()).hexdigest())

    def test_emit_defaults_off_and_no_emit_has_contract_only_classification(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sdk = root / "sdk"
            sdk.mkdir()
            source = sdk / "Example.bb"
            source.write_text("Print 1\n", encoding="cp1252")
            cli = root / "cli"
            cli.write_bytes(b"cli")

            with patch("sys.argv", ["audit_sdk_compatibility.py", "--sdk", str(sdk), "--cli", str(cli)]):
                self.assertFalse(audit.parse_args().emit)

            commands: list[list[str]] = []

            def fake_run(command, **kwargs):
                commands.append(command)
                return SimpleNamespace(returncode=0, stdout="", stderr="")

            with patch.object(audit.subprocess, "run", side_effect=fake_run):
                result = audit.audit_case(sdk, "test", source, cli, None, None, timeout=5)

            self.assertEqual(2, len(commands))
            self.assertNotIn("emit", result["runs"]["current"]["stages"])
            self.assertEqual(
                "contract_validation_succeeded",
                result["runs"]["current"]["stages"]["semantics"]["classification"],
            )
            self.assertEqual("not_attempted", result["runtimeExecution"])

    def test_zero_missing_implementation_summary_is_not_a_missing_provider(self):
        result = {
            "timed_out": False,
            "exit_code": 0,
            "stdout": "Semantic validation succeeded; 0 external call(s) lack implementations.",
            "stderr": "",
        }
        self.assertEqual("contract_validation_succeeded", audit.classify_stage("semantics", result))


class ClassificationTests(TestCase):
    @staticmethod
    def stage(exit_code: int, output: str) -> dict:
        return {"timed_out": False, "exit_code": exit_code, "stdout": output, "stderr": ""}

    def test_semantic_failures_preserve_uncertainty_about_undeclared_names(self):
        self.assertEqual(
            "unknown_symbol_missing_declaration",
            audit.classify_stage(
                "semantics", self.stage(1, "Unknown Basic function or external symbol: FOO")
            ),
        )
        self.assertEqual(
            "semantic_error",
            audit.classify_stage("semantics", self.stage(1, "Type mismatch for argument")),
        )

    def test_declared_provider_missing_and_native_linking_need_diagnostic_evidence(self):
        self.assertEqual(
            "declared_provider_missing",
            audit.classify_stage("semantics", self.stage(0, "Catalog has no implementation for FOO")),
        )
        self.assertEqual(
            "declared_provider_missing",
            audit.classify_stage(
                "semantics",
                self.stage(0, "Semantic validation succeeded; 2 external call(s) lack implementations."),
            ),
        )
        self.assertEqual(
            "declared_provider_missing",
            audit.classify_stage("emit", self.stage(1, "Required runtime provider is missing")),
        )
        self.assertEqual(
            "native_linking_error",
            audit.classify_stage("emit", self.stage(1, "Binary library foo.dll has no export BAR")),
        )
        self.assertEqual(
            "managed_emission_error",
            audit.classify_stage("emit", self.stage(1, "Failed to write managed output")),
        )

    def test_syntax_and_emit_classification_respect_prior_stage_diagnostics(self):
        self.assertEqual(
            "infrastructure_error",
            audit.classify_stage(
                "syntax", {"timed_out": False, "exit_code": None, "stdout": "", "stderr": "file missing"}
            ),
        )
        self.assertEqual(
            "syntax_check_error",
            audit.classify_stage("syntax", self.stage(1, "Could not open source file")),
        )
        self.assertEqual(
            "parser_syntax_error",
            audit.classify_stage("emit", self.stage(1, "source.bb(1,1): expected an expression")),
        )
        self.assertEqual(
            "unknown_symbol_missing_declaration",
            audit.classify_stage(
                "emit", self.stage(1, "Unknown Basic function or external symbol: DisplayMode")
            ),
        )



class SourceStageClassificationTests(TestCase):
    def test_lexer_and_unsupported_keyword_failures_remain_source_errors(self):
        for diagnostic in ("Unsupported keyword 'goto' cannot be used as a variable.", "Unterminated string literal.", "Expected Identifier; found '-'."):
            result = {"exit_code": 1, "timed_out": False, "stdout": "", "stderr": "sample.bb(3,1): " + diagnostic}
            for stage in ("syntax", "semantics", "emit"):
                self.assertEqual("parser_syntax_error", audit.classify_stage(stage, result))

    def test_missing_include_is_not_a_syntax_or_native_linking_failure(self):
        result = {"exit_code": 1, "timed_out": False, "stdout": "", "stderr": "main.bb(2,1): Unable to open include file 'absent.bb': File not found."}
        for stage in ("syntax", "semantics", "emit"):
            self.assertEqual("include_resolution_error", audit.classify_stage(stage, result))

if __name__ == "__main__":
    import unittest

    unittest.main()
