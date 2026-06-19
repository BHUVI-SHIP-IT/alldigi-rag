# Design Document — RAG Backend

## Overview

This document describes the technical design for an on-premises RAG (Retrieval-Augmented Generation) document intelligence platform. Administrators upload PDF/DOCX files; employees query those documents in natural language via a streaming chat interface. All computation runs locally on a CPU-only Linux server using ten containerised services. No data leaves the internal network.

The Angular 18 frontend (admin and employee apps) is already built and wired to a mock API at `/api`. The backend must satisfy the existing API contract exactly so the frontend requires zero modification. The employee app lives at `/` (root) and the admin app at `/admin/`.

### Key Constraints

- CPU-only inference (no GPU required)
- On-premises deployment — `ragnet` has `internal: true`, no outbound internet at runtime
- Single `docker-compose up -d` bootstrap
- Exact API contract compliance with the existing Angular mock API
- JWT role claims must be emitted as plain `role` (not ASP.NET schema URLs) because the Angular `decodeJwt` function reads `payload.role` first

---

## Architecture

### System Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                          HOST (port 80 only)                                 │
│                                                                              │
│   ┌──────────────────────────────────────────────────────────────────────┐  │
│   │                    NGINX  (ragnet ingress)                            │  │
│   │  /           → employee Angular SPA (built static)                   │  │
│   │  /admin/     → admin Angular SPA    (built static)                   │  │
│   │  /api/       → api:8080             (proxy_pass, SSE-aware)          │  │
│   │  /healthz    → 200 ok                                                │  │
│   └──────────────────────────┬───────────────────────────────────────────┘  │
│                              │ ragnet (internal bridge)                      │
│   ┌──────────────────────────▼───────────────────────────────────────────┐  │
│   │              API  (.NET 8 ASP.NET Core, port 8080)                   │  │
│   │  • Auth (ASP.NET Identity + JWT HS256)                               │  │
│   │  • Ingestion pipeline (orchestrator)                                 │  │
│   │  • RAG query loop (embed → cache → retrieve → prompt → stream)      │  │
│   │  • Rate-limit middleware (invalid JWT, 10/60s per IP)                │  │
│   └────┬──────────┬──────────┬──────────┬──────────┬────────────────────┘  │
│        │          │          │          │          │                         │
│   ┌────▼──┐  ┌────▼──┐  ┌───▼───┐  ┌──▼────┐  ┌──▼─────┐                 │
│   │Embed  │  │  LLM  │  │Qdrant │  │ Redis │  │Postgres│                  │
│   │:8000  │  │:8081  │  │:6333  │  │:6379  │  │ :5432  │                  │
│   │FastAPI│  │llama  │  │vector │  │cache  │  │ reldb  │                  │
│   │Python │  │ .cpp  │  │  db   │  │       │  │        │                  │
│   └───────┘  └───────┘  └───────┘  └───────┘  └────────┘                  │
│                                                                              │
│   ┌────────────────────────────────────────────────────────────────────┐    │
│   │  MinIO  (S3-compatible object store, port 9000 internal)           │    │
│   │  Bucket: documents   Key: {documentId} (UUID)                      │    │
│   └────────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────────┘

ragnet: internal Docker bridge (internal: true — no outbound internet)
Volumes: pg_data, qdrant_data, minio_data, model_cache, redis_data
```

### Service Inventory

| Service | Image / Runtime | Internal Port | Role |
|---------|----------------|--------------|------|
| `nginx` | nginx:1.27-alpine | 80 (→ host) | Sole ingress; serves SPA static assets + proxies `/api/` |
| `api` | mcr.microsoft.com/dotnet/aspnet:8.0 | 8080 | .NET 8 Web API; auth + ingestion + RAG |
| `embedding` | python:3.11-slim | 8000 | FastAPI; nomic-embed-text vectoriser + chunker |
| `llm` | ghcr.io/ggerganov/llama.cpp:server | 8081 | llama.cpp server; Llama 3.2 3B Q4_K_M CPU |
| `postgres` | postgres:16-alpine | 5432 | Relational store; users, documents, chunks, audit |
| `redis` | redis:7-alpine | 6379 | Semantic cache; AOF persistence |
| `qdrant` | qdrant/qdrant:latest | 6333 | Vector DB; 768-dim cosine collection "documents" |
| `minio` | minio/minio:latest | 9000 | Object store; raw uploaded files |

---

## Components and Interfaces

### 2.1 Nginx Ingress

Single Nginx container on the host-facing port 80. Serves pre-built Angular static assets and reverse-proxies `/api/` to `api:8080`. SSE streams require `proxy_buffering off` and `proxy_read_timeout` extended to 120s.

**Routing rules:**
- `GET /healthz` → inline `200 ok` (no upstream needed)
- `location /api/` → `proxy_pass http://api:8080/api/;` (SSE-aware: buffering off, chunked transfer)
- `location /admin/` → try_files against admin SPA root; fallback to `/admin/index.html`
- `location /` → try_files against employee SPA root; fallback to `/index.html`

