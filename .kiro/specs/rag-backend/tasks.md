# Implementation Plan: RAG Backend

## Overview

Implement the on-premises RAG document intelligence platform across seven delivery slices, following the design's slice map. Each slice builds on the previous and ends with all components wired together. The .NET 8 API lives at `services/api/`, the Python embedding service at `services/embedding/`, the LLM service at `services/llm/`, infrastructure at the repo root and `infra/nginx/`, and tests at `tests/Api.Tests/` (xUnit + FsCheck) and `tests/embedding.tests/` (pytest + Hypothesis). Angular apps at `apps/admin/` and `apps/employee/` are already built — do NOT modify them.

---

## Tasks

- [x] 1. Slice 0 — Infrastructure skeleton
  - [x] 1.1 Create `docker-compose.yml` at repo root
    - Define all eight services: `postgres`, `redis`, `qdrant`, `minio`, `embedding`, `llm`, `api`, `nginx`
    - Declare named volumes: `pg_data`, `qdrant_data`, `minio_data`, `model_cache`, `redis_data`
    - Define `ragnet` bridge network with `internal: true`
    - Add healthcheck stanzas for `postgres` (`pg_isready`), `redis` (`redis-cli ping`), `qdrant` (`GET /healthz`), `minio` (`GET /minio/health/live`), `embedding` (`GET /health`), `llm` (`GET /health`)
    - Wire `api`, `embedding`, `llm`, `nginx` with `depends_on: condition: service_healthy` on all stateful dependencies
    - Bind only port 80 on the `nginx` service; no other service binds a host port
    - Add `restart: unless-stopped` to all services
    - Configure Redis with `command: redis-server --appendonly yes`
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.7, 9.1–9.5, 10.1, 10.2, 10.7_
  - [x] 1.2 Create `.env.example` at repo root
    - Include all required keys: `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `JWT_SECRET`, `JWT_EXPIRY_MINUTES`, `SEED_ADMIN_EMAIL`, `SEED_ADMIN_PASSWORD`, `MODEL_URL`, `MODEL_FILE`, `N_CTX`, `N_THREADS`, `CACHE_TTL_DAYS`
    - Use non-secret placeholder values that work for local setup as-is
    - _Requirements: 1.6_
  - [x] 1.3 Create `infra/nginx/nginx.conf`
    - Add `location = /healthz` returning `200 ok` inline (no upstream)
    - Add `location /api/` with `proxy_pass http://api:8080/api/`, `proxy_buffering off`, `proxy_cache off`, `proxy_read_timeout 120s`, `proxy_http_version 1.1`, `Connection ""` header for SSE keep-alive
    - Add `location /admin/` serving admin SPA with `try_files $uri $uri/ /admin/index.html`
    - Add `location /` serving employee SPA with `try_files $uri $uri/ /index.html`
    - _Requirements: 1.7, 10.2, 10.4, 12.5_

  - [x] 1.4 Create `infra/nginx/Dockerfile`
    - Use multi-stage build: first stage copies pre-built Angular static assets from `apps/admin/dist/` and `apps/employee/dist/` into an intermediate layer; final stage is `nginx:1.27-alpine` copying those assets into `/usr/share/nginx/html/` and copying `nginx.conf` into `/etc/nginx/nginx.conf`
    - Do NOT build Angular — apps are already built in `apps/admin/` and `apps/employee/`
    - _Requirements: 10.4, 12.1–12.6_

