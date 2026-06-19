"""
main.py — FastAPI application for the RAG embedding service.

Endpoints:
  GET  /health         — Liveness/readiness probe
  POST /embed          — Batch document embedding
  POST /embed/query    — Single query embedding
  POST /process        — Document parse + chunk + embed pipeline
"""
import base64
import logging

from fastapi import FastAPI, HTTPException, status
from fastapi.responses import JSONResponse

import embedder
import chunker
from health import is_ready
from models import (
    EmbedQueryRequest,
    EmbedQueryResponse,
    EmbedRequest,
    EmbedResponse,
    ChunkResult,
    ProcessRequest,
    ProcessResponse,
)

logger = logging.getLogger(__name__)
app = FastAPI(title="RAG Embedding Service", version="1.0.0")


@app.get("/health")
async def health_check():
    """Readiness probe — returns 200 when model is loaded, 503 otherwise."""
    if not is_ready():
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={"status": "loading"},
        )
    return {"status": "ok"}


@app.post("/embed", response_model=EmbedResponse)
async def embed_texts_endpoint(request: EmbedRequest):
    """Embed a batch of document texts. Returns 768-dim vectors."""
    # texts validated non-empty by EmbedRequest model
    try:
        vectors = embedder.embed_texts(request.texts)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=str(exc),
        ) from exc
    return EmbedResponse(vectors=vectors, dim=768)


@app.post("/embed/query", response_model=EmbedQueryResponse)
async def embed_query_endpoint(request: EmbedQueryRequest):
    """Embed a single search query. Returns a 768-dim vector."""
    # text validated non-whitespace by EmbedQueryRequest model
    try:
        vector = embedder.embed_query(request.text)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=str(exc),
        ) from exc
    return EmbedQueryResponse(vector=vector)


@app.post("/process", response_model=ProcessResponse)
async def process_document(request: ProcessRequest):
    """Parse a base64-encoded PDF/DOCX, chunk it, and embed each chunk."""
    # --- Validate extension ---
    filename_lower = request.filename.lower()
    if not (filename_lower.endswith(".pdf") or filename_lower.endswith(".docx")):
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=(
                f"Unsupported file type for filename '{request.filename}'. "
                "Supported types: .pdf, .docx"
            ),
        )

    # --- Decode base64 ---
    try:
        file_bytes = base64.b64decode(request.file_b64)
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=f"Invalid base64 encoding: {exc}",
        ) from exc

    # --- Extract text ---
    try:
        text = chunker.extract_text(file_bytes, request.filename)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=str(exc),
        ) from exc

    # --- Chunk using the model's own tokenizer ---
    tokenizer = embedder._model.tokenizer
    raw_chunks = chunker.chunk_text(text, tokenizer, max_tokens=500)

    # --- Embed all chunks ---
    try:
        vectors = embedder.embed_texts(raw_chunks) if raw_chunks else []
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=str(exc),
        ) from exc

    chunks = [
        ChunkResult(ordinal=i, text=chunk_text, vector=vec)
        for i, (chunk_text, vec) in enumerate(zip(raw_chunks, vectors))
    ]

    return ProcessResponse(chunks=chunks)
