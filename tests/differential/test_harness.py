"""Unit tests for differential harness mechanics; fake data is not original output."""

import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from tools.differential import compare_capture, load_manifest, main, normalize_newlines, run_harness


class HarnessTests(unittest.TestCase):
    def test_normalizes_only_newline_style(self):
        self.assertEqual("a\nb\n", normalize_newlines("a\r\nb\r"))
        self.assertEqual(" a\n", normalize_newlines(" a\n"))

    def test_compares_phase_success_semantics_and_stdout(self):
        actual = {"phase": "run", "stdout": "ok\n", "exit_code": 0}
        self.assertEqual("match", compare_capture(actual, actual)[0])
        self.assertEqual(
            "mismatch",
            compare_capture(actual, {"phase": "run", "stdout": "different", "exit_code": 0})[0],
        )
        self.assertEqual(
            "mismatch",
            compare_capture(
                {"phase": "runtime_error", "stdout": "partial A", "exit_code": 7},
                {"phase": "runtime_error", "stdout": "", "exit_code": 2},
            )[0],
        )
        self.assertEqual(
            "match",
            compare_capture(
                {"phase": "compile_error", "stdout": "diagnostic A", "exit_code": 1},
                {"phase": "compile_error", "stdout": "diagnostic B", "exit_code": 2},
            )[0],
        )

    def test_no_original_is_pending_and_local_expectations_still_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "fixtures" / "hello.bb"
            source.parent.mkdir()
            source.write_text("Print 1\n", encoding="utf-8")
            manifest = root / "fixtures.json"
            manifest.write_text(json.dumps({"fixtures": [{
                "name": "hello", "source": "fixtures/hello.bb",
                "expected_phase": "run", "expected_stdout": "hello\n",
            }]}), encoding="utf-8")
            calls = [Mock(returncode=0, stdout="", stderr=""),
                     Mock(returncode=0, stdout="hello\n", stderr="")]
            runner = Mock(side_effect=calls)
            report = run_harness("dotnet", "cli.dll", manifest, root / "report.json", runner=runner)
            self.assertEqual("pass", report["fixtures"][0]["local_status"])
            self.assertEqual("pending", report["fixtures"][0]["comparison_status"])
            self.assertEqual(1, report["totals"]["comparison_pending"])
            self.assertFalse(any("original" in str(call.args) for call in runner.call_args_list))

    def test_malformed_original_adapter_is_adapter_error(self):
        self.assertEqual(
            "adapter_error",
            compare_capture({"phase": "run", "stdout": "x", "exit_code": 0}, "not json")[0],
        )
        self.assertEqual(
            "adapter_error",
            compare_capture({"phase": "run", "stdout": "x", "exit_code": 0},
                            {"phase": "run", "stdout": "x", "exit_code": True})[0],
        )
        self.assertEqual(
            "adapter_error",
            compare_capture({"phase": "run", "stdout": "x", "exit_code": 0},
                            {"phase": "run", "stdout": "x", "exit_code": 0, "stderr": ""})[0],
        )

    def test_manifest_rejects_duplicate_names_and_missing_sources(self):
        with tempfile.TemporaryDirectory() as temp:
            manifest = Path(temp) / "fixtures.json"
            item = {"name": "dupe", "source": "missing.bb", "expected_phase": "run"}
            manifest.write_text(json.dumps({"fixtures": [item, item]}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate fixture name"):
                load_manifest(manifest)
            manifest.write_text(json.dumps({"fixtures": [item]}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "does not exist"):
                run_harness("dotnet", "cli.dll", manifest, Path(temp) / "report.json",
                            runner=Mock())

    def test_original_timeout_is_adapter_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "hello.bb"
            source.write_text("Print 1\n", encoding="utf-8")
            manifest = root / "fixtures.json"
            manifest.write_text(json.dumps({"fixtures": [{
                "name": "hello", "source": "hello.bb", "expected_phase": "run",
            }]}), encoding="utf-8")
            runner = Mock(side_effect=[
                Mock(returncode=0, stdout="", stderr=""),
                Mock(returncode=0, stdout="ok\n", stderr=""),
                subprocess.TimeoutExpired(["adapter"], 1),
            ])
            report = run_harness("dotnet", "cli.dll", manifest, root / "report.json",
                                 original_runner="adapter", timeout=1, runner=runner)
            self.assertEqual("adapter_error", report["fixtures"][0]["comparison_status"])

    def test_local_timeout_fails_even_when_runtime_error_is_expected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "hello.bb").write_text("Print 1\n", encoding="utf-8")
            manifest = root / "fixtures.json"
            manifest.write_text(json.dumps({"fixtures": [{
                "name": "hello", "source": "hello.bb", "expected_phase": "runtime_error",
            }]}), encoding="utf-8")
            runner = Mock(side_effect=[
                Mock(returncode=0, stdout="", stderr=""),
                subprocess.TimeoutExpired(["dotnet", "hello.dll"], 1),
            ])
            report = run_harness("dotnet", "cli.dll", manifest, root / "report.json",
                                 runner=runner)
            self.assertEqual("fail", report["fixtures"][0]["local_status"])
            self.assertTrue(report["fixtures"][0]["actual"]["timed_out"])

    def test_local_timeout_cannot_match_original_runtime_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "hello.bb").write_text("Print 1\n", encoding="utf-8")
            manifest = root / "fixtures.json"
            manifest.write_text(json.dumps({"fixtures": [{
                "name": "hello", "source": "hello.bb", "expected_phase": "runtime_error",
            }]}), encoding="utf-8")
            runner = Mock(side_effect=[
                Mock(returncode=0, stdout="", stderr=""),
                subprocess.TimeoutExpired(["dotnet", "hello.dll"], 1),
                Mock(returncode=0, stdout=json.dumps({
                    "phase": "runtime_error", "stdout": "", "exit_code": 7,
                }), stderr=""),
            ])
            report = run_harness("dotnet", "cli.dll", manifest, root / "report.json",
                                 original_runner="adapter", timeout=1, runner=runner)
            self.assertEqual("mismatch", report["fixtures"][0]["comparison_status"])
            self.assertIn("local process timed out", report["fixtures"][0]["comparison_reason"])

    def test_require_original_fails_when_report_has_pending_comparisons(self):
        pending = {"totals": {"local_fail": 0, "comparison_mismatch": 0,
                              "comparison_adapter_error": 0, "comparison_pending": 1}}
        with patch("tools.differential.run_harness", return_value=pending), \
             patch("builtins.print"):
            self.assertEqual(1, main(["--require-original"]))


if __name__ == "__main__":
    unittest.main()