- [x] 2. Slice 1 — Python Embedding Service
  - [x] 2.1 Scaffold `services/embedding/` project files
    - Create `services/embedding/requirements.txt` with pinned versions: `fastapi==0.111.0`, `uvicorn[standard]==0.30.1`, `sentence-transformers==3.0.1`, `pypdf==4.2.0`, `python-docx==1.1.2`, `pydantic==2.7.1`
    - Create `services/embedding/Dockerfile`: base `python:3.11-slim`, copy and install requirements, copy source, expose port 8000, `CMD uvicorn main:app --host 0.0.0.0 --port 8000`
    - _Requirements: 2.1–2.10_
  - [x] 2.2 Implement `services/embedding/models.py`
    - Define Pydantic request/response models: `EmbedRequest`, `EmbedResponse`, `EmbedQueryRequest`, `EmbedQueryResponse`, `ProcessRequest`, `ChunkResult`, `ProcessResponse`
    - _Requirements: 2.1, 2.2, 2.3_
  - [x] 2.3 Implement `services/embedding/embedder.py`
    - Load `nomic-ai/nomic-embed-text-v1` via `sentence_transformers.SentenceTransformer` at module level (singleton)
    - Expose `embed_texts(texts: list[str]) -> list[list[float]]` and `embed_query(text: str) -> list[float]`
    - Validate output dimension == 768 before returning; raise `ValueError` if not
    - _Requirements: 2.1, 2.2, 2.10_
  - [x] 2.4 Implement `services/embedding/chunker.py`
    - Expose `extract_text(file_bytes: bytes, filename: str) -> str`: use `pypdf` for `.pdf` and `python-docx` for `.docx`; raise `ValueError` with unsupported-type message for other extensions; raise `ValueError` with parse-error message for corrupt files
    - Expose `chunk_text(text: str, tokenizer, max_tokens: int = 500) -> list[str]`: encode full text, window by `max_tokens` with no overlap, decode each window; guarantees `"".join(chunks) == full_text` at token level
    - _Requirements: 2.3, 2.4, 2.5, 2.6, 2.8_

  - [x] 2.5 Implement `services/embedding/health.py` and `services/embedding/main.py`
    - `health.py`: expose `is_ready() -> bool` returning `True` only after the sentence-transformer model is loaded
    - `main.py`: wire FastAPI app with all four endpoints (`POST /embed`, `POST /embed/query`, `POST /process`, `GET /health`)
    - `GET /health`: return `{"status":"ok"}` with HTTP 200 when ready; return HTTP 503 when not ready
    - `POST /embed`: validate `texts` is non-empty (HTTP 422 otherwise); call `embedder.embed_texts`; return `{"vectors": [...], "dim": 768}`
    - `POST /embed/query`: validate `text` is non-empty / non-whitespace (HTTP 422 otherwise); return `{"vector": [...]}`
    - `POST /process`: validate extension (`.pdf`/`.docx`, case-insensitive, HTTP 422 otherwise); decode base64; extract text; chunk; embed each chunk; return `{"chunks": [{"ordinal": N, "text": "...", "vector": [...]}]}`
    - _Requirements: 2.1–2.9_
  - [ ]* 2.6 Write property tests for embedding service — P1: Vector Dimension Invariant
    - Create `tests/embedding.tests/test_embed_properties.py`
    - Create `tests/embedding.tests/conftest.py` with `pytest` fixtures starting the embedding service
    - **Property 1: Vector Dimension Invariant** — use `@given(st.text(min_size=1))` (Hypothesis) to assert every `/embed/query` response vector has exactly 768 elements; separately test `/embed` with lists of 1–10 texts; separately test `/process` with synthetic PDF/DOCX bytes
    - `@settings(max_examples=100)` on all property tests
    - **Validates: Requirements 2.1, 2.2, 2.3**
  - [ ]* 2.7 Write property tests for embedding service — P5: Whitespace Input Rejection
    - In `tests/embedding.tests/test_embed_properties.py`
    - **Property 5: Whitespace Input Rejection** — use `@given(st.text(alphabet=st.characters(whitelist_categories=("Zs","Cc")), min_size=1))` to assert `/embed/query` returns HTTP 422 for any whitespace-only text
    - **Validates: Requirements 2.9**
  - [ ]* 2.8 Write property tests for embedding service — P2, P3, P4: Chunk invariants
    - Create `tests/embedding.tests/test_chunk_properties.py`
    - **Property 2: Chunk Size Invariant** — generate synthetic text of varying lengths; assert all chunks from `/process` have ≤ 500 nomic tokens
    - **Property 3: Text Conservation** — assert `"".join(chunk["text"] for chunk in response["chunks"])` equals full extracted text at the token level
    - **Property 4: Unsupported File Type Rejection** — use `@given(st.text(min_size=1).filter(lambda e: not e.lower().endswith((".pdf",".docx"))))` for filename; assert HTTP 422
    - **Validates: Requirements 2.4, 2.5, 2.8**

