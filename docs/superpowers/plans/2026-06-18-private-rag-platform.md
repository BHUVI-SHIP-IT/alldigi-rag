# On-Prem RAG Document Intelligence Platform — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a self-hosted, CPU-only RAG platform where admins ingest documents and employees query them in natural language, with no data ever leaving the company network.

**Architecture:** Ten containerised services on a Docker internal network behind Nginx. A .NET 8 Web API orchestrates auth, ingestion, and the RAG query loop; a Python FastAPI sidecar produces embeddings; llama.cpp serves a local Llama 3.2 3B model. Qdrant stores vectors, Postgres stores relational data + audit logs, Redis is the semantic cache, MinIO holds raw files.

**Tech Stack:** Docker Compose, Nginx, Angular 17+, .NET Core 8, Python 3.11 + FastAPI, nomic-embed-text, llama.cpp, Qdrant, Redis, PostgreSQL 16, MinIO.

## Global Constraints

- CPU-only inference — no GPU dependency anywhere.
- No outbound internet at runtime; no external LLM API keys.
- All inter-service traffic stays on the Docker internal network; Nginx is the sole ingress.
- Chunk size: 500 tokens. Embedding model: nomic-embed-text. LLM: Llama 3.2 3B Q4.
- Semantic cache threshold: cosine ≥ 0.92. Cache TTL default: 7 days.
- Retrieval: top-5 chunks by cosine similarity.
- All API endpoints require a valid JWT. Two roles: `Admin`, `Employee`.
- Every query is audit-logged: user, timestamp, retrieved sources.
- Passwords bcrypt-hashed via ASP.NET Identity. Persistent Docker volumes for Postgres, Qdrant, MinIO.

---

## Phase / Slice Map

Build in order. Each slice produces working, independently testable software. Slices 0–5 form the thin vertical (one PDF → one streamed answer); 6–8 add UIs and hardening.

- **Slice 0** — Infrastructure skeleton (compose, volumes, healthchecks)
- **Slice 1** — Embedding service (Python FastAPI)
- **Slice 2** — LLM service (llama.cpp)
- **Slice 3** — Middleware core + Auth (.NET 8, Postgres schema)
- **Slice 4** — Ingestion pipeline
- **Slice 5** — Query pipeline (RAG loop + SSE)
- **Slice 6** — Admin UI (Angular)
- **Slice 7** — Employee UI (Angular)
- **Slice 8** — Hardening (cache invalidation, audit UI, encryption, role tests)

---

## Slice 0 — Infrastructure Skeleton

### Task 0.1: Compose file with stateful services

**Files:**
- Create: `docker-compose.yml`
- Create: `.env.example`
- Create: `infra/nginx/nginx.conf`

**Interfaces:**
- Produces: service hostnames `postgres`, `redis`, `qdrant`, `minio`, `nginx` on network `ragnet`; named volumes `pg_data`, `qdrant_data`, `minio_data`.

- [ ] **Step 1: Write the compose file** defining `postgres` (postgres:16, env from `.env`, volume `pg_data:/var/lib/postgresql/data`, healthcheck `pg_isready`), `redis` (redis:7-alpine, healthcheck `redis-cli ping`), `qdrant` (qdrant/qdrant, volume `qdrant_data:/qdrant/storage`, healthcheck on `:6333/healthz`), `minio` (minio/minio, `server /data --console-address :9001`, volume `minio_data:/data`, healthcheck on `:9000/minio/health/live`). All on `networks: [ragnet]` with `restart: unless-stopped`. No published ports except Nginx later.
- [ ] **Step 2: Write `.env.example`** with `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `JWT_SECRET`, `JWT_EXPIRY_MINUTES`.
- [ ] **Step 3: Bring up the stateful tier**

Run: `cp .env.example .env && docker compose up -d postgres redis qdrant minio`
Expected: `docker compose ps` shows all four `healthy`.

- [ ] **Step 4: Verify each service answers**

Run: `docker compose exec redis redis-cli ping` → `PONG`; `curl -s localhost:6333/healthz` (after temp port map) → OK; `docker compose exec postgres pg_isready` → `accepting connections`.

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml .env.example infra/nginx/nginx.conf
git commit -m "feat(infra): stateful service tier with healthchecks"
```

### Task 0.2: Nginx ingress placeholder

**Files:**
- Modify: `infra/nginx/nginx.conf`
- Modify: `docker-compose.yml` (add `nginx` service, publish `80:80`)

