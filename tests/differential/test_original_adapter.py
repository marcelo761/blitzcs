"""Adapter unit tests; subprocess doubles are not original Blitz3D evidence."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from tools.original_blitz3d_adapter import AdapterError, instrument, capture, stage_project


class OriginalAdapterTests(unittest.TestCase):
    def test_instrumentation_preserves_expressions_strings_comments_and_inline_flow(self):
        result = instrument('If Touch() Then Print "Print:End" ; Print 9\nPrint -2^2\nEnd Function\n', 'Z:\\out.txt', 'Z:\\done.txt')
        self.assertIn('If Touch() Then WriteLine BlitzCSCaptureFile, "Print:End" ; Print 9', result)
        self.assertIn('WriteLine BlitzCSCaptureFile, -2^2', result)
        self.assertIn('End Function', result)

    def test_explicit_end_and_empty_print_have_capture_equivalents(self):
        result = instrument('Print\nIf 1 Then End\n', 'Z:\\out.txt', 'Z:\\done.txt')
        self.assertIn('WriteLine BlitzCSCaptureFile, ""\n', result)
        self.assertIn('If 1 Then BlitzCSCaptureFinish():End', result)

    def test_literal_block_markers_and_empty_print_before_else(self):
        result = instrument('Print "/* Print End */"\nIf 0 Then Print Else Print "ok"', 'out', 'done')
        self.assertIn('WriteLine BlitzCSCaptureFile, "/* Print End */"', result)
        self.assertIn('Then WriteLine BlitzCSCaptureFile, "" Else WriteLine BlitzCSCaptureFile, "ok"', result)

    def test_unsupported_includes_and_capture_name_collisions_fail_closed(self):
        for source in ['Include "other.bb"', 'Global blitzcscapturefile=1', 'Dialect "modern"', '/* comment */']:
            with self.assertRaises(AdapterError):
                instrument(source, 'out', 'done')

    def test_project_instrumentation_keeps_include_tree_and_wraps_each_source(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'sub').mkdir()
            main = root / 'main.bb'
            child = root / 'sub' / 'child.bb'
            main.write_text('Print 1\nInclude "sub/child.bb"\nPrint 3\n')
            child.write_text('Print 2\n')
            staged_root, hashes = stage_project(main, root / 'stage')
            staged_main = staged_root.read_text()
            staged_child = (root / 'stage' / 'sub' / 'child.bb').read_text()
            self.assertIn('Include "BlitzCSCaptureHelper.bb"', staged_main)
            self.assertIn('Include "sub/child.bb"', staged_main)
            self.assertIn('WriteLine BlitzCSCaptureFile, 1', staged_main)
            self.assertIn('WriteLine BlitzCSCaptureFile, 2', staged_child)
            self.assertEqual({'main.bb', 'sub/child.bb'}, set(hashes))

    def test_stage_project_retains_cycle_and_duplicate_include_edges(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            main = root / 'main.bb'
            child = root / 'child.bb'
            main.write_text('Include "child.bb"\nInclude "child.bb"\n')
            child.write_text('Include "main.bb"\nPrint 42\n')
            staged, hashes = stage_project(main, root / 'stage')
            self.assertIn('Include "child.bb"', staged.read_text())
            staged_child = (root / 'stage' / 'child.bb').read_text()
            self.assertIn('Include "main.bb"', staged_child)
            self.assertEqual({'main.bb', 'child.bb'}, set(hashes))

    def test_missing_include_is_left_for_the_original_compiler_to_diagnose(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            main = root / 'main.bb'
            main.write_text('Include "missing.bb"\n')
            staged, hashes = stage_project(main, root / 'stage')
            self.assertIn('Include "missing.bb"', staged.read_text())
            self.assertEqual({'main.bb'}, set(hashes))

    def test_included_function_body_does_not_get_root_completion_brackets(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            main = root / 'main.bb'
            child = root / 'child.bb'
            main.write_text('Function Value%()\nInclude "child.bb"\nReturn 42\nEnd Function\nPrint Value()\n')
            child.write_text('Print 7\n')
            staged, _ = stage_project(main, root / 'stage')
            staged_child = (root / 'stage' / 'child.bb').read_text()
            self.assertIn('WriteLine BlitzCSCaptureFile, 7', staged_child)
            self.assertIn('Return 42', staged.read_text())
            self.assertNotIn('BlitzCSCaptureDepth', staged_child)

    def test_compile_error_requires_original_source_diagnostic(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'input.bb'
            source.write_text('Exit\n')
            config = dict(home=root, wine='wine', prefix=root, display=':99', timeout=1)
            failed = Mock(returncode=255, stdout='"input.bb":2:1:2:1:Exit without loop\n', stderr='')
            result = capture(source, **config, runner=Mock(return_value=failed))
            self.assertEqual('compile_error', result['phase'])
            with self.assertRaises(AdapterError):
                capture(source, **config, runner=Mock(return_value=Mock(returncode=255, stdout='Unable to open runtime.dll\n', stderr='')))

    def test_timeout_is_infrastructure_failure_not_original_runtime_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'input.bb'
            source.write_text('Print 1\n')
            runner = Mock(side_effect=subprocess.TimeoutExpired(['wine'], 1))
            with self.assertRaises(AdapterError):
                capture(source, home=root, wine='wine', prefix=root, display=':99', timeout=1, runner=runner)

    def test_compile_timeout_is_recorded_before_adapter_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'input.bb'
            source.write_text('Print 1\n')
            evidence_dir = root / 'evidence'
            timeout_error = subprocess.TimeoutExpired(
                ['wine', 'blitzcc.exe'], 2.5,
                output=b'compile output so far', stderr=b'compile error so far',
            )
            runner = Mock(side_effect=timeout_error)
            with patch.dict(os.environ, {'BLITZ3D_EVIDENCE_DIR': str(evidence_dir)}):
                with self.assertRaises(AdapterError):
                    capture(source, home=root, wine='wine', prefix=root, display=':99',
                            timeout=2.5, runner=runner)

            record = json.loads((evidence_dir / 'input.json').read_text())['processes'][0]
            self.assertEqual('compile', record['stage'])
            self.assertEqual(runner.call_args.args[0], record['command'])
            self.assertEqual(2.5, record['timeout_seconds'])
            self.assertTrue(record['timed_out'])
            self.assertEqual('compile output so far', record['stdout'])
            self.assertEqual('compile error so far', record['stderr'])

    def test_run_timeout_is_recorded_with_partial_streams_and_remains_adapter_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'input.bb'
            source.write_text('Print 1\n')
            evidence_dir = root / 'evidence'
            commands = []

            def fake_process(command, **kwargs):
                commands.append(command)
                if '-o' in command:
                    program = Path(command[command.index('-o') + 1][2:].replace('\\', '/'))
                    program.touch()
                    return Mock(returncode=0, stdout='', stderr='')
                raise subprocess.TimeoutExpired(
                    command, kwargs['timeout'],
                    output=b'run output so far', stderr=b'run error so far',
                )

            with patch.dict(os.environ, {'BLITZ3D_EVIDENCE_DIR': str(evidence_dir)}):
                with self.assertRaises(AdapterError):
                    capture(source, home=root, wine='wine', prefix=root, display=':99',
                            timeout=3, runner=fake_process)

            records = json.loads((evidence_dir / 'input.json').read_text())['processes']
            self.assertEqual(['compile', 'run'], [record['stage'] for record in records])
            record = records[-1]
            self.assertEqual(commands[-1], record['command'])
            self.assertEqual(3, record['timeout_seconds'])
            self.assertTrue(record['timed_out'])
            self.assertEqual('run output so far', record['stdout'])
            self.assertEqual('run error so far', record['stderr'])

    def test_native_runtime_diagnostic_preserves_actual_zero_exit_status(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'input.bb'
            source.write_text('Print 1\n')
            def fake_process(command, **kwargs):
                if '-o' in command:
                    program = Path(command[command.index('-o') + 1][2:].replace('\\', '/'))
                    program.touch()
                    (program.parent / 'stdout.txt').write_bytes(b'before\r\n')
                    return Mock(returncode=0, stdout='', stderr='')
                return Mock(returncode=0, stdout='', stderr='0042:trace:msgbox:MSGBOX_OnInit L"Array index out of bounds"\n')
            actual = capture(source, home=root, wine='wine', prefix=root, display=':99', timeout=1, runner=fake_process)
            self.assertEqual({'phase': 'runtime_error', 'stdout': 'before\n', 'exit_code': 0}, actual)

    def test_abnormal_exit_evidence_preserves_instrumented_stdout_and_completion_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'input.bb'
            source.write_text('Print 1\n')
            evidence_dir = root / 'evidence'
            capture_paths = []

            def record_capture_paths(text, output_path, completion_path):
                capture_paths[:] = [output_path, completion_path]
                return instrument(text, output_path, completion_path)

            def fake_process(command, **kwargs):
                if '-o' in command:
                    Path(command[command.index('-o') + 1][2:].replace('\\', '/')).touch()
                    return Mock(returncode=0, stdout='', stderr='')
                output_path, completion_path = [Path(path[2:].replace('\\', '/')) for path in capture_paths]
                output_path.write_bytes(b'1\r\n')
                completion_path.write_bytes(b'complete\r\n')
                return Mock(returncode=-11, stdout='', stderr='')

            with patch.dict(os.environ, {'BLITZ3D_EVIDENCE_DIR': str(evidence_dir)}):
                with patch('tools.original_blitz3d_adapter.instrument', side_effect=record_capture_paths):
                    with self.assertRaises(AdapterError):
                        capture(source, home=root, wine='wine', prefix=root, display=':99',
                                timeout=1, runner=fake_process)

            evidence = json.loads((evidence_dir / 'input.json').read_text())
            self.assertEqual({'exists': True, 'size_bytes': 3, 'text': '1\r\n', 'hex': '310d0a'},
                             evidence['instrumented_stdout_file'])
            self.assertEqual({'exists': True, 'size_bytes': 10, 'text': 'complete\r\n',
                              'hex': '636f6d706c6574650d0a'}, evidence['completion_marker_file'])


if __name__ == '__main__':
    unittest.main()