- [x] 3. Slice 2 — LLM Service
  - [x] 3.1 Create `services/llm/entrypoint.sh`
    - Check for model file at `/models/${MODEL_FILE}`; if absent and `MODEL_URL` is non-empty, download with `wget -O /models/${MODEL_FILE} ${MODEL_URL}`
    - If download fails (non-zero exit), print error to stderr with URL and reason, exit 1
    - If model file still absent after check (no `MODEL_URL`), print error to stderr, exit 1
    - Start llama.cpp server: `/server -m /models/${MODEL_FILE} --host 0.0.0.0 --port 8081 --ctx-size ${N_CTX:-4096} --threads ${N_THREADS:-4} --no-gpu`
    - _Requirements: 3.1, 3.4, 3.5, 3.6, 3.7_
  - [x] 3.2 Create `services/llm/Dockerfile`
    - Base image `ghcr.io/ggerganov/llama.cpp:server`
    - Copy `entrypoint.sh` and make it executable
    - Expose port 8081
    - Set `ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]`
    - _Requirements: 3.1, 3.2, 3.3, 3.4_

- [x] 4. Checkpoint — Verify infrastructure and service builds
  - Ensure all Dockerfiles build without errors (`docker compose build`), `docker-compose.yml` validates (`docker compose config`), and `.env.example` contains all required keys. Ask the user if questions arise.

