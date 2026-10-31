import asyncio
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import Mock, patch
import wave

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts/cosyvoice_diagnostic/diagnostic.py'
spec = importlib.util.spec_from_file_location('cosyvoice_diagnostic', SCRIPT)
d = importlib.util.module_from_spec(spec)
spec.loader.exec_module(d)


class InputTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        audio = io.BytesIO()
        with wave.open(audio, 'wb') as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(48000)
            wav.writeframes(b'\0' * 960000)
        self.audio = audio.getvalue()  # Synthetic silence, never a real voice.
        (self.root / 'reference.wav').write_bytes(self.audio)
        self.request = {'reference_path': 'reference.wav', 'reference_sha256': d.digest(self.audio),
                        'text': '今天先把課文看完。', 'transcript': '這是測試用逐字稿。',
                        'seed': 1986, 'evidence_id': 'unit-test-only',
                        **dict.fromkeys(d.CONFIRMATIONS, True)}
        self.request['transcript_sha256'] = d.digest(self.request['transcript'].encode())
        self.path = self.root / 'request.json'

    def save(self):
        self.path.write_text(json.dumps(self.request))
        return self.path

    def test_validation_never_imports_gpu_or_executes(self):
        with patch.object(d, 'execute') as execute, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(d.main([str(self.save())]), 0)
        execute.assert_not_called()

    def test_every_confirmation_is_required_and_must_be_boolean(self):
        for key in d.CONFIRMATIONS:
            for bad in [False, 'true', 1, None]:
                with self.subTest(key=key, value=bad):
                    self.request[key] = bad
                    with self.assertRaises(d.DiagnosticError):
                        d.read_request(self.save())
            self.request[key] = True

    def test_hash_mismatch_and_prompt_injection_rejected(self):
        self.request['reference_sha256'] = '0' * 64
        with self.assertRaises(d.DiagnosticError):
            d.read_request(self.save())
        self.request['reference_sha256'] = d.digest(self.audio)
        self.request['transcript'] += '<|endofprompt|>'
        with self.assertRaises(d.DiagnosticError):
            d.read_request(self.save())

    def test_transcript_change_requires_new_hash(self):
        self.request['transcript'] += '多一個字'
        with self.assertRaises(d.DiagnosticError):
            d.read_request(self.save())

    def test_reference_snapshot_survives_input_replacement(self):
        _, snapshot = d.read_request(self.save())
        (self.root / 'reference.wav').write_bytes(b'changed')
        self.assertEqual(snapshot, self.audio)

    def test_truncated_wav_rejected_even_with_matching_hash(self):
        truncated = self.audio[:-100]
        (self.root / 'reference.wav').write_bytes(truncated)
        self.request['reference_sha256'] = d.digest(truncated)
        with self.assertRaises(d.DiagnosticError):
            d.read_request(self.save())

    def test_invalid_seed(self):
        for seed in [True, -1, 2**32, 1.5]:
            self.request['seed'] = seed
            with self.assertRaises(d.DiagnosticError):
                d.read_request(self.save())

    def test_existing_output_not_overwritten(self):
        output = self.root / 'existing'
        output.mkdir()
        with contextlib.redirect_stderr(io.StringIO()), patch.object(d, 'execute') as execute:
            self.assertEqual(d.main([str(self.save()), '--execute', '--output', str(output)]), 1)
        execute.assert_not_called()

    def test_error_output_does_not_include_exception_text(self):
        with patch.object(d, 'read_request', side_effect=ValueError('secret transcript redis://credential')):
            stderr = io.StringIO()
            with contextlib.redirect_stderr(stderr):
                self.assertEqual(d.main(['unused']), 1)
            self.assertNotIn('credential', stderr.getvalue())
            self.assertNotIn('transcript', stderr.getvalue())

    def test_failed_run_has_no_completed_marker(self):
        output = self.root / 'failed'
        with patch.object(d, 'execute', side_effect=RuntimeError('failure')), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(d.main([str(self.save()), '--execute', '--output', str(output)]), 1)
        self.assertTrue((output / 'started.json').exists())
        self.assertFalse((output / 'completed.json').exists())
        self.assertEqual(output.stat().st_mode & 0o777, 0o700)
        self.assertEqual((output / 'started.json').stat().st_mode & 0o777, 0o600)


