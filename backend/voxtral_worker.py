import argparse
import contextlib
import json
import os
import re
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")
os.environ.setdefault("PYTHONUTF8", "1")
os.environ.setdefault("PYTHONIOENCODING", "utf-8")

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stdin, "reconfigure"):
    sys.stdin.reconfigure(encoding="utf-8")


def configure_visible_accelerators() -> None:
    preference = os.environ.get("VOXSCRIBE_HARDWARE_ACCELERATION", "auto").strip().lower()
    if preference in {"", "auto"}:
        return

    if preference == "cpu":
        for variable_name in ("CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES"):
            os.environ[variable_name] = "-1"
        return

    match = re.fullmatch(r"gpu:(\d+)", preference)
    if not match:
        return

    selected_index = match.group(1)
    os.environ.setdefault("CUDA_DEVICE_ORDER", "PCI_BUS_ID")
    for variable_name in ("CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES"):
        os.environ[variable_name] = selected_index


configure_visible_accelerators()

import numpy as np
import soundfile as sf
import torch
from huggingface_hub import snapshot_download

from crisperwhisper_compat import prepare_crisperwhisper_transformers_backend
from media_audio import read_audio_mono
from phonon_runtime import phonon_runtime_is_available, prepare_phonon_model

DEFAULT_MODEL_KIND = os.environ.get("VOXSCRIBE_MODEL_KIND", "voxtral")
DEFAULT_MODEL_ID = (
    os.environ.get("VOXSCRIBE_MODEL_REPO_ID")
    or os.environ.get("VOXTRAL_MODEL_ID")
    or "mistralai/Voxtral-Mini-3B-2507"
)
DEFAULT_MODEL_PATH = os.environ.get("VOXSCRIBE_MODEL_PATH") or os.environ.get("VOXTRAL_MODEL_PATH")
DEFAULT_LANGUAGE = os.environ.get("VOXSCRIBE_LANGUAGE") or os.environ.get("VOXTRAL_LANGUAGE") or None
SILENCE_RMS_THRESHOLD = float(os.environ.get("VOXTRAL_SILENCE_RMS_THRESHOLD", "0.003"))
SILENCE_PEAK_THRESHOLD = float(os.environ.get("VOXTRAL_SILENCE_PEAK_THRESHOLD", "0.015"))
VOXTRAL_REALTIME_GPU_MAX_GB = os.environ.get("VOXTRAL_REALTIME_GPU_MAX_GB", "10.5")
VOXTRAL_REALTIME_CPU_MAX_GB = os.environ.get("VOXTRAL_REALTIME_CPU_MAX_GB", "24")
VOXTRAL_GPU_MAX_GB = os.environ.get("VOXTRAL_GPU_MAX_GB")
VOXTRAL_CPU_MAX_GB = os.environ.get("VOXTRAL_CPU_MAX_GB", "32")
DEFAULT_CHUNK_OVERLAP_SECONDS = float(os.environ.get("VOXSCRIBE_CHUNK_OVERLAP_SECONDS", "4.0"))
MIN_CHUNK_SECONDS = float(os.environ.get("VOXSCRIBE_MIN_CHUNK_SECONDS", "12.0"))
VOXTRAL_MAX_CHUNK_SECONDS = float(os.environ.get("VOXSCRIBE_VOXTRAL_MAX_CHUNK_SECONDS", "45.0"))
VOXTRAL_REALTIME_MAX_CHUNK_SECONDS = float(os.environ.get("VOXSCRIBE_VOXTRAL_REALTIME_MAX_CHUNK_SECONDS", "24.0"))
WHISPER_MAX_CHUNK_SECONDS = float(os.environ.get("VOXSCRIBE_WHISPER_MAX_CHUNK_SECONDS", "30.0"))
QWEN_MAX_CHUNK_SECONDS = float(os.environ.get("VOXSCRIBE_QWEN_MAX_CHUNK_SECONDS", "60.0"))

_gpu_probe_result: tuple[bool, str | None] | None = None


def emit(**payload: Any) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def normalize_text(text: str) -> str:
    return text.replace("\r\n", "\n").strip()