- [x] 5. Slice 3 — .NET 8 API core scaffold, models, and auth
  - [x] 5.1 Scaffold .NET 8 API project
    - Create `services/api/RagBackend.Api.csproj` targeting `net8.0` with package references: `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Qdrant.Client`, `StackExchange.Redis`, `AWSSDK.S3`; use exact pinned versions
    - Create `services/api/Dockerfile`: multi-stage build (`mcr.microsoft.com/dotnet/sdk:8.0` for build, `mcr.microsoft.com/dotnet/aspnet:8.0` for runtime); expose port 8080; `ENTRYPOINT dotnet RagBackend.Api.dll`
    - Create `services/api/appsettings.json` with placeholder configuration sections: `ConnectionStrings`, `Redis`, `Minio`, `Embedding`, `Llm`, `Qdrant`, `Jwt`, `Seed`
    - _Requirements: 4.1–4.10, 10.3_
  - [x] 5.2 Implement EF Core data models and `AppDbContext`
    - Create `services/api/Models/AppUser.cs`: extends `IdentityUser`, adds `string Role` property
    - Create `services/api/Models/Document.cs`: `Id` (Guid), `FileName`, `UploaderId`, `Status`, `CreatedAt` (DateTimeOffset), `ChunkCount`
    - Create `services/api/Models/Chunk.cs`: `Id` (Guid), `DocumentId` (Guid FK), `Ordinal`, `Text`, `QdrantPointId` (Guid); unique constraint on `(DocumentId, Ordinal)`
    - Create `services/api/Models/AuditLog.cs`: `Id` (Guid), `UserEmail`, `Query`, `RetrievedSources` (string[]), `CreatedAt` (DateTimeOffset)
    - Create `services/api/Data/AppDbContext.cs`: `IdentityDbContext<AppUser>`; configure `AuditLog.RetrievedSources` with `HasColumnType("text[]")`; add `DbSet<Document>`, `DbSet<Chunk>`, `DbSet<AuditLog>`
    - Generate initial EF Core migration: `dotnet ef migrations add InitialSchema -o Migrations`
    - _Requirements: 4.9, 5.1–5.4, 8.1_
  - [x] 5.3 Implement DTO classes
    - Create `services/api/DTOs/LoginRequest.cs`, `LoginResponse.cs`, `DocumentRecord.cs`, `UploadResponse.cs`, `QueryRequest.cs`, `AuditLogDto.cs`
    - All DTOs use JSON-serializable property names matching the API contract exactly (`documentId`, `fileName`, `uploader`, `userEmail`, `retrievedSources`, etc.)
    - _Requirements: 12.1–12.6_

  - [x] 5.4 Implement `TokenService` and `SeedService`
    - Create `services/api/Services/TokenService.cs`: generate HS256 JWT with claims `email` (plain name), `role` (plain name — NOT `ClaimTypes.Role`), `iat`, `exp`; `exp = iat + JWT_EXPIRY_MINUTES * 60`
    - Create `services/api/Data/SeedService.cs`: on startup, if `SEED_ADMIN_EMAIL` is set and no user with that email exists, create Admin user via `UserManager`; if user already exists, skip silently
    - _Requirements: 4.1–4.4, 4.10_
  - [x] 5.5 Implement `InvalidJwtRateLimitMiddleware` and `GlobalExceptionMiddleware`
    - Create `services/api/Middleware/InvalidJwtRateLimitMiddleware.cs`: track invalid JWT attempts per source IP in `IMemoryCache` keyed by `"ratelimit:{ip}:{windowStart}"` where `windowStart = utcSeconds / 60`; return HTTP 429 after >10 failures within a 60-second window
    - Create `services/api/Middleware/GlobalExceptionMiddleware.cs`: catch unhandled exceptions; return structured JSON error responses with appropriate HTTP status codes; never expose stack traces
    - _Requirements: 10.5, 10.6_
  - [x] 5.6 Implement `AuthController` and wire `Program.cs`
    - Create `services/api/Controllers/AuthController.cs`: `POST /api/auth/login` — validate request, look up user by email, verify bcrypt password hash, return `LoginResponse` or HTTP 401; do NOT indicate which field was wrong
    - Create `services/api/Program.cs`: register all services (Identity, JWT bearer auth, EF Core with Npgsql, HttpClient factories, Redis, Qdrant, MinIO S3 client, all service classes); add auth/authz middleware; register `InvalidJwtRateLimitMiddleware` and `GlobalExceptionMiddleware`; call `MigrateAsync()`, `SeedService.SeedAsync()`, ensure MinIO bucket, ensure Qdrant collection on startup
    - Configure `PasswordHasherOptions` with `IterationCount >= 10` (maps to bcrypt cost factor)
    - _Requirements: 4.1–4.10, 10.3, 10.5_
  - [-]* 5.7 Write unit tests for `TokenService`
    - Create `tests/Api.Tests/Unit/TokenServiceTests.cs`
    - Test JWT structure: verify HS256 algorithm, required claims present (`email`, `role`, `iat`, `exp`), role is plain string not URI
    - _Requirements: 4.2, 4.3_

  - [ ]* 5.8 Write property tests for auth — P6: JWT Expiry Invariant
    - Create `tests/Api.Tests/Properties/JwtPropertiesTests.cs` (FsCheck + xUnit)
    - **Property 6: JWT Expiry Invariant** — use `Prop.ForAll<int>` generating valid `JWT_EXPIRY_MINUTES` values (1–1440); assert `exp == iat + minutes * 60` for every generated value; run with `FsCheck.Xunit` `[Property]` attribute, minimum 100 samples
    - **Validates: Requirements 4.4**
  - [ ]* 5.9 Write property tests for auth — P9: Admin Seed Idempotency
    - In `tests/Api.Tests/Properties/JwtPropertiesTests.cs`
    - **Property 9: Admin Seed Idempotency** — use `Prop.ForAll<PositiveInt>` to call `SeedService.SeedAsync()` N times with the same email; query PostgreSQL and assert exactly 1 user record exists with that email
    - **Validates: Requirements 4.10**
  - [ ]* 5.10 Write property tests for auth — P7, P8: Role Enforcement
    - Create `tests/Api.Tests/Properties/RoleEnforcementTests.cs`
    - **Property 7: Employee Role Enforcement** — use `Prop.ForAll` generating valid Employee JWTs; assert `POST /api/documents`, `GET /api/documents`, and `GET /api/audit` all return HTTP 403
    - **Property 8: Admin Role Enforcement** — use `Prop.ForAll` generating valid Admin JWTs; assert `POST /api/query` returns HTTP 403
    - **Validates: Requirements 4.7, 4.8, 5.10, 6.13**