### 2.2 .NET 8 Web API (`services/api/`)

ASP.NET Core 8 Controllers. Handles all authentication, ingestion orchestration, and the RAG query loop.

**Key packages:**
- `Microsoft.AspNetCore.Identity` (bcrypt, cost factor ≥ 10 via `PasswordHasherOptions`)
- `Microsoft.AspNetCore.Authentication.JwtBearer` (HS256 validation)
- `Npgsql.EntityFrameworkCore.PostgreSQL` (EF Core provider)
- `Qdrant.Client` (official .NET SDK)
- `StackExchange.Redis`
- `AWSSDK.S3` (MinIO S3-compatible via `AmazonS3Config.ServiceURL`)

**Startup flow:**
1. Load configuration from environment / `.env`
2. Apply pending EF Core migrations (`db.Database.MigrateAsync()`)
3. Run admin seed (`SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD`) — idempotent
4. Ensure MinIO `documents` bucket exists (create if absent)
5. Ensure Qdrant `documents` collection exists (vector size 768, distance Cosine)

### 2.3 Embedding Service (`services/embedding/`)

Python 3.11 FastAPI service. Loads `nomic-ai/nomic-embed-text-v1` via `sentence-transformers`. Uses the model's own tokenizer for 500-token chunking.

**Endpoints:** `POST /embed`, `POST /embed/query`, `POST /process`, `GET /health`

**Document extraction:** `pypdf` for PDF; `python-docx` for DOCX. Chunking uses the nomic tokenizer with a 500-token hard window and no overlap (ensures text conservation invariant).

### 2.4 LLM Service (`services/llm/`)

llama.cpp HTTP server running Llama 3.2 3B Q4_K_M on CPU only. Shell entrypoint downloads the model to `model_cache` volume on first run, then starts the inference server.

**Endpoint used by API:** `POST /v1/chat/completions` (OpenAI-compatible streaming, `choices[0].delta.content`).

---

## Service File Structures

### `services/api/`

```
services/api/
├── Dockerfile
├── RagBackend.Api.csproj
├── Program.cs
├── appsettings.json
├── Migrations/
├── Data/
│   ├── AppDbContext.cs
│   └── SeedService.cs
├── Models/
│   ├── AppUser.cs
│   ├── Document.cs
│   ├── Chunk.cs
│   └── AuditLog.cs
├── DTOs/
│   ├── LoginRequest.cs / LoginResponse.cs
│   ├── DocumentRecord.cs / UploadResponse.cs
│   ├── QueryRequest.cs
│   └── AuditLogDto.cs
├── Controllers/
│   ├── AuthController.cs
│   ├── DocumentsController.cs
│   ├── QueryController.cs
│   └── AuditController.cs
├── Services/
│   ├── TokenService.cs
│   ├── IngestionService.cs
│   ├── RagService.cs
│   ├── EmbeddingClient.cs
│   ├── LlmClient.cs
│   ├── QdrantService.cs
│   ├── CacheService.cs
│   ├── MinioService.cs
│   └── AuditService.cs
└── Middleware/
    ├── InvalidJwtRateLimitMiddleware.cs
    └── GlobalExceptionMiddleware.cs
```

### `services/embedding/`

```
services/embedding/
├── Dockerfile
├── requirements.txt
├── main.py
├── models.py
├── embedder.py
├── chunker.py
└── health.py
```

### `services/llm/`

```
services/llm/
├── Dockerfile
└── entrypoint.sh
```

---

## Database Schema

### PostgreSQL Tables

