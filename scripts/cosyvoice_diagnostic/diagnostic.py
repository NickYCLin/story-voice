"""Offline, opt-in accent comparison. Default is validation only (no GPU/Redis)."""
from __future__ import annotations

import argparse
import asyncio
import gc
import hashlib
import importlib
import io
import json
import logging
import os
from pathlib import Path
import random
import subprocess
import sys
import tempfile
import wave

PREFIX = "You are a helpful assistant.<|endofprompt|>"
TAIWAN = "You are a helpful assistant. 請用自然的台灣國語說話。<|endofprompt|>"
MODES = ("A", "B", "C")
CONFIRMATIONS = (
    "private_evaluation_allowed", "taiwan_mandarin_confirmed",
    "single_speaker_confirmed", "full_words_confirmed", "transcript_verified",
)


class DiagnosticError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_private(path: Path, data: bytes) -> None:
    # Refuse existing files, including symlinks. Never replace a previous result.
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)


def read_request(path: Path) -> tuple[dict, bytes]:
    if path.stat().st_size > 32768:
        raise DiagnosticError("request too large")
    request = json.loads(path.read_text(encoding="utf-8"))
    if any(request.get(key) is not True for key in CONFIRMATIONS):
        raise DiagnosticError("reference confirmation incomplete")
    if not isinstance(request.get("evidence_id"), str) or not request["evidence_id"].strip():
        raise DiagnosticError("authorization evidence required")
    seed = request.get("seed")
    if type(seed) is not int or not 0 <= seed < 2**32:
        raise DiagnosticError("invalid seed")
    for key in ("text", "transcript"):
        value = request.get(key)
        if not isinstance(value, str) or not 1 <= len(value) <= 1000:
            raise DiagnosticError("invalid text")
        if value != value.strip() or "<|" in value or "|>" in value:
            raise DiagnosticError("use plain, trimmed text without prompt tokens")
    if digest(request["transcript"].encode()) != request.get("transcript_sha256"):
        raise DiagnosticError("transcript hash mismatch")
    reference = path.parent / request["reference_path"]
    if reference.stat().st_size > 10 * 1024 * 1024:
        raise DiagnosticError("reference too large")
    audio = reference.read_bytes()
    if digest(audio) != request.get("reference_sha256"):
        raise DiagnosticError("reference hash mismatch")
    with wave.open(io.BytesIO(audio), "rb") as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getcomptype()) != (1, 2, 48000, "NONE"):
            raise DiagnosticError("reference must be mono PCM16 48 kHz WAV")
        if not 10 <= wav.getnframes() / wav.getframerate() <= 30:
            raise DiagnosticError("reference must be 10–30 seconds")
        frames = wav.readframes(wav.getnframes())
        if len(frames) != wav.getnframes() * 2:
            raise DiagnosticError("truncated reference")
    # Bytes are snapshotted once; the source path is never reopened for inference.
    return request, audio


def verify_files(root: Path, files: dict) -> None:
    for name, expected in files.items():
        path = root / name
        hasher = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
                hasher.update(block)
        if hasher.hexdigest() != expected:
            raise DiagnosticError("runtime/model pin mismatch")


def ensure_gpu_idle(run=subprocess.run) -> None:
    result = run(
        ["nvidia-smi", "--query-compute-apps=pid", "--format=csv,noheader,nounits"],
        capture_output=True, text=True, timeout=10, check=False,
    )
    # Fail closed if visibility is unavailable; even an idle resident service
    # can hold a model in memory. Do not kill or restart any process here.
    if result.returncode or result.stdout.strip():
        raise DiagnosticError("GPU idle state not established")


def reset_seed(seed: int, numpy, torch) -> None:
    random.seed(seed)
    numpy.random.seed(seed)
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)


def compare(request: dict, reference: Path, output: Path, runtime) -> list[dict]:
    results = []
    for mode in MODES:
        instruction = None if mode == "A" else PREFIX if mode == "B" else TAIWAN
        prompt = PREFIX + request["transcript"] if mode == "A" else instruction
        # Normalize once and record exactly what is passed to inference. Using
        # frontend=False afterwards avoids a second, potentially different pass.
        segments = runtime.normalize(request["text"], split=True)
        actual_prompt = runtime.normalize(prompt, split=False) if mode == "A" else prompt
        name = mode + ".wav"
        row = {
            "mode": mode, "method": "inference_zero_shot" if mode == "A" else "inference_instruct2",
            "seed": request["seed"], "reference_sha256": request["reference_sha256"],
            "transcript_sha256": request["transcript_sha256"],
            "normalized_text": segments, "actual_prompt": actual_prompt,
            "frontend": runtime.frontend_name, "zero_shot_spk_id": "",
            "text_frontend_during_inference": False, "speed": 1.0, "stream": False,
            "audio_file": name,
        }
        write_private(output / (mode + ".input.json"), json.dumps(row, ensure_ascii=False, indent=2).encode())
        runtime.reset_seed(request["seed"])
        audio = runtime.synthesize(mode, segments, actual_prompt, reference)
        write_private(output / name, audio)
        row["audio_sha256"] = digest(audio)
        write_private(output / (mode + ".json"), json.dumps(row, ensure_ascii=False, indent=2).encode())
        results.append(row)
    return results