- [x] 6. Slice 4 — Document ingestion pipeline
  - [x] 6.1 Implement `MinioService`
    - Create `services/api/Services/MinioService.cs`
    - Inject `IAmazonS3` configured with `ForcePathStyle = true`, `ServiceURL = "http://minio:9000"`
    - Expose `UploadAsync(Guid documentId, Stream fileStream, string contentType)` — puts object in `documents` bucket with key = documentId string
    - Expose `GetFileBase64Async(Guid documentId)` — downloads object and returns base64-encoded string
    - Expose `EnsureBucketAsync()` — creates `documents` bucket if it does not exist
    - _Requirements: 5.2_
  - [x] 6.2 Implement `QdrantService`
    - Create `services/api/Services/QdrantService.cs`
    - Inject `QdrantClient` configured for `qdrant:6333`
    - Expose `EnsureCollectionAsync()` — creates `documents` collection (vector size 768, Cosine distance) if it does not exist
    - Expose `UpsertChunkAsync(Guid pointId, float[] vector, Dictionary<string,string> payload)` — upserts a single point with payload `{documentId, chunkId, text, fileName}`
    - Expose `SearchAsync(float[] queryVector, int topK = 5)` — returns top-K results with payload
    - _Requirements: 5.4, 5.8, 6.4_
  - [x] 6.3 Implement `EmbeddingClient`
    - Create `services/api/Services/EmbeddingClient.cs`
    - Typed `HttpClient` with `BaseAddress = Embedding__BaseUrl`, timeout 30s for `/process`, 5s for `/embed/query`
    - Expose `ProcessAsync(string fileBase64, string fileName)` — POST to `/process`, deserialise chunks
    - Expose `EmbedQueryAsync(string text)` — POST to `/embed/query`, return `float[]`
    - Throw descriptive exceptions on non-2xx; callers map to HTTP 502
    - _Requirements: 5.4, 5.5, 6.2, 6.16_

  - [x] 6.4 Implement `IngestionService` (background queue worker)
    - Create `services/api/Services/IngestionService.cs` as `IHostedService` backed by `Channel<Guid>`
    - Expose `EnqueueAsync(Guid documentId)` for `DocumentsController` to call after 201 is returned
    - Worker loop: dequeue document ID → set status `processing` → fetch file from MinIO → base64 encode → call `EmbeddingClient.ProcessAsync` → open EF Core transaction → for each chunk: INSERT `Chunk` row + `QdrantService.UpsertChunkAsync` → UPDATE Document `status=indexed`, `chunkCount=N` → commit → trigger cache invalidation (call `CacheService.InvalidateByFileNameAsync`)
    - On any error after `processing`: rollback chunks, `DELETE` any partially written Chunk rows for that documentId, set status `failed`, log error with documentId
    - _Requirements: 5.3, 5.4, 5.5, 7.1_
  - [x] 6.5 Implement `DocumentsController`
    - Create `services/api/Controllers/DocumentsController.cs`
    - `POST /api/documents` (Admin only): validate extension (`.pdf`/`.docx`, HTTP 422); validate size ≤50 MB (HTTP 413); generate UUID v4; upload to MinIO; INSERT Document row with `status=queued`; enqueue to `IngestionService`; return HTTP 201 `{ documentId }`
    - `GET /api/documents` (Admin only): query PostgreSQL, join `UploaderId → AspNetUsers.Email` to populate `uploader`; return `DocumentRecord[]` ordered by `CreatedAt` descending
    - Both endpoints return HTTP 403 for Employee JWTs
    - _Requirements: 5.1–5.7, 5.9–5.12, 12.2, 12.3_
  - [ ]* 6.6 Write unit tests for `IngestionService`
    - Create `tests/Api.Tests/Unit/IngestionServiceTests.cs`
    - Test happy-path: verify Chunk rows inserted, Document status set to `indexed`, `chunkCount` updated
    - Test failure path: verify Chunk rows deleted and status set to `failed` on embedding error
    - _Requirements: 5.4, 5.5_
  - [ ]* 6.7 Write property tests for ingestion — P10, P11, P12, P13
    - Create `tests/Api.Tests/Properties/IngestionPropertiesTests.cs`
    - **Property 10: Upload UUID v4 Format** — `Prop.ForAll` uploading valid small PDF/DOCX byte arrays; assert `documentId` matches UUID v4 regex `^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`
    - **Property 11: Chunk Count Consistency** — after indexed, assert `GET /api/documents` `chunkCount` == `SELECT COUNT(*) FROM "Chunks" WHERE "DocumentId" = id`
    - **Property 12: PG–Qdrant Text Consistency** — for each Chunk row, fetch Qdrant point by `QdrantPointId`; assert `payload.text == Chunk.Text`
    - **Property 13: Duplicate Filename Isolation** — upload same filename twice; assert two distinct UUIDs returned and both records present in `GET /api/documents`
    - **Validates: Requirements 5.1, 5.7, 5.8, 5.9**