class ExecutionTests(unittest.TestCase):
    def test_matrix_resets_seed_and_records_actual_inputs(self):
        runtime = Mock()
        runtime.frontend_name = 'fake-normalizer'
        runtime.normalize.side_effect = lambda text, split: ['NORMALIZED'] if split else text
        runtime.synthesize.return_value = b'fake-audio'
        request = {'text': 'original', 'transcript': 'reference words', 'seed': 77,
                   'reference_sha256': 'reference-hash', 'transcript_sha256': 'transcript-hash'}
        with tempfile.TemporaryDirectory() as folder:
            rows = d.compare(request, Path('snapshot.wav'), Path(folder), runtime)
            self.assertEqual([r['mode'] for r in rows], ['A', 'B', 'C'])
            self.assertEqual(runtime.reset_seed.call_count, 3)
            calls = runtime.method_calls
            for i, call in enumerate(calls):
                if call[0] == 'synthesize':
                    self.assertEqual(calls[i-1][0], 'reset_seed')
                    self.assertEqual(calls[i-1].args, (77,))
            self.assertEqual(rows[0]['actual_prompt'], d.PREFIX + 'reference words')
            self.assertEqual(rows[1]['actual_prompt'], d.PREFIX)
            self.assertEqual(rows[2]['actual_prompt'], d.TAIWAN)
            self.assertTrue(all(row['normalized_text'] == ['NORMALIZED'] for row in rows))
            self.assertTrue(all(row['zero_shot_spk_id'] == '' for row in rows))
            self.assertTrue(all(not row['text_frontend_during_inference'] for row in rows))
            self.assertTrue(all(row['reference_sha256'] == 'reference-hash' for row in rows))
            self.assertEqual((Path(folder) / 'A.wav').stat().st_mode & 0o777, 0o600)

    def test_synthesis_routes_with_empty_cache_and_no_second_normalization(self):
        runtime = d.CosyVoiceRuntime()
        runtime.model = Mock()
        runtime.model.inference_zero_shot.return_value = []
        runtime.model.inference_instruct2.return_value = []
        for mode in d.MODES:
            with self.assertRaises(d.DiagnosticError):
                runtime.synthesize(mode, ['normalized'], 'prompt', Path('ref.wav'))
        self.assertEqual(runtime.model.inference_zero_shot.call_count, 1)
        self.assertEqual(runtime.model.inference_instruct2.call_count, 2)
        for method in [runtime.model.inference_zero_shot, runtime.model.inference_instruct2]:
            for call in method.call_args_list:
                self.assertEqual(call.kwargs, {'zero_shot_spk_id': '', 'stream': False,
                                              'speed': 1.0, 'text_frontend': False})

    def test_lease_denial_never_calls_operation(self):
        operation = Mock()
        class BusyExecutor:
            async def execute(self, callback):
                raise RuntimeError('busy')
        class Lock:
            closed = False
            async def close(self): self.closed = True
        lock = Lock()
        with self.assertRaises(RuntimeError):
            asyncio.run(d.under_executor(BusyExecutor(), lock, operation))
        operation.assert_not_called()
        self.assertTrue(lock.closed)

    def test_resident_gpu_process_blocks_model_load(self):
        factory = Mock()
        with self.assertRaises(d.DiagnosticError):
            d.locked_comparison({}, None, None, factory=factory,
                                idle_check=lambda: d.ensure_gpu_idle(
                                    lambda *a, **k: types.SimpleNamespace(returncode=0, stdout='1234\n')))
        factory.assert_not_called()

    def test_missing_gpu_visibility_fails_closed(self):
        with self.assertRaises(d.DiagnosticError):
            d.ensure_gpu_idle(lambda *a, **k: types.SimpleNamespace(returncode=1, stdout=''))

    def test_cleanup_finishes_before_executor_releases(self):
        events = []
        runtime = Mock()
        runtime.load.side_effect = lambda: events.append('load')
        runtime.close.side_effect = lambda: events.append('cleanup')
        class Executor:
            async def execute(self, callback):
                events.append('acquire')
                callback()
                events.append('release')
        class Lock:
            async def close(self): events.append('close-lock')
        with patch.object(d, 'compare', side_effect=lambda *a: events.append('compare')):
            asyncio.run(d.under_executor(Executor(), Lock(), lambda: d.locked_comparison(
                {}, None, None, factory=lambda: runtime, idle_check=lambda: None)))
        self.assertEqual(events, ['acquire', 'load', 'compare', 'cleanup', 'release', 'close-lock'])

    def test_cleanup_uncertainty_terminates_instead_of_returning(self):
        runtime = Mock()
        runtime.close.side_effect = RuntimeError('CUDA uncertain')
        fatal = Mock(side_effect=SystemExit(70))
        with patch.object(d, 'compare'), self.assertRaises(SystemExit):
            d.locked_comparison({}, None, None, factory=lambda: runtime,
                                idle_check=lambda: None, fatal_exit=fatal)
        fatal.assert_called_once_with(70)

    def test_reset_seed_covers_all_random_sources(self):
        numpy, torch = Mock(), Mock()
        with patch.object(d.random, 'seed') as python_seed:
            d.reset_seed(123, numpy, torch)
        python_seed.assert_called_once_with(123)
        numpy.random.seed.assert_called_once_with(123)
        torch.manual_seed.assert_called_once_with(123)
        torch.cuda.manual_seed_all.assert_called_once_with(123)

    def test_failed_inference_preserves_exact_attempt_inputs(self):
        runtime = Mock()
        runtime.frontend_name = 'fake'
        runtime.normalize.side_effect = lambda text, split: ['normalized'] if split else text
        runtime.synthesize.side_effect = RuntimeError('failed')
        request = {'text': 'original', 'transcript': 'reference', 'seed': 1,
                   'reference_sha256': 'hash', 'transcript_sha256': 'transcript-hash'}
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(RuntimeError):
                d.compare(request, Path('ref'), Path(folder), runtime)
            row = json.loads((Path(folder) / 'A.input.json').read_text())
            self.assertEqual(row['normalized_text'], ['normalized'])
            self.assertEqual(row['seed'], 1)
            self.assertFalse((Path(folder) / 'A.json').exists())
            self.assertFalse((Path(folder) / 'B.input.json').exists())

    def test_pin_mismatch_refuses_unknown_runtime(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / 'module.py').write_bytes(b'changed')
            with self.assertRaises(d.DiagnosticError):
                d.verify_files(Path(folder), {'module.py': '0' * 64})

    def test_private_writer_refuses_symlink(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'target'
            target.write_bytes(b'keep')
            link = Path(folder) / 'link'
            link.symlink_to(target)
            with self.assertRaises(FileExistsError):
                d.write_private(link, b'replace')
            self.assertEqual(target.read_bytes(), b'keep')


if __name__ == '__main__':
    unittest.main()
