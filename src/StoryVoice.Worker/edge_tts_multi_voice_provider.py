#!/usr/bin/env python3
import argparse
import asyncio
import json
import math
import os
import sys
import tempfile
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any

import edge_tts


MANIFEST_SCHEMA_VERSION = "storyvoice:multi-voice-manifest:v1"
TIMELINE_SCHEMA_VERSION = "storyvoice:multi-voice-timeline:v1"
DEFAULT_MAX_CHARS = 5_000
DEFAULT_MAX_ATTEMPTS = 3
BREAK_CHARACTERS = "\n。！？；，、,.!?:："


def split_text(text: str, max_chars: int = DEFAULT_MAX_CHARS) -> list[str]:
    if max_chars < 1:
        raise ValueError("max_chars must be positive")

    remaining = text.strip()
    chunks: list[str] = []
    while remaining:
        if len(remaining) <= max_chars:
            chunks.append(remaining)
            break

        window = remaining[:max_chars]
        boundary = max(window.rfind(character) for character in BREAK_CHARACTERS)
        cut = boundary + 1 if boundary >= max_chars // 2 else max_chars
        chunk = remaining[:cut].strip()
        if chunk:
            chunks.append(chunk)
        remaining = remaining[cut:].lstrip()

    return chunks


async def run_subprocess(args: list[str], *, input_bytes: bytes | None = None) -> bytes:
    process = await asyncio.create_subprocess_exec(
        *args,
        stdin=asyncio.subprocess.PIPE if input_bytes is not None else None,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    try:
        stdout, _stderr = await process.communicate(input_bytes)
    except BaseException:
        if process.returncode is None:
            try:
                process.kill()
            except ProcessLookupError:
                pass
        await process.communicate()
        raise
    if process.returncode != 0:
        raise RuntimeError(f"{Path(args[0]).name} exited with {process.returncode}")
    return stdout


SubprocessRunner = Callable[..., Awaitable[bytes]]


async def generate_silence(
    path: Path,
    milliseconds: int,
    *,
    ffmpeg_bin: str = "ffmpeg",
    runner: SubprocessRunner | None = None,
) -> bool:
    seconds = max(milliseconds, 0) / 1000
    if seconds <= 0:
        return False

    run = runner or run_subprocess
    await run([
        ffmpeg_bin, "-y", "-f", "lavfi", "-i", "anullsrc=r=24000:cl=mono",
        "-t", f"{seconds:.3f}", "-c:a", "libmp3lame", "-q:a", "9", str(path),
    ])
    return True


async def concat_audio(
    parts: list[Path],
    output_path: Path,
    *,
    normalize_loudness: bool = False,
    target_i: float = -16.0,
    target_tp: float = -1.5,
    target_lra: float = 11.0,
    ffmpeg_bin: str = "ffmpeg",
    runner: SubprocessRunner | None = None,
) -> None:
    if not parts:
        raise ValueError("no audio parts to concatenate")

    list_path = output_path.parent / "concat_list.txt"
    with list_path.open("w", encoding="utf-8") as handle:
        for part in parts:
            escaped = str(part).replace("'", "'\\''")
            handle.write(f"file '{escaped}'\n")

    run = runner or run_subprocess
    if normalize_loudness:
        await run([
            ffmpeg_bin, "-y", "-f", "concat", "-safe", "0", "-i", str(list_path),
            "-af", f"loudnorm=I={target_i:.1f}:TP={target_tp:.1f}:LRA={target_lra:.1f}",
            "-c:a", "libmp3lame", "-q:a", "2", str(output_path),
        ])
    else:
        await run([
            ffmpeg_bin, "-y", "-f", "concat", "-safe", "0", "-i", str(list_path),
            "-c", "copy", str(output_path),
        ])


async def probe_duration_seconds(
    path: Path,
    *,
    ffprobe_bin: str = "ffprobe",
    runner: SubprocessRunner | None = None,
) -> float:
    run = runner or run_subprocess
    output = await run([
        ffprobe_bin, "-v", "error", "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1", str(path),
    ])
    try:
        return float(output.decode().strip())
    except ValueError as error:
        raise RuntimeError("ffprobe did not report a usable duration") from error


async def synthesize_multi_voice(
    turns: list[dict[str, Any]],
    output_path: str,
    *,
    normalize_loudness: bool = False,
    max_chars: int = DEFAULT_MAX_CHARS,
    max_attempts: int = DEFAULT_MAX_ATTEMPTS,
    max_concurrent_chunks: int = 1,
    communicator_factory: Callable[..., Any] | None = None,
    delay: Callable[[float], Awaitable[None]] | None = None,
    progress_reporter: Callable[[int, int], None] | None = None,
    subprocess_runner: SubprocessRunner | None = None,
    ffmpeg_bin: str = "ffmpeg",
    ffprobe_bin: str = "ffprobe",
) -> list[dict[str, int]]:
    if not turns:
        raise ValueError("manifest has no turns")
    if max_attempts < 1:
        raise ValueError("max_attempts must be positive")
    if type(max_concurrent_chunks) is not int or not 1 <= max_concurrent_chunks <= 4:
        raise ValueError("max_concurrent_chunks must be an integer between 1 and 4")
    for turn in turns:
        if not str(turn.get("text", "")).strip():
            raise ValueError("a turn's text is empty")
        if not str(turn.get("voice", "")).strip():
            raise ValueError("a turn is missing a voice")

    output = Path(output_path).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    factory = communicator_factory or edge_tts.Communicate
    wait = delay or asyncio.sleep
    run = subprocess_runner or run_subprocess

    turn_chunks = [split_text(str(turn["text"]), max_chars) for turn in turns]
    total_chunks = sum(len(chunks) for chunks in turn_chunks)
    if total_chunks == 0:
        raise ValueError("no synthesizable text in manifest")

    async def probe_part_ms(path: Path) -> int:
        seconds = await probe_duration_seconds(path, ffprobe_bin=ffprobe_bin, runner=run)
        if not math.isfinite(seconds) or seconds <= 0:
            raise RuntimeError("ffprobe reported an unusable part duration")
        return round(seconds * 1000)

    completed = 0
    timeline: list[dict[str, int]] = []
    # Integer-millisecond cursor built from per-part probed durations, so turn boundaries stay
    # monotonic by construction instead of accumulating float rounding drift.
    cursor_ms = 0
    with tempfile.TemporaryDirectory(prefix="edge-tts-multi-", dir=output.parent) as directory:
        work = Path(directory)
        sequence: list[Path] = []
        durations: dict[tuple[int, int], int] = {}
        pending = iter(
            (turn_index, chunk_index, turn, chunk)
            for turn_index, (turn, chunks) in enumerate(zip(turns, turn_chunks))
            for chunk_index, chunk in enumerate(chunks)
        )
        failed = False

        async def synthesize_chunks() -> None:
            nonlocal completed, failed
            while not failed:
                item = next(pending, None)
                if item is None:
                    return
                turn_index, chunk_index, turn, chunk = item
                part = work / f"{turn_index:04d}-{chunk_index:04d}.mp3"
                try:
                    for attempt in range(1, max_attempts + 1):
                        part.unlink(missing_ok=True)
                        try:
                            communicate = factory(chunk, str(turn["voice"]),
                                rate=str(turn.get("rate", "+0%")),
                                pitch=str(turn.get("pitch", "+0Hz")),
                                volume=str(turn.get("volume", "+0%")))
                            await communicate.save(str(part))
                            if not part.exists() or part.stat().st_size < 1:
                                raise RuntimeError("edge-tts returned empty audio")
                            break
                        except Exception:
                            if attempt == max_attempts:
                                raise RuntimeError(
                                    f"edge-tts turn {turn_index + 1} chunk {chunk_index + 1} "
                                    f"failed after {max_attempts} attempts"
                                ) from None
                            await wait(float(2 ** (attempt - 1)))
                    durations[turn_index, chunk_index] = await probe_part_ms(part)
                    completed += 1
                    if progress_reporter is not None:
                        progress_reporter(completed, total_chunks)
                except BaseException:
                    failed = True
                    raise

        workers = [asyncio.create_task(synthesize_chunks())
                   for _ in range(min(max_concurrent_chunks, total_chunks))]
        try:
            await asyncio.gather(*workers)
        except BaseException:
            for worker in workers:
                worker.cancel()
            # No task may retain a part file when TemporaryDirectory removes it.
            await asyncio.gather(*workers, return_exceptions=True)
            raise

        # Completion order never controls playback order or speaker pauses.
        for turn_index, (turn, chunks) in enumerate(zip(turns, turn_chunks)):
            pause_before_ms = int(turn.get("pauseBeforeMs", 0) or 0)
            silence_path = work / f"{turn_index:04d}-pause.mp3"
            if await generate_silence(
                silence_path, pause_before_ms, ffmpeg_bin=ffmpeg_bin, runner=run
            ):
                sequence.append(silence_path)
                cursor_ms += await probe_part_ms(silence_path)

            turn_start_ms = cursor_ms
            for chunk_index in range(len(chunks)):
                part = work / f"{turn_index:04d}-{chunk_index:04d}.mp3"
                sequence.append(part)
                cursor_ms += durations[turn_index, chunk_index]

            timeline.append({
                "index": turn_index,
                "startMs": turn_start_ms,
                "durationMs": cursor_ms - turn_start_ms,
            })

        candidate = work / "complete.mp3"
        await concat_audio(
            sequence,
            candidate,
            normalize_loudness=normalize_loudness,
            ffmpeg_bin=ffmpeg_bin,
            runner=run,
        )
        duration = await probe_duration_seconds(candidate, ffprobe_bin=ffprobe_bin, runner=run)
        if duration <= 0 or not candidate.exists() or candidate.stat().st_size < 1:
            raise RuntimeError("multi-voice synthesis produced no usable audio")
        os.replace(candidate, output)

    return timeline


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--max-concurrent-chunks", type=int, choices=range(1, 5), default=1)
    parser.add_argument("--no-loudness-norm", action="store_true", help="Disable EBU R128 loudness normalization")
    args = parser.parse_args()

    manifest = json.loads(sys.stdin.read())
    if manifest.get("schemaVersion") != MANIFEST_SCHEMA_VERSION:
        raise ValueError(
            f"unsupported manifest schemaVersion: {manifest.get('schemaVersion')!r}"
        )

    timeline = await synthesize_multi_voice(
        manifest["turns"],
        args.output,
        normalize_loudness=not args.no_loudness_norm,
        max_concurrent_chunks=args.max_concurrent_chunks,
        progress_reporter=lambda completed, total: print(
            f"STORYVOICE_PROGRESS {completed}/{total}",
            file=sys.stderr,
            flush=True,
        ),
    )
    # stdout carries only this machine-readable timeline; diagnostics stay on stderr.
    print(
        json.dumps({"schemaVersion": TIMELINE_SCHEMA_VERSION, "turns": timeline}),
        flush=True,
    )


if __name__ == "__main__":
    asyncio.run(main())