- [x] 7. Checkpoint — Verify ingestion slice
  - Ensure all `tests/Api.Tests/Unit/IngestionServiceTests.cs` pass and `POST /api/documents` returns HTTP 201 with a valid UUID. Ensure documents transition to `indexed`. Ask the user if questions arise.

- [x] 8. Slice 5 — Query pipeline (RAG loop)
  - [x] 8.1 Implement `CacheService`
    - Create `services/api/Services/CacheService.cs`
    - Inject `IConnectionMultiplexer` (StackExchange.Redis)
    - Implement cosine similarity: `static float CosineSimilarity(float[] a, float[] b)` as specified in the design (dot product / (|a| × |b|))
    - Expose `GetCachedAnswerAsync(float[] queryVector, float threshold = 0.92f)` — `SMEMBERS cache:keys` → for each key `HGET` vector → compute cosine → if ≥ threshold return `(answer, sources)` tuple; return null on miss
    - Expose `StoreCacheEntryAsync(float[] queryVector, string answer, string[] sources, int ttlSeconds)` — `HSET cache:{uuid} vector/answer/sources` + `EXPIRE` + `SADD cache:keys`
    - Expose `InvalidateByFileNameAsync(string fileName)` — `SMEMBERS cache:keys` → for each key `HGET sources` → if `fileName` in sources: `DEL key` + `SREM cache:keys key`; log error and continue if Redis unreachable
    - _Requirements: 6.3, 6.9, 6.10, 7.1–7.4_
  - [x] 8.2 Implement `LlmClient`
    - Create `services/api/Services/LlmClient.cs`
    - Typed `HttpClient` with `BaseAddress = Llm__BaseUrl`
    - Expose `StreamCompletionAsync(string prompt, CancellationToken ct)` returning `IAsyncEnumerable<string>`: POST to `/v1/chat/completions` with `"stream": true` using `HttpCompletionOption.ResponseHeadersRead`; parse SSE lines for `choices[0].delta.content`; yield each non-null content string; stop on `[DONE]`
    - Throw descriptive exception on non-2xx; caller maps to HTTP 502
    - _Requirements: 3.3, 6.5, 6.6, 6.17_
  - [x] 8.3 Implement `AuditService`
    - Create `services/api/Services/AuditService.cs`
    - Expose `WriteAuditLogAsync(string userEmail, string query, string[] retrievedSources)` — INSERT `AuditLog` row to PostgreSQL via EF Core
    - _Requirements: 6.11, 6.12, 8.1_

  - [x] 8.4 Implement `RagService`
    - Create `services/api/Services/RagService.cs`
    - Inject `EmbeddingClient`, `CacheService`, `QdrantService`, `LlmClient`, `AuditService`
    - Implement `ExecuteQueryAsync(string question, string userEmail, HttpResponse response, CancellationToken ct)`:
      1. Call `EmbeddingClient.EmbedQueryAsync(question)` → `queryVector`
      2. Call `CacheService.GetCachedAnswerAsync(queryVector)` → cache hit/miss
      3. Cache HIT: emit `event: sources\ndata: {JSON(entry.sources)}\n\n`, stream cached answer as token events, emit `data: [DONE]\n\n`, call `AuditService.WriteAuditLogAsync`; return
      4. Cache MISS: call `QdrantService.SearchAsync(queryVector, 5)` → top-5 chunks; extract distinct `fileName` values → `sources`; build prompt (system + chunk texts + question); emit `event: sources\ndata: {JSON(sources)}\n\n`; call `LlmClient.StreamCompletionAsync`; for each token emit `data: {"token":"<value>"}\n\n` within 100ms; emit `data: [DONE]\n\n`; call `CacheService.StoreCacheEntryAsync`; call `AuditService.WriteAuditLogAsync`
    - _Requirements: 6.2–6.12_
  - [x] 8.5 Implement `QueryController`
    - Create `services/api/Controllers/QueryController.cs`
    - `POST /api/query` (Employee only): validate `question` non-null/non-empty after trim (HTTP 400); validate length ≤2000 (HTTP 400); set `Content-Type: text/event-stream`; call `RagService.ExecuteQueryAsync`
    - Catch embedding/LLM exceptions and return HTTP 502 with appropriate messages
    - Return HTTP 403 for Admin JWTs
    - _Requirements: 6.1, 6.13–6.17, 12.5_
  - [-]* 8.6 Write unit tests for `CacheService`
    - Create `tests/Api.Tests/Unit/CacheServiceTests.cs`
    - Test `CosineSimilarity` with known vectors; test cache hit/miss threshold boundary (0.919 → miss, 0.920 → hit); test TTL calculation
    - _Requirements: 6.3_
  - [ ]* 8.7 Write property tests for query pipeline — P14, P15, P16
    - Create `tests/Api.Tests/Properties/QueryPropertiesTests.cs`
    - **Property 14: SSE Stream Structure Invariant** — `Prop.ForAll` generating valid question strings; assert SSE output starts with exactly one `event: sources`, followed by one or more `data: {"token":"..."}` events, followed by exactly one `data: [DONE]`
    - **Property 15: Source Attribution Consistency** — for cache-miss queries, assert filenames in `sources` event equal distinct `FileName` values of PostgreSQL Document rows linked to top-5 Qdrant points
    - **Property 16: Audit Log Completeness** — after each completed query, query PostgreSQL; assert `AuditLog` row has non-null `UserEmail`, non-empty `Query`, non-empty `RetrievedSources`
    - **Validates: Requirements 6.7, 6.8, 6.11, 6.12, 12.5**

