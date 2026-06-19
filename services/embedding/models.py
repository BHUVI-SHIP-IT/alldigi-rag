"""
Pydantic v2 request/response models for the embedding service.

Covers:
  - POST /embed         → EmbedRequest / EmbedResponse
  - POST /embed/query   → EmbedQueryRequest / EmbedQueryResponse
  - POST /process       → ProcessRequest / ChunkResult / ProcessResponse
"""

from pydantic import BaseModel, ConfigDict, Field, field_validator


class EmbedRequest(BaseModel):
    """Request body for POST /embed."""

    model_config = ConfigDict(strict=True)

    texts: list[str] = Field(..., description="One or more texts to embed.")

    @field_validator("texts")
    @classmethod
    def texts_must_be_non_empty(cls, v: list[str]) -> list[str]:
        if not v:
            raise ValueError("texts must contain at least one entry")
        return v


class EmbedResponse(BaseModel):
    """Response body for POST /embed."""

    model_config = ConfigDict(strict=False)

    vectors: list[list[float]] = Field(
        ..., description="One embedding vector per input text."
    )
    dim: int = Field(768, description="Dimensionality of each vector (always 768).")


class EmbedQueryRequest(BaseModel):
    """Request body for POST /embed/query."""

    model_config = ConfigDict(strict=True)

    text: str = Field(..., description="Single query string to embed.")

    @field_validator("text")
    @classmethod
    def text_must_be_non_whitespace(cls, v: str) -> str:
        if not v.strip():
            raise ValueError(
                "text must contain at least one non-whitespace character"
            )
        return v


class EmbedQueryResponse(BaseModel):
    """Response body for POST /embed/query."""

    model_config = ConfigDict(strict=False)

    vector: list[float] = Field(
        ..., description="768-dimensional embedding vector for the query."
    )


class ProcessRequest(BaseModel):
    """Request body for POST /process."""

    model_config = ConfigDict(strict=True)

    file_b64: str = Field(..., description="Base64-encoded file content (PDF or DOCX).")
    filename: str = Field(..., description="Original filename including extension.")


class ChunkResult(BaseModel):
    """A single document chunk with its embedding vector."""

    model_config = ConfigDict(strict=False)

    ordinal: int = Field(
        ..., ge=0, description="Zero-based sequential index of the chunk."
    )
    text: str = Field(..., description="Decoded text content of the chunk.")
    vector: list[float] = Field(
        ..., description="768-dimensional embedding vector for this chunk."
    )


class ProcessResponse(BaseModel):
    """Response body for POST /process."""

    model_config = ConfigDict(strict=False)

    chunks: list[ChunkResult] = Field(
        ..., description="Ordered list of chunks extracted from the document."
    )
