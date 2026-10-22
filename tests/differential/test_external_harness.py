"""External catalog plumbing; fake responses test harness mechanics only."""
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock
from tools.differential import run_harness


class ExternalHarnessTests(unittest.TestCase):
    def test_external_catalog_is_passed_only_to_basic_build(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "native.bb").write_text("Print FileSize(\"x\")\n")
            manifest = root / "fixtures.json"
            manifest.write_text(json.dumps({"fixtures": [{"name": "native", "source": "native.bb",
                "expected_phase": "run", "expected_stdout": "7\n"}]}))
            catalog = root / "native.json"
            catalog.write_text('{"functions": []}')
            runner = Mock(side_effect=[Mock(returncode=0, stdout="", stderr=""),
                                       Mock(returncode=0, stdout="7\n", stderr="")])
            report = run_harness("dotnet", "cli.dll", manifest, root / "report.json",
                                 runner=runner, externals=catalog)
            self.assertEqual("pass", report["fixtures"][0]["local_status"])
            self.assertEqual(["--externals", str(catalog.resolve())], runner.call_args_list[0].args[0][-2:])
            self.assertNotIn("--externals", runner.call_args_list[1].args[0])