- [x] 9. Slice 6 — Hardening: rate limiting, cache invalidation, audit endpoint, Nginx wiring
  - [x] 9.1 Implement `AuditController`
    - Create `services/api/Controllers/AuditController.cs`
    - `GET /api/audit` (Admin only): query `AuditLog` table ordered by `CreatedAt` descending; project to `AuditLogDto[]`; return HTTP 200
    - Return HTTP 403 for Employee JWTs
    - Catch `PostgresException`/`NpgsqlException` on unreachable DB and return HTTP 503 with "Audit log temporarily unavailable"
    - Return empty array `[]` when no entries exist
    - _Requirements: 8.1–8.5, 12.4_
  - [x] 9.2 Verify `InvalidJwtRateLimitMiddleware` wiring
    - Confirm middleware is registered before auth middleware in `Program.cs`
    - Confirm log entry includes source IP and rejection reason on invalid JWT (Requirement 10.5)
    - Confirm HTTP 429 response after >10 invalid JWT requests within 60 seconds from the same IP
    - _Requirements: 10.5, 10.6_
  - [x] 9.3 Verify cache invalidation integration in `IngestionService`
    - Confirm `CacheService.InvalidateByFileNameAsync` is called before Document status is written as `indexed`
    - Confirm on Redis unreachable: error is logged, document still transitions to `indexed`, invalidation is retried on next ingestion
    - _Requirements: 7.1, 7.4_
  - [x] 9.4 Final `docker-compose.yml` wiring review
    - Verify all `depends_on` conditions are correct for `api` (postgres, redis, qdrant, minio, embedding, llm all `service_healthy`)
    - Verify `nginx depends_on: [api]`
    - Verify only port 80 is published to host
    - Verify all services are on `ragnet` with `internal: true`
    - Verify all five named volumes are declared and mounted correctly
    - _Requirements: 1.1–1.5, 9.1–9.5, 10.1, 10.2_

  - [ ]* 9.5 Write property tests for cache invalidation — P17, P18
    - Create `tests/Api.Tests/Properties/CachePropertiesTests.cs`
    - **Property 17: Cache Eviction Completeness** — ingest document D; seed several cache entries some referencing D.fileName; re-ingest D; scan all Redis cache entries; assert zero entries contain D.fileName in sources
    - **Property 18: Cache Eviction Precision** — same setup; assert all cache entries NOT referencing D.fileName remain present and unchanged after invalidation
    - **Validates: Requirements 7.2, 7.3**
  - [ ]* 9.6 Write property test for audit ordering — P19
    - In `tests/Api.Tests/Properties/CachePropertiesTests.cs` (or a separate `AuditPropertiesTests.cs`)
    - **Property 19: Audit Log Ordering** — `Prop.ForAll<PositiveInt>` generating N queries (N 1–20); assert `GET /api/audit` returns entries where each `createdAt[i] >= createdAt[i+1]` (descending order)
    - **Validates: Requirements 8.1**
  - [-]* 9.7 Write integration tests
    - Create `tests/Api.Tests/Integration/AuthControllerTests.cs`: test valid login returns JWT + user; test wrong password returns 401 without field differentiation; test missing bearer returns 401
    - Create `tests/Api.Tests/Integration/DocumentsControllerTests.cs`: test 201 with valid PDF; test 422 on wrong extension; test 413 on oversized file; test 403 for Employee
    - Create `tests/Api.Tests/Integration/QueryControllerTests.cs`: test 400 on empty question; test 400 on question >2000 chars; test 403 for Admin; test 200 SSE stream on valid question
    - Create `tests/Api.Tests/Integration/AuditControllerTests.cs`: test 200 with entries ordered DESC; test 200 empty array; test 403 for Employee
    - _Requirements: 4.1–4.10, 5.1–5.12, 6.1–6.17, 8.1–8.5_

