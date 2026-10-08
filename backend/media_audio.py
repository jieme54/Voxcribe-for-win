"""Read local audio files and audio tracks from video containers."""

from pathlib import Path

import numpy as np
import soundfile as sf


MEDIA_SAMPLE_RATE = 16000


class MediaDecodeError(ValueError):
    """A media file has no usable audio track."""


def read_audio_mono(media_path: Path) -> tuple[np.ndarray, int]:
    if not media_path.is_file():
        raise FileNotFoundError(f"Fichier audio ou vidéo introuvable : {media_path}")

    # Keep the lightweight path for microphone recordings and native audio formats.
    try:
        audio, sampling_rate = sf.read(str(media_path), dtype="float32")
    except (sf.SoundFileError, OSError):
        return decode_media_audio(media_path)

    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    if audio.size == 0:
        raise MediaDecodeError("La piste audio de ce fichier est vide.")
    return np.asarray(audio, dtype=np.float32), sampling_rate


def decode_media_audio(media_path: Path) -> tuple[np.ndarray, int]:
    try:
        import av
        if tuple(map(int, av.__version__.split(".")[:3])) < (19, 0, 1):
            raise ImportError("PyAV 19.0.1 or later is required for reliable MPEG decoding")
    except ImportError as exc:
        raise RuntimeError(
            "Le décodeur audio/vidéo PyAV manque ou est trop ancien dans ce runtime. "
            "Relancez l'installation du runtime depuis les paramètres de Voxcribe."
        ) from exc

    samples = bytearray()
    try:
        # Detect the container from its contents, including files with unusual extensions.
        # Limit FFmpeg to local inputs, as transcription must remain entirely local.
        with av.open(str(media_path), mode="r", options={"protocol_whitelist": "file,pipe"}) as container:
            if not container.streams.audio:
                raise MediaDecodeError("Ce fichier ne contient aucune piste audio à transcrire.")

            audio_stream = container.streams.audio[0]
            resampler = av.AudioResampler(format="fltp", layout="mono", rate=MEDIA_SAMPLE_RATE)
            for frame in container.decode(audio_stream):
                # Some containers have discontinuous timestamps. Transcription uses the
                # decoded samples in order, rather than the video playback timeline.
                frame.pts = None
                for mono_frame in resampler.resample(frame):
                    samples.extend(mono_frame.to_ndarray().tobytes())
            for mono_frame in resampler.resample(None):
                samples.extend(mono_frame.to_ndarray().tobytes())
    except MediaDecodeError:
        raise
    except (av.error.FFmpegError, ValueError) as exc:
        raise MediaDecodeError(
            "Impossible de lire la piste audio : fichier endommagé, protégé "
            "ou format/codec non pris en charge."
        ) from exc

    if not samples:
        raise MediaDecodeError("La piste audio de ce fichier est vide ou illisible.")

    # Share the decoded buffer instead of duplicating long recordings in memory.
    return np.frombuffer(samples, dtype=np.float32), MEDIA_SAMPLE_RATE
