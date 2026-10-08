"""Local Phonon-2 artifacts and the pinned Fermion CPU runtime."""

import hashlib
import importlib.metadata
import json
from pathlib import Path

PHONON_REPO_ID = "FermionResearch/Phonon-2"
PHONON_RUNTIME_VERSION = "0.2.9"
PHONON_ARCHIVE_NAME = "phonon-2.bps.tar.zst"
# Release pin from Fermion's 0.2.9 catalog; the archive contains byte-plane data,
# so it must be restored by Fermion's unpacker rather than ordinary tar extraction.
PHONON_ARCHIVE_SHA256 = "98125795b6dda72f5c6eee9ba33d19815df65dcb18b50a357bf9f73c9935309e"
PHONON_UNPACKED_DIRECTORY = "model_phonon2_c4c_int6"


def phonon_runtime_is_available() -> bool:
    try:
        if importlib.metadata.version("fermion-research") != PHONON_RUNTIME_VERSION:
            return False
        import scipy  # noqa: F401
        import zstandard  # noqa: F401
        from fermion._speech.engine_phonon2_cpu import load  # noqa: F401
        from fermion._speech.fetch import _unpack  # noqa: F401

        return True
    except Exception:
        return False


def validate_phonon_archive(model_dir: Path) -> Path:
    archive = model_dir / PHONON_ARCHIVE_NAME
    if not archive.is_file():
        raise FileNotFoundError(f"Archive Phonon-2 introuvable : {archive}")
    with archive.open("rb") as handle:
        digest = hashlib.file_digest(handle, "sha256").hexdigest()
    if digest != PHONON_ARCHIVE_SHA256:
        raise ValueError("Archive Phonon-2 incomplete ou corrompue. Telechargez le modele a nouveau.")
    return archive


def is_unpacked_phonon_model(model_dir: Path) -> bool:
    return all(
        (model_dir / name).is_file() and (model_dir / name).stat().st_size > 0
        for name in ("config.json", "packed_manifest.json", "model.fermion")
    )


def prepare_phonon_model(model_dir: Path) -> Path:
    if is_unpacked_phonon_model(model_dir):
        return model_dir

    unpacked_dir = model_dir / PHONON_UNPACKED_DIRECTORY
    if is_unpacked_phonon_model(unpacked_dir):
        return unpacked_dir

    archive = validate_phonon_archive(model_dir)
    from fermion._speech.fetch import _unpack

    # The official unpacker checks the checksum of every restored member and
    # renames a staging directory only after the whole artifact is complete.
    _unpack(archive, unpacked_dir)
    if not is_unpacked_phonon_model(unpacked_dir):
        raise FileNotFoundError("Le modele Phonon-2 decompresse est incomplet.")

    marker = model_dir / ".voxcribe-model.json"
    if marker.is_file():
        payload = json.loads(marker.read_text(encoding="utf-8"))
        payload["sizeBytes"] = sum(path.stat().st_size for path in model_dir.rglob("*") if path.is_file())
        marker.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return unpacked_dir
