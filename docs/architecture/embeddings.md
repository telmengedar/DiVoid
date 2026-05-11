# Architectural Document: Automatic Content Embeddings on Node Create/Update

Resolves DiVoid task **#180** ("Generate vector embeddings on node content create/update — Postgres-only"). This document lands the open architectural questions; the implementing agent (John) builds from here.

---

## 1. Problem Statement

`POST /api/nodes/{id}/content` is the single entry point through which a node's blob and `ContentType` reach the database. Today the call writes only `Content` and `ContentType`; `Embedding` remains untouched. We need to populate `Embedding` automatically — on both create and update of content — for nodes whose `ContentType` denotes textual data, using the Postgres-side `embedding(model, text)` custom function already in use across Toni's stack (mamgo-backend). The downstream consumer is a future semantic-search feature; this task does not build search itself.

Success criteria:

- Every successful `POST /api/nodes/{id}/content` with a text content-type on a Postgres deployment leaves `Node.Embedding` populated with a fresh vector reflecting the just-written content.
- Every successful `POST /api/nodes/{id}/content` with a non-text content-type on a Postgres deployment leaves `Node.Embedding` cleared.
- On a SQLite deployment (test fixture), the embedding step is skipped entirely. The content upload still returns 200; `Embedding` remains at its prior value.
- On a Postgres deployment where the `embedding(...)` function is *not* installed, the call fails loudly — a misconfigured production database surfaces immediately rather than silently degrading.
- The design composes with the existing `embed` PATCH op rather than competing with it.
- Test coverage runs on in-memory SQLite without invoking the Postgres function.

## 2. Scope & Non-Scope

**In scope.**

- Embedding generation on the content-write path inside `NodeService.UploadContent`.
- The text/non-text content-type gating predicate.
- The database-capability detection that decides whether to even attempt embedding.
- A one-shot backfill mechanism for the 174 pre-existing content-bearing nodes.
- The relationship to the existing `embed` PATCH op in `DatabasePatchExtensions.cs`.

**Out of scope.**

- Semantic-search query API (the *consumer*) — separate downstream task.
- Re-architecting the patch pipeline beyond clarifying when `embed` should still be used.
- Migrating away from `gemini-embedding-001` to a different model.
- Reintroducing local SQLite as a deployment target (it remains a test-only substrate).

## 3. Assumptions & Constraints