- [ ] **Step 1:** Configure Nginx with an `upstream api { server api:8080; }` block (commented until Slice 3) and a `location /healthz { return 200 'ok'; }`.
- [ ] **Step 2: Verify** `curl localhost/healthz` → `ok`.
- [ ] **Step 3: Commit** `git commit -m "feat(infra): nginx ingress with healthz"`.

---

## Slice 1 — Embedding Service (Python FastAPI)

**Files:**
- Create: `services/embedding/app/main.py`
- Create: `services/embedding/app/embedder.py`
- Create: `services/embedding/requirements.txt`
- Create: `services/embedding/Dockerfile`
- Test: `services/embedding/tests/test_embedder.py`

**Interfaces:**
- Produces: `POST /embed {"texts": [str]} -> {"vectors": [[float]], "dim": int}`; `POST /embed/query {"text": str} -> {"vector": [float]}`. Embedding dim = 768 (nomic-embed-text).

- [ ] **Step 1: Write failing test**

```python
def test_embed_returns_fixed_dim(client):
    r = client.post("/embed", json={"texts": ["hello world"]})
    body = r.json()
    assert r.status_code == 200
    assert body["dim"] == 768
    assert len(body["vectors"][0]) == 768
```

- [ ] **Step 2: Run it, verify fail** — `pytest -q` → ImportError/404.
- [ ] **Step 3: Implement `embedder.py`** loading `nomic-embed-text` via sentence-transformers (CPU), and `main.py` exposing `/embed`, `/embed/query`, `/health`.
- [ ] **Step 4: Run tests, verify pass.**
- [ ] **Step 5: Write Dockerfile** (python:3.11-slim, install reqs, `uvicorn app.main:app --host 0.0.0.0 --port 8000`) and add `embedding` service to compose on `ragnet`.
- [ ] **Step 6: Commit** `git commit -m "feat(embedding): nomic-embed FastAPI service"`.

---

## Slice 2 — LLM Service (llama.cpp)

**Files:**
- Create: `services/llm/Dockerfile`
- Create: `services/llm/entrypoint.sh`
- Modify: `docker-compose.yml` (add `llm` service)
- Test: `services/llm/tests/test_stream.sh`

**Interfaces:**
- Produces: OpenAI-compatible `POST /v1/chat/completions` with `"stream": true` on `llm:8081`, serving Llama 3.2 3B Q4.

- [ ] **Step 1:** Write `Dockerfile` building `llama.cpp` server; `entrypoint.sh` that, on first run, fetches `Llama-3.2-3B-Instruct-Q4_K_M.gguf` from `MODEL_URL` into a mounted `model_cache` volume, then runs `llama-server -m <model> --host 0.0.0.0 --port 8081 -c 4096`.
- [ ] **Step 2:** Add `llm` service to compose with volume `model_cache:/models`, healthcheck on `:8081/health`.
- [ ] **Step 3: Smoke test** `test_stream.sh`: `curl -N llm:8081/v1/chat/completions -d '{"messages":[{"role":"user","content":"Say hi"}],"stream":true}'` → SSE chunks containing `data:`.
- [ ] **Step 4: Verify** stream emits tokens incrementally.
- [ ] **Step 5: Commit** `git commit -m "feat(llm): llama.cpp Llama-3.2-3B streaming server"`.

---

## Slice 3 — Middleware Core + Auth (.NET 8)

**Files:**
- Create: `services/api/Api.csproj`, `Program.cs`
- Create: `services/api/Data/AppDbContext.cs`
- Create: `services/api/Models/{Document,Chunk,AuditLog,AppUser}.cs`
- Create: `services/api/Auth/AuthController.cs`, `Auth/JwtTokenService.cs`
- Create: `services/api/Migrations/` (EF)
- Create: `services/api/Dockerfile`
- Test: `services/api.Tests/AuthTests.cs`

**Interfaces:**
- Produces: `POST /api/auth/register` (Admin-seeded), `POST /api/auth/login -> {token}`. Roles `Admin`/`Employee`. EF entities: `Document(Id, FileName, UploaderId, Status, CreatedAt)`, `Chunk(Id, DocumentId, QdrantPointId, Text, Ordinal)`, `AuditLog(Id, UserId, Query, RetrievedSources, CreatedAt)`. `[Authorize(Roles="Admin")]` / `[Authorize(Roles="Employee")]` guards.