```sql
-- AspNetUsers (ASP.NET Identity + Role column)
-- "Role" column: 'Admin' | 'Employee'

-- Documents
CREATE TABLE "Documents" (
    "Id"          UUID         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "FileName"    TEXT         NOT NULL,
    "UploaderId"  TEXT         NOT NULL REFERENCES "AspNetUsers"("Id"),
    "Status"      TEXT         NOT NULL DEFAULT 'queued',
    "CreatedAt"   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    "ChunkCount"  INTEGER      NOT NULL DEFAULT 0
);

-- Chunks
CREATE TABLE "Chunks" (
    "Id"            UUID    NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "DocumentId"    UUID    NOT NULL REFERENCES "Documents"("Id") ON DELETE CASCADE,
    "Ordinal"       INTEGER NOT NULL,
    "Text"          TEXT    NOT NULL,
    "QdrantPointId" UUID    NOT NULL,
    UNIQUE ("DocumentId", "Ordinal")
);

-- AuditLogs
CREATE TABLE "AuditLogs" (
    "Id"               UUID        NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "UserEmail"        TEXT        NOT NULL,
    "Query"            TEXT        NOT NULL,
    "RetrievedSources" TEXT[]      NOT NULL,
    "CreatedAt"        TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

### EF Core Notes

- `AppUser : IdentityUser` adds `Role` string property
- `AuditLog.RetrievedSources` maps to `TEXT[]` via `HasColumnType("text[]")`
- EF Core migrations applied at startup via `db.Database.MigrateAsync()`

### Qdrant Point Payload

```json
{
  "documentId": "uuid-string",
  "chunkId":    "uuid-string",
  "text":       "chunk text content",
  "fileName":   "original-filename.pdf"
}
```

Vector: float32[768], distance metric: Cosine, collection: `"documents"`.

### Redis Cache Schema

```
HSET cache:{uuid}  vector   "[0.12, -0.04, ...]"   (JSON float array)
                   answer   "full assembled answer text"
                   sources  '["file1.pdf","file2.docx"]'
EXPIRE cache:{uuid}  604800   (CACHE_TTL_DAYS * 86400, default 7 days)
SADD cache:keys  cache:{uuid}  (index for eviction scanning)
```

---

## API Endpoint Specifications

All endpoints prefixed `/api/`. Auth via `Authorization: Bearer <JWT>` (required except `/auth/login`).

> **Critical:** ASP.NET Identity emits role claims as long URI strings by default. `TokenService` MUST use `new Claim("role", user.Role)` — NOT `ClaimTypes.Role` — so the Angular `decodeJwt` can read `payload.role` directly.

### POST /api/auth/login
**Request:** `{ "email": string, "password": string }`
**Success (200):** `{ "token": "eyJ...", "user": { "email": "...", "role": "Admin"|"Employee" } }`
**JWT claims:** `email`, `role`, `iat`, `exp`
**Errors:** 401 (bad credentials — no field differentiation)

### GET /api/documents _(Admin only)_
**Success (200):** `[{ "id", "fileName", "uploader", "status", "createdAt", "chunkCount" }]`
Note: `uploader` is the email of the uploading admin (resolved from `UploaderId → AspNetUsers.Email`).

### POST /api/documents _(Admin only)_
**Request:** `multipart/form-data`, field `file` (PDF or DOCX, max 50 MB)
**Success (201):** `{ "documentId": "uuid-v4" }`
**Errors:** 413 (>50 MB), 422 (unsupported extension)

### GET /api/audit _(Admin only)_
**Success (200):** `[{ "id", "userEmail", "query", "retrievedSources", "createdAt" }]` ordered `createdAt` DESC
**Errors:** 503 (PostgreSQL unreachable)

### POST /api/query _(Employee only)_
**Request:** `{ "question": string }` (1–2000 chars after trim)
**Success (200):** `Content-Type: text/event-stream`
```
event: sources
data: ["employee-handbook.pdf","security-policy.docx"]

