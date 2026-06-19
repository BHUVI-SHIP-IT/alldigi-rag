"""
embedder.py — Singleton wrapper around nomic-ai/nomic-embed-text-v1.

The model is loaded once at module import using a thread-safe Event so that
health.py can report readiness before responding to requests.

Nomic-embed-text task prefixes:
  - Queries  : "search_query: <text>"
  - Documents: "search_document: <text>"  (or no prefix for raw chunks)
"""

import threading
from sentence_transformers import SentenceTransformer

# ---------------------------------------------------------------------------
# Singleton load
# ---------------------------------------------------------------------------

_EXPECTED_DIM = 768

_model_loaded = threading.Event()

_model: SentenceTransformer = SentenceTransformer(
    "nomic-ai/nomic-embed-text-v1",
    trust_remote_code=True,
)
_model_loaded.set()


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def embed_texts(texts: list[str]) -> list[list[float]]:
    """Embed a batch of document texts.

    Each text is prefixed with "search_document: " as required by the
    nomic-embed-text task specification.

    Args:
        texts: Non-empty list of strings to embed.

    Returns:
        A list of 768-dimensional float vectors, one per input text.

    Raises:
        ValueError: If any output vector does not have exactly 768 dimensions.
    """
    prefixed = [f"search_document: {t}" for t in texts]
    vectors = _model.encode(prefixed, normalize_embeddings=True).tolist()

    for i, vec in enumerate(vectors):
        if len(vec) != _EXPECTED_DIM:
            raise ValueError(
                f"embed_texts: expected dimension {_EXPECTED_DIM}, "
                f"got {len(vec)} at index {i}"
            )

    return vectors


def embed_query(text: str) -> list[float]:
    """Embed a single search query.

    The text is prefixed with "search_query: " as required by the
    nomic-embed-text task specification for asymmetric retrieval.

    Args:
        text: The query string to embed.

    Returns:
        A 768-dimensional float vector.

    Raises:
        ValueError: If the output vector does not have exactly 768 dimensions.
    """
    prefixed = f"search_query: {text}"
    vector: list[float] = _model.encode(prefixed, normalize_embeddings=True).tolist()

    if len(vector) != _EXPECTED_DIM:
        raise ValueError(
            f"embed_query: expected dimension {_EXPECTED_DIM}, got {len(vector)}"
        )

    return vector
