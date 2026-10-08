import contextlib
import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np

import model_manager
import phonon_runtime
import voxtral_worker


def write_unpacked_model(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for name in ("config.json", "packed_manifest.json", "model.fermion"):
        (directory / name).write_bytes(b"test artifact")


class PhononArtifactTests(unittest.TestCase):
    def test_corrupt_download_never_creates_ready_marker(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            (snapshot / phonon_runtime.PHONON_ARCHIVE_NAME).write_bytes(b"incomplete download")
            destination = root / "model"
            with patch("huggingface_hub.snapshot_download", return_value=str(snapshot)):
                with self.assertRaisesRegex(ValueError, "corrompue"):
                    model_manager.command_download(phonon_runtime.PHONON_REPO_ID, destination, [])
            self.assertFalse((destination / model_manager.MARKER_FILE_NAME).exists())

    def test_missing_archive_has_actionable_error(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(FileNotFoundError, "Phonon-2 introuvable"):
                phonon_runtime.prepare_phonon_model(Path(temporary))

    def test_checksum_is_checked_before_unpacking(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / phonon_runtime.PHONON_ARCHIVE_NAME).write_bytes(b"corrupt")
            with patch("fermion._speech.fetch._unpack") as unpack:
                with self.assertRaises(ValueError):
                    phonon_runtime.prepare_phonon_model(directory)
                unpack.assert_not_called()

    def test_preparation_restores_artifacts_once_and_updates_installed_size(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            archive = directory / phonon_runtime.PHONON_ARCHIVE_NAME
            archive.write_bytes(b"verified archive")
            marker = directory / model_manager.MARKER_FILE_NAME
            marker.write_text(json.dumps({"repoId": phonon_runtime.PHONON_REPO_ID, "version": 3, "sizeBytes": 0}))

            def unpack(source, destination):
                self.assertEqual(source, archive)
                write_unpacked_model(destination)

            with patch.object(phonon_runtime, "PHONON_ARCHIVE_SHA256", hashlib.sha256(archive.read_bytes()).hexdigest()):
                with patch("fermion._speech.fetch._unpack", side_effect=unpack) as restore:
                    first = phonon_runtime.prepare_phonon_model(directory)
                    self.assertEqual(phonon_runtime.prepare_phonon_model(directory), first)
                    self.assertEqual(restore.call_count, 1)
            payload = json.loads(marker.read_text())
            self.assertGreater(payload["sizeBytes"], 0)
            self.assertEqual(payload["version"], 3)

    def test_partial_unpacked_directory_is_not_ready(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            write_unpacked_model(directory)
            (directory / "model.fermion").write_bytes(b"")
            self.assertFalse(phonon_runtime.is_unpacked_phonon_model(directory))

    def test_already_unpacked_local_model_needs_no_download_or_write(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            write_unpacked_model(directory)
            with patch.object(phonon_runtime, "validate_phonon_archive") as validate:
                self.assertEqual(phonon_runtime.prepare_phonon_model(directory), directory)
                validate.assert_not_called()


class PhononEngineTests(unittest.TestCase):
    def test_local_load_uses_cpu_and_keeps_runtime_logs_out_of_json_stdout(self):
        engine = voxtral_worker.build_engine("phonon", phonon_runtime.PHONON_REPO_ID, None, "local model")
        stdout = io.StringIO()
        stderr = io.StringIO()
        loaded = object()

        def load(*args, **kwargs):
            print("third-party runtime log")
            self.assertEqual(args, ("cpu", Path("unpacked model")))
            self.assertEqual(kwargs["backend"], "phonon2-five-value")
            return loaded

        with patch.object(voxtral_worker, "phonon_runtime_is_available", return_value=True):
            with patch.object(voxtral_worker, "prepare_phonon_model", return_value=Path("unpacked model")):
                with patch("fermion._speech.backends.load", side_effect=load):
                    with patch.object(voxtral_worker, "snapshot_download") as download:
                        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                            engine.load()
                            engine.load()
                        download.assert_not_called()
        self.assertIs(engine.model, loaded)
        self.assertIn("third-party runtime log", stderr.getvalue())
        for line in stdout.getvalue().splitlines():
            self.assertEqual(json.loads(line)["type"], "status")

    def test_long_audio_is_passed_to_fermion_in_full(self):
        audio = np.full(16000 * 75, 0.1, dtype=np.float32)
        seen = []

        def transcribe_array(wave):
            seen.append(len(wave))
            return "  Complete recording.  ", 1.0, 75.0

        engine = voxtral_worker.PhononEngine(
            phonon_runtime.PHONON_REPO_ID,
            model=SimpleNamespace(transcribe_array=transcribe_array),
        )
        self.assertEqual(engine.transcribe_audio(audio, 16000), "Complete recording.")
        self.assertEqual(seen, [len(audio)])

    def test_french_request_is_rejected_before_loading_weights(self):
        engine = voxtral_worker.PhononEngine(phonon_runtime.PHONON_REPO_ID)
        with patch.object(engine, "load") as load:
            with self.assertRaisesRegex(ValueError, "uniquement l'anglais"):
                engine.transcribe_chunk(np.ones(16000, dtype=np.float32), 16000, "fr")
            load.assert_not_called()

    def test_missing_runtime_explains_how_to_install_it(self):
        engine = voxtral_worker.PhononEngine(phonon_runtime.PHONON_REPO_ID)
        with patch.object(voxtral_worker, "phonon_runtime_is_available", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "parametres des modeles"):
                engine.load()


if __name__ == "__main__":
    unittest.main()