def resample_audio(audio: np.ndarray, sampling_rate: int, target_sampling_rate: int) -> tuple[np.ndarray, int]:
    if sampling_rate == target_sampling_rate:
        return np.asarray(audio, dtype=np.float32), sampling_rate

    import librosa

    resampled = librosa.resample(audio, orig_sr=sampling_rate, target_sr=target_sampling_rate)
    return np.asarray(resampled, dtype=np.float32), target_sampling_rate


def write_temp_wav(audio: np.ndarray, sampling_rate: int) -> Path:
    handle = tempfile.NamedTemporaryFile(prefix="voxcribe-voxtral-", suffix=".wav", delete=False)
    handle.close()
    temp_path = Path(handle.name)
    sf.write(str(temp_path), audio, sampling_rate, subtype="PCM_16")
    return temp_path


def is_probably_silent(audio: np.ndarray) -> bool:
    if audio.size == 0:
        return True

    rms = float(np.sqrt(np.mean(np.square(audio), dtype=np.float64)))
    peak = float(np.max(np.abs(audio)))
    return rms < SILENCE_RMS_THRESHOLD and peak < SILENCE_PEAK_THRESHOLD


def probe_gpu_acceleration() -> tuple[bool, str | None]:
    global _gpu_probe_result
    if _gpu_probe_result is not None:
        return _gpu_probe_result

    if not torch.cuda.is_available():
        _gpu_probe_result = (False, None)
        return _gpu_probe_result

    try:
        # Device discovery and empty allocations can succeed even when the PyTorch
        # wheel has no executable kernel for the selected GPU (notably sm_120 with
        # a CUDA 12.6 wheel).  Exercise and synchronize a real kernel before any
        # model is placed on the accelerator.
        probe = torch.empty(1, device="cuda:0", dtype=torch.float32)
        probe.fill_(1.0)
        probe.mul_(2.0)
        torch.cuda.synchronize(0)
        if float(probe.cpu().item()) != 2.0:
            raise RuntimeError("Le test de calcul GPU a retourne un resultat invalide.")

        _gpu_probe_result = (True, None)
    except Exception as exc:  # noqa: BLE001
        _gpu_probe_result = (False, str(exc))

    return _gpu_probe_result


def gpu_acceleration_is_usable() -> bool:
    return probe_gpu_acceleration()[0]


def clear_torch_memory() -> None:
    if not gpu_acceleration_is_usable():
        return

    try:
        torch.cuda.empty_cache()
    except Exception:
        pass


def select_torch_dtype(prefer_bfloat16: bool = True) -> Any:
    if not gpu_acceleration_is_usable():
        return torch.float32

    if prefer_bfloat16:
        try:
            if torch.cuda.is_bf16_supported():
                return torch.bfloat16
        except Exception:
            pass

    return torch.float16


def build_max_memory(gpu_max_gb: str | None, cpu_max_gb: str) -> dict[Any, str]:
    max_memory: dict[Any, str] = {"cpu": f"{cpu_max_gb}GiB"}
    if not gpu_acceleration_is_usable():
        return max_memory

    if gpu_max_gb:
        max_memory[0] = f"{gpu_max_gb}GiB"
        return max_memory

    try:
        free_bytes, _ = torch.cuda.mem_get_info()
        usable_gib = max((free_bytes / (1024**3)) * 0.85, 1.0)
        max_memory[0] = f"{usable_gib:.1f}GiB"
    except Exception:
        pass

    return max_memory


def load_transformers_model(model_class: Any, model_source: str, model_dtype: Any, **kwargs: Any) -> Any:
    dtype_kwargs = dict(kwargs)
    dtype_kwargs["dtype"] = model_dtype
    try:
        return model_class.from_pretrained(model_source, **dtype_kwargs)
    except TypeError as exc:
        message = str(exc).lower()
        if "dtype" not in message and "unexpected keyword" not in message:
            raise

        legacy_kwargs = dict(kwargs)
        legacy_kwargs["torch_dtype"] = model_dtype
        return model_class.from_pretrained(model_source, **legacy_kwargs)


