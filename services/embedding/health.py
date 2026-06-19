"""
health.py — Readiness check for the embedding service.

Reports True only after the sentence-transformer model is fully loaded.
"""
from embedder import _model_loaded


def is_ready() -> bool:
    """Return True if the nomic-embed-text model has been loaded and is ready."""
    return _model_loaded.is_set()