class CosyVoiceRuntime:
    def __init__(self):
        self.model = None
        self.torch = None

    def load(self):
        # Called only while the existing executor owns the shared GPU lease.
        import numpy
        import torch
        import torchaudio
        from cosyvoice.cli.cosyvoice import AutoModel
        self.numpy, self.torch, self.torchaudio = numpy, torch, torchaudio
        if not torch.cuda.is_available():
            raise DiagnosticError("GPU unavailable")
        self.model = AutoModel(model_dir="/models/cosyvoice3")
        if self.model.__class__.__name__ != "CosyVoice3":
            raise DiagnosticError("unexpected model class")
        self.frontend_name = self.model.frontend.text_frontend

    def normalize(self, text, *, split):
        return self.model.frontend.text_normalize(text, split=split, text_frontend=True)

    def reset_seed(self, seed):
        reset_seed(seed, self.numpy, self.torch)

    def synthesize(self, mode, segments, prompt, reference):
        method = self.model.inference_zero_shot if mode == "A" else self.model.inference_instruct2
        chunks = []
        for segment in segments:
            for item in method(segment, prompt, str(reference), zero_shot_spk_id="",
                               stream=False, speed=1.0, text_frontend=False):
                chunks.append(item["tts_speech"].detach().cpu())
        if not chunks:
            raise DiagnosticError("empty synthesis")
        waveform = self.torch.cat(chunks, dim=-1)
        if not self.torch.isfinite(waveform).all():
            raise DiagnosticError("non-finite synthesis")
        output = io.BytesIO()
        self.torchaudio.save(output, waveform, self.model.sample_rate, format="wav",
                            encoding="PCM_S", bits_per_sample=16)
        return output.getvalue()

    def close(self):
        self.model = None
        gc.collect()
        if self.torch is not None and self.torch.cuda.is_available():
            self.torch.cuda.synchronize()
            self.torch.cuda.empty_cache()
            self.torch.cuda.synchronize()


def locked_comparison(request, reference, output, factory=CosyVoiceRuntime,
                      idle_check=ensure_gpu_idle, fatal_exit=os._exit):
    idle_check()
    runtime = factory()
    try:
        runtime.load()
        return compare(request, reference, output, runtime)
    finally:
        try:
            runtime.close()
        except BaseException:
            # Do not let the executor release a lease with uncertain CUDA state.
            fatal_exit(70)
            raise DiagnosticError("GPU cleanup uncertain")


async def under_executor(executor, lock, operation):
    try:
        return await executor.execute(operation)
    finally:
        await lock.close()


def execute(request, audio, output, pins):
    sys.dont_write_bytecode = True
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    verify_files(Path("/app"), pins["executor_files"])
    verify_files(Path("/opt/CosyVoice"), pins["source_files"])
    verify_files(Path("/models/cosyvoice3"), pins["model_files"])
    if (Path("/models/cosyvoice3/cosyvoice.yaml").exists()
            or Path("/models/cosyvoice3/cosyvoice2.yaml").exists()):
        raise DiagnosticError("ambiguous AutoModel configuration")
    # Never import app.py: that would initialize the web app and work roots.
    sys.path[:0] = ["/app", "/opt/CosyVoice", "/opt/CosyVoice/third_party/Matcha-TTS"]
    GpuExecutor = importlib.import_module("gpu_executor").GpuExecutor
    RedisGpuExecutionLock = importlib.import_module("gpu_lock").RedisGpuExecutionLock
    settings = importlib.import_module("gpu_settings").GpuExecutorSettings.from_env()
    lock = RedisGpuExecutionLock(settings.redis_url, settings.lease_milliseconds)
    executor = GpuExecutor(lock, queue_timeout_seconds=0.05,
                           watchdog_seconds=settings.watchdog_seconds,
                           heartbeat_interval_seconds=settings.heartbeat_interval_seconds)
    # Diagnostic text is private; native CosyVoice logs may contain prompts.
    logging.disable(logging.CRITICAL)
    with tempfile.TemporaryDirectory(prefix="reference-", dir=output) as temporary:
        reference = Path(temporary) / "reference.wav"
        write_private(reference, audio)
        return asyncio.run(under_executor(
            executor, lock, lambda: locked_comparison(request, reference, output)))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path)
    parser.add_argument("--execute", action="store_true", help="opt in to real GPU inference")
    parser.add_argument("--output", type=Path, help="new private directory; required for execution")
    args = parser.parse_args(argv)
    try:
        request, audio = read_request(args.request)
        if not args.execute:
            print(json.dumps({"validated": True, "executed": False, "modes": MODES,
                              "reference_sha256": digest(audio)}))
            return 0
        if args.output is None:
            raise DiagnosticError("output required")
        pins = json.loads(Path(__file__).with_name("pins.json").read_text())
        args.output.mkdir(mode=0o700, parents=False, exist_ok=False)
        # A failed/uncertain attempt must remain distinguishable from success.
        write_private(args.output / "started.json", json.dumps({
            "state": "started", "pins": pins, "evidence_id": request["evidence_id"],
            "reference_sha256": digest(audio), "seed": request["seed"],
        }, indent=2).encode())
        results = execute(request, audio, args.output, pins)
        write_private(args.output / "completed.json", json.dumps({"state": "completed", "results": results},
                      ensure_ascii=False, indent=2).encode())
        print(json.dumps({"executed": True, "completed": True, "modes": MODES}))
        return 0
    except Exception as error:
        # Exception messages may include credentials, transcripts or filenames.
        print(json.dumps({"completed": False, "error_type": type(error).__name__,
                          "action": "Inspect private inputs/runtime and partial output; do not auto-retry."}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