- [ ] 10. Final checkpoint — Ensure all tests pass
  - Run `dotnet test tests/Api.Tests/` and `pytest tests/embedding.tests/`; all tests must pass. Ask the user if questions arise.

---

## Notes

- Tasks marked with `*` are optional and can be skipped for a faster MVP delivery
- Each task references the specific requirements it satisfies for full traceability
- Property-based tests use FsCheck (`[Property]` attribute, minimum 100 samples) for .NET and Hypothesis (`@settings(max_examples=100)`) for Python
- The Angular apps at `apps/admin/` and `apps/employee/` are pre-built — do NOT modify them; the Nginx Dockerfile copies their `dist/` output at image build time
- Background ingestion via `Channel<Guid>` + `IHostedService` means the 201 response is returned before processing completes; documents in `processing` state on API restart should be retried or surfaced as `failed`
- The `role` JWT claim MUST use `new Claim("role", value)` and NOT `ClaimTypes.Role` so the Angular `decodeJwt` reads `payload.role` directly
- All secrets are injected via environment variables; `.env.example` serves as the operator's checklist


## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2", "1.3", "1.4"] },
    { "id": 1, "tasks": ["2.1", "3.1", "5.1"] },
    { "id": 2, "tasks": ["2.2", "2.3", "2.4", "3.2", "5.2"] },
    { "id": 3, "tasks": ["2.5", "5.3", "5.4"] },
    { "id": 4, "tasks": ["2.6", "2.7", "2.8", "5.5", "5.6"] },
    { "id": 5, "tasks": ["5.7", "5.8", "5.9", "5.10", "6.1", "6.2", "6.3"] },
    { "id": 6, "tasks": ["6.4", "8.1", "8.2", "8.3"] },
    { "id": 7, "tasks": ["6.5", "6.6", "6.7", "8.4"] },
    { "id": 8, "tasks": ["8.5", "8.6"] },
    { "id": 9, "tasks": ["8.7", "9.1", "9.2", "9.3", "9.4"] },
    { "id": 10, "tasks": ["9.5", "9.6", "9.7"] }
  ]
}
```