- DiVoid is Postgres-only in production as of 2026-05-11 (per task #180 and migration #179). The `embedding(text, text)` custom function and its underlying Vertex AI bridge are installed in that database the same way they are in mamgo-backend's Postgres instance.
- The model is `gemini-embedding-001` with output dimension **3072**, matching `Node.Embedding`'s existing `[Size(3072)]` declaration and the dimension used across the mamgo-backend production pipeline.
- Tests run on in-memory SQLite (`DatabaseFixture`). The `embedding()` function does not exist there; the architecture must avoid calling it in that environment by construction, not by exception handling.
- The Pooshit.Ocelot primitive is `DB.CustomFunction("embedding", DB.Constant(<model>), DB.Constant(<text>)).Type<float[]>()`, executed as the right-hand side of an `Update<Node>().Set(...)`. The function evaluates inside Postgres; the float[] never crosses the .NET boundary in either direction — only the input text does (via the parameter bind) and the SQL UPDATE writes the column directly.
- Vertex AI roundtrip latency for `gemini-embedding-001` is on the order of 200–800 ms per call for short-to-medium text (empirical from the mamgo embedding-comparison script: ~$0.05–$0.15 for 250 calls = sub-second per call). DiVoid content is typically smaller than mamgo's structured-paragraph prompt outputs, so we expect the lower end of that range.
- `ContentType` is whatever string the caller sent on the `POST` (it is bound from `Request.ContentType`). It may include a charset suffix (e.g. `text/plain; charset=utf-8`).
- Text content is encoded with UTF-8 in practice. There is no separate charset metadata column. We treat the blob bytes as UTF-8 when decoding for the embedding step; mojibake is acceptable in the rare non-UTF-8 case — embeddings are best-effort by design.
- The active database type is known at startup and is stable for the lifetime of the process. It is configured at `Database:Type` in `appsettings.json` and observable through the same surface that `DatabaseExtensions.cs` already branches on.

## 4. Architectural Overview

The embedding step is a **second SQL operation on the same content-write transaction**, gated by two predicates evaluated in order:

1. **Database capability** — is the active database Postgres? (Decided once at startup.)
2. **Text content** — does the just-written `ContentType` qualify as text?

Only when both are true does the second UPDATE run. On Postgres the call propagates failures (the transaction rolls back, the caller sees a 500). On SQLite the step is skipped entirely — no try/catch, no log warning, just no call.

```
POST /api/nodes/{id}/content
        |
        v
+----------------------+
|  NodeController      |
|  UploadContent       |
+----------+-----------+
           |
           v
+--------------------------------+
|  NodeService                   |
|  UploadContent                 |
|                                |
|  1. Begin tx                   |
|  2. UPDATE Content/ContentType |
|  3. if (db is Postgres)        |
|       if (isTextContent)       |
|         UPDATE Embedding = f() |
|       else                     |
|         UPDATE Embedding = null|
|     else (SQLite)              |
|       skip — no embedding step |
|  4. Commit                     |
+--------------------------------+
                |
                v   (Postgres path only)
   +-----------------------------+
   |  database.Update<Node>()    |
   |  .Set(n.Embedding ==        |
   |    DB.CustomFunction(       |
   |      "embedding",           |
   |      DB.Constant(model),    |
   |      DB.Constant(text))     |
   |    .Type<float[]>())        |
   +-----------------------------+
                |
                v
   +-----------------------------+
   |  Postgres: embedding(...)   |
   |    -> Vertex AI Gemini      |
   |    -> float[3072]           |
   +-----------------------------+
```

A separate `EmbeddingBackfillService` (one-shot, console-style invocation) handles the existing 174 nodes (§11 Decision 6).

## 5. Components & Responsibilities

### NodeService.UploadContent (modified)

**Owns.** The content-write transaction. Two gating decisions in order: (1) is this deployment capable of embedding (Postgres?), (2) does this content qualify as text. The orchestration: write content, conditionally write embedding (or clear it on non-text), then commit. No exception handling for embedding failures — they propagate.

**Does not own.** The SQL shape of the embedding call (delegated to a small construct described below). The model name. The mapping from content-type to "is text."

### Embedding capability detection

**Owns.** A single boolean computed at startup: "is the active database Postgres?" — derived from `Database:Type` config (the same source `DatabaseExtensions.cs` already branches on). This boolean is what `NodeService.UploadContent` checks before attempting the embedding step.

**Does not own.** Distinguishing "Postgres without the function installed" from "Postgres with the function installed." That distinction is left to fail loudly at SQL execution time — per Toni's directive, a misconfigured Postgres should crash, not silently no-op.

See §11 Decision 9 for the DI shape (inline branch vs interface seam) — both work; the doc recommends one.

### TextContentTypePredicate (new helper, may be a static method)

**Owns.** The classification function `ContentType → bool`. Single source of truth for what "text" means in this codebase (§11 Decision 3).

### EmbeddingBackfillService (new, one-shot)

**Owns.** Iterating the existing content-bearing nodes whose `Embedding` is null and whose `ContentType` qualifies, then issuing the same `UPDATE Embedding = embedding(...)` per node. Designed to be safely re-runnable. It is Postgres-only by construction — it consults the same capability flag and exits with a clear message if invoked on SQLite.

**Does not own.** Re-uploading content. Modifying any column other than `Embedding`.

### Existing `embed` PATCH op (unchanged)

Stays for the small number of cases where a *caller* wants to embed a string that is not the node's content blob (e.g. embedding a derived summary into some other `[AllowPatch]` `float[]` column should one ever be added). It is not deprecated by this work; it is orthogonal. The new auto-embedding flow does not call it; it shares the underlying SQL primitive but issues its own `Update`.

## 6. Interactions & Data Flow

Sequence for `POST /api/nodes/{id}/content` with `Content-Type: text/markdown` on a **Postgres** deployment:

1. Controller binds `Request.ContentType` and `Request.Body`, calls `nodeService.UploadContent(id, contentType, stream, ct)`.
2. Service reads the stream into a byte array (existing behaviour).
3. Service opens a transaction.
4. Service issues `Update<Node>().Set(Content, ContentType).Where(Id == id).ExecuteAsync(tx)`. If 0 rows, throws `NotFoundException<Node>` — unchanged.
5. Service checks the embedding-capability flag. Postgres → continue. SQLite → jump to step 9.
6. Service evaluates the text-content predicate against `contentType`. If false → step 7b. If true → step 7a.
7a. Service decodes the byte array as UTF-8 into a string and issues `Update<Node>().Set(Embedding == DB.CustomFunction("embedding", DB.Constant(model), DB.Constant(text)).Type<float[]>()).Where(Id == id).ExecuteAsync(tx)`. **No try/catch.** Any failure (Postgres function missing, Vertex AI down, network blip) propagates, the transaction rolls back, the caller gets a 500.
7b. Service issues `Update<Node>().Set(Embedding == null).Where(Id == id).ExecuteAsync(tx)` to clear any stale embedding from a prior text upload. (See §11 Decision 8 for the rationale.)
8. *(no step 8 — error handling lives where it always did, in the global middleware that turns thrown exceptions into HTTP responses)*
9. Service commits the transaction.
10. Controller returns 200.

Sequence for the same request on a **SQLite** deployment (i.e. the test fixture):

1. Controller binds, calls service.
2. Service reads the stream.
3. Service opens a transaction.
4. Service writes `Content` + `ContentType`. If 0 rows, `NotFoundException<Node>`.
5. Service checks capability — SQLite. Skip embedding entirely.
6. Commit.
7. Controller returns 200.

`Embedding` is left at whatever value it had before. No clear, no write, no attempted call to a function that does not exist.

Sequence for **Postgres-without-the-embedding-function** (misconfiguration):

- Step 7a's `ExecuteAsync` throws a Postgres error ("function embedding(text, text) does not exist" or similar). The transaction rolls back. The content write at step 4 is also rolled back. The caller sees a 500. This is intentional — a production database missing its required function is an operational error that must surface, not be papered over.

## 7. Data Model (Conceptual)

No schema changes. `Node.Embedding` is already declared as `float[]` with `[Size(3072)]`, matching the production `gemini-embedding-001` output dimension. The column type at the Postgres level is whatever Pooshit.Ocelot maps `float[]` with `Size(3072)` to — based on the mamgo-backend precedent that uses `DB.Cast(..., CastType.Vector)` for similarity queries, this is or can be a pgvector `vector(3072)` column. Confirming the actual Postgres column type emitted by `SchemaService.CreateOrUpdateSchema<Node>` is John's first investigation step (§14 Step 1); if it is a plain `real[]`, that is sufficient to store the value but will need a follow-up migration to `vector(3072)` before search can use pgvector index types like ivfflat or hnsw. The search-side work owns that migration; we do not block on it here.

No new entities. No new tables. No new columns.

## 8. Contracts & Interfaces (Abstract)

### Embedding capability check

**Purpose.** A single boolean accessible from `NodeService.UploadContent` and `EmbeddingBackfillService`: "is this deployment running on Postgres?"

**Source.** Derived at startup from `Database:Type` config. Same surface `DatabaseExtensions.cs` reads.

**Shape.** Either an injected `bool` (or small value object) registered as a singleton, or a property on a small interface (see §11 Decision 9). Either way: read-only, fixed for the process lifetime, no I/O.

### TextContentTypePredicate

**Purpose.** Single classifier "is this a content-type we should embed?"

**Input.** The raw `ContentType` string from the request (may carry charset suffix).

**Output.** A boolean.

**Definition.** See §11 Decision 3 for the chosen allowlist.

### Embedding write semantics (on the Postgres path)

**Purpose.** Update `Node.Embedding` for a given id to a fresh vector derived from a given text.

**Mechanism.** A single `Update<Node>()` against the ambient transaction, using the Ocelot `DB.CustomFunction(...)` primitive on the RHS of `.Set(...)`. The vector is computed and written entirely server-side; no float[] crosses the .NET boundary.

**Semantics.**
- On success: `Node.Embedding` for the id contains the fresh vector; the change is part of the same transaction as the content write.
- On failure: exception propagates. The transaction rolls back (including the content write at step 4). Caller sees a 500.

**Invariants.**
- The write does not re-read `Node.Content`. The text is decoded from the just-written `byte[]` blob in-memory and passed as a parameter.
- The write joins the ambient transaction; it does not start its own.

## 9. Cross-Cutting Concerns

**Logging.** Embedding successes are silent at info level (they will be high-volume). On the Postgres path, embedding *failures* are not caught locally — they propagate as exceptions and are logged by the global middleware that already handles unhandled exceptions in the request pipeline. On the SQLite path there is nothing to log; the step did not run. A single debug-level log line on startup announces "embedding step enabled" or "embedding step disabled (SQLite)" so operators can confirm the configured behaviour.

**Authentication / authorization.** Unchanged. `UploadContent` is already `[Authorize(Policy = "write")]`. Embedding generation does not introduce a new principal or new permission.

**Transactions and consistency.** Content write and embedding write are part of the same transaction. They commit atomically or roll back atomically. There is no window in which `Content` is committed without the matching `Embedding` (on the Postgres path) — a Vertex AI failure rolls back the content write too.

**Cancellation.** The cancellation token threaded through the controller (`HttpContext.RequestAborted`) reaches both UPDATEs. A cancellation mid-embedding aborts the second UPDATE; the transaction has not yet committed, so the first UPDATE is also rolled back; the caller gets a cancellation. (Today `UploadContent` does not accept a `CancellationToken`. Adding one is a small mechanical change John handles as part of this task; pattern is established elsewhere in the service.)

**Idempotency.** Re-uploading the same content yields the same `Embedding` (Vertex AI is deterministic for a fixed model and input). Re-uploading different content overwrites both blob and vector. No special idempotency key needed.

**Retries.** None. v1 keeps the failure path simple. The backfill service is the deliberate retry mechanism for nodes whose embedding never landed (or whose write failed); it can be re-run.

**Caching.** Out of scope. Vertex AI itself caches at the cost layer; we do not add a layer on top.

**Concurrency.** Two concurrent `POST /content` calls to the same node id serialize through Postgres row-level locking on `UPDATE Node WHERE Id = ?`. Last writer wins for both `Content` and `Embedding` — they are paired writes inside one transaction each, so the pairings cannot tear across requests.

## 10. Quality Attributes & Trade-offs

**Latency.** The change adds one extra synchronous Postgres roundtrip per text-content upload, and that roundtrip transitively waits on Vertex AI. Expected upper bound: ~1 second added to `POST /content`. For DiVoid's usage pattern (interactive note-taking, agent-driven, low-QPS by design) this is acceptable — see §11 Decision 4 for the sync-vs-async decision and its rationale.

**Throughput.** Vertex AI quota is the binding constraint. mamgo-backend operates at higher volume than DiVoid will and has not hit quota limits; we are well within the envelope.

**Availability.** On Postgres, the content-write endpoint's availability is now coupled to Vertex AI's availability for text uploads — if Vertex AI is down, text content writes fail. This is a deliberate choice (see §11 Decision 5) to keep `Content` and `Embedding` strictly consistent and to make a misconfigured production database surface immediately. Non-text uploads are unaffected; they do not touch Vertex AI.

**Maintainability.** Small surface: one capability check, one helper predicate, one one-shot backfill service, and minor edits to one existing service method. No new exception-handling layer, no provider abstraction (the inline branch is sufficient — see §11 Decision 9). Tests stay on SQLite by virtue of the capability check, not by virtue of a mock.

**Trade-off accepted.** Synchronous embedding adds latency to every text-content upload. Rejected alternative: async via background queue. Rationale in §11 Decision 4.

**Trade-off accepted.** A Vertex AI outage causes text-content upload failures (not silent embedding failures). Rejected alternative: catch and swallow. Rationale in §11 Decision 5 — the alternative would also hide a misconfigured production database, and the symmetric handling (loud crash either way) is what Toni explicitly asked for.

**Trade-off accepted.** We tie ourselves to `gemini-embedding-001` and its 3072-dim output. Rejected alternative: a configurable model name. The single hardcoded model matches mamgo's precedent and `Node.Embedding`'s `[Size(3072)]`. Changing it later means re-embedding everything, which would be a deliberate project anyway.

## 11. Decisions (resolving task #180 open questions)

### Decision 1: Embedding model

**Choice:** `gemini-embedding-001` at **3072 dimensions**.

**Rationale:** Confirmed by Toni. Matches the mamgo-backend reference implementation (used across `JobService.cs`, `CampaignItemTargetMapper.cs`, the existing DiVoid `embed` PATCH op, and the mamgo embedding-comparison script) and matches `Node.Embedding`'s `[Size(3072)]` provisioning. `gemini-embedding-001` handles mixed German + English natively, addressing the multilingual concern.

**Trade-off:** 3072-dim vectors are 4× larger than 768-dim alternatives. Storage cost per node grows from ~3 KB to ~12 KB. Acceptable at DiVoid's scale (low-thousands of nodes).

### Decision 2: Input pre-processing

**Choice:** Raw UTF-8-decoded blob text, with **no** metadata prefix, **no** markdown stripping, **no** HTML stripping.

**Rationale:** The simplest variant that does not compromise search quality. Modern embedding models are robust to markdown punctuation; stripping it adds code surface and a new invalidation trigger (e.g. an HTML parser bug becomes our problem). Metadata prefixing (`"<type>: <name>\n\n<content>"`) couples the embedding to fields the user might later rename — a `Name` patch would silently invalidate the embedding without re-triggering it. We deliberately keep the embedding's input dependency narrow: `Content` only. If search quality turns out to need metadata in the vector, that is a measurable change for the search-side work, not a blocking decision now.

**What is fed to the function:** `Encoding.UTF8.GetString(node.Content)`.

**Trade-off:** Markdown noise (`#`, `*`, `[]()`) ends up in the embedding. In practice it adds modest signal-to-noise, not enough to justify the parser. Accepted.

### Decision 3: Text content-type predicate

**Choice:** A small allowlist matched case-insensitively against the part of `ContentType` before any `;` separator. Initial allowlist:

- `text/*` — any subtype (`text/plain`, `text/markdown`, `text/html`, `text/csv`, `text/xml`, …)
- `application/json`
- `application/xml`
- `application/x-yaml`, `application/yaml`
- `application/javascript`, `application/x-sh` (treated as text for source-code embedding)

Everything else returns false — `image/*`, `audio/*`, `video/*`, `application/octet-stream`, `application/pdf` (PDFs need text extraction, not in scope), unknown/missing.

**Rationale:** `text/*` covers the dominant cases. The `application/*` additions are explicit because the prefix rule would otherwise exclude them. PDFs are deliberately excluded — embedding the raw PDF binary bytes as UTF-8 is meaningless; doing it right needs a text-extraction step that does not belong in this task.

**Trade-off:** Some textual content slips through misclassification by sender (e.g. a Markdown file uploaded with `application/octet-stream`). Acceptable: re-upload with the correct header fixes it.

### Decision 4: Sync vs async

**Choice:** **Synchronous**, inside the existing content-write transaction.

**Rationale:** Three reasons. (1) Consistency: the strongest possible guarantee that `Content` and `Embedding` reflect the same moment in time — no inconsistency window during which a search would see stale or missing vectors. This rationale is *strengthened* under the new failure model: because we no longer swallow embedding failures, the transactional atomicity is now the only mechanism that prevents a write where `Content` advanced but `Embedding` did not. (2) Simplicity: no background queue, no `IHostedService`, no retry state machine, no operational surface to monitor. (3) Acceptable latency: at ~1 second p99 added on a write-path endpoint that is not on any user-facing hot path (DiVoid is an agent + power-user tool, not a high-QPS public API). mamgo-backend uses the same synchronous pattern at higher volumes than DiVoid will see.

The case for async would be (a) sub-second latency budget on the write path, or (b) Vertex AI quota throttling causing queue buildup. Neither applies to DiVoid in any foreseeable horizon.

**Trade-off:** Every text-content upload pays the Vertex AI roundtrip and is coupled to Vertex AI's availability for success. If we ever introduce a bulk-upload endpoint or per-upload becomes too slow, we revisit.

### Decision 5: Failure semantics

**Choice:** **Detect at the database-capability boundary, do not try/catch at the call site.**

- On a **SQLite** deployment (test fixture, or any future case where the database lacks the `embedding(...)` function), the embedding step is **skipped entirely** by an `if` check before the call. No call, no exception, no log warning. Content upload succeeds; `Node.Embedding` is left at its prior value (typically null).
- On a **Postgres** deployment, the embedding UPDATE is issued directly with **no try/catch**. Any failure — function missing, Vertex AI timeout, network blip — propagates, the transaction rolls back, the content write also rolls back, the caller sees a 500.

**Rationale:** Toni's directive (paraphrased): *"detect these cases if it is reasonable; an insufficient Postgres setup may crash and be loud about the missing function."* The decision splits cleanly into two cases:

1. **Known-incapable environments (SQLite).** Detection is cheap and reliable — the database type is fixed at startup. Skipping is the architecturally cleaner choice than catching an exception we know in advance will be thrown.
2. **Expected-capable environments (Postgres).** Any failure here is genuinely exceptional — function missing means the database is misconfigured; Vertex AI down means an outage. Both should surface, not be papered over with a Warning log that nobody reads.

The previous design (catch all failures, log Warning, content always succeeds) was rejected because it hides exactly the failure mode Toni cares about: a production Postgres without the `embedding` function would silently leave every node unembedded forever, and the only signal would be a log line. Loud crash is better.

**Trade-off:** On Postgres, a Vertex AI outage now causes text-content uploads to fail (500) for the duration of the outage. Non-text uploads continue to succeed (no Vertex AI involvement). This is the price of strict consistency and loud-on-misconfiguration; we accept it because:

- Vertex AI outages are rare and short.
- The content write rolling back means the user knows their upload did not land; they can retry. No silent half-state.
- The alternative — content lands, embedding silently does not — produces a corpus where some nodes are unembedded for reasons no one can later reconstruct.

**No re-upload-after-failure stale-vector race.** Because the transaction rolls back on failure, there is no window where new content lands paired with an old vector. The previous design had this race; this one does not.

### Decision 6: Backfill of existing 174 nodes

**Choice:** **Part of this task** — a dedicated `EmbeddingBackfillService` exposed via a small admin-only endpoint or CLI entry point, runnable once, idempotent. Postgres-only by construction.

**Rationale:** Of the three options (a) include / (b) defer / (c) implicit-on-next-update — (c) is the worst because most nodes will never be touched again ("session-log" entries are write-once by nature) and would remain forever unembedded, defeating the eventual search feature for exactly the corpus it most needs. (b) is the boring choice but creates a follow-up task whose only purpose is to call one method, and risks the backfill never happening. (a) is the right scope: the service is ~50 lines of conceptual logic, runs once per environment, and unblocks search.

**Mechanism.** First check the capability flag — if SQLite, exit with a clear "backfill is Postgres-only" message. Otherwise iterate `Node` rows where `Embedding IS NULL AND Content IS NOT NULL AND TextContentTypePredicate(ContentType)`. For each, issue the same `UPDATE Embedding = embedding(...)` used by the live path, in its own short transaction so a single failure does not abort the batch. Log progress periodically. Safe to re-run because the predicate filters out anything already embedded.

**Exposure.** Implemented as `dotnet run --project Backend -- backfill-embeddings`, wired into the existing `CliDispatcher` alongside `create-admin`. The CLI pattern is already established and keeps admin operations off the HTTP surface. Invoke: `dotnet run --project Backend -- backfill-embeddings` (against the live Postgres configuration in `appsettings.json` / environment variables). On SQLite it exits immediately with a "capability disabled" log line.

**Trade-off:** Adds one new endpoint or CLI verb. Worth it.

### Decision 7: Dimension and storage

**Choice:** Keep `Node.Embedding` as `float[]` with `[Size(3072)]`. **Do not** migrate to pgvector `vector(3072)` as part of this task.

**Rationale:** The current declaration accepts the writes the new code will produce — mamgo-backend writes `gemini-embedding-001` output through the same Ocelot primitive into `float[3072]` columns and it works. The pgvector cast for cosine queries (`DB.Cast(..., CastType.Vector)` in mamgo's similarity mapper) is applied at *read* time, in the search query. As long as the Postgres column allows that cast — which it does if Ocelot's `float[]` maps to `real[]` and pgvector accepts the implicit conversion, or if Ocelot already provisions a `vector` column — search will work without a schema change here.

**Follow-up flagged.** John verifies, as the first investigation step in §14, what Postgres column type Ocelot emits for `float[]` `[Size(3072)]`. If it is `real[]`, search may want a migration to `vector(3072)` for ANN indexing later — but that is the search task's problem, not this one. Either way, the embedding *value* gets stored correctly today.

### Decision 8: Reuse of existing `embed` PATCH op + clear-on-non-text

**Choice (PATCH op):** **Sit alongside.** The new auto-embedding flow does not call the PATCH op; the PATCH op remains in `DatabasePatchExtensions.cs` for explicit caller-driven embedding of patch values.

**Rationale:** The two operations have different shapes:

- The PATCH op takes an arbitrary string value supplied by the caller and writes it into an arbitrary `[AllowPatch] float[]` property. It is generic over property and content.
- The new auto-flow takes the just-written `Content` blob, decodes it, and writes specifically into `Node.Embedding`. It is specialized to content.

Merging them — e.g. having `UploadContent` issue a synthetic PATCH operation — would require either marking `Embedding` as `[AllowPatch]` (opening it to arbitrary external patches, which we do not want) or special-casing the patch dispatcher to allow internal-only patches. Both are worse than just calling the SQL primitive directly.

**When should anyone call the existing `embed` PATCH op?** When they have a derived text that they want to embed into a `[AllowPatch] float[]` property *other than* the auto-managed `Node.Embedding`. There is no such property today, so the PATCH op has no current production use — but the cost of leaving it is zero, and it gives the system a documented escape hatch.

**Choice (clear-on-non-text):** Clear `Embedding` to null when a non-text upload replaces text content (on the Postgres path only — SQLite skips the entire embedding step, including the clear).

**Rationale:** A node's `Embedding` is a derived attribute of its `Content`. If content is replaced with non-text, the old vector is stale and meaningless for search ranking. Leaving it would mean a "this PNG is about Kubernetes" cosine hit because the old markdown was about Kubernetes — actively misleading. Clearing is one extra `UPDATE` per non-text upload, negligible cost.

**Trade-off:** Two call sites for the same Postgres function. They share the model identifier — a hardcoded `"gemini-embedding-001"` in each. If we ever change the model, we change it in two places. Flagged as a §13 open question (centralize the model name as a constant).

### Decision 9: DI shape — inline branch vs interface seam

**Choice:** **Inline branch** against the capability flag. No `IContentEmbeddingProvider` interface.

**Shape.** A small singleton (e.g. an `IEmbeddingCapability` with a single `bool IsEnabled` property, or a struct holding the same bit) registered in `Startup.ConfigureServices` based on `Database:Type`. `NodeService` and `EmbeddingBackfillService` inject it and read it inline at their decision points. The Ocelot `Update<Node>().Set(Embedding == DB.CustomFunction(...))` call lives directly in `NodeService.UploadContent` — no provider class.

**Rationale.** Considered two options:

1. **Interface seam (`IContentEmbeddingProvider` with `Postgres` + `NoOp` implementations).** Clean DI pattern; trivial to override in tests; matches a "swap behaviour by registration" pattern.
2. **Inline branch.** A single `if (capability.IsEnabled)` directly in `UploadContent`. Smaller surface, fewer types, but the SQL is no longer behind an abstraction.

Picking the inline branch because:

- The capability is fixed at startup and decided by config; there is no runtime polymorphism to express. The interface would have exactly two implementations forever, one of which is `return Task.CompletedTask`.
- Tests do not need to override the *embedding behaviour*; they need the SQLite path to be taken, which the capability flag already guarantees. A SQLite test fixture naturally produces `IsEnabled = false`.
- The embedding call is two lines of Ocelot at one call site. Wrapping it in an interface adds three files (interface + two implementations) and a DI registration to hide two lines.
- The previous design's main reason to introduce the seam was to make embedding failures swallowable in tests. The new failure model removes that need entirely — there is no swallowing.

**Trade-off:** Less testable in isolation. If a future feature needs to vary embedding behaviour at runtime (e.g. multiple models, A/B testing), the seam would be reintroduced then. For now, YAGNI.

**Override in tests.** Not needed at the embedding boundary — the SQLite-backed `DatabaseFixture` produces `IsEnabled = false` naturally. Tests verify (a) the content lands, (b) `Embedding` stays at its prior value because the step was skipped.

### Decision 10: Test strategy

**Choice:** Tests run unchanged on SQLite. The capability flag is `false` in that fixture, so the embedding step is skipped. The text-content predicate is a pure function and gets its own table-driven unit tests independent of the database.

**Rationale.** Tests cannot invoke the Postgres custom function. Under the new failure model that is fine by construction: SQLite means `IsEnabled = false` means the embedding code path is not reached. No mock provider, no fake exception, no special test wiring — the same configuration check that prevents the call in production-on-SQLite (hypothetical) prevents it in test.

**What John writes.**

- Unit tests for `TextContentTypePredicate` — table-driven over the allowlist and a few negatives. Pure function, no DB.
- Integration test (`NodeContentHttpTests`-style) for `POST /content` with text content-type on SQLite — content lands, `Embedding` stays at its prior value, endpoint returns 200.
- Integration test for `POST /content` with non-text content-type on SQLite — content lands, `Embedding` stays at its prior value (the clear-on-non-text path is also skipped on SQLite by the capability check).
- Backfill service test on SQLite — verify the service exits with the "Postgres-only" message and writes nothing.
- A **production smoke test** (executed only against a real Postgres instance — outside the SQLite-bound unit suite) verifies the actual SQL call shape works end-to-end and that `Embedding` is populated after an upload. That smoke check is John's call before opening the PR; not part of the SQLite-bound CI test suite.

**What the previous design tested that this one does not.** "Provider throws → upload still 200." That test goes away because that behaviour goes away. The new equivalent test would be "Postgres path with a deliberately broken function call → caller sees 500" — that lives in the production smoke check, not in unit tests.

### Decision 11: Search-side guardrails — what NOT to constrain

The decisions above are intentionally permissive at the search-side boundary in three places:

1. **Model and dimension.** Fixed to `gemini-embedding-001` 3072-dim. Search will compute cosine on the same vectors. Changing the model later forces a re-embedding of everything; that is a known, acceptable cost path.
2. **No metadata in the input.** Search cannot rely on the embedding having semantic context from `Type` or `Name`. If search needs that signal, it must surface it separately (e.g. a filter, a hybrid retrieval pass) rather than expecting it baked into the vector.
3. **No normalization.** We pass the raw `Encoding.UTF8.GetString(...)` to the function. mamgo's search-side does its own normalization on the *query* string before embedding (or not — the precedent uses the same call shape on both sides). The choice not to normalize on the indexing side means search must mirror this choice for cosine to be meaningful. **This is the only decision here that materially constrains search**: search must also pass raw text, no normalization, no metadata prefix, when embedding the query string. The downstream search-task design doc should reference this constraint.

## 12. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Postgres function missing in a deployed environment | Low (caught at first text upload) | Text-content uploads return 500 until fixed | This is intentional per Decision 5. The loud crash *is* the alert. Non-text uploads still succeed. |
| Vertex AI quota exhaustion | Low | Text-content uploads return 500 during outage | Once quota resets, normal operation resumes. Backfill service can fill in any nodes whose embedding never landed — though under the new failure model, those nodes' content never landed either, so the affected set is the user's retries. |
| Vertex AI latency spikes >10s | Low | Text-content uploads slow accordingly; long ones may hit request timeout | Cancellation token aborts the request; the transaction rolls back; caller retries. |
| Hardcoded model in two call sites (auto-flow + `embed` PATCH op) drifts | Low | Two different vector spaces in one column | Centralize the model identifier as a single internal constant (§13). |
| Capability flag misconfigured (e.g. SQLite reported as Postgres) | Very low | First text upload to such an environment fails loudly with a clear SQL error | Same loud-failure path as a misconfigured Postgres. Operator fixes config. |

## 13. Open Questions (non-blocking)

These do not gate implementation but should be answered before search starts.

1. **Centralize the model identifier.** Should `"gemini-embedding-001"` live as a single `const` referenced by both the auto-flow call site and the `embed` PATCH op? Recommended yes — one-line change, removes a future drift class. John can do this as part of the same PR.
2. **Pgvector column type.** What does Ocelot actually emit for `float[] [Size(3072)]` on Postgres? If `real[]`, flag the migration-to-`vector(3072)` as a follow-up under the search task. If already `vector`, no action.
3. **Admin endpoint vs CLI for backfill.** Project precedent (`CliCreateAdminTests`) suggests CLI is already a pattern. Endpoint would be one more `[Authorize(Policy = "admin")]` route. John picks the form that matches the codebase, no architectural difference.
4. **Idle-update suppression.** If `UploadContent` is called with identical bytes to what's already stored, do we re-embed? Today's `UploadContent` does not check — it always issues the UPDATE. We inherit that. A future optimisation could compare hashes and skip both writes; for now, every upload re-embeds. Negligible cost. Worth noting: under the new failure model, idle re-embedding also means a Vertex AI outage during an idle re-upload causes the content write to roll back even though no content changed. Suppression would close that minor footgun.

## 14. Implementation Guidance for the Next Agent

Build in this order. Each step is self-contained enough to land independently if needed.

**Note (post-split):** This work shipped as two PRs. Steps 1–5 and step 7 landed in **PR A** (`feat/embeddings-on-content-write`, PR #23). Step 6 landed in **PR B** (`feat/embeddings-backfill-cli`). PR B depends on PR A; it was branched from PR A and targets `main` once PR A is merged.

1. **Verify the Postgres column type.** Inspect `SchemaService.CreateOrUpdateSchema<Node>` against a real Postgres instance to confirm what `float[] [Size(3072)]` maps to. Record the finding in this doc's Decision 7 footnote so the search-side task picks it up. (No code change unless the type is wrong.)
2. **Introduce the capability flag.** Add a small singleton (`IEmbeddingCapability` with `bool IsEnabled` or equivalent) registered in `Startup.ConfigureServices` based on `Database:Type` — `true` for Postgres, `false` for SQLite. Mirrors how `DatabaseExtensions.cs` already branches. **(PR A)**
3. **Add the predicate.** Implement `TextContentTypePredicate` as a static helper. Unit-test it against the allowlist in §11 Decision 3 and a representative set of negatives. Includes the strip-charset-suffix logic. **(PR A)**
4. **Modify `NodeService.UploadContent`.** Inject `IEmbeddingCapability`. Add the gating `if (capability.IsEnabled)` per §6. Inside the Postgres branch: if text content → issue the embedding UPDATE (no try/catch); else → issue the clear-to-null UPDATE. Plumb a `CancellationToken` parameter (cf. mainline pattern in `ListPagedByPath`). **(PR A)**
5. **Update integration tests** per §11 Decision 10. Most existing tests need no change — they run on SQLite, which now means the embedding step simply doesn't execute. Add the new positive/negative tests called out above. **(PR A)**
6. **Add `EmbeddingBackfillService`** per §11 Decision 6. Capability-flag-guarded; Postgres-only. Iterate the qualifying-and-unembedded set, issue the same UPDATE per node in its own transaction. Expose via the existing `CliDispatcher` pattern as the `backfill-embeddings` verb. Unit-test the SQLite-exit-cleanly path. **(PR B)**
7. **(Optional, see §13.1)** Extract the model identifier to a single shared constant. **(PR A — done in `TextContentTypePredicate.EmbeddingModel`)**
8. **Smoke against a real Postgres** before merging. Verify end-to-end: upload a markdown blob, confirm `Embedding` is populated with a 3072-dim vector. Run `backfill-embeddings` against the online instance to embed the 174 pre-existing nodes. This is not in CI; it is a manual post-merge check for Toni.

---

## Precedent: alignment with mamgo-backend

The mamgo-backend pattern propagates SQL failures from its `Update<Job>().Set(... DB.CustomFunction("embedding", ...) ...)` calls — no try/catch wraps them. Under this design DiVoid now matches that behaviour exactly on the Postgres path: the embedding UPDATE is issued without an exception handler, and failures propagate to the transaction boundary and out to the caller. The earlier version of this design diverged from mamgo by swallowing failures locally; that deviation is removed. DiVoid and mamgo-backend share one failure model for the `embedding(...)` SQL primitive.
