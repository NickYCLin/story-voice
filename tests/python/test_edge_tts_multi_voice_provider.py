import asyncio
import importlib.util
import json
import shutil
from pathlib import Path
import sys
import tempfile
import traceback
import types
import unittest
from unittest.mock import AsyncMock, patch


fake_edge_tts = types.ModuleType("edge_tts")
fake_edge_tts.Communicate = object
sys.modules.setdefault("edge_tts", fake_edge_tts)
module_path = (
    Path(__file__).parents[2]
    / "src"
    / "StoryVoice.Worker"
    / "edge_tts_multi_voice_provider.py"
)
spec = importlib.util.spec_from_file_location("edge_tts_multi_voice_provider", module_path)
provider = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(provider)


class RecordingRunner:
    """Stubs out ffmpeg/ffprobe subprocess calls so most tests stay fast and
    deterministic, while still recording exactly what commands were requested."""

    def __init__(self):
        self.calls = []
        self.probe_duration = b"1.250000\n"

    async def __call__(self, args, *, input_bytes=None):
        self.calls.append(args)
        program = Path(args[0]).name
        if program == "ffmpeg":
            # The last argument is always the output path for both silence
            # generation and concat — write a marker file so downstream
            # existence/size checks succeed.
            output_path = Path(args[-1])
            output_path.write_bytes(b"fake-mp3-bytes")
            return b""
        if program == "ffprobe":
            return self.probe_duration
        raise AssertionError(f"unexpected subprocess: {args[0]}")