data: {"token":"This "}
data: {"token":"is "}
data: [DONE]
```
**Errors:** 400 (missing/empty/too-long question), 502 (embedding or LLM unreachable)

### GET /healthz _(Nginx-level)_
Returns `200 ok` regardless of upstream health.

---

## Key Algorithms

### Ingestion Pipeline

```
POST /api/documents
  ├─ Validate extension (.pdf/.docx) → 422 if invalid
  ├─ Validate size (≤50 MB) → 413 if exceeded
  ├─ Generate documentId = UUID v4
  ├─ Upload raw file to MinIO bucket "documents" key=documentId
  ├─ INSERT Document(id, fileName, uploaderId, status="queued", ...)
  ├─ Return HTTP 201 { documentId }
  └─ [Background IHostedService queue via Channel<Guid>]
       ├─ UPDATE Document SET status="processing"
       ├─ GET file from MinIO → base64 encode
       ├─ POST embedding:8000/process { file_b64, filename }
       ├─ BEGIN transaction
       │   ├─ For each chunk: INSERT Chunk + Upsert Qdrant point
       │   └─ UPDATE Document SET status="indexed", chunkCount=N
       ├─ Scan Redis cache; evict entries where sources ∋ fileName
       └─ [On any error]: DELETE Chunks for doc, UPDATE status="failed"
```

Background processing via `IHostedService` + `Channel<Guid>` — 201 returned before processing.

### RAG Query Loop

```
POST /api/query { question }
  ├─ Validate question (non-empty, ≤2000 chars)
  ├─ POST embedding:8000/embed/query → queryVector[768]
  ├─ CACHE CHECK: scan cache:keys; cosine(queryVector, entry.vector) ≥ 0.92?
  │     HIT  → emit SSE(sources) + stream cached answer + [DONE] + audit; return
  │     MISS → continue
  ├─ QDRANT SEARCH: top-5 cosine sim → [{payload.fileName, text}]
  ├─ sources = distinct(fileName from top-5)
  ├─ BUILD PROMPT: system + context chunks + question
  ├─ EMIT: "event: sources\ndata: {JSON(sources)}\n\n"
  ├─ POST llm:8081/v1/chat/completions stream:true
  │     → for each delta.content: emit "data: {"token":"..."}\n\n"
  ├─ EMIT: "data: [DONE]\n\n"
  ├─ CACHE STORE: HSET + EXPIRE + SADD cache:keys
  └─ INSERT AuditLog(userEmail, query, retrievedSources, createdAt)
```

### Semantic Cache Cosine Similarity

```csharp
static float CosineSimilarity(float[] a, float[] b)
{
    float dot = 0, normA = 0, normB = 0;
    for (int i = 0; i < a.Length; i++) {
        dot += a[i] * b[i]; normA += a[i]*a[i]; normB += b[i]*b[i];
    }
    return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
}
```

Threshold ≥ 0.92. O(N × 768) per query — acceptable for v1 cache sizes.

### Cache Invalidation on Re-ingest

```
After document D transitions to indexed:
  keys = SMEMBERS cache:keys
  for key in keys:
    entry_sources = HGET key sources (JSON array)
    if D.fileName in entry_sources:
      DEL key; SREM cache:keys key
```

Scan happens before status is written as `indexed` in PostgreSQL.

### Chunking Algorithm (Python)

```python
def chunk_document(text: str, tokenizer, max_tokens: int = 500) -> list[str]:
    tokens = tokenizer.encode(text)
    chunks = []
    start = 0
    while start < len(tokens):
        end = min(start + max_tokens, len(tokens))
        chunks.append(tokenizer.decode(tokens[start:end]))
        start = end
    return chunks  # ordinals implicit via enumerate
```

No overlap — ensures text conservation (join(chunks) == full_text).

---

## Docker Compose

```yaml
networks:
  ragnet:
    driver: bridge
    internal: true

volumes:
  pg_data:
  qdrant_data:
  minio_data:
  model_cache:
  redis_data:

