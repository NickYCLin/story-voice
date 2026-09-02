import asyncio
import importlib.util
import json
import shutil
import subprocess
from pathlib import Path
import sys
import tempfile
import types
import unittest


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
                subprocess.run(
                    [
                        "ffmpeg", "-y", "-f", "lavfi", "-i", "anullsrc=r=24000:cl=mono",
                        "-t", "0.2", "-c:a", "libmp3lame", "-q:a", "9", path,
                    ],
                    check=True,
                    capture_output=True,
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