class EdgeTtsMultiVoiceProviderTests(unittest.IsolatedAsyncioTestCase):
    async def test_default_waits_for_one_chunk_before_starting_the_next(self):
        entered = asyncio.Queue()
        release = asyncio.Event()

        class WaitingCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text

            async def save(self, path):
                await entered.put(self.text)
                await release.wait()
                Path(path).write_bytes(b"chunk")

        with tempfile.TemporaryDirectory() as directory:
            task = asyncio.create_task(provider.synthesize_multi_voice(
                [{"text": text, "voice": "v"} for text in ["first", "second"]],
                str(Path(directory) / "book.mp3"), communicator_factory=WaitingCommunicate,
                subprocess_runner=RecordingRunner()))
            try:
                self.assertEqual("first", await asyncio.wait_for(entered.get(), 5))
                await asyncio.sleep(0)
                self.assertTrue(entered.empty())
                release.set()
                await asyncio.wait_for(task, 5)
                self.assertEqual("second", entered.get_nowait())
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_parallel_completion_keeps_chunk_order_timing_and_a_bounded_number_of_tasks(self):
        entered = asyncio.Queue()
        gates = {stem: asyncio.Event() for stem in ["0000-0000", "0000-0001", "0001-0000", "0001-0001"]}
        active = 0
        peak = 0
        reports = []
        three_finished = asyncio.Event()
        concat_lines = []

        class ControlledCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                pass

            async def save(self, path):
                nonlocal active, peak
                active += 1
                peak = max(peak, active)
                stem = Path(path).stem
                try:
                    await entered.put(stem)
                    await gates[stem].wait()
                    Path(path).write_bytes(stem.encode())
                finally:
                    active -= 1

        class DurationRunner(RecordingRunner):
            async def __call__(self, args, *, input_bytes=None):
                if "concat" in args:
                    concat_lines.extend(Path(args[args.index("-i") + 1]).read_text(encoding="utf-8").splitlines())
                if Path(args[0]).name == "ffprobe":
                    stem = Path(args[-1]).stem
                    if stem.endswith("-pause"):
                        return b"0.25"
                    if stem != "complete":
                        turn, chunk = map(int, stem.split("-"))
                        return str((turn * 2 + chunk + 1) / 10).encode()
                return await super().__call__(args, input_bytes=input_bytes)

        def report(completed, total):
            reports.append((completed, total))
            if completed == 3:
                three_finished.set()

        with tempfile.TemporaryDirectory() as directory:
            task = asyncio.create_task(provider.synthesize_multi_voice(
                [{"text": "Aa", "voice": "n"}, {"text": "Bb", "voice": "c", "pauseBeforeMs": 250}],
                str(Path(directory) / "book.mp3"), max_chars=1, max_concurrent_chunks=2,
                communicator_factory=ControlledCommunicate, subprocess_runner=DurationRunner(), progress_reporter=report))
            try:
                self.assertEqual("0000-0000", await asyncio.wait_for(entered.get(), 5))
                self.assertEqual("0000-0001", await asyncio.wait_for(entered.get(), 5))
                self.assertEqual(2, active)
                gates["0000-0001"].set()
                self.assertEqual("0001-0000", await asyncio.wait_for(entered.get(), 5))
                gates["0001-0000"].set()
                self.assertEqual("0001-0001", await asyncio.wait_for(entered.get(), 5))
                gates["0001-0001"].set()
                await asyncio.wait_for(three_finished.wait(), 5)
                self.assertFalse(task.done())
                gates["0000-0000"].set()
                timeline = await asyncio.wait_for(task, 5)
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
            self.assertEqual(2, peak)
            self.assertEqual(0, active)
            self.assertEqual([(1, 4), (2, 4), (3, 4), (4, 4)], reports)
            self.assertEqual([{"index": 0, "startMs": 0, "durationMs": 300},
                              {"index": 1, "startMs": 550, "durationMs": 700}], timeline)
            expected_parts = ["0000-0000.mp3", "0000-0001.mp3", "0001-pause.mp3", "0001-0000.mp3", "0001-0001.mp3"]
            self.assertEqual(len(expected_parts), len(concat_lines))
            for filename, line in zip(expected_parts, concat_lines):
                self.assertIn(filename, line)

    async def test_parallel_failure_cancels_siblings_before_cleanup_and_preserves_previous_output(self):
        second_entered = asyncio.Event()
        calls = []
        active = 0

        class FailingCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text

            async def save(self, path):
                nonlocal active
                active += 1
                calls.append(self.text)
                try:
                    if self.text == "first":
                        await second_entered.wait()
                        raise RuntimeError("synthetic-private-provider-detail")
                    second_entered.set()
                    await asyncio.Event().wait()
                finally:
                    await asyncio.sleep(0)
                    # Cleanup must await siblings before removing their directory.
                    Path(path).write_bytes(b"cleanup-marker")
                    active -= 1

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            output.write_bytes(b"previous-output")
            with self.assertRaises(RuntimeError) as raised:
                await asyncio.wait_for(provider.synthesize_multi_voice(
                    [{"text": text, "voice": "v"} for text in ["first", "second", "third"]],
                    str(output), max_concurrent_chunks=2, max_attempts=1,
                    communicator_factory=FailingCommunicate, subprocess_runner=RecordingRunner()), 5)
            self.assertNotIn("synthetic-private-provider-detail", "".join(traceback.format_exception(raised.exception)))
            self.assertEqual(["first", "second"], calls)
            self.assertEqual(0, active)
            self.assertEqual(b"previous-output", output.read_bytes())
            self.assertEqual([output], list(Path(directory).iterdir()))

    async def test_cancelling_parallel_synthesis_waits_for_all_writers(self):
        entered = asyncio.Queue()
        stopped = []

        class WaitingCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text

            async def save(self, path):
                await entered.put(self.text)
                try:
                    await asyncio.Event().wait()
                finally:
                    await asyncio.sleep(0)
                    Path(path).write_bytes(b"cleanup-marker")
                    stopped.append(self.text)

        with tempfile.TemporaryDirectory() as directory:
            task = asyncio.create_task(provider.synthesize_multi_voice(
                [{"text": text, "voice": "v"} for text in ["first", "second", "third"]],
                str(Path(directory) / "book.mp3"), max_concurrent_chunks=2,
                communicator_factory=WaitingCommunicate, subprocess_runner=RecordingRunner()))
            try:
                await asyncio.wait_for(entered.get(), 5)
                await asyncio.wait_for(entered.get(), 5)
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
            self.assertCountEqual(["first", "second"], stopped)
            self.assertTrue(entered.empty())
            self.assertEqual([], list(Path(directory).iterdir()))

    async def test_parallel_retry_only_repeats_the_failed_chunk(self):
        attempts = {}

        class FlakyCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text

            async def save(self, path):
                attempts[self.text] = attempts.get(self.text, 0) + 1
                if Path(path).exists():
                    raise AssertionError("partial output was not cleared before retry")
                Path(path).write_bytes(b"chunk")
                if self.text == "retry" and attempts[self.text] == 1:
                    raise RuntimeError("transient")

        with tempfile.TemporaryDirectory() as directory:
            await provider.synthesize_multi_voice(
                [{"text": text, "voice": "v"} for text in ["retry", "success"]],
                str(Path(directory) / "book.mp3"), max_concurrent_chunks=2,
                communicator_factory=FlakyCommunicate, subprocess_runner=RecordingRunner(),
                delay=lambda _: asyncio.sleep(0))
        self.assertEqual({"retry": 2, "success": 1}, attempts)

    async def test_invalid_parallel_limits_are_rejected_before_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            for limit in [0, 5, True, 1.5]:
                with self.subTest(limit=limit), self.assertRaises(ValueError):
                    await provider.synthesize_multi_voice([{"text": "test", "voice": "v"}],
                        str(Path(directory) / "book.mp3"), max_concurrent_chunks=limit)
            self.assertEqual([], list(Path(directory).iterdir()))

    async def test_cancelled_subprocess_is_killed_and_drained(self):
        entered = asyncio.Event()

        class Process:
            returncode = None
            drained = False

            async def communicate(self, _input=None):
                if self.returncode is not None:
                    self.drained = True
                    return b"", b""
                entered.set()
                await asyncio.Event().wait()

            def kill(self):
                self.returncode = -9

        process = Process()
        with patch.object(provider.asyncio, "create_subprocess_exec", new=AsyncMock(return_value=process)):
            task = asyncio.create_task(provider.run_subprocess(["synthetic-ffmpeg"]))
            await asyncio.wait_for(entered.wait(), 5)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual(-9, process.returncode)
        self.assertTrue(process.drained)

    async def test_synthesizes_each_turn_with_its_own_voice_and_reports_progress(self):
        saved = []

        class FakeCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text
                self.voice = voice
                self.rate = rate
                self.pitch = pitch
                self.volume = volume

            async def save(self, path):
                saved.append((self.text, self.voice, self.rate, self.pitch, self.volume))
                Path(path).write_bytes(b"chunk")

        reports = []
        runner = RecordingRunner()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            timeline = await provider.synthesize_multi_voice(
                [
                    {"text": "風穿過長廊。", "voice": "narrator-voice", "rate": "-5%", "pitch": "-2Hz", "volume": "-3%", "pauseBeforeMs": 0},
                    {"text": "「你終於來了。」", "voice": "alice-voice", "rate": "+0%", "pitch": "+4Hz", "volume": "+2%", "pauseBeforeMs": 200},
                ],
                str(output),
                communicator_factory=FakeCommunicate,
                subprocess_runner=runner,
                progress_reporter=lambda completed, total: reports.append((completed, total)),
            )

            self.assertTrue(output.exists())
            self.assertEqual(
                [
                    ("風穿過長廊。", "narrator-voice", "-5%", "-2Hz", "-3%"),
                    ("「你終於來了。」", "alice-voice", "+0%", "+4Hz", "+2%"),
                ],
                saved,
            )
            self.assertEqual([(1, 2), (2, 2)], reports)
            # Every probed part reports 1.25s: turn 0 is one chunk; turn 1 starts after that
            # chunk plus its own probed 1.25s silence clip, and its start excludes the pause.
            self.assertEqual(
                [
                    {"index": 0, "startMs": 0, "durationMs": 1250},
                    {"index": 1, "startMs": 2500, "durationMs": 1250},
                ],
                timeline,
            )

    async def test_generates_silence_only_when_pause_before_is_positive(self):
        class FakeCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                pass

            async def save(self, path):
                Path(path).write_bytes(b"chunk")

        runner = RecordingRunner()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            await provider.synthesize_multi_voice(
                [
                    {"text": "第一句", "voice": "v", "pauseBeforeMs": 0},
                    {"text": "第二句", "voice": "v", "pauseBeforeMs": 300},
                ],
                str(output),
                communicator_factory=FakeCommunicate,
                subprocess_runner=runner,
            )

        ffmpeg_calls = [call for call in runner.calls if Path(call[0]).name == "ffmpeg"]
        silence_calls = [call for call in ffmpeg_calls if "anullsrc=r=24000:cl=mono" in call]
        # Exactly one silence clip: the 300ms pause before the second turn.
        self.assertEqual(1, len(silence_calls))
        self.assertIn("0.300", " ".join(silence_calls[0]))

    async def test_concatenates_parts_in_order_via_ffmpeg_concat_demuxer_and_validates_with_ffprobe(self):
        class FakeCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                pass

            async def save(self, path):
                Path(path).write_bytes(b"chunk")

        runner = RecordingRunner()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            await provider.synthesize_multi_voice(
                [{"text": "一句話", "voice": "v", "pauseBeforeMs": 150}],
                str(output),
                communicator_factory=FakeCommunicate,
                subprocess_runner=runner,
            )

        concat_calls = [call for call in runner.calls if "-f" in call and "concat" in call]
        self.assertEqual(1, len(concat_calls))
        probe_calls = [call for call in runner.calls if Path(call[0]).name == "ffprobe"]
        # One timeline probe per part (the 150ms silence clip and the single chunk),
        # plus the final full-output validation probe.
        self.assertEqual(3, len(probe_calls))
        # The concat call must come after every synthesis/silence ffmpeg call, and the
        # final validation probe must be the very last subprocess invocation before publish.
        self.assertLess(runner.calls.index(concat_calls[0]), runner.calls.index(probe_calls[-1]))
        self.assertEqual(runner.calls[-1], probe_calls[-1])

    async def test_rejects_a_manifest_with_no_turns(self):
        with self.assertRaises(ValueError):
            await provider.synthesize_multi_voice(
                [], "/tmp/unused.mp3", subprocess_runner=RecordingRunner()
            )

    async def test_rejects_a_turn_with_blank_text_or_missing_voice(self):
        runner = RecordingRunner()
        with self.assertRaises(ValueError):
            await provider.synthesize_multi_voice(
                [{"text": "  ", "voice": "v"}], "/tmp/unused.mp3", subprocess_runner=runner
            )
        with self.assertRaises(ValueError):
            await provider.synthesize_multi_voice(
                [{"text": "hi", "voice": ""}], "/tmp/unused.mp3", subprocess_runner=runner
            )

    async def test_retries_a_failed_chunk_before_giving_up(self):
        attempts = {}

        class FlakyCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text

            async def save(self, path):
                attempts[self.text] = attempts.get(self.text, 0) + 1
                if attempts[self.text] == 1:
                    raise RuntimeError("transient")
                Path(path).write_bytes(b"chunk")

        async def no_delay(_seconds):
            await asyncio.sleep(0)

        runner = RecordingRunner()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            await provider.synthesize_multi_voice(
                [{"text": "重試片段", "voice": "v"}],
                str(output),
                communicator_factory=FlakyCommunicate,
                subprocess_runner=runner,
                delay=no_delay,
                max_attempts=3,
            )

        self.assertEqual(2, attempts["重試片段"])

    async def test_does_not_publish_when_ffprobe_reports_no_duration(self):
        class FakeCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                pass

            async def save(self, path):
                Path(path).write_bytes(b"chunk")

        class ZeroDurationRunner(RecordingRunner):
            def __init__(self):
                super().__init__()
                self.probe_duration = b"0.000000\n"

        runner = ZeroDurationRunner()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            with self.assertRaises(RuntimeError):
                await provider.synthesize_multi_voice(
                    [{"text": "一句話", "voice": "v"}],
                    str(output),
                    communicator_factory=FakeCommunicate,
                    subprocess_runner=runner,
                )

            self.assertFalse(output.exists())