services:
  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: ${POSTGRES_DB}
    volumes: [pg_data:/var/lib/postgresql/data]
    networks: [ragnet]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER} -d ${POSTGRES_DB}"]
      interval: 10s; timeout: 5s; retries: 12; start_period: 30s

  redis:
    image: redis:7-alpine
    restart: unless-stopped
    command: redis-server --appendonly yes
    volumes: [redis_data:/data]
    networks: [ragnet]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s; timeout: 5s; retries: 12

  qdrant:
    image: qdrant/qdrant:latest
    restart: unless-stopped
    volumes: [qdrant_data:/qdrant/storage]
    networks: [ragnet]
    healthcheck:
      test: ["CMD-SHELL", "wget -qO- http://localhost:6333/healthz || exit 1"]
      interval: 10s; timeout: 5s; retries: 12; start_period: 20s

  minio:
    image: minio/minio:latest
    restart: unless-stopped
    command: server /data --console-address ":9001"
    environment: { MINIO_ROOT_USER, MINIO_ROOT_PASSWORD }
    volumes: [minio_data:/data]
    networks: [ragnet]
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost:9000/minio/health/live || exit 1"]
      interval: 10s; timeout: 5s; retries: 12; start_period: 20s

  embedding:
    build: { context: ./services/embedding }
    restart: unless-stopped
    networks: [ragnet]
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost:8000/health || exit 1"]
      interval: 15s; timeout: 10s; retries: 8; start_period: 60s

  llm:
    build: { context: ./services/llm }
    restart: unless-stopped
    environment: { MODEL_URL, MODEL_FILE, N_CTX: 4096, N_THREADS: 4 }
    volumes: [model_cache:/models]
    networks: [ragnet]
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost:8081/health || exit 1"]
      interval: 20s; timeout: 15s; retries: 10; start_period: 120s

  api:
    build: { context: ./services/api }
    restart: unless-stopped
    environment:
      ConnectionStrings__Default: Host=postgres;Port=5432;Database=${POSTGRES_DB};...
      Redis__ConnectionString: redis:6379
      Minio__Endpoint: minio:9000
      Embedding__BaseUrl: http://embedding:8000
      Llm__BaseUrl: http://llm:8081
      Qdrant__Host: qdrant
      Jwt__Secret: ${JWT_SECRET}
      Seed__AdminEmail: ${SEED_ADMIN_EMAIL}
      Seed__AdminPassword: ${SEED_ADMIN_PASSWORD}
      ASPNETCORE_URLS: http://+:8080
    networks: [ragnet]
    depends_on:
      postgres: { condition: service_healthy }
      redis: { condition: service_healthy }
      qdrant: { condition: service_healthy }
      minio: { condition: service_healthy }
      embedding: { condition: service_healthy }
      llm: { condition: service_healthy }

  nginx:
    build: { context: ./infra/nginx }
    restart: unless-stopped
    ports: ["80:80"]
    networks: [ragnet]
    depends_on: [api]
```

---

## Nginx Configuration

```nginx
events { worker_connections 1024; }

