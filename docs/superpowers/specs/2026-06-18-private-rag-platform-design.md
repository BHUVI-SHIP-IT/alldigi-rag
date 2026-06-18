# On-Prem RAG Document Intelligence Platform — Design Spec

**Date:** 2026-06-18
**Status:** Approved (derived from architecture brief)

> Product name intentionally omitted. Referred to here as "the platform".

## 1. Purpose

A self-hosted, CPU-only, AI-powered document intelligence platform. Administrators ingest internal documents (policy manuals, HR handbooks, compliance docs); employees query them in natural language through a chat interface. **No employee query, document content, or generated answer ever leaves the company network.**

Core constraint: the company owns its data at rest, in transit, and during inference. No third-party LLM provider.

## 2. Success Criteria

- Admin uploads PDF/DOCX/URL → document is chunked, embedded, indexed, and marked queryable.
- Employee asks a natural-language question → receives a streamed answer with source attribution in 6–10 s (fresh) or ~100 ms (cached).
- Role separation enforced: Admin (manage docs) vs Employee (query only).
- Every query logged: user, timestamp, retrieved sources.
- Entire stack starts with `docker-compose up -d` on a Linux server, no GPU, no outbound internet at runtime.

## 3. Architecture — Component Stack

| # | Layer | Technology | Purpose |
|---|-------|-----------|---------|
| 1 | Reverse Proxy | Nginx | Serves Angular apps, routes API traffic |
| 2 | Admin UI | Angular 17+ | Document upload & management |
| 3 | Employee UI | Angular 17+ | Chat interface, real-time streaming |
| 4 | Middleware | .NET Core 8 Web API | Auth, RAG orchestration, SSE streaming |
| 5 | Embedding | Python FastAPI + nomic-embed | Text → vectors |
| 6 | LLM Engine | llama.cpp + Llama 3.2 3B Q4 | Answer generation, CPU-only |
| 7 | Vector DB | Qdrant | Stores & searches embeddings |
| 8 | Cache | Redis | Semantic cache for repeated queries |
| 9 | Relational DB | PostgreSQL | Documents, users, chunks, audit logs |
| 10 | File Store | MinIO | Raw PDF/DOCX files, S3-compatible |

## 4. Data Flows

### 4.1 Document Ingestion
1. **Upload** — raw file received by .NET API, stored in MinIO.
2. **Register** — metadata (filename, uploader, timestamp) written to PostgreSQL.
3. **Process** — Python sidecar parses file, splits into 500-token chunks, generates embeddings (nomic-embed-text).
4. **Index** — vectors + chunk text stored in Qdrant; chunk references written to PostgreSQL linking each chunk to its source document.
5. **Ready** — document status set to `indexed`; queryable.

### 4.2 Employee Query
1. **Query received** — Angular chat UI → .NET API over HTTPS.
2. **Cache check** — Python sidecar embeds query; Redis checked for semantically similar cached answer (cosine ≥ 0.92). Hit → return in ~100 ms.
3. **Retrieval** — on miss, Qdrant cosine search returns top-5 chunks.
4. **Prompt build** — .NET API builds structured prompt (chunks + question) → llama.cpp.
5. **Stream answer** — Llama 3.2 3B streams tokens back to Angular via SSE.
6. **Cache store** — answer + query vector stored in Redis.

## 5. Security & Privacy

- **Network isolation:** all 10 services on Docker internal network; Nginx sole ingress; no outbound internet at runtime; no external LLM API keys.
- **Auth:** ASP.NET Identity, bcrypt-hashed passwords in PostgreSQL. JWT on login with configurable expiry; all endpoints require valid token.
- **Roles:** Admin (doc management), Employee (query only), enforced at middleware. Employees cannot reach admin panel, document list, raw files, or audit logs.
- **Data at rest:** MinIO files on company disk (server-side encryption supported); PostgreSQL + Qdrant on persistent Docker volumes; data survives restarts.
- **LLM privacy:** Llama 3.2 3B runs locally via llama.cpp as a stateless inference engine — retains nothing.

## 6. Performance Targets

| Step | Latency |
|------|---------|
| Query embedding | 30–80 ms |
| Redis cache check | 2–5 ms |
| Qdrant search | 10–30 ms |
| LLM first token | 1–2 s |
| Full answer (~150 tokens) | 6–10 s streaming |
| Cache hit (end-to-end) | ~100 ms |

- Semantic cache: cosine ≥ 0.92, TTL configurable (default 7 days), invalidated on document re-ingest/update.

## 7. Decomposition into Buildable Slices

The platform is too large for one plan. It decomposes into ordered, independently buildable slices:

- **Slice 0 — Infrastructure skeleton:** docker-compose with Postgres, Redis, Qdrant, MinIO, Nginx; healthchecks; volumes; internal network. No app logic.
- **Slice 1 — Embedding service:** Python FastAPI wrapping nomic-embed. `/embed` endpoint. Standalone, testable.
- **Slice 2 — LLM service:** llama.cpp server with Llama 3.2 3B Q4, OpenAI-compatible streaming endpoint.
- **Slice 3 — Middleware core + Auth:** .NET 8 Web API, ASP.NET Identity, JWT, roles, Postgres schema (users, documents, chunks, audit_logs).
- **Slice 4 — Ingestion pipeline:** upload → MinIO → register → chunk → embed (Slice 1) → index in Qdrant → status. Admin-only.
- **Slice 5 — Query pipeline:** cache check (Redis) → Qdrant retrieval → prompt build → LLM stream (Slice 2) → SSE to client → cache store → audit log.
- **Slice 6 — Admin UI (Angular):** login, upload, document list/status.
- **Slice 7 — Employee UI (Angular):** login, chat with streamed answers + source attribution.
- **Slice 8 — Hardening:** cache invalidation, full audit trail UI, encryption-at-rest config, role enforcement tests.

**Recommended first build:** a thin vertical slice spanning 0→5 (one PDF, one question, streamed answer) before polishing UIs, to validate the RAG loop end-to-end early.

## 8. Out of Scope (v1, per future-upgrade path)

GPU/vLLM acceleration; larger models (8B/70B); distributed Qdrant cluster; department-level permissions; external HR/IT/ticketing integrations.