- [ ] **Step 1: Write failing test** — `AuthTests.Login_WithValidCreds_ReturnsJwt` posting to `/api/auth/login`, asserting 200 + non-empty token; `Login_BadCreds_Returns401`.
- [ ] **Step 2: Run, verify fail.**
- [ ] **Step 3: Scaffold** ASP.NET Identity over `AppDbContext` (Npgsql), `JwtTokenService` signing with `JWT_SECRET`, `AuthController` login/register. Add `[Authorize]` default policy.
- [ ] **Step 4: Add EF migration** `InitialCreate` and apply on startup (`db.Database.Migrate()`).
- [ ] **Step 5: Run tests, verify pass.**
- [ ] **Step 6:** Add `api` service to compose, uncomment Nginx upstream, route `/api/` → `api:8080`.
- [ ] **Step 7: Verify** `curl localhost/api/auth/login` returns token end-to-end through Nginx.
- [ ] **Step 8: Commit** `git commit -m "feat(api): .NET 8 core, Identity, JWT, schema"`.

---

## Slice 4 — Ingestion Pipeline

**Files:**
- Create: `services/api/Ingestion/DocumentsController.cs`
- Create: `services/api/Ingestion/MinioStorage.cs`
- Create: `services/api/Ingestion/QdrantIndexer.cs`
- Create: `services/api/Ingestion/IngestionService.cs`
- Create: `services/embedding/app/parsing.py` (PDF/DOCX/URL → text → 500-tok chunks)
- Modify: `services/embedding/app/main.py` (add `/process`)
- Test: `services/api.Tests/IngestionTests.cs`, `services/embedding/tests/test_parsing.py`

**Interfaces:**
- Consumes: embedding `/embed`; Qdrant `:6333`; MinIO.
- Produces: `POST /api/documents` (multipart, Admin) → 202 with `documentId`; `GET /api/documents` (Admin) → list with `status`. Embedding `/process {"file_b64","filename"} -> {"chunks":[{"ordinal","text","vector"}]}`.

- [ ] **Step 1: Write failing parsing test** — `test_parsing.py`: a 1,200-token sample splits into 3 chunks of ≤500 tokens, ordinals 0,1,2.
- [ ] **Step 2: Run, verify fail.**
- [ ] **Step 3: Implement `parsing.py`** (pypdf, python-docx, trafilatura for URL) + `/process` endpoint embedding each chunk.
- [ ] **Step 4: Verify parsing tests pass.**
- [ ] **Step 5: Write failing `IngestionTests`** — upload sample PDF as Admin → 202; poll `GET /api/documents` until `status == "indexed"`; assert chunk rows exist and Qdrant point count > 0. Assert Employee upload → 403.
- [ ] **Step 6: Implement** `MinioStorage.Put`, `IngestionService` (store→register→call `/process`→`QdrantIndexer.Upsert`→write Chunk rows→set status `indexed`), `DocumentsController` (`[Authorize(Roles="Admin")]`).
- [ ] **Step 7: Run, verify pass.**
- [ ] **Step 8: Commit** `git commit -m "feat(ingest): upload, parse, embed, index pipeline"`.

---

## Slice 5 — Query Pipeline (RAG Loop + SSE)

**Files:**
- Create: `services/api/Query/QueryController.cs`
- Create: `services/api/Query/SemanticCache.cs` (Redis)
- Create: `services/api/Query/Retriever.cs` (Qdrant top-5)
- Create: `services/api/Query/PromptBuilder.cs`
- Create: `services/api/Query/LlmClient.cs` (SSE relay)
- Create: `services/api/Query/AuditWriter.cs`
- Test: `services/api.Tests/QueryTests.cs`

**Interfaces:**
- Consumes: embedding `/embed/query`; Redis; Qdrant; llm `/v1/chat/completions`.
- Produces: `POST /api/query {"question": str}` (Employee) → `text/event-stream` of answer tokens, terminating with a `sources` event listing source document filenames.

- [ ] **Step 1: Write failing test** — `QueryTests.CacheHit_ReturnsStoredAnswer`: seed Redis with a query vector + answer; post a paraphrase with cosine ≥ 0.92; assert response matches cached answer and LLM was NOT called.
- [ ] **Step 2: Run, verify fail.**
- [ ] **Step 3: Implement `SemanticCache`** (`TryGet(vector) -> answer?` via cosine over stored vectors; `Store(vector, answer, ttl)`).
- [ ] **Step 4: Verify cache test passes.**
- [ ] **Step 5: Write failing test** `CacheMiss_RetrievesAndStreams`: empty cache → asserts Qdrant queried (top-5), prompt built, SSE stream emitted, answer cached, audit row written with user + sources.
- [ ] **Step 6: Implement** `Retriever`, `PromptBuilder` (chunks + question → structured prompt), `LlmClient` (relay llama.cpp SSE → client SSE), `AuditWriter`, and `QueryController` wiring the full flow: embed → cache check → retrieve → prompt → stream → store → audit.
- [ ] **Step 7: Run, verify pass.**
- [ ] **Step 8: End-to-end manual check** — register Admin, upload a policy PDF, login as Employee, `curl -N localhost/api/query` → streamed answer + sources.
- [ ] **Step 9: Commit** `git commit -m "feat(query): RAG loop with semantic cache, SSE, audit"`.

