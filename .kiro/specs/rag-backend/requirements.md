# Requirements Document

## Introduction

This document specifies the backend requirements for an on-premises RAG (Retrieval-Augmented Generation) document intelligence platform. Administrators upload internal documents (PDF, DOCX); employees query those documents in natural language through a chat interface. All computation runs locally on a CPU-only Linux server using ten containerised services. No document content, query, or generated answer ever leaves the company network.

The Angular 18 frontend (admin and employee apps) is already built and wired to a mock API. The backend must satisfy the existing API contract exactly so the frontend requires zero modification.

## Glossary

- **API**: The .NET Core 8 Web API service that serves all HTTP endpoints and orchestrates auth, ingestion, and the RAG query loop.
- **Admin**: A user with the `Admin` role who may upload and manage documents and view audit logs. Cannot access the Employee query endpoint.
- **Employee**: A user with the `Employee` role who may submit natural-language queries. Cannot access document management or audit endpoints.
- **Embedding_Service**: A Python FastAPI sidecar that wraps the `nomic-embed-text` model and exposes HTTP endpoints for vectorising text (768-dimensional vectors).
- **LLM_Service**: A llama.cpp server running Llama 3.2 3B Q4_K_M CPU-only, exposing an OpenAI-compatible chat completions endpoint with SSE streaming.
- **Qdrant**: The vector database service that stores and retrieves document chunk embeddings by cosine similarity.
- **Redis**: The in-memory store used as a semantic cache for query-answer pairs.
- **PostgreSQL**: The relational database storing users, documents, chunks, and audit logs.
- **MinIO**: The S3-compatible object store that holds raw uploaded files (PDF, DOCX).
- **Nginx**: The sole ingress reverse proxy; no service other than Nginx exposes a port to the host network.
- **Chunk**: A segment of at most 500 tokens extracted from a document during ingestion, stored in PostgreSQL and indexed in Qdrant alongside its embedding vector.
- **JWT**: A JSON Web Token signed with the platform's `JWT_SECRET`, issued on login, required on every API request.
- **Semantic_Cache**: The Redis-backed mechanism that stores query vectors alongside their answers and returns a cached answer when an incoming query vector has cosine similarity ≥ 0.92 with a stored vector.
- **SSE**: Server-Sent Events — the streaming HTTP protocol used to relay LLM tokens to the Angular chat UI.
- **RAG_Loop**: The query pipeline: embed → cache check → Qdrant retrieval → prompt build → LLM stream → cache store → audit write.
- **Audit_Log**: A PostgreSQL row recording a completed query: user email, query text, retrieved source filenames, and timestamp.
- **ragnet**: The Docker internal bridge network on which all ten services communicate. No service on `ragnet` has outbound internet access at runtime.

---

## Requirements

### Requirement 1: Infrastructure Skeleton

**User Story:** As a platform operator, I want all backend services to start from a single `docker-compose up -d` command on a Linux server, so that deployment requires no manual configuration beyond copying an `.env` file.

#### Acceptance Criteria

