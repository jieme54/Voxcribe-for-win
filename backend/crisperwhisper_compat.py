"""Compatibility helpers for CrisperWhisper's portable Transformers backend."""

from __future__ import annotations

import importlib
import sys
import types


_HALLUCINATION_MODULE = "crisperwhisper.hallucination"
_CTRANSLATE2_MODULE = "ctranslate2"


def prepare_crisperwhisper_transformers_backend() -> None:
    """Make CrisperWhisper 2.0's Transformers path usable without CT2.

    CrisperWhisper 2.0.2 documents CTranslate2 as optional, but its shared
    hallucination helpers import ``ctranslate2`` unconditionally.  The pure
    Transformers engine only calls the backend-independent helpers from that
    module.  On Windows, where Nyra does not publish its CT2 fork, preloading
    the module with a temporary placeholder preserves those helpers without
    pretending that the CT2 backend itself is available.

    A future package version that fixes the optional import takes the normal
    path and never uses the compatibility placeholder.
    """

    if _HALLUCINATION_MODULE in sys.modules:
        return

    try:
        importlib.import_module(_HALLUCINATION_MODULE)
        return
    except ModuleNotFoundError as exc:
        if exc.name != _CTRANSLATE2_MODULE:
            raise

    placeholder = types.ModuleType(_CTRANSLATE2_MODULE)
    placeholder.__doc__ = (
        "Temporary Voxcribe placeholder used only while importing "
        "CrisperWhisper's backend-independent helpers."
    )
    sys.modules[_CTRANSLATE2_MODULE] = placeholder
    try:
        importlib.import_module(_HALLUCINATION_MODULE)
    finally:
        if sys.modules.get(_CTRANSLATE2_MODULE) is placeholder:
            del sys.modules[_CTRANSLATE2_MODULE]