---

## Slice 6 — Admin UI (Angular)

**Files:**
- Create: `apps/admin/` (Angular 17 standalone)
- Create: `apps/admin/src/app/auth/` (login, JWT interceptor, auth guard)
- Create: `apps/admin/src/app/documents/` (upload component, list+status)
- Create: `apps/admin/Dockerfile`, modify Nginx to serve `/admin`
- Test: `apps/admin/src/app/documents/upload.component.spec.ts`

**Interfaces:**
- Consumes: `/api/auth/login`, `/api/documents` (GET/POST).

- [ ] **Step 1: Write failing spec** — upload component calls `DocumentsService.upload(file)` on submit and shows the returned `documentId`.
- [ ] **Step 2: Run, verify fail** (`ng test --watch=false`).
- [ ] **Step 3: Implement** login page, JWT interceptor + auth guard (redirect if not Admin), upload component (multipart), document list polling `status`.
- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Build & serve** via Nginx; manually upload a PDF and watch status reach `indexed`.
- [ ] **Step 6: Commit** `git commit -m "feat(admin-ui): login, upload, document management"`.

---

## Slice 7 — Employee UI (Angular)

**Files:**
- Create: `apps/employee/` (Angular 17 standalone)
- Create: `apps/employee/src/app/auth/`
- Create: `apps/employee/src/app/chat/` (chat component, SSE client, source-attribution chips)
- Create: `apps/employee/Dockerfile`, modify Nginx to serve `/`
- Test: `apps/employee/src/app/chat/chat.component.spec.ts`

**Interfaces:**
- Consumes: `/api/auth/login`, `/api/query` (SSE).

- [ ] **Step 1: Write failing spec** — chat component appends streamed tokens to the active message and renders a source chip per returned source.
- [ ] **Step 2: Run, verify fail.**
- [ ] **Step 3: Implement** login, auth guard (Employee role), chat UI consuming SSE via `EventSource`/fetch-stream, rendering tokens incrementally and source attribution on the `sources` event.
- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Manual check** — login as Employee, ask a question, watch the answer stream word-by-word with sources.
- [ ] **Step 6: Commit** `git commit -m "feat(employee-ui): streaming chat with source attribution"`.

---

## Slice 8 — Hardening

**Files:**
- Modify: `services/api/Ingestion/IngestionService.cs` (cache invalidation on re-ingest)
- Create: `services/api/Query/AuditController.cs` (`GET /api/audit`, Admin)
- Create: `apps/admin/src/app/audit/` (audit log viewer)
- Modify: `docker-compose.yml` / MinIO config (server-side encryption)
- Test: `services/api.Tests/{CacheInvalidationTests,RoleEnforcementTests}.cs`

- [ ] **Step 1: Write failing test** `RoleEnforcementTests`: Employee → `/api/documents`, `/api/audit` each return 403; Admin → 200.
- [ ] **Step 2: Run, verify fail.**
- [ ] **Step 3: Implement/confirm** role guards on all admin endpoints; `AuditController`.
- [ ] **Step 4: Write failing test** `CacheInvalidationTests`: re-ingesting a document evicts Redis entries whose retrieved sources include that document.
- [ ] **Step 5: Implement** cache invalidation keyed by source document id.
- [ ] **Step 6:** Enable MinIO server-side encryption; document the volume/encryption setup in README.
- [ ] **Step 7: Run all tests, verify pass.**
- [ ] **Step 8: Commit** `git commit -m "feat(hardening): role tests, audit UI, cache invalidation, encryption"`.

---

## Self-Review Notes

- **Spec coverage:** ingestion (Slice 4), query+cache+SSE (Slice 5), auth/roles (Slices 3, 8), audit (Slices 5, 8), both UIs (6, 7), network isolation + volumes (Slice 0), local LLM (Slice 2), embedding (Slice 1), encryption-at-rest (Slice 8). All spec sections map to a task.
- **Performance targets** are validated implicitly by the streaming/cache design; a load/latency benchmark is intentionally deferred (YAGNI for v1 functional build) — add a dedicated benchmarking task only if required.
- **Out-of-scope** items (GPU/vLLM, larger models, distributed Qdrant, department permissions, external integrations) are excluded per spec §8.