1. THE Platform SHALL define all listed services (`postgres`, `redis`, `qdrant`, `minio`, `nginx`, `embedding`, `llm`, `api`) in a single `docker-compose.yml` with a shared `ragnet` internal Docker network.
2. THE Platform SHALL declare named Docker volumes (`pg_data`, `qdrant_data`, `minio_data`, `model_cache`) so that persisted data survives container restarts and `docker compose down` without `-v`.
3. WHEN `docker compose up -d` completes, THE Platform SHALL report all stateful services (`postgres`, `redis`, `qdrant`, `minio`) as `healthy` within 120 seconds via their respective healthcheck commands (`pg_isready`, `redis-cli ping`, `GET /healthz`, `GET /minio/health/live`).
4. WHEN `docker compose up -d` is run, the `api`, `embedding`, and `llm` services SHALL declare `depends_on` with `condition: service_healthy` on all stateful services they require, so that application containers do not start until their dependencies are healthy.
5. THE Platform SHALL publish only port 80 (Nginx) to the host; no other service SHALL publish a port to the host network.
6. THE Platform SHALL provide an `.env.example` file containing all required environment variable keys (`POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `JWT_SECRET`, `JWT_EXPIRY_MINUTES`) with non-secret placeholder values so the file can be used as-is for local setup.
7. WHEN `GET /healthz` is issued to Nginx on port 80, THE Nginx SHALL return HTTP 200 with body `ok` regardless of the health status of upstream services.

---

### Requirement 2: Embedding Service

**User Story:** As the API, I want a dedicated embedding sidecar that converts text to 768-dimensional vectors using `nomic-embed-text`, so that both ingestion and query paths produce consistent, comparable embeddings without coupling model loading to the .NET process.

#### Acceptance Criteria

1. THE Embedding_Service SHALL expose `POST /embed` accepting `{ "texts": [string] }` and returning `{ "vectors": [[number]], "dim": number }` where `dim` equals 768 and each inner array has exactly 768 elements.
2. THE Embedding_Service SHALL expose `POST /embed/query` accepting `{ "text": string }` and returning `{ "vector": [number] }` with exactly 768 elements.
3. THE Embedding_Service SHALL expose `POST /process` accepting `{ "file_b64": string, "filename": string }` and returning `{ "chunks": [{ "ordinal": number, "text": string, "vector": [number] }] }` where each chunk contains at most 500 tokens (measured by the same tokenizer used for chunking, as specified in criterion 12) and each vector has exactly 768 elements.
4. WHEN a `/process` request contains a PDF or DOCX file, THE Embedding_Service SHALL split the document into chunks where every chunk contains at most 500 tokens and chunk ordinals are sequential integers starting at 0. *(Property: chunk size invariant)*
5. FOR ALL documents processed by `/process`, the concatenation of all chunk texts SHALL equal the full extracted text of the document (no tokens dropped or duplicated), measured using the tokenizer specified in criterion 12. *(Property: text conservation across chunks)*
6. WHEN a `/process` request contains a file whose base64 decodes to a corrupt or unparseable document body, THE Embedding_Service SHALL return HTTP 422 with an error message indicating that the file content could not be parsed.
7. THE Embedding_Service SHALL expose `GET /health` returning HTTP 200 only after the `nomic-embed-text` model is loaded and able to process requests; the endpoint SHALL return HTTP 503 if the model is still loading.
8. IF a `/process` request contains a file with an extension other than `.pdf` or `.docx` (case-insensitive), THEN THE Embedding_Service SHALL return HTTP 422 with an error message indicating the unsupported file type and listing the supported types.
9. IF a `POST /embed` request contains an empty `texts` array or a `POST /embed/query` request contains an empty or whitespace-only `text` string, THEN THE Embedding_Service SHALL return HTTP 422 with an error message indicating that the input must contain at least one non-empty text.
10. THE Embedding_Service SHALL use the `nomic-ai/nomic-embed-text-v1` model (or its sentence-transformers equivalent) as the tokenizer and encoder for all `/embed`, `/embed/query`, and `/process` operations, so that token counts and vector dimensions are consistent across all endpoints.

---

### Requirement 3: LLM Service

**User Story:** As the API, I want a local LLM that streams token completions over SSE using an OpenAI-compatible interface, so that the RAG query loop can relay tokens to the Angular chat UI without calling any external service.

#### Acceptance Criteria

1. THE LLM_Service SHALL run Llama 3.2 3B Q4_K_M via `llama.cpp` on CPU only, with no GPU-related flags or libraries required at runtime.
2. THE LLM_Service SHALL expose `POST /v1/chat/completions` on port 8081 within the `ragnet` network, accepting `{ "messages": [{"role": string, "content": string}], "stream": true }` and compatible with the OpenAI chat completions API streaming format.
3. WHEN a streaming chat completion request is received, THE LLM_Service SHALL respond with `Content-Type: text/event-stream` and emit `data:` lines where each line (except the final) contains a JSON object with a `choices[0].delta.content` string field, and the final line is `data: [DONE]`.
4. THE LLM_Service SHALL expose `GET /health` returning HTTP 200 only after the model is fully loaded and able to process inference requests; the endpoint SHALL return HTTP 503 if the model is still loading.
5. WHEN the `model_cache` volume does not contain the model file on startup and `MODEL_URL` is set, THE LLM_Service SHALL download the model file to the `model_cache` volume before starting the inference server, and SHALL reuse the cached file on subsequent starts without re-downloading.
6. IF the model file is not present in `model_cache` and `MODEL_URL` is not set or is empty, THEN THE LLM_Service SHALL exit with exit code 1 and write a message to stderr indicating that a model file path or download URL must be provided.
7. IF the model file download from `MODEL_URL` fails (e.g., connection refused, HTTP 4xx/5xx, checksum mismatch), THEN THE LLM_Service SHALL exit with exit code 1 and write a message to stderr identifying the URL and the failure reason.

---

### Requirement 4: Authentication and Authorisation

**User Story:** As a platform operator, I want all API endpoints protected by JWT-based authentication with two roles (Admin and Employee), so that document management is restricted to admins and query access is restricted to employees.

#### Acceptance Criteria

1. WHEN a `POST /api/auth/login` request is received with a valid email (≤ 254 characters) and a password between 8 and 72 characters that bcrypt-matches the stored hash, THE API SHALL return HTTP 200 with `{ "token": string, "user": { "email": string, "role": "Admin"|"Employee" } }`.
2. WHEN a login request is received with a valid email and bcrypt-matching password, THE API SHALL return a JWT signed with `JWT_SECRET` using HS256 and containing the claims `email` (string), `role` ("Admin" or "Employee"), `iat` (Unix epoch seconds), and `exp` (Unix epoch seconds).
3. IF a login request contains an email not registered in PostgreSQL or a password that does not match the stored bcrypt hash, THEN THE API SHALL return HTTP 401 with a response body that does not indicate which of the two fields was incorrect.
4. WHEN a valid login request is processed, THE API SHALL return a JWT where the `exp` claim equals `iat + (JWT_EXPIRY_MINUTES × 60)`, where `JWT_EXPIRY_MINUTES` is between 1 and 1440 inclusive. *(Property: token expiry invariant over all valid logins)*
5. WHEN an HTTP request arrives at any endpoint other than `POST /api/auth/login` without an `Authorization: Bearer <token>` header, THE API SHALL return HTTP 401.
6. WHEN an HTTP request arrives at any endpoint other than `POST /api/auth/login` with a JWT that is expired, has an invalid signature, is malformed (not three dot-separated base64url segments), uses an unexpected algorithm (anything other than HS256), or is missing required claims (`email`, `role`), THEN THE API SHALL return HTTP 401.
7. WHEN a request is made to `POST /api/documents`, `GET /api/documents`, or `GET /api/audit` using a valid JWT with role `Employee`, THE API SHALL return HTTP 403. *(Property: role enforcement invariant for Admin-only endpoints)*
8. WHEN a request is made to `POST /api/query` using a valid JWT with role `Admin`, THE API SHALL return HTTP 403. *(Property: role enforcement invariant for Employee-only endpoints)*
9. THE API SHALL store user passwords as bcrypt hashes with a cost factor of at least 10; plaintext passwords SHALL NOT be stored in PostgreSQL, and password values SHALL NOT appear in any application log.
10. WHEN the Admin seed mechanism is invoked (via environment variable `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` at startup), THE API SHALL create an Admin user with the provided credentials if and only if no user with that email already exists; if the user already exists, THE API SHALL leave the existing record unchanged and continue startup normally.

---

### Requirement 5: Document Ingestion Pipeline

**User Story:** As an Admin, I want to upload PDF and DOCX files through the API so that the documents are stored, chunked, embedded, and indexed for employee queries.

#### Acceptance Criteria

1. THE API SHALL expose `POST /api/documents` as a multipart/form-data endpoint (Admin only) accepting a single file field named `file` and returning HTTP 201 with `{ "documentId": string }` where `documentId` is a UUID v4.
2. WHEN a document upload is accepted, THE API SHALL store the raw file in MinIO under the `documents` bucket with the `documentId` as the object key before returning the 201 response.
3. WHEN a document is stored in MinIO, THE API SHALL write a PostgreSQL row with fields `(id, fileName, uploaderId, status="queued", createdAt, chunkCount=0)`.
4. WHILE a document has status `queued`, THE API SHALL transition it to `processing`, then call `POST /process` on the Embedding_Service, then for each returned chunk write a Chunk row to PostgreSQL with `(id, documentId, ordinal, text, qdrantPointId)` and upsert the chunk vector into Qdrant under the `documents` collection; if all chunk rows and Qdrant upserts succeed, THE API SHALL set the document status to `indexed`. If any step after the `processing` transition fails, THE API SHALL roll back any partially written chunk rows and set the status to `failed`.
5. IF the Embedding_Service is unreachable or returns a non-2xx response within 30 seconds, or IF Qdrant is unreachable or returns a non-2xx response during vector upsert, THEN THE API SHALL remove any partially written Chunk rows for that document, set the document status to `failed`, and log the error with the documentId; the document record SHALL remain in PostgreSQL.
6. THE API SHALL expose `GET /api/documents` (Admin only) returning an array of `DocumentRecord` objects: `[{ "id": string, "fileName": string, "uploader": string, "status": "queued"|"processing"|"indexed"|"failed", "createdAt": string (ISO 8601 UTC), "chunkCount": number }]`.
7. FOR ALL documents with status `indexed`, the `chunkCount` field returned by `GET /api/documents` SHALL equal the count of Chunk rows in PostgreSQL with that `documentId`. *(Property: chunk count consistency invariant)*
8. FOR ALL Chunk rows in PostgreSQL, the `text` field SHALL equal the `text` value of the corresponding chunk in the Qdrant point payload for that `qdrantPointId`. *(Property: text consistency between PostgreSQL and Qdrant)*
9. WHEN a document is uploaded with the same filename as an existing document, THE API SHALL create a new document record with a new UUID and SHALL NOT modify or delete the previous record.
10. IF a request to `POST /api/documents` or `GET /api/documents` is made with a valid JWT with role `Employee`, THEN THE API SHALL return HTTP 403.
11. IF an uploaded file has an extension other than `.pdf` or `.docx` (case-insensitive), THEN THE API SHALL return HTTP 422 with a message indicating the unsupported type, without storing the file or creating a database record.
12. IF an uploaded file exceeds 50 MB in size, THEN THE API SHALL return HTTP 413 with a message indicating the size limit, without storing the file or creating a database record.

---

### Requirement 6: Query Pipeline — RAG Loop

**User Story:** As an Employee, I want to submit a natural-language question and receive a streamed answer grounded in indexed company documents, so that I get accurate, source-attributed responses without waiting for the full answer to generate.

#### Acceptance Criteria

1. THE API SHALL expose `POST /api/query` (Employee only) accepting `{ "question": string }` and responding with `Content-Type: text/event-stream`.
2. WHEN a query is received, THE API SHALL call `POST /embed/query` on the Embedding_Service to produce a 768-dimensional query vector before any cache or retrieval step.
3. WHILE processing a query, THE API SHALL check the Semantic_Cache in Redis before querying Qdrant; IF the cosine similarity between the incoming query vector and any cached query vector is ≥ 0.92, THEN THE API SHALL return the cached answer as a valid SSE stream without calling Qdrant or the LLM_Service.
4. WHEN a cache miss occurs, THE API SHALL query Qdrant for the top-5 chunks by cosine similarity to the query vector and use those chunks to build the prompt.
5. WHEN a cache miss occurs and the top-5 chunks are retrieved, THE API SHALL build a structured prompt containing the chunk texts and the question, and forward it to the LLM_Service `POST /v1/chat/completions` with `"stream": true`.
6. WHILE streaming an LLM response, THE API SHALL relay each token to the client as an SSE `data` event in the format `data: {"token":"<value>"}` within 100 ms of receiving it from the LLM_Service.
7. FOR ALL completed queries (cache hit or miss), THE SSE stream SHALL emit events in this exact order: first a single `event: sources\ndata: [<filenames>]` event, then one or more `data: {"token":"..."}` events, then a final `data: [DONE]` event. *(Property: SSE stream structure invariant)*
8. FOR ALL cache-miss queries, the filenames in the `sources` SSE event SHALL equal the distinct `fileName` values of the PostgreSQL Document rows whose chunks were among the top-5 retrieved Qdrant points. *(Property: source attribution consistency)*
9. WHEN a cache-hit query completes, THE sources event SHALL contain the `retrievedSources` array that was stored alongside the cached answer in Redis at cache-store time.
10. WHEN a query completes (cache hit or miss), THE API SHALL store the query vector, the full assembled answer text, and the `retrievedSources` array in Redis with a TTL of `CACHE_TTL_DAYS` × 86400 seconds (default 604800 seconds / 7 days).
11. WHEN a query completes (cache hit or miss), THE API SHALL write an Audit_Log row to PostgreSQL containing `(id, userEmail, query, retrievedSources, createdAt)` where `userEmail` is the `email` claim from the JWT, `query` is the original question string, and `retrievedSources` is the same filenames array emitted in the `sources` SSE event.
12. FOR ALL completed queries, the Audit_Log row written to PostgreSQL SHALL contain a non-null `userEmail` matching the JWT `email` claim, a non-empty `query` string, and a non-empty `retrievedSources` array. *(Property: audit log completeness invariant)*
13. IF a request to `POST /api/query` is made with a valid JWT with role `Admin`, THEN THE API SHALL return HTTP 403.
14. IF the `question` field is absent, null, or after trimming whitespace is an empty string, THEN THE API SHALL return HTTP 400 with a message indicating that `question` is required and must be non-empty.
15. IF the `question` field exceeds 2000 characters, THEN THE API SHALL return HTTP 400 with a message indicating the maximum allowed length.
16. IF the Embedding_Service is unreachable or returns a non-2xx response when called during query processing, THEN THE API SHALL return HTTP 502 with a message indicating that the embedding service is unavailable.
17. IF the LLM_Service is unreachable or returns a non-2xx response when called during query processing, THEN THE API SHALL return HTTP 502 with a message indicating that the language model service is unavailable.

---

### Requirement 7: Semantic Cache Invalidation

**User Story:** As a platform operator, I want the semantic cache to be invalidated when a document is re-ingested, so that employees always receive answers grounded in the latest version of the document corpus.

#### Acceptance Criteria

1. WHEN a document with `documentId` D transitions to status `indexed`, THE API SHALL scan all Redis cache entries and evict every entry whose stored `retrievedSources` array contains the `fileName` associated with document D, before the status is written to PostgreSQL as `indexed`.
2. FOR ALL documents that complete ingestion, after invalidation completes THE Semantic_Cache SHALL contain zero entries whose `retrievedSources` array includes that document's `fileName`. *(Property: cache eviction completeness — applies when Redis is reachable)*
3. WHEN cache invalidation runs for document D, THE API SHALL NOT evict Redis entries whose `retrievedSources` arrays do not contain the `fileName` of document D. *(Property: cache eviction precision — unrelated entries preserved)*
4. IF Redis is unreachable during cache invalidation, THEN THE API SHALL log the error with the documentId and fileName, continue marking the document as `indexed` in PostgreSQL, and retry the invalidation scan on the next ingestion of any document.

---

### Requirement 8: Audit Log API

**User Story:** As an Admin, I want to retrieve all audit log entries through the API so that I can review employee query activity, including which documents were used to answer each question.

#### Acceptance Criteria

1. THE API SHALL expose `GET /api/audit` (Admin only) returning an array of AuditLog objects: `[{ "id": string, "userEmail": string, "query": string, "retrievedSources": [string], "createdAt": string (ISO 8601 UTC) }]` ordered by `createdAt` descending (most recent first).
2. THE API SHALL return all audit log entries stored in PostgreSQL without server-side pagination or filtering for v1.
3. IF a request to `GET /api/audit` is made with a valid JWT with role `Employee`, THEN THE API SHALL return HTTP 403.
4. WHEN `GET /api/audit` is called by a valid Admin token and no queries have been made, THE API SHALL return HTTP 200 with an empty JSON array `[]`.
5. IF PostgreSQL is unreachable when `GET /api/audit` is called, THEN THE API SHALL return HTTP 503 with a message indicating that the audit log is temporarily unavailable.

---

### Requirement 9: Data Persistence and Storage

**User Story:** As a platform operator, I want all platform data (documents, users, vectors, cached answers, raw files) to persist across container restarts so that a service restart does not result in data loss.

#### Acceptance Criteria

1. THE Platform SHALL store all PostgreSQL data on the named Docker volume `pg_data` so that the data survives `docker compose restart`.
2. THE Platform SHALL store all Qdrant vector data on the named Docker volume `qdrant_data` so that indexed embeddings survive `docker compose restart`.
3. THE Platform SHALL store all MinIO object data on the named Docker volume `minio_data` so that uploaded raw files survive `docker compose restart`.
4. THE Platform SHALL store all LLM model files on the named Docker volume `model_cache` so that the model is not re-downloaded on `docker compose restart`.
5. THE Platform SHALL configure Redis with `appendonly yes` persistence so that the semantic cache survives `docker compose restart` and is stored on the named Docker volume `redis_data`.
6. WHEN `docker compose restart` is issued, after all services report healthy, THE API SHALL serve `GET /api/documents` returning documents that were `indexed` before the restart still showing `indexed` status.

---

### Requirement 10: Network Isolation and Security

**User Story:** As a platform operator, I want all inter-service communication to remain on an internal Docker network with no outbound internet access at runtime, so that no company data can leave the network through the platform.

#### Acceptance Criteria

1. THE Platform SHALL configure all services on the `ragnet` Docker bridge network with `internal: true` so that no container can initiate outbound TCP/UDP connections to the internet at runtime.
2. THE Nginx SHALL be the sole service with a port binding to the host (port 80); all other services SHALL communicate only over `ragnet` using their service name as hostname.
3. THE API SHALL accept HTTP traffic only through Nginx; the API container port (8080) SHALL NOT be bound to any host interface.
4. THE Platform SHALL route all HTTP requests with a path prefix of `/api/` through Nginx to the API service at `api:8080`; all other paths SHALL be served as static Angular app assets from the Nginx container.
5. WHEN a JWT arrives at the API with a signature that does not match `JWT_SECRET`, THE API SHALL return HTTP 401, write a structured log entry including the source IP address and the reason for rejection, and SHALL NOT process the request further.
6. WHEN the same source IP address sends more than 10 requests with invalid JWTs within a fixed 60-second window, THE API SHALL return HTTP 429 for all subsequent requests from that IP within the same window, resetting the counter at the start of each new 60-second window.
7. THE Platform SHALL use `restart: unless-stopped` on all services so that individual container crashes trigger automatic restart without operator intervention.

---

### Requirement 11: Performance Targets

**User Story:** As an Employee, I want query responses to begin streaming within 2 seconds for fresh queries and complete within 10 seconds, and return near-instantly for repeated questions, so that the chat interface feels responsive.

#### Acceptance Criteria

1. WHEN a query results in a Semantic_Cache hit, THE API SHALL begin emitting the first SSE token to the client within 200 ms of receiving the request.
2. WHEN a query results in a cache miss, THE API SHALL begin emitting the first SSE token to the client within 3 seconds of receiving the request on the reference hardware (Linux server, no GPU, 8+ CPU cores, 16 GB RAM).
3. WHEN a cache-miss query completes, THE API SHALL have delivered a full answer of at least 50 tokens within 15 seconds of receiving the request on the reference hardware.
4. THE Embedding_Service SHALL return a response to `POST /embed/query` within 200 ms for a single query text on the reference hardware.
5. WHEN a Qdrant cosine search is performed against a collection of up to 100,000 chunk vectors, THE Qdrant SHALL return the top-5 results within 100 ms.

---

### Requirement 12: API Contract Compatibility

**User Story:** As the Angular frontend team, I want the backend to match the existing mock API contract exactly so that neither the admin nor the employee Angular app requires any code changes to connect to the real backend.

#### Acceptance Criteria

1. THE API SHALL implement `POST /api/auth/login` returning exactly `{ "token": string, "user": { "email": string, "role": "Admin"|"Employee" } }` with HTTP 200 on success, matching the mock server contract.
2. THE API SHALL implement `GET /api/documents` returning an array where each element contains at minimum the fields `id` (string), `fileName` (string), `uploader` (string), `status` ("queued"|"processing"|"indexed"|"failed"), `createdAt` (ISO 8601 string), and `chunkCount` (number), matching the `DocumentRecord` TypeScript interface.
3. THE API SHALL implement `POST /api/documents` as a `multipart/form-data` request with a field named `file`, returning `{ "documentId": string }` with HTTP 201 on success.
4. THE API SHALL implement `GET /api/audit` returning an array where each element contains at minimum the fields `id` (string), `userEmail` (string), `query` (string), `retrievedSources` (string[]), and `createdAt` (ISO 8601 UTC string), matching the `AuditLog` TypeScript interface.
5. THE API SHALL implement `POST /api/query` returning `Content-Type: text/event-stream` with the event sequence: first `event: sources\ndata: <JSON array of filenames>`, then one or more `data: {"token":"<string>"}` events, then `data: [DONE]`, matching the SSE parsing logic in `query.service.ts`.
6. FOR ALL SSE token events emitted by the API, the JSON payload SHALL contain a `token` string field (not `content`) so that the Angular `handleEvent` function routes it through the token handler path without modification.