http {
    include /etc/nginx/mime.types;

    upstream api_backend { server api:8080; keepalive 32; }

    server {
        listen 80;
        server_name _;

        location = /healthz {
            access_log off;
            add_header Content-Type text/plain;
            return 200 "ok";
        }

        location /api/ {
            proxy_pass         http://api_backend/api/;
            proxy_http_version 1.1;
            proxy_set_header   Host $host;
            proxy_set_header   X-Real-IP $remote_addr;
            proxy_set_header   Connection "";
            proxy_buffering    off;
            proxy_cache        off;
            proxy_read_timeout 120s;
            proxy_set_header   Accept-Encoding "";
        }

        location /admin/ {
            root /usr/share/nginx/html;
            try_files $uri $uri/ /admin/index.html;
        }

        location / {
            root /usr/share/nginx/html;
            try_files $uri $uri/ /index.html;
        }
    }
}
```

---

## Security Design

### JWT
- Algorithm: HS256 only
- `JWT_SECRET` env var (≥32 bytes)
- Claims: `email`, `role` (plain names — NOT `ClaimTypes.Role` URIs)
- Validation: signature, algorithm, expiry, required claims

### Passwords
- bcrypt via ASP.NET Identity `PasswordHasher`, cost factor ≥ 10
- Plaintext never stored, logged, or returned
- Min 8 chars, max 72 chars (bcrypt hard limit)

### RBAC

| Endpoint | Admin | Employee |
|----------|-------|----------|
| POST /api/auth/login | ✓ | ✓ |
| GET /api/documents | ✓ | 403 |
| POST /api/documents | ✓ | 403 |
| GET /api/audit | ✓ | 403 |
| POST /api/query | 403 | ✓ |

### Rate Limiting
Custom `InvalidJwtRateLimitMiddleware`: >10 invalid JWT requests from same IP within a fixed 60-second window → HTTP 429. Counter stored in `IMemoryCache` keyed by `"ratelimit:{ip}:{windowStart}"` where `windowStart = utcSeconds / 60`.

### Network Isolation
- `ragnet: internal: true` — no outbound TCP/UDP from any container
- Only port 80 (Nginx) bound to host
- All secrets via environment variables (not image layers)

---

## Inter-Service Communication

### API → Embedding
Typed `HttpClient` (`EmbeddingClient`), timeout 30s for `/process`, 5s for `/embed/query`.

### API → LLM (SSE relay)
```csharp
using var response = await _http.SendAsync(request,
    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
// Parse each "data: {...}" line for choices[0].delta.content
// Relay as "data: {"token":"..."}\n\n" to client
```

### API → Qdrant
`Qdrant.Client` NuGet SDK. Upsert + search on `"documents"` collection.

### API → Redis
`StackExchange.Redis`. `HSET/HGET/EXPIRE` for cache entries; `SADD/SMEMBERS/SREM` for `cache:keys` index.

### API → MinIO
`AWSSDK.S3` with `ForcePathStyle = true` and `ServiceURL = "http://minio:9000"`.

---

## Error Handling

| Scenario | HTTP | Message |
|----------|------|---------|
| Invalid credentials | 401 | "Invalid credentials" |
| Missing/expired/invalid JWT | 401 | "Unauthorized" |
| Wrong role | 403 | "Forbidden" |
| Empty question | 400 | "question is required and must be non-empty" |
| Question >2000 chars | 400 | "question must not exceed 2000 characters" |
| Unsupported file type | 422 | "Unsupported file type. Supported: .pdf, .docx" |
| File >50 MB | 413 | "File size exceeds the 50 MB limit" |
| Embedding service down | 502 | "Embedding service unavailable" |
| LLM service down | 502 | "Language model service unavailable" |
| PostgreSQL unreachable (audit) | 503 | "Audit log temporarily unavailable" |
| Too many invalid JWTs | 429 | "Too many requests" |

---

## Environment Variables (`.env.example`)

```dotenv
POSTGRES_USER=raguser
POSTGRES_PASSWORD=changeme_postgres
POSTGRES_DB=ragdb
MINIO_ROOT_USER=minioadmin
MINIO_ROOT_PASSWORD=changeme_minio
JWT_SECRET=changeme_jwt_secret_at_least_32_chars
JWT_EXPIRY_MINUTES=480
SEED_ADMIN_EMAIL=admin@example.com
SEED_ADMIN_PASSWORD=changeme_admin_password
MODEL_URL=https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf
MODEL_FILE=llama-3.2-3b-instruct-q4_k_m.gguf
N_CTX=4096
N_THREADS=4
CACHE_TTL_DAYS=7
```

---

## Correctness Properties

### Property 1: Vector Dimension Invariant
All `/embed`, `/embed/query`, `/process` endpoints return exactly 768-element vectors regardless of input.

**Validates: Requirements 2.1, 2.2, 2.3**

### Property 2: Chunk Size Invariant
Every chunk produced by `/process` contains at most 500 tokens (nomic tokenizer).

**Validates: Requirements 2.4**

### Property 3: Text Conservation
Joining all chunk texts reproduces the full extracted document text with no tokens dropped or duplicated.

**Validates: Requirements 2.5**

### Property 4: Unsupported File Type Rejection
Files with extensions other than `.pdf`/`.docx` sent to `/process` return HTTP 422.

**Validates: Requirements 2.8**

### Property 5: Whitespace Input Rejection
Whitespace-only strings sent to `/embed/query` return HTTP 422.

**Validates: Requirements 2.9**

### Property 6: JWT Expiry Invariant
For all valid logins: `exp == iat + JWT_EXPIRY_MINUTES * 60`.

**Validates: Requirements 4.4**

### Property 7: Employee Role Enforcement
Valid Employee JWTs receive HTTP 403 on all Admin-only endpoints (documents, audit).

**Validates: Requirements 4.7, 5.10**

### Property 8: Admin Role Enforcement
Valid Admin JWTs receive HTTP 403 on `POST /api/query`.

**Validates: Requirements 4.8, 6.13**

### Property 9: Admin Seed Idempotency
N invocations of the admin seed with the same email produce exactly 1 user record in PostgreSQL.

**Validates: Requirements 4.10**

### Property 10: Upload UUID v4 Format
All valid file uploads return a `documentId` matching the UUID v4 regex pattern.

**Validates: Requirements 5.1**

### Property 11: Chunk Count Consistency
`chunkCount` in `GET /api/documents` always equals the Chunk row count in PostgreSQL for that documentId.

**Validates: Requirements 5.7**

### Property 12: PG–Qdrant Text Consistency
Every PostgreSQL Chunk row's `text` field equals the `text` value in the corresponding Qdrant point payload.

**Validates: Requirements 5.8**

### Property 13: Duplicate Filename Isolation
Two uploads of the same filename produce two independent document records with different UUIDs, both intact.

**Validates: Requirements 5.9**

### Property 14: SSE Stream Structure Invariant
All completed queries emit events in this exact order: exactly one `event: sources` event, then one or more `data: {"token":"..."}` events, then exactly one `data: [DONE]` event.

**Validates: Requirements 6.7, 12.5**

### Property 15: Source Attribution Consistency
The filenames in the `sources` SSE event equal the distinct FileNames of the PostgreSQL Document rows linked to the top-5 retrieved Qdrant chunk points.

**Validates: Requirements 6.8**

### Property 16: Audit Log Completeness
Every completed query writes an AuditLog row with non-null userEmail, non-empty query, and non-empty retrievedSources.

**Validates: Requirements 6.11, 6.12**

### Property 17: Cache Eviction Completeness
After re-ingesting document D, the Redis semantic cache contains zero entries whose `sources` array includes D.fileName.

**Validates: Requirements 7.2**

### Property 18: Cache Eviction Precision
Cache entries whose `sources` arrays do not include D.fileName remain unchanged after D's invalidation scan.

**Validates: Requirements 7.3**

### Property 19: Audit Log Ordering
`GET /api/audit` always returns entries sorted by `createdAt` descending (most recent first).

**Validates: Requirements 8.1**
| P18 | Cache eviction precision: entries not referencing D.fileName survive D's invalidation scan | 7.3 |
| P19 | Audit log ordering: GET /api/audit always returns entries sorted by createdAt DESC | 8.1 |

---

## Testing Strategy

**Property-based testing libraries:**
- .NET: FsCheck + xUnit (`FsCheck.Xunit`), minimum 100 iterations per property
- Python: Hypothesis, `@settings(max_examples=100)` on all property tests

**Test tree:**
```
tests/
├── Api.Tests/
│   ├── Unit/         TokenServiceTests, CacheServiceTests, IngestionServiceTests
│   ├── Integration/  AuthControllerTests, DocumentsControllerTests, QueryControllerTests, AuditControllerTests
│   └── Properties/   JwtPropertiesTests (P6,P9), RoleEnforcementTests (P7,P8),
│                     IngestionPropertiesTests (P10-P13), QueryPropertiesTests (P14-P16),
│                     CachePropertiesTests (P17-P19)
└── embedding.tests/
    ├── test_embed_properties.py   (P1, P5)
    ├── test_chunk_properties.py   (P2, P3, P4)
    └── test_health.py
```

---

## Design Decisions

**Why in-process cosine similarity for cache?** Redis native vector search requires premium modules. For v1 cache sizes (<10,000 entries), O(N×768) computation in .NET is <50ms.

**Why `Channel<Guid>` for ingestion queue vs. a message broker?** Avoids additional service; single CPU-bound consumer is sufficient. Trade-off: pending jobs lost on API restart (documents stay `processing`).

**Why plain `role` claim in JWT?** ASP.NET Identity emits `ClaimTypes.Role` URIs by default — these break the Angular `decodeJwt` which reads `payload.role`. Using `new Claim("role", value)` fixes this without any frontend changes.

**Why `appendonly yes` for Redis?** AOF durability — cache miss on restart is acceptable, but AOF aligns with the requirement wording and costs little.

**Why MinIO for file storage?** Decouples raw files from processing. Enables re-ingestion without re-upload. S3-compatible API allows future migration to cloud S3 without code changes.

**Why Nginx Dockerfile multi-stage copy for static assets?** Cleaner than bind-mount volumes — images are self-contained. Angular apps are built once and copied into the Nginx image at build time. Subsequent starts are fast (image cache hit).

---

## Data Models

### AppUser (ASP.NET Identity)
```
Id: string (GUID)          PK
Email: string              unique, ≤254 chars
NormalizedEmail: string    uppercase index
PasswordHash: string       bcrypt hash ($2b$, cost ≥10)
Role: string               "Admin" | "Employee"
(+ standard Identity columns)
```

### Document
```
Id: UUID                   PK
FileName: string           original uploaded filename
UploaderId: string         FK → AspNetUsers.Id
Status: string             "queued" | "processing" | "indexed" | "failed"
CreatedAt: DateTimeOffset  UTC timestamp
ChunkCount: int            0 until indexed
```

### Chunk
```
Id: UUID                   PK
DocumentId: UUID           FK → Documents.Id (CASCADE DELETE)
Ordinal: int               0-based sequential index
Text: string               chunk text content
QdrantPointId: UUID        ID of the corresponding Qdrant vector point
```

### AuditLog
```
Id: UUID                   PK
UserEmail: string          JWT email claim of querying employee
Query: string              original question text
RetrievedSources: string[] filenames of documents used in answer
CreatedAt: DateTimeOffset  UTC timestamp
```

### DTOs

**LoginRequest:** `{ email: string, password: string }`
**LoginResponse:** `{ token: string, user: { email: string, role: string } }`
**DocumentRecord:** `{ id, fileName, uploader, status, createdAt, chunkCount }`
**UploadResponse:** `{ documentId: string }`
**QueryRequest:** `{ question: string }`
**AuditLogDto:** `{ id, userEmail, query, retrievedSources, createdAt }`

### Property 1: Vector Dimension Invariant
For all valid text inputs to `/embed`, `/embed/query`, or `/process`, every returned vector SHALL have exactly 768 elements.

### Property 2: Chunk Size Invariant
For all valid documents processed by `/process`, every chunk SHALL contain at most 500 tokens as measured by the nomic-embed-text-v1 tokenizer.

### Property 3: Text Conservation Across Chunks
For all valid documents, joining all chunk texts SHALL reproduce the full extracted document text with no tokens dropped or duplicated.

### Property 4: Unsupported File Type Rejection
For all files with extensions not in {".pdf", ".docx"}, `/process` SHALL return HTTP 422.

### Property 5: Whitespace Input Rejection
For all whitespace-only strings submitted to `/embed/query`, the endpoint SHALL return HTTP 422.

### Property 6: JWT Token Expiry Invariant
For all valid credential pairs, the returned JWT's `exp` SHALL equal `iat + (JWT_EXPIRY_MINUTES × 60)`.

### Property 7: Role Enforcement — Employee Cannot Access Admin Endpoints
For all valid Employee JWTs, requests to `POST /api/documents`, `GET /api/documents`, and `GET /api/audit` SHALL return HTTP 403.

### Property 8: Role Enforcement — Admin Cannot Access Employee Endpoints
For all valid Admin JWTs, `POST /api/query` SHALL return HTTP 403.

### Property 9: Admin Seed Idempotency
For any number of invocations of the admin seed with the same email, the database SHALL contain exactly one user with that email.

### Property 10: Document Upload Returns Valid UUID v4
For all valid file uploads, the returned `documentId` SHALL match the UUID v4 pattern.

### Property 11: Chunk Count Consistency Invariant
For all indexed documents, `chunkCount` in `GET /api/documents` SHALL equal the Chunk row count in PostgreSQL for that documentId.

### Property 12: Text Consistency Between PostgreSQL and Qdrant
For all Chunk rows, the `text` field SHALL equal the `text` value in the corresponding Qdrant point payload.

### Property 13: Duplicate Filename Isolation
Uploading two files with the same filename SHALL create two distinct document records with different UUIDs.

### Property 14: SSE Stream Structure Invariant
For all completed queries, the SSE stream SHALL emit: exactly one `event: sources` event, then one or more token events, then exactly one `data: [DONE]` event — in that order.

### Property 15: Source Attribution Consistency
For all cache-miss queries, the filenames in the `sources` event SHALL equal the distinct FileNames of the top-5 retrieved Qdrant chunk documents.

### Property 16: Audit Log Completeness Invariant
For all completed queries, the AuditLog row SHALL contain non-null userEmail, non-empty query, and non-empty retrievedSources.

### Property 17: Cache Eviction Completeness
After re-ingesting document D, the Redis cache SHALL contain zero entries whose sources array includes D.fileName.

### Property 18: Cache Eviction Precision
Cache entries whose sources do NOT include D.fileName SHALL remain unchanged after D's invalidation scan.

### Property 19: Audit Log Ordering Invariant
`GET /api/audit` SHALL always return entries sorted by createdAt descending.