@unittest.skipUnless(
    shutil.which("ffmpeg") and shutil.which("ffprobe"),
    "requires a real ffmpeg/ffprobe on PATH",
)
class EdgeTtsMultiVoiceProviderRealFfmpegTests(unittest.IsolatedAsyncioTestCase):
    """Exercises the actual ffmpeg concat + ffprobe pipeline (no network calls —
    each 'synthesized chunk' is a real tiny silent MP3 rendered by ffmpeg itself
    standing in for edge-tts output) so the audio composition contract is proven
    against the real binaries, not just a stubbed subprocess runner."""

    async def test_real_ffmpeg_concatenates_narrator_and_character_turns_into_valid_audio(self):
        class RealAudioCommunicate:
            def __init__(self, text, voice, rate, pitch, volume):
                self.text = text

            async def save(self, path):
                await provider.run_subprocess(
                    [
                        "ffmpeg", "-y", "-f", "lavfi", "-i", "anullsrc=r=24000:cl=mono",
                        "-t", "0.2", "-c:a", "libmp3lame", "-q:a", "9", path,
                    ],
                )

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "book.mp3"
            timeline = await provider.synthesize_multi_voice(
                [
                    {"text": "旁白開場。", "voice": "narrator", "pauseBeforeMs": 0},
                    {"text": "「你好。」", "voice": "alice", "pauseBeforeMs": 150},
                ],
                str(output),
                communicator_factory=RealAudioCommunicate,
                max_concurrent_chunks=2,
            )

            self.assertTrue(output.exists())
            self.assertGreater(output.stat().st_size, 0)
            duration = await provider.probe_duration_seconds(output)
            # Two ~0.2s spoken clips plus a 0.15s pause: comfortably over 0.4s,
            # comfortably under a generous upper bound.
            self.assertGreater(duration, 0.4)
            self.assertLess(duration, 3.0)
            # Real probed timings: turn 0 starts at zero; turn 1 starts after turn 0's
            # ~0.2s clip and its own ~0.15s pause (MP3 frame padding makes both a bit
            # longer than requested, so only sanity-bound the values).
            self.assertEqual([0, 1], [entry["index"] for entry in timeline])
            self.assertEqual(0, timeline[0]["startMs"])
            self.assertGreater(timeline[1]["startMs"], timeline[0]["durationMs"])
            self.assertLess(timeline[1]["startMs"], 1_500)
            self.assertLessEqual(
                timeline[1]["startMs"] + timeline[1]["durationMs"],
                round(duration * 1000) + 100,
            )


if __name__ == "__main__":
    unittest.main()