def is_memory_pressure_error(exc: Exception) -> bool:
    message = str(exc).lower()
    return "out of memory" in message or "cuda out of memory" in message


def build_audio_chunks(
    audio: np.ndarray,
    sampling_rate: int,
    max_chunk_seconds: float,
    overlap_seconds: float,
) -> list[np.ndarray]:
    if audio.size == 0 or max_chunk_seconds <= 0:
        return [audio]

    chunk_samples = max(int(max_chunk_seconds * sampling_rate), 1)
    overlap_samples = max(int(overlap_seconds * sampling_rate), 0)
    overlap_samples = min(overlap_samples, max(chunk_samples // 3, 0))
    step_samples = max(chunk_samples - overlap_samples, 1)

    chunks: list[np.ndarray] = []
    start_index = 0
    total_samples = len(audio)

    while start_index < total_samples:
        end_index = min(start_index + chunk_samples, total_samples)
        chunks.append(np.asarray(audio[start_index:end_index], dtype=np.float32))
        if end_index >= total_samples:
            break

        start_index += step_samples

    return chunks


def normalize_overlap_token(token: str) -> str:
    return re.sub(r"[^\wÀ-ÿ]+", "", token, flags=re.UNICODE).casefold()


def merge_transcript_text(existing_text: str, chunk_text: str) -> str:
    left = normalize_text(existing_text)
    right = normalize_text(chunk_text)
    if not left:
        return right

    if not right:
        return left

    if right in left or left.endswith(right):
        return left

    left_words = left.split()
    right_words = right.split()
    max_overlap = min(len(left_words), len(right_words), 80)

    for overlap_size in range(max_overlap, 1, -1):
        left_slice = left_words[-overlap_size:]
        right_slice = right_words[:overlap_size]
        if all(
            normalize_overlap_token(left_word) == normalize_overlap_token(right_word)
            for left_word, right_word in zip(left_slice, right_slice, strict=False)
        ):
            suffix_words = right_words[overlap_size:]
            if not suffix_words:
                return left

            return normalize_text(f"{left} {' '.join(suffix_words)}")

    return normalize_text(f"{left} {right}")


def compute_generation_limit(duration_seconds: float, minimum: int, maximum: int) -> int:
    estimated = int(max(duration_seconds, 1.0) * 8.0)
    return max(minimum, min(maximum, estimated))


@dataclass
class BaseEngine:
    model_id: str
    language: str | None = None
    model_path: str | None = None
    model_source: str | None = None

    def resolve_model_source(self) -> str:
        if self.model_source:
            return self.model_source

        if self.model_path:
            self.model_source = self.model_path
            return self.model_source

        local_only = os.environ.get("HF_HUB_OFFLINE") in {"1", "true", "True"} or os.environ.get(
            "VOXTRAL_LOCAL_ONLY"
        ) in {"1", "true", "True"}

        try:
            self.model_source = snapshot_download(
                repo_id=self.model_id,
                local_files_only=local_only,
            )
        except Exception:
            if local_only:
                raise

            self.model_source = self.model_id

        return self.model_source

    def load(self) -> None:
        raise NotImplementedError

    def target_sampling_rate(self) -> int:
        return 16000

    def max_chunk_seconds(self) -> float | None:
        return None

    def chunk_overlap_seconds(self) -> float:
        return DEFAULT_CHUNK_OVERLAP_SECONDS

    def minimum_retry_chunk_seconds(self) -> float:
        return MIN_CHUNK_SECONDS

    def transcribe_chunk(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        raise NotImplementedError

    def transcribe_audio(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        max_chunk_seconds = self.max_chunk_seconds()
        if not max_chunk_seconds:
            return normalize_text(self.transcribe_chunk(audio, sampling_rate, language))

        duration_seconds = len(audio) / float(sampling_rate) if sampling_rate > 0 else 0.0
        if duration_seconds <= max_chunk_seconds:
            return normalize_text(self.transcribe_chunk_with_retry(audio, sampling_rate, language, duration_seconds))

        chunks = build_audio_chunks(
            audio,
            sampling_rate,
            max_chunk_seconds=max_chunk_seconds,
            overlap_seconds=self.chunk_overlap_seconds(),
        )

        merged_text = ""
        total_chunks = len(chunks)
        for index, chunk_audio in enumerate(chunks, start=1):
            emit(type="status", message=f"Transcription locale en cours... ({index}/{total_chunks})")
            chunk_duration_seconds = len(chunk_audio) / float(sampling_rate) if sampling_rate > 0 else 0.0
            chunk_text = self.transcribe_chunk_with_retry(
                chunk_audio,
                sampling_rate,
                language,
                chunk_duration_seconds,
            )
            merged_text = merge_transcript_text(merged_text, chunk_text)

        return normalize_text(merged_text)

    def transcribe_chunk_with_retry(
        self,
        audio: np.ndarray,
        sampling_rate: int,
        language: str | None,
        duration_seconds: float,
    ) -> str:
        try:
            text = self.transcribe_chunk(audio, sampling_rate, language)
            clear_torch_memory()
            return normalize_text(text)
        except Exception as exc:
            clear_torch_memory()
            if (
                not is_memory_pressure_error(exc)
                or duration_seconds <= self.minimum_retry_chunk_seconds()
                or sampling_rate <= 0
            ):
                raise

            next_chunk_seconds = max(duration_seconds / 2.0, self.minimum_retry_chunk_seconds())
            emit(
                type="status",
                message=(
                    "Segment trop lourd pour la m\u00e9moire disponible, nouvelle tentative avec un d\u00e9coupage plus fin..."
                ),
            )

            chunks = build_audio_chunks(
                audio,
                sampling_rate,
                max_chunk_seconds=next_chunk_seconds,
                overlap_seconds=min(self.chunk_overlap_seconds(), max(next_chunk_seconds / 6.0, 1.0)),
            )
            merged_text = ""
            total_chunks = len(chunks)
            for index, chunk_audio in enumerate(chunks, start=1):
                emit(type="status", message=f"Transcription locale en cours... ({index}/{total_chunks})")
                chunk_duration = len(chunk_audio) / float(sampling_rate)
                chunk_text = self.transcribe_chunk_with_retry(chunk_audio, sampling_rate, language, chunk_duration)
                merged_text = merge_transcript_text(merged_text, chunk_text)

            return normalize_text(merged_text)

    def transcribe(self, audio_path: Path, language: str | None = None) -> str:
        audio, sampling_rate = read_audio_mono(audio_path)
        self.load()
        audio, sampling_rate = resample_audio(audio, sampling_rate, self.target_sampling_rate())
        if is_probably_silent(audio):
            emit(type="status", message="Audio trop faible ou silencieux.")
            return ""

        emit(type="status", message="Transcription locale en cours...")
        return self.transcribe_audio(audio, sampling_rate, language)


@dataclass
class VoxtralEngine(BaseEngine):
    processor: Any | None = None
    model: Any | None = None
    realtime: bool = False

    def load(self) -> None:
        if self.model is not None and self.processor is not None:
            return

        from transformers import AutoProcessor, VoxtralForConditionalGeneration

        self.realtime = "Realtime" in self.model_id
        emit(type="status", message=f"Chargement du mod\u00e8le {self.model_id}...")
        model_source = self.resolve_model_source()

        if self.realtime:
            try:
                from transformers import VoxtralRealtimeForConditionalGeneration
            except ImportError as exc:  # noqa: PERF203
                raise RuntimeError(
                    "Le support local du mod\u00e8le Voxtral Realtime n'est pas disponible dans cette version de transformers."
                ) from exc

            self.processor = AutoProcessor.from_pretrained(model_source)

            realtime_dtype = select_torch_dtype(prefer_bfloat16=False)
            realtime_device_map = "auto" if gpu_acceleration_is_usable() else "cpu"
            try:
                self.model = load_transformers_model(
                    VoxtralRealtimeForConditionalGeneration,
                    model_source,
                    realtime_dtype,
                    device_map=realtime_device_map,
                    low_cpu_mem_usage=True,
                )
            except Exception:
                max_memory = {"cpu": f"{VOXTRAL_REALTIME_CPU_MAX_GB}GiB"}
                if gpu_acceleration_is_usable():
                    max_memory[0] = f"{VOXTRAL_REALTIME_GPU_MAX_GB}GiB"

                offload_folder = tempfile.mkdtemp(prefix="voxcribe-rt-offload-")
                self.model = load_transformers_model(
                    VoxtralRealtimeForConditionalGeneration,
                    model_source,
                    realtime_dtype,
                    device_map=realtime_device_map,
                    low_cpu_mem_usage=True,
                    max_memory=max_memory,
                    offload_folder=offload_folder,
                )
        else:
            dtype = select_torch_dtype(prefer_bfloat16=False)
            self.processor = AutoProcessor.from_pretrained(model_source)
            model_kwargs: dict[str, Any] = {
                "device_map": "auto" if gpu_acceleration_is_usable() else "cpu",
                "low_cpu_mem_usage": True,
            }
            try:
                self.model = load_transformers_model(
                    VoxtralForConditionalGeneration,
                    model_source,
                    dtype,
                    **model_kwargs,
                )
            except Exception:
                clear_torch_memory()
                emit(
                    type="status",
                    message="M\u00e9moire GPU insuffisante ou chargement trop lourd, nouvelle tentative avec offload CPU...",
                )
                offload_kwargs = dict(model_kwargs)
                offload_kwargs["max_memory"] = build_max_memory(VOXTRAL_GPU_MAX_GB, VOXTRAL_CPU_MAX_GB)
                offload_kwargs["offload_folder"] = tempfile.mkdtemp(prefix="voxcribe-voxtral-offload-")
                self.model = load_transformers_model(
                    VoxtralForConditionalGeneration,
                    model_source,
                    dtype,
                    **offload_kwargs,
                )

        self.model.eval()
        emit(type="status", message="Mod\u00e8le pr\u00eat.")

    def move_inputs(self, inputs: Any) -> dict[str, Any]:
        moved_inputs: dict[str, Any] = {}
        for key, value in inputs.items():
            if isinstance(value, torch.Tensor):
                if torch.is_floating_point(value):
                    moved_inputs[key] = value.to(device=self.model.device, dtype=self.model.dtype)
                else:
                    moved_inputs[key] = value.to(device=self.model.device)
            else:
                moved_inputs[key] = value

        return moved_inputs

    def apply_transcription_request(self, audio_reference: Path, language: str | None = None) -> Any:
        effective_language = language or self.language
        if not effective_language:
            from mistral_common.protocol.transcription.request import TranscriptionRequest
            from transformers.feature_extraction_utils import BatchFeature
            from transformers.models.voxtral.processing_voxtral import VoxtralProcessorKwargs, load_audio_as

            output_kwargs = self.processor._merge_kwargs(
                VoxtralProcessorKwargs,
            )
            text_kwargs = output_kwargs["text_kwargs"]
            audio_kwargs = output_kwargs["audio_kwargs"]
            common_kwargs = output_kwargs.get("common_kwargs", {})

            for key in ("return_dict", "tokenize"):
                text_kwargs.pop(key, None)
                audio_kwargs.pop(key, None)

            return_tensors = (
                common_kwargs.pop("return_tensors", None)
                or text_kwargs.pop("return_tensors", None)
                or audio_kwargs.pop("return_tensors", None)
            )
            if return_tensors != "pt":
                raise ValueError(f"{self.processor.__class__.__name__} only supports return_tensors='pt'.")

            audio_buffer = load_audio_as(
                str(audio_reference),
                return_format="buffer",
                force_mono=True,
                sampling_rate=audio_kwargs["sampling_rate"],
            )

            transcription_request = TranscriptionRequest.from_openai(
                {
                    "model": self.model_id,
                    "file": audio_buffer,
                }
            )
            tokenized_request = self.processor.tokenizer.tokenizer.encode_transcription(transcription_request)
            max_source_positions = audio_kwargs.pop("max_source_positions")

            encoding = self.processor.tokenizer(
                [tokenized_request.tokens],
                add_special_tokens=False,
                **text_kwargs,
            )
            data = dict(encoding)
            data["input_features"] = self.processor._retrieve_input_features(
                [audio.audio_array for audio in tokenized_request.audios],
                max_source_positions,
                **audio_kwargs,
            )

            return BatchFeature(data=data, tensor_type=return_tensors)

        request_variants: list[dict[str, Any]] = [
            {
                "audio": str(audio_reference),
                "model_id": self.model_id,
                "language": effective_language,
            }
        ]

        last_error: Exception | None = None
        for method_name in ("apply_transcription_request", "apply_transcrition_request"):
            method = getattr(self.processor, method_name, None)
            if method is None:
                continue

            for request_kwargs in request_variants:
                try:
                    return method(**request_kwargs)
                except (TypeError, ValueError, RuntimeError) as exc:
                    last_error = exc

        if last_error is not None:
            raise last_error

        raise AttributeError("Aucune méthode de transcription Voxtral compatible n'a été trouvée.")

    def target_sampling_rate(self) -> int:
        return int(getattr(self.processor.feature_extractor, "sampling_rate", 16000))

    def max_chunk_seconds(self) -> float | None:
        return VOXTRAL_REALTIME_MAX_CHUNK_SECONDS if self.realtime else VOXTRAL_MAX_CHUNK_SECONDS

    def transcribe_chunk(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        request_audio_path = write_temp_wav(audio, sampling_rate)
        duration_seconds = len(audio) / float(sampling_rate) if sampling_rate > 0 else 0.0

        try:
            if self.realtime:
                inputs = self.processor(audio, sampling_rate=sampling_rate, return_tensors="pt")
                moved_inputs = self.move_inputs(inputs)

                with torch.inference_mode():
                    outputs = self.model.generate(
                        **moved_inputs,
                        max_new_tokens=compute_generation_limit(duration_seconds, minimum=256, maximum=1024),
                    )

                text = self.processor.batch_decode(outputs, skip_special_tokens=True)[0]
                return normalize_text(text)

            inputs = self.apply_transcription_request(request_audio_path, language)
            moved_inputs = self.move_inputs(inputs)

            with torch.inference_mode():
                outputs = self.model.generate(
                    **moved_inputs,
                    do_sample=False,
                    max_new_tokens=compute_generation_limit(duration_seconds, minimum=384, maximum=1536),
                )

            generated = outputs[:, inputs.input_ids.shape[1] :]
            text = self.processor.batch_decode(generated, skip_special_tokens=True)[0]
            return normalize_text(text)
        finally:
            clear_torch_memory()
            try:
                request_audio_path.unlink(missing_ok=True)
            except OSError:
                pass


@dataclass
class WhisperEngine(BaseEngine):
    pipe: Any | None = None

    def load(self) -> None:
        if self.pipe is not None:
            return

        from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline

        emit(type="status", message=f"Chargement du mod\u00e8le {self.model_id}...")
        model_source = self.resolve_model_source()
        torch_dtype = torch.float16 if gpu_acceleration_is_usable() else torch.float32
        processor = AutoProcessor.from_pretrained(model_source)
        model_kwargs: dict[str, Any] = {
            "low_cpu_mem_usage": True,
            "use_safetensors": True,
        }

        if gpu_acceleration_is_usable():
            model_kwargs["device_map"] = "auto"

        model = load_transformers_model(
            AutoModelForSpeechSeq2Seq,
            model_source,
            torch_dtype,
            **model_kwargs,
        )

        self.pipe = pipeline(
            task="automatic-speech-recognition",
            model=model,
            tokenizer=processor.tokenizer,
            feature_extractor=processor.feature_extractor,
        )
        emit(type="status", message="Mod\u00e8le pr\u00eat.")

    def target_sampling_rate(self) -> int:
        return int(getattr(self.pipe.feature_extractor, "sampling_rate", 16000))

    def max_chunk_seconds(self) -> float | None:
        return WHISPER_MAX_CHUNK_SECONDS

    def transcribe_chunk(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        generate_kwargs: dict[str, Any] = {"task": "transcribe"}
        effective_language = language or self.language
        if effective_language:
            generate_kwargs["language"] = effective_language

        try:
            result = self.pipe(
                {"raw": audio, "sampling_rate": sampling_rate},
                return_timestamps=False,
                generate_kwargs=generate_kwargs,
            )
            return normalize_text(result.get("text", ""))
        finally:
            clear_torch_memory()


@dataclass
class CrisperWhisperEngine(BaseEngine):
    model: Any | None = None

    def load(self) -> None:
        if self.model is not None:
            return

        try:
            prepare_crisperwhisper_transformers_backend()
            from crisperwhisper import CrisperWhisperModel
        except ImportError as exc:
            raise RuntimeError(
                "Le runtime CrisperWhisper n'est pas install\u00e9. "
                "R\u00e9parez ou r\u00e9installez le runtime local depuis les param\u00e8tres."
            ) from exc

        emit(type="status", message=f"Chargement du mod\u00e8le {self.model_id}...")
        model_source = self.resolve_model_source()
        use_gpu = gpu_acceleration_is_usable()
        self.model = CrisperWhisperModel(
            model_source,
            backend="transformers",
            device="cuda" if use_gpu else "cpu",
            device_index=0,
            compute_type="float16" if use_gpu else "float32",
        )
        emit(type="status", message="Mod\u00e8le pr\u00eat.")

    def transcribe_chunk(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        effective_language = language or self.language or "en"

        try:
            result = self.model.transcribe(
                audio,
                sr=sampling_rate,
                language=effective_language,
                mode="verbatim",
                word_timestamps=False,
            )
            return normalize_text(result.text)
        finally:
            clear_torch_memory()


@dataclass
class QwenEngine(BaseEngine):
    model: Any | None = None

    def load(self) -> None:
        if self.model is not None:
            return

        try:
            from qwen_asr import Qwen3ASRModel
        except ImportError as exc:  # noqa: PERF203
            raise RuntimeError(
                "Le runtime qwen-asr n'est pas install\u00e9 dans l'environnement Python du backend."
            ) from exc

        emit(type="status", message=f"Chargement du mod\u00e8le {self.model_id}...")
        model_source = self.resolve_model_source()
        torch_dtype = select_torch_dtype(prefer_bfloat16=True)
        device_map = "cuda:0" if gpu_acceleration_is_usable() else "cpu"

        self.model = Qwen3ASRModel.from_pretrained(
            model_source,
            dtype=torch_dtype,
            device_map=device_map,
            max_inference_batch_size=1,
            max_new_tokens=1024,
        )
        emit(type="status", message="Mod\u00e8le pr\u00eat.")

    def max_chunk_seconds(self) -> float | None:
        return QWEN_MAX_CHUNK_SECONDS

    def transcribe_chunk(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        effective_language = language or self.language
        chunk_audio_path = write_temp_wav(audio, sampling_rate)

        try:
            results = self.model.transcribe(
                audio=str(chunk_audio_path),
                language=effective_language,
            )
            if not results:
                return ""

            merged_text = ""
            for item in results:
                merged_text = merge_transcript_text(merged_text, getattr(item, "text", ""))

            return normalize_text(merged_text)
        finally:
            clear_torch_memory()
            try:
                chunk_audio_path.unlink(missing_ok=True)
            except OSError:
                pass


@dataclass
class PhononEngine(BaseEngine):
    model: Any | None = None

    def load(self) -> None:
        if self.model is not None:
            return
        if not phonon_runtime_is_available():
            raise RuntimeError(
                "Le composant Phonon-2 manque ou est incompatible. "
                "Installez Phonon-2 depuis les parametres des modeles."
            )

        emit(type="status", message="Chargement de Phonon-2 sur CPU (anglais uniquement)...")
        try:
            with contextlib.redirect_stdout(sys.stderr):
                from fermion._speech import backends

                model_dir = prepare_phonon_model(Path(self.resolve_model_source()))
                self.model = backends.load(
                    "cpu", model_dir, profile="five-value", backend="phonon2-five-value", quiet=True
                )
        except SystemExit as exc:
            raise RuntimeError(f"Impossible de charger Phonon-2 : {exc}") from exc

    def transcribe_chunk(self, audio: np.ndarray, sampling_rate: int, language: str | None = None) -> str:
        requested_language = (language or self.language or "en").strip().lower()
        if requested_language not in {"en", "english"}:
            raise ValueError("Phonon-2 transcrit uniquement l'anglais. Choisissez un modele multilingue pour cette langue.")
        self.load()
        audio, _ = resample_audio(audio, sampling_rate, self.target_sampling_rate())
        # Fermion cuts long audio at pauses; keep its complete-file behavior so
        # imported recordings are not limited to a single decoding window.
        with contextlib.redirect_stdout(sys.stderr):
            text, _, _ = self.model.transcribe_array(audio)
        return normalize_text(text)


def build_engine(model_kind: str, model_id: str, language: str | None, model_path: str | None) -> BaseEngine:
    kind = (model_kind or "voxtral").strip().lower()
    if kind == "whisper":
        return WhisperEngine(model_id=model_id, language=language, model_path=model_path)
    if kind == "crisperwhisper":
        return CrisperWhisperEngine(model_id=model_id, language=language, model_path=model_path)
    if kind == "qwen":
        return QwenEngine(model_id=model_id, language=language, model_path=model_path)
    if kind == "phonon":
        return PhononEngine(model_id=model_id, language=language, model_path=model_path)

    return VoxtralEngine(model_id=model_id, language=language, model_path=model_path)


def serve_stdio(engine: BaseEngine) -> int:
    gpu_usable, gpu_error = (True, None) if isinstance(engine, PhononEngine) else probe_gpu_acceleration()
    if not isinstance(engine, PhononEngine) and torch.cuda.is_available() and not gpu_usable:
        detail = f" ({gpu_error})" if gpu_error else ""
        emit(
            type="status",
            message=(
                "Le runtime PyTorch ne peut pas executer de calcul sur ce GPU; "
                f"bascule automatique sur le CPU.{detail}"
            ),
        )

    engine.load()
    emit(type="ready", message="Worker pr\u00eat.")

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        try:
            command = json.loads(line)
        except json.JSONDecodeError as exc:
            emit(type="error", error=f"JSON invalide : {exc}")
            continue

        command_type = command.get("type")
        request_id = command.get("requestId")

        if command_type == "shutdown":
            emit(type="status", message="Arr\u00eat du worker.")
            return 0

        if command_type != "transcribe":
            emit(type="error", requestId=request_id, error=f"Commande inconnue : {command_type}")
            continue

        audio_path = command.get("audioPath")
        if not audio_path:
            emit(type="error", requestId=request_id, error="audioPath manquant.")
            continue

        try:
            text = engine.transcribe(Path(audio_path), language=command.get("language"))
            emit(type="transcription", requestId=request_id, success=True, text=text)
        except Exception as exc:  # noqa: BLE001
            emit(type="error", requestId=request_id, error=str(exc))

    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Worker de transcription local via stdio.")
    parser.add_argument("--stdio", action="store_true", help="Utiliser stdin/stdout pour le protocole JSON.")
    parser.add_argument("--model-kind", default=DEFAULT_MODEL_KIND, help="Famille du mod\u00e8le \u00e0 charger.")
    parser.add_argument("--model-id", default=DEFAULT_MODEL_ID, help="Mod\u00e8le Hugging Face \u00e0 charger.")
    parser.add_argument("--model-path", default=DEFAULT_MODEL_PATH, help="Chemin local du mod\u00e8le \u00e0 charger.")
    parser.add_argument("--language", default=DEFAULT_LANGUAGE, help="Code de langue ISO 639-1 optionnel.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    engine = build_engine(
        model_kind=args.model_kind,
        model_id=args.model_id,
        language=args.language,
        model_path=args.model_path,
    )

    if not args.stdio:
        emit(type="error", error="Ce worker attend --stdio.")
        return 1

    return serve_stdio(engine)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        emit(type="error", error=str(exc))
        raise
