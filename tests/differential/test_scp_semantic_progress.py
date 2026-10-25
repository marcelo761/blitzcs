import hashlib
import json
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import subprocess
from pathlib import Path

from tools.scp_semantic_progress import extract_signatures, external_signatures, advance, default_value, main


class SemanticProgressContractTests(unittest.TestCase):
    def fixture(self, root, source):
        file = root / 'bbgraphics.cpp'
        file.write_text(source)
        catalog = root / 'native.json'
        catalog.write_text(json.dumps({'source_files': [
            {'file': file.name, 'sha256': hashlib.sha256(file.read_bytes()).hexdigest()}
        ]}))
        return catalog

    def test_exact_return_types_and_trailing_defaults_without_bindings(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            catalog = self.fixture(root, '\n'.join([
                'rtSym("%Windowed3D",fn);',
                'rtSym("Graphics3D%width%height%depth=0%mode=0",fn);',
                'rtSym("Flip%vwait=1",fn);',
                'rtSym("DebugLog$text",fn);']))
            signatures, _ = extract_signatures(catalog, root)
            self.assertEqual('Integer', signatures['windowed3d']['returnType'])
            self.assertEqual('Void', signatures['graphics3d']['returnType'])
            self.assertEqual([0, 0], signatures['graphics3d']['defaults'])
            self.assertEqual([1], signatures['flip']['defaults'])
            self.assertEqual(['String'], signatures['debuglog']['parameters'])
            self.assertFalse(any('method' in x for x in signatures.values()))

    def test_changed_original_source_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            catalog = self.fixture(root, 'rtSym("%Windowed3D",fn);')
            (root / 'bbgraphics.cpp').write_text('rtSym("%Windowed3D%invented",fn);')
            with self.assertRaisesRegex(ValueError, 'hash differs'):
                extract_signatures(catalog, root)

    def test_unsupported_or_conflicting_contracts_are_not_guessed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            catalog = self.fixture(root, '\n'.join([
                'rtSym("%Broken%a=0%b",fn);',
                'rtSym("%Bad%a=not_a_number",fn);',
                'rtSym("%Conflict",fn);',
                'rtSym("$Conflict",fn);']))
            signatures, rejected = extract_signatures(catalog, root)
            self.assertFalse(signatures)
            self.assertEqual({'broken', 'bad', 'conflict'}, {x['name'].lower() for x in rejected})

    def test_comments_and_unsupported_conflicts_do_not_create_verified_signatures(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            catalog = self.fixture(root, '\n'.join([
                '// rtSym("%Disabled%invented=9",fn);',
                '/* rtSym("%Commented",fn); */',
                'rtSym("%Thing%value",fn);',
                'rtSym("%Thing!unsupported",fn);']))
            signatures, rejected = extract_signatures(catalog, root)
            self.assertFalse(signatures)
            self.assertEqual(['Thing'], [x['name'] for x in rejected])

    def test_decls_pointers_and_partial_parameters_remain_explicit_dependencies(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'user32.decls').write_text('\n'.join([
                '.lib "user32.dll"',
                'api_SetWindowLong%(hwnd%,index%,value%):"SetWindowLongA"',
                'api_GetClientRect%(hwnd%,rect*):"GetClientRect"',
                'Bad%(a%garbage):"Bad"']))
            signatures, rejected = external_signatures(root)
            self.assertEqual(['api_setwindowlong'], list(signatures))
            self.assertEqual({'api_GetClientRect', 'Bad'}, {x['name'] for x in rejected})

    def test_output_cannot_overwrite_an_input_library(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runtime = root / 'runtime'
            runtime.mkdir()
            catalog = self.fixture(runtime, 'rtSym("%Windowed3D",fn);')
            source = root / 'source'
            source.mkdir()
            entry = source / 'main.bb'
            entry.write_text('Print Windowed3D()')
            library = root / 'binding.dll'
            library.write_bytes(b'preserve-me')
            bindings = root / 'bindings.json'
            bindings.write_text(json.dumps({'functions': [
                {'name': 'Bound', 'assembly': str(library), 'method': 'Bound', 'parameters': []}]}))
            with self.assertRaisesRegex(ValueError, 'output'):
                advance(entry, 'not-executed', root / 'cli.dll', bindings, catalog, runtime, library, 2, 1)
            self.assertEqual(b'preserve-me', library.read_bytes())

    def test_untagged_decls_return_is_void_and_strings_preserve_basic_backslashes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'commands.decls').write_text('EngineVoid(value%) : "Command"')
            signatures, _ = external_signatures(root)
            self.assertEqual('Void', signatures['enginevoid']['returnType'])
            from tools.generate_scp_native_manifest import parse_application_declarations
            self.assertEqual('void', parse_application_declarations(root)[2][0]['return_type'])
            self.assertEqual('C:\\new', default_value('"C:\\new"', 'string'))

    def setup_advance(self, root):
        runtime, source = root / 'runtime', root / 'source'
        runtime.mkdir()
        source.mkdir()
        native = self.fixture(runtime, 'rtSym("%Windowed3D",fn);')
        entry = source / 'main.bb'
        entry.write_text('Print Windowed3D()')
        bindings = root / 'bindings.json'
        bindings.write_text('{"functions": []}')
        return [entry, 'not-executed', root / 'cli.dll', bindings, native, runtime, root / 'generated.json']

    def test_output_cannot_overwrite_cli_runtime_assets(self):
        for asset in ['cli.deps.json', 'cli.runtimeconfig.json', 'compiler.deps.json']:
            with self.subTest(asset=asset), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                args = self.setup_advance(root)
                host = root / 'host'
                host.mkdir()
                args[2] = host / 'cli.dll'
                args[2].write_bytes(b'cli')
                output = host / asset
                output.write_text('preserve host asset')
                args[6] = output
                with patch('tools.scp_semantic_progress.subprocess.run', return_value=SimpleNamespace(returncode=0, stderr='', stdout='')) as run:
                    with self.assertRaisesRegex(ValueError, 'output'):
                        advance(*args, 0, 1)
                    run.assert_not_called()
                self.assertEqual('preserve host asset', output.read_text())

    def test_cli_report_cannot_overwrite_cli_configuration_through_symlink(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            configuration = args[2].with_suffix('.runtimeconfig.json')
            configuration.write_text('preserve CLI configuration')
            report = root / 'report.json'
            report.symlink_to(configuration)
            argv = ['scp_semantic_progress', '--entry', str(args[0]), '--cli', str(args[2]),
                    '--bindings', str(args[3]), '--native-catalog', str(args[4]),
                    '--runtime-source-dir', str(args[5]), '--catalog-output', str(args[6]),
                    '--report', str(report)]
            with patch('sys.argv', argv), patch('builtins.print'), patch('tools.scp_semantic_progress.subprocess.run') as run:
                self.assertEqual(2, main())
                run.assert_not_called()
            self.assertEqual('preserve CLI configuration', configuration.read_text())
            self.assertFalse(args[6].exists())

    def test_output_cannot_overwrite_symlinked_managed_dependency_configuration(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            libraries = root / 'libraries'
            libraries.mkdir()
            library = libraries / 'binding.dll'
            library.write_bytes(b'managed input')
            args[3].write_text(json.dumps({'functions': [{'name': 'Bound', 'assembly': str(library)}]}))
            args[6].write_text('preserve managed configuration')
            library.with_suffix('.deps.json').symlink_to(args[6])
            with patch('tools.scp_semantic_progress.subprocess.run', return_value=SimpleNamespace(returncode=0, stderr='', stdout='')) as run:
                with self.assertRaisesRegex(ValueError, 'output'):
                    advance(*args, 0, 1)
                run.assert_not_called()
            self.assertEqual('preserve managed configuration', args[6].read_text())

    def test_cli_rejects_catalog_and_report_hardlinked_to_each_other(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            args[6].write_text('preserve prior output')
            report = root / 'report.json'
            report.hardlink_to(args[6])
            argv = ['scp_semantic_progress', '--entry', str(args[0]), '--cli', str(args[2]),
                    '--bindings', str(args[3]), '--native-catalog', str(args[4]),
                    '--runtime-source-dir', str(args[5]), '--catalog-output', str(args[6]),
                    '--report', str(report)]
            with patch('sys.argv', argv), patch('builtins.print'), patch('tools.scp_semantic_progress.subprocess.run') as run:
                self.assertEqual(2, main())
                run.assert_not_called()
            self.assertEqual('preserve prior output', args[6].read_text())
            self.assertEqual('preserve prior output', report.read_text())

    def test_output_cannot_overwrite_cli_through_hardlink(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            args[2].write_bytes(b'preserve CLI assembly')
            args[6].hardlink_to(args[2])
            with patch('tools.scp_semantic_progress.subprocess.run', return_value=SimpleNamespace(returncode=0, stderr='', stdout='')) as run:
                with self.assertRaisesRegex(ValueError, 'output'):
                    advance(*args, 0, 1)
                run.assert_not_called()
            self.assertEqual(b'preserve CLI assembly', args[2].read_bytes())

    def test_output_hardlink_cannot_overwrite_secondary_source_inputs(self):
        for kind in ['included_basic', 'declarations', 'runtime_cpp']:
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                args = self.setup_advance(root)
                if kind == 'runtime_cpp':
                    original = args[5] / 'bbgraphics.cpp'
                elif kind == 'declarations':
                    original = args[0].parent / 'api.decls'
                    original.write_text('Bound%(value%):"Bound"')
                else:
                    original = args[0].parent / 'nested' / 'secondary.bb'
                    original.parent.mkdir()
                    original.write_text('; included source')
                    args[0].write_text('Include "nested/secondary.bb"\nPrint Windowed3D()')
                original_bytes = original.read_bytes()
                args[6].hardlink_to(original)
                with patch('tools.scp_semantic_progress.subprocess.run', return_value=SimpleNamespace(returncode=0, stderr='', stdout='')) as run:
                    with self.assertRaisesRegex(ValueError, 'output'):
                        advance(*args, 0, 1)
                    run.assert_not_called()
                self.assertEqual(original_bytes, original.read_bytes())

    def test_zero_budget_checks_once_without_adding_any_declaration(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            failure = SimpleNamespace(returncode=1, stderr="Unknown BASIC function or external symbol 'windowed3d'.", stdout='')
            with patch('tools.scp_semantic_progress.subprocess.run', return_value=failure) as run:
                result = advance(*args, 0, 1)
            self.assertEqual(1, run.call_count)
            self.assertFalse(result['semantic_validated'])
            self.assertFalse(result['assembly_emitted'])
            self.assertEqual([], result['added_declarations'])
            self.assertIn('check-semantics', run.call_args.args[0])

    def test_repeated_unknown_stops_and_generated_declarations_have_no_implementation(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            failure = SimpleNamespace(returncode=1, stderr="Unknown BASIC function or external symbol 'windowed3d'.", stdout='')
            with patch('tools.scp_semantic_progress.subprocess.run', return_value=failure) as run:
                result = advance(*args, 3, 1)
            self.assertEqual(2, run.call_count)
            self.assertEqual(['windowed3d'], result['added_declarations'])
            functions = json.loads((root / 'generated.json').read_text())['functions']
            self.assertEqual(1, len(functions))
            self.assertNotIn('method', functions[0])

    def test_timeout_is_not_semantic_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            args[6].write_text('preserve prior catalog')
            with patch('tools.scp_semantic_progress.subprocess.run', side_effect=subprocess.TimeoutExpired('cli', 1)):
                with self.assertRaises(subprocess.TimeoutExpired):
                    advance(*args, 1, 1)
            self.assertEqual('preserve prior catalog', args[6].read_text())

    def test_invalid_iteration_and_timeout_budgets_are_rejected_before_execution(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            args = self.setup_advance(root)
            for budget, timeout in [(-1, 1), (1, 0), (1, float('nan')), (1, float('inf'))]:
                with self.subTest(budget=budget, timeout=timeout):
                    with self.assertRaises(ValueError):
                        advance(*args, budget, timeout)

    def test_unbound_external_is_a_dependency_failure_not_a_language_type_error(self):
        from tools.scp_compile_probe import classify_result
        self.assertEqual('external_missing', classify_result('compiler_error',
            "file.bb(3,7): External function 'windowed3d' has a declared signature but no implementation."))

    def test_original_lifecycle_validation_requires_observed_sizes_and_completion_points(self):
        from tools.probe_windowed3d_original import lifecycle_errors
        capture = json.loads(Path('docs/evidence/m17-original.json').read_text())['capture']
        self.assertEqual([], lifecycle_errors(capture))
        wrong_size = dict(capture, stdout=capture['stdout'].replace('160x120', '1x1'))
        self.assertIn('graphics3d-size', lifecycle_errors(wrong_size))
        missing_flip = dict(capture, stdout=capture['stdout'].replace('flip-default-completed=1\n', ''))
        self.assertIn('flip-default-completed', lifecycle_errors(missing_flip))
