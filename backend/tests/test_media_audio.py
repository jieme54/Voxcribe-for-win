import sys
import tempfile
import unittest
from fractions import Fraction
from pathlib import Path
from unittest.mock import patch

import av
import numpy as np
import soundfile as sf

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from media_audio import MEDIA_SAMPLE_RATE, MediaDecodeError, decode_media_audio, read_audio_mono


def write_media(
    path: Path,
    container_format: str,
    audio_codec: str | None = "aac",
    video_codec: str | None = None,
    layout: str = "stereo",
) -> None:
    """Create real encoded media, with no downloaded fixtures or FFmpeg executable."""
    with av.open(str(path), "w", format=container_format) as container:
        video = None
        audio = None
        if video_codec:
            video = container.add_stream(video_codec, rate=25)
            video.width = 32
            video.height = 32
            video.pix_fmt = "yuv420p"
        if audio_codec:
            audio = container.add_stream(audio_codec, rate=48000)
            audio.layout = layout
            audio.bit_rate = 192000

        # All streams must be declared before muxing writes the container header.
        if video is not None:
            frame = av.VideoFrame.from_ndarray(np.zeros((32, 32, 3), dtype=np.uint8), format="rgb24")
            frame.pts = 0
            frame.time_base = Fraction(1, 25)
            for packet in video.encode(frame):
                container.mux(packet)
            for packet in video.encode(None):
                container.mux(packet)

        if audio is not None:
            channels = len(av.AudioLayout(layout).channels)
            # A fractional duration also exercises the resampler's final buffered samples.
            tone = 0.2 * np.sin(2 * np.pi * 440 * np.arange(20736) / 48000)
            samples = np.tile(tone.astype(np.float32), (channels, 1))
            frame = av.AudioFrame.from_ndarray(samples, format="fltp", layout=layout)
            frame.sample_rate = 48000
            frame.pts = 0
            frame.time_base = Fraction(1, 48000)
            resampler = av.AudioResampler(format=audio.codec_context.format, layout=layout, rate=48000)
            for converted in resampler.resample(frame) + resampler.resample(None):
                for packet in audio.encode(converted):
                    container.mux(packet)
            for packet in audio.encode(None):
                container.mux(packet)


class MediaAudioTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def assert_readable(self, path: Path):
        original = path.read_bytes()
        audio, rate = read_audio_mono(path)
        self.assertEqual(audio.ndim, 1)
        self.assertEqual(audio.dtype, np.float32)
        self.assertTrue(np.isfinite(audio).all())
        self.assertAlmostEqual(len(audio) / rate, 0.432, delta=0.08)
        self.assertGreater(float(np.sqrt(np.mean(audio ** 2))), 0.02)
        self.assertEqual(path.read_bytes(), original, "Import must preserve the original file")

    def test_common_audio_containers(self):
        for suffix, container, codec in (
            (".wav", "wav", "pcm_s16le"),
            (".mp3", "mp3", "libmp3lame"),
            (".flac", "flac", "flac"),
            (".ogg", "ogg", "libvorbis"),
            (".opus", "ogg", "libopus"),
            (".m4a", "mp4", "aac"),
            (".aac", "adts", "aac"),
            (".wma", "asf", "wmav2"),
            (".aiff", "aiff", "pcm_s16be"),
            (".caf", "caf", "pcm_s16le"),
            (".ac3", "ac3", "ac3"),
        ):
            with self.subTest(format=suffix):
                path = self.root / f"audio{suffix}"
                if codec == "libvorbis":
                    # The FFmpeg wheel has a Vorbis decoder but no libvorbis encoder.
                    tone = (0.2 * np.sin(2 * np.pi * 440 * np.arange(20736) / 48000)).astype(np.float32)
                    sf.write(path, np.column_stack((tone, tone)), 48000, format="OGG", subtype="VORBIS")
                else:
                    write_media(path, container, codec)
                self.assert_readable(path)
                audio, rate = decode_media_audio(path)
                self.assertEqual(rate, MEDIA_SAMPLE_RATE)
                self.assertAlmostEqual(len(audio) / rate, 0.432, delta=0.08)

    def test_video_containers_extract_audio(self):
        for suffix, container, audio_codec, video_codec in (
            (".mp4", "mp4", "aac", "mpeg4"),
            (".mkv", "matroska", "flac", "mpeg4"),
            (".mov", "mov", "pcm_s16le", "mpeg4"),
            (".avi", "avi", "libmp3lame", "mpeg4"),
            (".webm", "webm", "libopus", "libvpx"),
            (".wmv", "asf", "wmav2", "wmv2"),
            (".ts", "mpegts", "mp2", "mpeg2video"),
            (".mpg", "mpeg", "mp2", "mpeg2video"),
            (".flv", "flv", "libmp3lame", "flv"),
        ):
            with self.subTest(format=suffix):
                path = self.root / f"video{suffix}"
                write_media(path, container, audio_codec, video_codec)
                self.assert_readable(path)

    def test_multichannel_audio_becomes_mono_at_speech_sample_rate(self):
        path = self.root / "surround.mkv"
        write_media(path, "matroska", "flac", "mpeg4", layout="5.1")
        audio, rate = decode_media_audio(path)
        self.assertEqual(rate, MEDIA_SAMPLE_RATE)
        self.assertEqual(audio.shape, (6912,))
        self.assertGreater(float(np.max(np.abs(audio))), 0.05)

    def test_unknown_uppercase_and_missing_extensions_and_unicode_paths(self):
        for name in ("réunion été.MP4", "recording.unusual", "recording"):
            with self.subTest(name=name):
                path = self.root / name
                write_media(path, "mp4", "aac", "mpeg4")
                self.assert_readable(path)

    def test_video_without_audio_has_explicit_error(self):
        path = self.root / "silent.mp4"
        write_media(path, "mp4", audio_codec=None, video_codec="mpeg4")
        with self.assertRaisesRegex(MediaDecodeError, "aucune piste audio"):
            read_audio_mono(path)

    def test_invalid_file_has_explicit_error(self):
        path = self.root / "invalid.mp4"
        path.write_bytes(b"This is not an audio or video file.")
        with self.assertRaisesRegex(MediaDecodeError, "format/codec non pris en charge"):
            read_audio_mono(path)

    def test_empty_audio_has_explicit_error(self):
        path = self.root / "empty.wav"
        sf.write(path, np.empty(0, dtype=np.float32), 16000)
        with self.assertRaisesRegex(MediaDecodeError, "vide"):
            read_audio_mono(path)

    def test_missing_files_and_directories_are_rejected(self):
        for path in (self.root / "missing.mp4", self.root):
            with self.subTest(path=path), self.assertRaises(FileNotFoundError):
                read_audio_mono(path)

    def test_missing_decoder_explains_runtime_repair_without_breaking_wav(self):
        path = self.root / "video.mp4"
        write_media(path, "mp4", "aac", "mpeg4")
        with patch.dict(sys.modules, {"av": None}):
            with self.assertRaisesRegex(RuntimeError, "installation du runtime"):
                read_audio_mono(path)
            wav_path = self.root / "microphone.wav"
            sf.write(wav_path, np.ones(1600, dtype=np.float32) * 0.1, 16000)
            audio, rate = read_audio_mono(wav_path)
            self.assertEqual((len(audio), rate), (1600, 16000))

    def test_old_decoder_is_rejected_before_opening_mpeg(self):
        path = self.root / "video.mpg"
        write_media(path, "mpeg", "mp2", "mpeg2video")
        with patch.object(av, "__version__", "17.0.0"), patch.object(av, "open") as open_media:
            with self.assertRaisesRegex(RuntimeError, "trop ancien"):
                read_audio_mono(path)
            open_media.assert_not_called()

    def test_first_audio_track_is_selected(self):
        path = self.root / "two_tracks.mkv"
        with av.open(str(path), "w", format="matroska") as container:
            streams = [container.add_stream("flac", rate=16000) for _ in range(2)]
            for stream in streams:
                stream.layout = "mono"
            for stream, level in zip(streams, (0.1, 0.8)):
                frame = av.AudioFrame.from_ndarray(
                    np.full((1, 1600), level, dtype=np.float32), format="fltp", layout="mono"
                )
                frame.sample_rate = 16000
                frame.pts = 0
                frame.time_base = Fraction(1, 16000)
                resampler = av.AudioResampler(format=stream.codec_context.format, layout="mono", rate=16000)
                for converted in resampler.resample(frame) + resampler.resample(None):
                    for packet in stream.encode(converted):
                        container.mux(packet)
                for packet in stream.encode(None):
                    container.mux(packet)
        audio, rate = read_audio_mono(path)
        self.assertEqual(rate, 16000)
        self.assertTrue(np.allclose(audio, 0.1, atol=0.001))


if __name__ == "__main__":
    unittest.main()
