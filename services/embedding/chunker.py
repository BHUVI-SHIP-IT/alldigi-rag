"""
chunker.py — Text extraction from PDF/DOCX and 500-token windowed chunking.

Tokenizer is the model's own tokenizer, passed in as a parameter (the 
SentenceTransformer tokenizer from nomic-embed-text-v1).
"""
import io
from pathlib import Path

import pypdf
import docx


SUPPORTED_EXTENSIONS = {".pdf", ".docx"}


def extract_text(file_bytes: bytes, filename: str) -> str:
    """Extract plain text from a PDF or DOCX file.

    Args:
        file_bytes: Raw file bytes.
        filename: Original filename (used to determine type by extension).

    Returns:
        Extracted text as a single string (pages/paragraphs joined with newlines).

    Raises:
        ValueError: If the extension is not .pdf or .docx (case-insensitive).
        ValueError: If the file content cannot be parsed.
    """
    ext = Path(filename).suffix.lower()
    if ext not in SUPPORTED_EXTENSIONS:
        raise ValueError(
            f"Unsupported file type: '{ext}'. Supported types: .pdf, .docx"
        )

    try:
        if ext == ".pdf":
            reader = pypdf.PdfReader(io.BytesIO(file_bytes))
            pages = [page.extract_text() or "" for page in reader.pages]
            return "\n".join(pages)
        else:  # .docx
            document = docx.Document(io.BytesIO(file_bytes))
            paragraphs = [p.text for p in document.paragraphs]
            return "\n".join(paragraphs)
    except ValueError:
        raise
    except Exception as exc:
        raise ValueError(f"Could not parse file content: {exc}") from exc


def chunk_text(text: str, tokenizer, max_tokens: int = 500) -> list[str]:
    """Split text into non-overlapping chunks of at most max_tokens tokens.

    Uses the provided tokenizer to encode/decode, guaranteeing that
    joining all returned chunks reconstructs the original text at the token
    level (text conservation invariant).

    Args:
        text: Full document text to chunk.
        tokenizer: The sentence-transformers model tokenizer.
        max_tokens: Maximum number of tokens per chunk (default 500).

    Returns:
        List of decoded chunk strings (may be empty if text tokenises to zero tokens).
    """
    if not text:
        return []

    token_ids = tokenizer.encode(text, add_special_tokens=False)
    chunks: list[str] = []
    start = 0
    while start < len(token_ids):
        end = min(start + max_tokens, len(token_ids))
        chunk_ids = token_ids[start:end]
        chunks.append(tokenizer.decode(chunk_ids))
        start = end

    return chunks
