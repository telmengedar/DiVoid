# Architectural Document: Provider-Pluggable Embedding Generation

Resolves DiVoid task **#2596**. Discussion-first design (per brief §5 / DiVoid #1165): the doc is committed, **no implementation PR is opened**.

**Delta pass (2026-07-01).** A prior revision of this doc recommended **Option C** (uniform SQL token), which required a **partial revert of PR #81** (the F1–F4 server-side name+content composition). Toni **rejected the revert** and chose a new **Option D**: the provider abstraction sits at the level of *the embed operation itself* — the provider **owns the write** (executes the whole operation, or something in between), so the Google provider keeps its server-side composition and the HTTP provider composes app-side, with **no call-site branching**. This revision reworks §4, §5, §6, §8, §11 and §14 for Option D. Everything Toni locked (fail-closed + bounded timeout, deploy-time dimension gate, `embed` op removal, Phase-0 reflect-probe) is unchanged.

**Implementation pass (2026-07-02, PR #154).** The four WHERE-predicated UPDATEs (`BuildEmbeddingBranchOperations`) were collapsed into a **single CASE-expression UPDATE** (`BuildEmbeddingUpdate`) at implementation time — architect Sarah's ruling (DiVoid #2632): one Postgres round-trip instead of four, same SQL machinery, cleaner. The SQL path uses the **constant** budget `MaxLength − sep.Length` (7998) because the dynamic form `MaxLength − len(name) − len(sep)` is not SQL-expressible in Ocelot's CASE renderer. The C# composer (`EmbeddingInputComposer`) was aligned to the same constant budget in PR #154 round 3 (DiVoid #2596), so the two paths are now byte-for-byte identical for any (name, content) input — see §6.7. The `embed` PATCH op was removed per Decision 4.

Load-bearing contracts: **Code Contracts #114** (§0 principles, Ocelot idioms, `[AllowPatch]` discipline) and **Design Contracts #1136** (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §3 configurability-is-not-free, §5 Pre-Design Checklist).

Precedent this design builds on: **#440** (embeddings-v2 composition), **#626 + #627 / PR #81** (SQL-side branch-by-WHERE server-side composition — this design **PRESERVES** it under Option D; see §4.4), **#180** (v1 failure-tolerance constraint), **#786** (dimension/vector-space incompatibility — PARKED, but authoritative on migration mechanics).

---

## 1. Problem Statement

Embedding generation is hard-coupled to the Postgres `embedding(model, text)` function supplied by GCP's `google_ml_integration` extension. That coupling is inlined at **six** call sites (verified in code, §4.1). DiVoid therefore only produces embeddings when its Postgres runs on GCP Cloud SQL with that extension installed.

**Goal (user's verbatim words, 2026-07-01, task #2596):**
> "currently embedding only works with an installed google extension in postgres - surely there are alternatives. so we could have a configuration or something where you can adjust your embedding provider and theoretically use divoid somewhere else than just with a database in gcp."
>
> "any postgres host - but 'dropping' the google extension entirely is not the goal, we still want to use it - but someone who has another environment should be able to use another provider - so google ml extension is one of many possibilities to generate embeddings."

So: introduce a **provider seam**. The `google_ml_integration` provider stays first-class and is the default. An operator on a non-GCP Postgres configures a different provider (an HTTP embeddings API). The graph becomes portable to any Postgres + pgvector host.

**Success criteria.**

- A single configuration value selects the embedding provider. Absent/`None` → embeddings off (test/SQLite path), no special-casing of `Database:Type`.
- The `google_ml_integration` provider produces identical behaviour to today (default, first-class), **including its server-side name+content composition (PR #81 F1–F4)**.
- One reference app-side (HTTP) provider proves the seam is real — an OpenAI-compatible `/v1/embeddings` client, which simultaneously covers OpenAI, Ollama, LM Studio, and Hugging Face text-embeddings-inference (they share the wire shape). **No further providers are designed** (YAGNI — §2, brief §4b).
- The query (search) path uses the same configured provider to produce the query vector, then a **portable pgvector cosine** expression that needs no google extension.
- Dimension mismatch between the configured provider and the `Node.Embedding` column is a **fail-closed** startup error, not a silent corruption (§7, §11 Decision 3).

## 2. Scope & Non-Scope

**In scope.**

- The `IEmbeddingProvider` seam (operation-level, Option D) and its concrete implementations (google, one HTTP reference, null).
- The configuration shape that selects and parameterises a provider.
- Refactoring all six coupling sites (§4.1) to route through the seam.
- A **single source of truth for the composition policy** (separator, truncation budget, ordering, text-content predicate) consumed by *both* composition paths — the Google SQL builder and the C# `EmbeddingInputComposer` — plus a **parity guard test** (§6.7). This is the DRY containment that Option D's two-path cost demands.
- Evolving `IEmbeddingCapability` from an is-Postgres boolean to a provider-derived capability, with a `NullEmbeddingProvider` replacing the SQLite special-case.
- The failure-tolerance posture for the app-side network hop (§9, fail-closed + bounded timeout — locked).
- The dimension/vector-space migration story (fail-closed gate; the actual re-backfill is existing machinery — §7, §12).

**Out of scope (explicit).**

- Implementing the model upgrade #786 (stays PARKED).
- The 8000-char cap reconciliation #690 (independent).
- Any provider beyond google + one HTTP reference — no OpenAI-vs-Cohere-vs-Azure matrix (YAGNI).
- Changing the composition *policy* (name + content rules, separator, cap) — locked by #440 Decision 1/3. This design *relocates* the policy constants to a shared holder; it does not change their values.
- Changing the similarity operator (cosine via `DB.VCos` / pgvector `<=>`).
- Async/queued embedding generation (still synchronous-in-transaction).
- Multi-provider-at-once, per-node provider selection, or runtime provider switching without redeploy (YAGNI — one provider per deployment).
- A general composition-abstraction layer (e.g. an `IEmbeddingComposer` polymorphic over provider). The two composition paths are contained by *shared constants + a guard test*, not a new abstraction (KISS — §6.7, §11 Decision 5).

## 3. Assumptions & Constraints

- **pgvector is the storage + query substrate on every real deployment.** `Node.Embedding` is `float[]` `[Size(3072)]` (`Backend/Models/Nodes/Node.cs:47`); the cosine query uses `DB.VCos` → pgvector `<=>` (`NodeMapper.cs:105`). pgvector is *not* google-specific — it runs on any Postgres. Only *vector generation* is currently google-coupled; *storage and ranking are already portable.* This is the key enabler.
- **The `google_ml_integration` `embedding(model, text)` function is a synchronous in-database call** returning `float[3072]`. It blocks the transaction across Vertex AI latency (~200–500 ms). This is unchanged.
- **Composition policy is settled** and lives in *two* places: the C# `EmbeddingInputComposer.Compose(name, content, contentType)` (`Backend/Services/Embeddings/EmbeddingInputComposer.cs`) and the SQL `GoogleMlEmbeddingProvider.BuildEmbeddingUpdate` CASE-expression UPDATE (`Backend/Services/Embeddings/GoogleMlEmbeddingProvider.cs`). This design keeps both paths (Option D preserves the SQL path) and introduces a single constants holder both consume (§5.9, §6.7).
- **The corpus is small** (~600 nodes today; "low thousands" projected — #626 §3). All round-trip / bandwidth trade-offs are calibrated to that, not a public-API workload. Holding a transaction open across a model call is acceptable at this QPS. In particular, the HTTP provider re-reading a node's columns in-transaction (§6) is negligible at this scale.
- **Model identifier is currently a single const** `TextContentTypePredicate.EmbeddingModel = "gemini-embedding-001"`, referenced at every site except the `embed` PATCH op which hardcodes the literal (`DatabasePatchExtensions.cs:75`). Under the seam the model becomes provider config.
- **Startup already uses a fail-closed pattern** for missing config (`Keycloak:Audience`, `Startup.cs:120-124`). The dimension gate reuses that idiom.
- **Ocelot can bind a `float[]` constant that Postgres accepts as a `vector`** — assumed, not yet verified. The existing code binds `float[]` in `Set` and casts columns to `CastType.Vector`; binding a literal vector for the app-side write and query paths needs a Phase-0 probe (§13 Q1), mirroring how #627 probed the v0.22 helpers before committing.

## 4. Architectural Overview

### 4.1 The current coupling (verified in code)

| # | Site | File:line | Shape today | Composition today |
|---|------|-----------|-------------|-------------------|
| 1 | Create (name-only embed) | `NodeService.cs:149-155` | inline `embedding(model, DB.Constant(nameInput))` in `UPDATE` | C# (name truncation) |
| 2 | Content upload (composed) | `NodeService.cs:1083` | C# compose → `embedding(model, DB.Constant(composed))` | C# (`EmbeddingInputComposer`) |
| 3 | Name-PATCH regen (F1–F4) | `NodeService.cs:931 → 996` | four SQL-side WHERE-predicated composition branches, each inline `embedding(model, …)` | **server-side SQL** (PR #81) |
| 4 | Backfill sweep | `EmbeddingBackfillService.cs:75,83` | C# compose → `embedding(model, DB.Constant(composed))` | C# (`EmbeddingInputComposer`) |
| 5 | `embed` PATCH op | `DatabasePatchExtensions.cs:74-76` | hardcoded `embedding('gemini-embedding-001', value)` | none (raw value) |
| 6 | Query vector (search) | `NodeMapper.cs:105-107` | inline `embedding(model, queryText)` cast to vector, VCos cosine | none (raw query) |

Sites 1–5 are **write** paths; site 6 is the **query** path. Note today's Google path is *not* uniformly server-side: only site 3 (name-PATCH, PR #81) composes inside Postgres; sites 2 and 4 already compose in C# and bind a constant. Toni's Option D directive is specifically to **preserve site 3's server-side composition** rather than collapse it (as the earlier Option C recommended).

### 4.2 The seam (Option D — operation-level)

The provider abstraction is **the embed operation itself**, not a token the call site must interpret. The decisive property: **the call site delegates the write to the provider and does not branch on provider shape.** The differentiation lives *inside* each provider.

Two members carry the seam, at two different altitudes, deliberately:

1. **Write side — the provider executes the operation.**
   > *Given a node identity inside an open transaction, make that node's `Embedding` column reflect the embedding of its current persisted `(name, content, contentType)`.*
   - **Google provider** runs the **PR #81 F1–F4 WHERE-predicated server-side UPDATEs** against the node's own columns. The name+content composition happens *inside Postgres* (`concat` + `left(convert_from(content,…))`), the vector is generated *inside the same statement* (`embedding(...)`), and **content never travels to .NET**. Atomic, one-round-trip, no vector transfer — exactly today's site-3 behaviour, now reused for every write path.
   - **HTTP provider** reads `(name, content, contentType)` in-transaction, composes in C# via `EmbeddingInputComposer`, POSTs the composed text to the configured endpoint (bounded timeout), and writes the returned `float[]` as a constant-vector `SET Embedding = DB.Constant(vector)` (or `SET Embedding = null` when composition yields null). Composition happens *in C#*.
   - **Null provider** is a no-op (never invoked; every write path gates on `IsEnabled`).

2. **Query side — the provider provides a token.**
   > *Given a query text, yield the SQL token for its embedding vector, usable as the left operand of the cosine comparison.*
   - **Google:** an inline `embedding(model, queryText)` function-call token (identical SQL to today).
   - **HTTP:** performs one network call, returns a constant-vector token.

**Why two altitudes (the "or something in between" Toni left to me).** A write is a *self-contained unit* the provider can own end-to-end. Google executes a single CASE-expression UPDATE (`BuildEmbeddingUpdate`) that composes and generates the vector entirely inside Postgres. The query vector, by contrast, is **not** a standalone operation — it is an *operand* inside the mapper's ranking `SELECT` that `NodeMapper` owns; the provider can only contribute an expression token there. So: **executes on the write path, provides-a-token on the query path.** This asymmetry is the honest minimum; it is not two abstractions but one provider exposing the two altitudes its two call contexts actually require.

```
                     +----------------------------------------------------+
                     |                 IEmbeddingProvider                 |
                     |                                                    |
  write call sites   |  RegenerateEmbedding(db, tx, nodeId)  [EXECUTES]   |
  (create/content/   |    google: run F1-F4 server-side UPDATEs           |  <- content never leaves DB
   name-PATCH/       |            (PR #81 preserved, reused everywhere)   |
   backfill)  ---->  |    http:   read cols -> compose (C#) -> POST ->    |  <- vector via HTTP, in-tx
                     |            SET Embedding = DB.Constant(vector)      |
                     |    null:   no-op (never called; gated)             |
                     |                                                    |
  query call site    |  QueryVectorToken(queryText)          [TOKEN]      |
  (NodeMapper) ---->  |    google: embedding(model, queryText)             |
                     |    http:   POST -> DB.Constant(queryVector)         |
                     |                                                    |
                     |  Dimension : int   (fail-closed startup gate)      |
                     |  IsEnabled : bool  (capability)                    |
                     +---------------------------+------------------------+
                                                 |
              +------------------+---------------+----------------+
              | shared composition policy (EmbeddingCompositionPolicy) |
              |   Separator "\n\n" | MaxLength 8000 | text predicate    |
              |   consumed by  ->  EmbeddingInputComposer (C#)          |
              |               ->  BuildEmbeddingUpdate (SQL, single CASE) |
              |   guarded by   ->  parity test (§6.7)                   |
              +--------------------------------------------------------+
```

### 4.3 What lives where

- **Composition (name+content → text):** **two paths, by design under Option D.** Google composes *server-side in SQL* (F1–F4, PR #81 preserved). HTTP composes *app-side in C#* (`EmbeddingInputComposer`). Both draw their policy constants from one shared holder (§5.9). The two-path DRY cost and its containment are addressed head-on in §6.7 — this is the price of D over C, accepted deliberately.
- **Vector generation (text → vector):** the provider. Server-side for google, app-side for HTTP.
- **Storage + ranking:** unchanged pgvector — portable already.

### 4.4 What Option D preserves that Option C gave up: server-side composition (PR #81)

Option C would have made the provider return a *token* for pre-composed text, which forced composition to always happen in C# and therefore required **deleting F1–F4** — a partial revert of PR #81. Toni rejected that. Under **Option D the provider owns the whole write operation**, so the Google provider is free to keep composing *inside Postgres*. The PR #81 server-side `concat`/`left`/`convert_from`/`embedding(...)` machinery is preserved and relocated into `GoogleMlEmbeddingProvider.BuildEmbeddingUpdate` — a **single CASE-expression UPDATE** that collapses the four WHERE-predicated UPDATEs into one statement. Google therefore never materializes a node's `Content` blob into .NET for embedding purposes on any write path — a strict retention (and mild extension) of PR #81's bandwidth optimization.

**Implementation note (CASE collapse).** The design originally described four separate WHERE-predicated UPDATEs (`BuildEmbeddingBranchOperations`). During implementation, Sarah (architect) ruled that a single CASE-expression UPDATE (`BuildEmbeddingUpdate`) is both sufficient and cleaner — one round-trip to Postgres instead of four, with the branch selection done server-side by the CASE WHEN conditions. The SQL behaviour is preserved: each WHEN branch computes the same `embedding(model, concat(...))` as the original corresponding UPDATE; the ELSE NULL clause handles nodes with no embeddable surface. Both the SQL builder and the C# composer use the **constant** content budget `MaxLength − sep.Length` (7998), making the two paths byte-for-byte identical — the C# path was aligned to the constant budget in PR #154 round 3 (see §6.7).

The cost of preserving server-side composition is a **second composition path** (Google in SQL, HTTP in C#) that must stay policy-aligned. That cost is real and is contained, not waved away — see §6.7.

## 5. Components & Responsibilities

### 5.1 `IEmbeddingProvider` (new interface)

**Owns.** The operation contract: (a) *execute* the embedding write for a node inside a transaction; (b) *provide* the query-vector token; (c) expose output dimension and enabled-capability.

**Does not own.** The composition *policy values* (those live in `EmbeddingCompositionPolicy`, §5.9). The field write (name/content) that precedes the embedding write — that stays in `NodeService`. The cosine ranking assembly (that is `NodeMapper`). Reading rows for non-embedding purposes.

**Members (prose — no signatures per architect discipline):**
- **RegenerateEmbedding(database, transaction, nodeId, ct)** → executes the embedding write so node N's `Embedding` reflects its current persisted `(name, content, contentType)`. Google: single CASE-expression UPDATE (`BuildEmbeddingUpdate`). HTTP: read-compose-POST-write constant. Null: not callable (gated). *This is the write seam; no call site branches.*
- **QueryVectorToken(queryText)** → an Ocelot SQL token for the query vector. Google: inline `embedding(model, text)`. HTTP: one call → constant-vector token.
- **Dimension** → the integer output dimension the provider produces. Feeds the startup fail-closed gate and, for HTTP, the `dimensions` request parameter where supported.
- **IsEnabled** → whether a real provider is configured (false for the null provider). Subsumes today's `IEmbeddingCapability.IsEnabled`.

### 5.2 `GoogleMlEmbeddingProvider`

**Owns.** `RegenerateEmbedding` implemented as a **single CASE-expression UPDATE (`BuildEmbeddingUpdate`)** (composition + vector both inside Postgres, against the node's own columns — three WHEN branches cover name+content, name-only, and content-only; ELSE NULL for no embeddable surface); `QueryVectorToken` as inline `embedding(model, text)`; `Dimension = 3072`; `IsEnabled = true`. Model comes from config (default `gemini-embedding-001`). Preserves today's atomic, no-round-trip, no-vector-transfer, no-content-materialization behaviour — now in a single statement instead of four. Consumes `EmbeddingCompositionPolicy` for the separator and truncation budget it emits into SQL (no literal `"\n\n"`/`8000`).

### 5.3 `HttpEmbeddingProvider` (the one reference app-side provider)

**Owns.** `RegenerateEmbedding` implemented as: read `(name, content, contentType)` for the node in-transaction, compose via `EmbeddingInputComposer`, POST to a configured OpenAI-compatible `/v1/embeddings` endpoint (endpoint + model + api-key + dimension from config) under a bounded timeout, parse the returned vector, write it as a constant-vector `SET` (or `SET Embedding = null` when composition returns null). `QueryVectorToken` as one call → constant token. This single implementation covers OpenAI, Ollama, LM Studio, and HF text-embeddings-inference (shared wire shape).

**Does not own.** Retry policy beyond a single attempt; the transaction's own rollback (see §9). Composition policy values.

### 5.4 `NullEmbeddingProvider`

**Owns.** Being the active provider when no provider is configured (or on SQLite/test). `IsEnabled = false`; `RegenerateEmbedding`/`QueryVectorToken`/`Dimension` never used because every call site gates on `IsEnabled` first. Replaces the `Database:Type != "Sqlite"` special-case at `Startup.cs:106` and `CliDispatcher.cs:56`.

### 5.5 `EmbeddingInputComposer` (retained — the C# composition path)

Kept as the composition implementation for the **HTTP** path (and any future app-side provider). Consumes its policy constants from `EmbeddingCompositionPolicy` (§5.9) instead of its own private `MaxLength`/`Separator`. Under Option D it is **no longer the sole path** — the Google SQL path composes independently; §6.7 governs their alignment.

### 5.6 `IEmbeddingCapability` (folded into the provider)

`IEmbeddingCapability.IsEnabled` is subsumed by `IEmbeddingProvider.IsEnabled`. Recommendation: **delete `IEmbeddingCapability` / `EmbeddingCapability`** and inject `IEmbeddingProvider` where the capability is read today (`NodeService` ctor, `EmbeddingBackfillService` ctor, the search guards at `NodeService.cs:712,795`, the `Embedding IS NOT NULL` predicate at `NodeService.cs:615`). DRY consolidation (§2 form-3: the capability interface becomes a pure restatement of "is the provider real"). If Toni prefers to keep `IEmbeddingCapability` as a thin readability alias, that is acceptable but not recommended.

### 5.7 `NodeService` (modified — writes delegate to the provider)

**Owns.** Persisting the field change (name/content) in the transaction, then calling **`provider.RegenerateEmbedding(database, transaction, nodeId, ct)`** — one call, no branching — for create, content, and name-PATCH. Under Option D `NodeService` no longer contains the SQL composition branches nor the C# `EmbeddingInputComposer.Compose` call at the write sites; both move behind the provider (`BuildEmbeddingUpdate` inside `GoogleMlEmbeddingProvider`, the compose-and-bind inside `HttpEmbeddingProvider`). Search guards (`isSemantic && !provider.IsEnabled` → `SemanticSearchUnavailableException`) unchanged in meaning.

### 5.8 `NodeMapper` (modified — query vector via provider)

**Owns.** Obtaining the query-vector token from **`provider.QueryVectorToken(queryText)`** instead of inlining `embedding(model, queryText)`. For google the emitted SQL is identical to today. For HTTP, the provider makes one call to produce the query vector, bound as a constant; the `DB.VCos` cosine comparison is unchanged (pgvector, portable). The mapper (or the `ListPaged` code that constructs it) gains a provider dependency.

### 5.9 `EmbeddingCompositionPolicy` (new — single source of truth for composition policy)

**Owns.** The composition *policy constants*, and nothing else: the `Separator` (`"\n\n"`), the `MaxLength` budget (`8000`), and the text-content predicate rule (delegating to `TextContentTypePredicate`). A pure static holder — no I/O, no SQL, no DI.

**Consumed by both composition paths:**
- `EmbeddingInputComposer` (C#) references `EmbeddingCompositionPolicy.Separator` / `.MaxLength` instead of its own private constants.
- `GoogleMlEmbeddingProvider`'s server-side SQL builder emits `DB.Constant(EmbeddingCompositionPolicy.Separator)` and computes the content truncation budget from `EmbeddingCompositionPolicy.MaxLength` — **no literal `"\n\n"` or `8000` in the SQL builder.**

**Why a constants holder and not an abstraction.** The two paths cannot share *code* (one emits SQL trees, the other manipulates C# strings), but they can and must share *values and rules*. A shared abstraction over composition would be an over-engineered layer with two structurally different implementations (KISS violation). A shared constants holder + a parity guard test (§6.7) is the minimum that makes drift a compile-time or test-time failure. This is the containment Design Contracts §1 (DRY) demands without the §2 over-abstraction it warns against.

**Does not own.** The act of composition (that is still `EmbeddingInputComposer` for C# and the SQL builder for Google). It is values + predicate only.

## 6. Interactions & Data Flow

**Write (any of create / content / name-PATCH), provider-agnostic call site:**

1. Persist the field change (name and/or content) in the transaction (unchanged).
2. If `provider.IsEnabled`: call `provider.RegenerateEmbedding(database, transaction, nodeId, ct)`. **No branching at the call site.**
   - **Google:** runs the single CASE-expression UPDATE (`BuildEmbeddingUpdate`) against the node's columns; the matching WHEN branch composes text server-side and generates the vector in the same statement. Content never enters .NET.
   - **HTTP:** reads `(name, content, contentType)` in-transaction, composes in C#, POSTs to the endpoint *now* (inside the transaction window, bounded timeout), writes the returned constant vector — or `SET Embedding = null` when composition is null.
3. Commit. A provider failure (Vertex AI error, HTTP timeout/error) propagates and rolls the transaction back (fail-closed — §9, locked).

*Note on the HTTP in-transaction re-read (step 2, HTTP branch):* on content-upload and backfill the caller already holds the row/blob, so the provider's re-read is redundant work. Accepted for a clean, non-leaky seam (the provider sources its own inputs rather than the call site passing "hint" arguments a Google provider would ignore). At DiVoid's scale (§3) the re-read is negligible; this is consistent with #626's own "bandwidth is secondary at this scale" finding. If a future profile shows it matters, an optional materialized-input fast path can be added behind the same method without changing call sites.

**Query (search):**

1. `provider.QueryVectorToken(queryText)` yields the query-vector token (google: inline function; HTTP: one HTTP call → constant).
2. `1 - VCos(cast(queryToken, vector), cast(Embedding, vector))` ranks rows (unchanged pgvector cosine).
3. `Embedding IS NOT NULL` predicate and `ORDER BY similarity DESC` unchanged.

**Null provider (SQLite/test/unconfigured):** every write path skips step 2 (gated on `IsEnabled`); search throws `SemanticSearchUnavailableException`. Same observable behaviour as today's SQLite path.

### 6.7 The two-composition-path DRY tension (named head-on)

Option D deliberately keeps **two composition implementations**: Google composes in SQL (`concat(name, sep, left(convert_from(content,'UTF8'), budget))`), HTTP composes in C# (`EmbeddingInputComposer`). If the composition *policy* ever changes, both must change in lockstep or embeddings **drift between deployments** (a Google-hosted node and an HTTP-hosted node would embed different text for the same input, silently degrading cross-deployment search parity and any future re-host/backfill). This is the genuine cost of preserving Google's server-side path (§4.4) and it is the price of D over C. It is not papered over.

**The policy knobs that must stay aligned across the two paths:**

| Knob | C# path (`EmbeddingInputComposer`) | SQL path (`BuildEmbeddingUpdate`) | Containment |
|---|---|---|---|
| **Separator** | `"\n\n"` | `DB.Constant("\n\n")` in `concat` | Both read `EmbeddingCompositionPolicy.Separator` |
| **Truncation budget** | `MaxLength = 8000` | `left(..., MaxLength − sep.Length)` | Both read `EmbeddingCompositionPolicy.MaxLength` |
| **Ordering** | `name + sep + content` | `concat(name, sep, content)` | Fixed by policy doc + parity test |
| **Text-content predicate** | `TextContentTypePredicate.IsText` — case-insensitive prefix match over `TextPrefixes` (`["text/", "application/json", "application/xml"]`); charset suffixes handled naturally | `ContentType ILIKE prefix[0]+'%' OR ILIKE prefix[1]+'%' OR ILIKE prefix[2]+'%'` — OR-chain built from `TextContentTypePredicate.TextPrefixes` so the two paths share the same set | Both consume `TextContentTypePredicate.TextPrefixes`; `EmbeddingCompositionParityTests` CP7–CP10 guard charset-suffix + case + whitespace-name inputs |
| **Name-empty semantics** | `IsNullOrEmpty(name)` — whitespace-only treated as non-empty (aligned to SQL) | `Name != null AND Name != ''` — whitespace passes | Both treat whitespace-only names identically; parity test CP10 guards this |
| **Null/empty-surface semantics** | `Compose` returns null → caller writes `SET null` | ELSE NULL in CASE expression | Parity test asserts same trigger |
| **Content truncation *semantics*** | content clipped to `MaxLength − sep.Length` = 7998 chars (PR #154 round 3 aligned the C# path to the SQL constant budget) | `left(content, MaxLength − sep.Length)` = `left(content, 7998)` — both paths identical | Both derive from `EmbeddingCompositionPolicy.MaxLength` and `.Separator`; `EmbeddingCompositionParityTests` CP-PARITY asserts byte-for-byte alignment |

**The containment (KISS — no new abstraction):**
1. **`EmbeddingCompositionPolicy`** (§5.9) is the single source of truth for separator, budget, ordering rule, and text predicate. Neither path carries its own literals.
2. **A parity guard test (§14 milestone 4):** for a representative set of `(name, content, contentType)` tuples — name-only, content-only, both-fit, both-truncated, empty→null, non-text→name-only — assert the C# composer output equals a reference rendering of the SQL composition built from the *same* policy constants. This fails loudly if either path is edited to diverge (a literal reintroduced, ordering flipped, budget desynced). *Honest limitation:* a unit/NUnit test on the SQLite path can assert (a) the SQL builder emits the shared policy constants (retarget the existing `EmbeddingPatchSqlShapeTests` off literal `8000`/`'\n\n'`) and (b) a C# reference of the SQL semantics equals the composer output; a *fully behavioural* parity (real Postgres executing `concat`/`left`/`convert_from`) requires a Postgres integration test — **recommended, not mandated** (out of scope to build a Postgres test harness this cycle if one does not already exist; flagged in §13 Q4).

**§6.7 trade-off statement (per #1136 §4).** *Option D preserves Google's server-side composition (PR #81 F1–F4) at the cost of a second composition path that must stay policy-aligned with the C# composer. The cost is contained — not eliminated — by (a) a single `EmbeddingCompositionPolicy` holder both paths consume, and (b) a parity guard test (`EmbeddingCompositionParityTests` CP-PARITY, CP7–CP10) that fails on drift. As of PR #154 the C# composer and SQL builder use the same constant budget and the same prefix-based text gate (CF#A resolution, DiVoid #2596): `TextContentTypePredicate.TextPrefixes` is consumed by `IsText` (C#) and as an ILIKE OR-chain in `BuildEmbeddingUpdate` (SQL) — charset suffixes, case, and whitespace-name edge cases are now handled identically. The remaining risk is a future change to server-side SQL semantics that has no C# equivalent and falls below the parity test's granularity; this is bounded by keeping the policy surface deliberately tiny and by the recommended Postgres integration parity test. The benefit bought is: Google keeps atomic, single-statement, no-content-transfer writes on every path, and PR #81 ships intact rather than being reverted. At DiVoid's scale and given Toni's explicit preference to retain the Google optimization, the trade favours D.*

## 7. Data Model (Conceptual)

No new entities, links, or columns. `Node.Embedding` stays `float[]`.

**Dimension is a deploy-time property, not a runtime knob.** The column's `[Size(3072)]` is compile-time. An operator adopting a provider with a different dimension (e.g. a 768-dim model) must: change the `[Size(...)]` attribute, let `SchemaService.CreateOrUpdateSchema<Node>` rebuild, and run the existing backfill. **Config declares the provider's dimension; startup asserts it equals the column size and refuses to start on mismatch** (fail-closed — §11 Decision 3). This turns a silent corruption (writing a 768-vector into a 3072 column, or vector-space garbage) into an explicit, loud operator action — the #786 constraint made a startup gate.

## 8. Contracts & Interfaces (Abstract)

**`IEmbeddingProvider`**

| Member | Contract |
|---|---|
| RegenerateEmbedding(db, tx, nodeId, ct) | Precondition: the field write (name/content) for `nodeId` is already applied in `tx`; caller has checked `IsEnabled`. Effect: node `nodeId`'s `Embedding` is set to the embedding of its current persisted `(name, content, contentType)`, or to `null` when there is no embeddable surface. Google: executes a single CASE-expression UPDATE (`BuildEmbeddingUpdate`); no content leaves the DB; no vector transfer. HTTP: reads the node's columns in `tx`, composes in C#, performs **exactly one** network call under a bounded timeout, writes a constant vector; on failure **throws** (no swallow) so `tx` rolls back. Null: never invoked. Idempotent w.r.t. the node's current state (re-running yields the same embedding). |
| QueryVectorToken(queryText) | Input: a non-null query text. Output: an Ocelot SQL token whose evaluated value is the `float[Dimension]` embedding of `queryText`, usable as a `VCos` operand. Google: no side effects, server-side inline function. HTTP: performs exactly one network call; on failure throws. |
| Dimension | Constant per provider. Google: 3072. HTTP: from config. Invariant: must equal the `Node.Embedding` column size or startup fails. |
| IsEnabled | False only for `NullEmbeddingProvider`. True for google and HTTP. Every write path and search guard reads this before doing embedding work. |

**Composition policy contract** (`EmbeddingCompositionPolicy`, §5.9): deterministic values + predicate — `Separator = "\n\n"`, `MaxLength = 8000`, ordering `name` then `content`, text-content = `TextContentTypePredicate.IsText`. Both composition paths (C# composer, Google SQL builder) MUST derive from it. Parity test (§6.7) enforces equivalence.

**Config → provider selection contract** (§10): a single `Embedding:Provider` value maps to exactly one implementation; unknown/absent → null provider; misconfiguration (missing endpoint for HTTP, dimension mismatch) → fail-closed startup error.

## 9. Cross-Cutting Concerns

- **Failure tolerance — LOCKED: fail-closed uniformly + bounded timeout.** A model/HTTP failure rolls back the user's write. The google function fails only if Vertex AI is down; the HTTP provider adds a wider external failure surface (timeouts, rate limits, unreachable self-hosted server), so it runs under a **bounded HTTP timeout** to fail fast rather than holding the transaction/row lock indefinitely. No fail-open/deferred-backfill path is designed (Toni's decision; also avoids YAGNI-building a deferral/repair mechanism). Consequence, stated plainly: on a non-GCP host an unreachable embedding server makes writes fail — the operator's write-uptime is coupled to their chosen embedding backend. Accepted.
- **Transactions & consistency.** Every embedding write stays in the same transaction as the field write (#440 posture). Atomicity of `(field, embedding)` holds for *both* provider families — google generates the vector during the UPDATE; HTTP generates it just before the write, still inside the transaction. No new tearing window.
- **Auth / secrets.** The HTTP provider's API key is a secret: config via environment variable / the existing secret mechanism, **never** committed to appsettings or logged (§10). No new principal or endpoint authorization.
- **Observability.** One startup line naming the active provider + dimension (replaces the implicit `Database:Type` inference). HTTP provider: log failures at warning with endpoint + status (never the key or payload). Success stays silent (#440 posture).
- **Concurrency / idempotency / retries / caching.** Unchanged from #440 §9. No retry at the provider layer beyond the single attempt; the transaction's rollback is the consistency mechanism; backfill is the deliberate re-run.

## 10. Configuration Shape

A single `Embedding` config section replaces the `Database:Type != "Sqlite"` inference:

| Key | Applies to | Meaning |
|---|---|---|
| `Embedding:Provider` | all | `GoogleMlIntegration` (default) \| `OpenAiCompatible` \| `None`. Absent → `None` (null provider). |
| `Embedding:Model` | google, http | model identifier. Default `gemini-embedding-001` for google. |
| `Embedding:Dimension` | google, http | declared output dimension; **fail-closed** against the column size. |
| `Embedding:Endpoint` | http only | the `/v1/embeddings` URL (OpenAI, Ollama, TEI, LM Studio). |
| `Embedding:ApiKey` | http only | secret — from env/secret store, not appsettings. Optional for keyless local servers (Ollama). |
| `Embedding:TimeoutSeconds` | http only | bounded timeout for the app-side hop (§9). Sensible default; overridable per environment. |

**Configurability justification (per #1136 §3):** every knob here genuinely differs across environments/providers (endpoint, model, key, timeout differ by deployment; provider selection is the entire point of the task). `Dimension` is not speculative tuning — it is the input to a fail-closed safety assertion *and* the `dimensions` request param for providers that support it. No "for future tuning" knobs. The 8000-char cap stays in `EmbeddingCompositionPolicy` as a `const` (not promoted to config — #690 owns that question).

## 11. Decisions

### Decision 1 — Interface shape: **Option D (chosen by Toni), operation-level seam**

**Candidates considered:**

- **Option A — dual-shape.** Provider yields *either* a SQL text-expression to inline (google) *or* a materialized `float[]` (HTTP); the write path branches on which. Keeps server-side composition but every write call site branches on provider shape and F1–F4 grows a "materialize" sibling. High call-site complexity.
- **Option B — uniform vector materialization.** Provider always returns `float[]`; google implemented as a scalar `SELECT embedding(...)` round-trip. Simplest interface, but loses google's atomicity, adds a round-trip, and pulls the vector to .NET. Worst google runtime.
- **Option C — uniform SQL token (prior recommendation).** Provider returns a SQL *token* for pre-composed text; composition always in C#; write uniformly `SET Embedding = token`, no call-site branch. Clean, but **requires deleting F1–F4** (a partial revert of PR #81) because a token needs pre-composed text. **Rejected by Toni** — he wants the Google server-side composition kept.
- **Option D — operation-level seam (CHOSEN).** The provider **owns the write operation** (executes it) and **provides the query token**. Google keeps F1–F4 server-side composition *inside its `RegenerateEmbedding`*; HTTP composes in C# *inside its `RegenerateEmbedding`*. **No call-site branching** — the differentiation lives inside each provider, not at the seam. This is the "a bit like A but cleaner" Toni described: A's per-site branch is replaced by a single delegated call, and the shape divergence is fully encapsulated.

**Seam altitude (the "or something in between"):** **executes-the-operation on the write path, provides-a-token on the query path** (justified in §4.2). "Executes" is chosen over "provides the setter" specifically because F1–F4 is four WHERE-predicated UPDATEs, not one setter expression — only an operation-level seam preserves PR #81 verbatim. The query path stays token-level because the query vector is an operand inside the mapper's `SELECT`, not a standalone operation.

**Consequence Toni accepts for Option D:** two composition paths (Google SQL + HTTP C#) to keep policy-aligned. Contained by `EmbeddingCompositionPolicy` + a parity test (§6.7). This is the deliberate inverse of Option C's trade: C paid one-composition-path for a PR #81 revert; D pays a PR #81-preserving second path for a small, contained DRY cost.

### Decision 2 — Failure tolerance for the app-side hop: **LOCKED (fail-closed + bounded timeout)**

Keep #440 fail-closed uniformly; add a bounded HTTP timeout for the app-side hop (§9). A model/HTTP failure rolls back the user's write. No fail-open/deferred-backfill path. (Toni's locked decision; not reopened.)

### Decision 3 — Dimension handling: **deploy-time, fail-closed startup gate** (accepted)

Dimension is deploy-time, gated fail-closed at startup (config `Embedding:Dimension` must equal the `Node.Embedding` column size). Not a runtime knob; not dynamically resized. Adopting a different-dimension provider is an explicit schema-change + backfill operation (§7, §12). No speculative multi-dimension machinery (YAGNI).

### Decision 4 — The `embed` PATCH op: **remove** (re-confirmed under Option D)

Remove it. `DatabasePatchExtensions.cs:74-76` hardcodes `embedding('gemini-embedding-001', value)` in a *generic* patch extension; it is already deprecated (CLAUDE.md, #440 Decision 5) and has no production caller.

*Re-confirmation under Option D (per brief).* Under D the write goes through `provider.RegenerateEmbedding`, so the only alternative to removal is routing the `embed` op through the provider — which would inject an `IEmbeddingProvider` dependency into a **generic, provider-agnostic patch extension**. That is *worse* layering under D than it was under C: the whole point of the seam is that the generic patch machinery knows nothing about embedding providers. Removal is therefore *more* clearly correct under Option D, not less. Removal deletes the `case "embed":` branch and its CLAUDE.md mention. (If Toni wants zero API-surface churn this cycle, the fallback is to leave the op untouched-but-deprecated for one more cycle; but since it hardcodes google and would survive the de-google-ing refactor, removing it now is the recommendation.)

### Decision 5 — No new abstraction beyond the provider and the policy holder

No `IEmbeddingComposer` polymorphic over provider, no per-write strategy objects, no provider *factory* interface (a switch on the config string at startup registration is enough). The two seams the task requires are: (a) `IEmbeddingProvider` (the runtime-polymorphic operation seam) and (b) `EmbeddingCompositionPolicy` (a pure constants holder, *not* an abstraction — it has no polymorphism, just shared values). Everything else stays concrete. The two composition *renderings* (SQL, C#) remain concrete code in their respective providers; they are aligned by shared constants + a test, not unified by a new layer (§5.9, §6.7).

## 12. Migration / Rollout Strategy

- **Same-dimension providers (google ↔ any 3072-dim HTTP model):** config change + restart + run the existing backfill (`cli backfill-embeddings`) to re-baseline vectors into the new provider's vector space (spaces are incompatible even at equal dimension — #786). No schema change.
- **Different-dimension provider:** edit `[Size(...)]`, schema rebuild (existing `DatabaseModelService`), config `Dimension` to match, restart (fail-closed gate passes), backfill. One operator sequence, documented in the backfill CLI help.
- **Default deployment (GCP):** `Embedding:Provider = GoogleMlIntegration`, dimension 3072 — behaviourally identical to today; no migration.
- **Rollback:** revert the config to google + backfill. Symmetric.

The backfill machinery (`EmbeddingBackfillService`) already exists; under Option D it calls `provider.RegenerateEmbedding` per row (Google: single CASE UPDATE; HTTP: compose-and-POST). No new migration tooling.

## 13. Open Questions (non-blocking, resolved at implementation time)

1. **Ocelot constant-vector binding (Phase-0 probe).** Confirm Ocelot can bind a `float[]` constant that Postgres accepts as `vector` for both the HTTP write (`SET Embedding = DB.Constant(vector)`) and the HTTP query (`VCos(cast(DB.Constant(queryVector), vector), …)`). Mirror the #627 reflect-probe approach against the on-disk assembly before committing to the HTTP path. If binding is intractable, the HTTP write falls back to a parameterized raw literal — still app-side, still single-statement.
2. **`embedding(model, NULL)` semantics** — already flagged in #440 §13.1; under Option D the null case never calls the model (Google's F4 branch and HTTP's null-composition both write `SET Embedding = null` directly), so this question is sidestepped for both providers.
3. **HTTP query-vector caching.** Repeated identical search queries re-call the HTTP provider. At current search volume, not worth a cache (YAGNI). Revisit only if search QPS and provider cost make it measurable.
4. **Behavioural composition-parity test on real Postgres.** The §6.7 parity guard is fully behavioural only against a real Postgres executing `concat`/`left`/`convert_from`. Whether a Postgres integration harness exists (or should be stood up this cycle) is an implementation-time call; the unit-level shape+reference parity is the mandated minimum, the Postgres behavioural parity is recommended.

## 14. Implementation Guidance for the Next Agent

Ordered milestones (one PR for the whole task per #1165 default — but this is discussion-first, so **no PR until Toni greenlights Option D**). Build order once greenlit:

1. **Phase 0 — probe** the constant-vector binding (Q1) and the fail-closed gate idiom. Gate the HTTP path on Q1's result; if it fails, return to the orchestrator before writing the HTTP provider.
2. **Composition policy holder** — introduce `EmbeddingCompositionPolicy`; move `EmbeddingInputComposer`'s `MaxLength`/`Separator` into it; have the composer reference it. Pure refactor, behaviour-preserving.
3. **Provider seam** — introduce `IEmbeddingProvider`; implement `GoogleMlEmbeddingProvider` (implement `BuildEmbeddingUpdate` as a **single CASE-expression UPDATE** with three WHEN branches + ELSE NULL, retargeted off literal `8000`/`"\n\n"` onto the policy holder; use **constant budget** `MaxLength − sep.Length` for content truncation; align the C# composer to the same constant — see §6.7) and `NullEmbeddingProvider`. Wire selection at `Startup.cs`/`CliDispatcher.cs` from the `Embedding` config section, replacing the `Database:Type` inference. Fold/remove `IEmbeddingCapability` (§5.6). Fail-closed dimension gate at startup.
4. **Route the write sites through the seam** — sites 1, 2, 3, 4 (create, content, name-PATCH, backfill) call `provider.RegenerateEmbedding`. Google behaviour is server-side-composed everywhere (F1–F4 preserved, now reused on create/content/backfill too). **Parity guard test** (§6.7): representative-input parity between the C# composer and a reference rendering of the SQL composition from the shared policy; retarget `EmbeddingPatchSqlShapeTests` off literals onto the policy constants; add a Postgres behavioural parity test if a harness exists (Q4).
5. **Route the query path through the seam** — `NodeMapper` obtains the query-vector token from `provider.QueryVectorToken` (site 6). Google SQL unchanged; portability now latent.
6. **Remove the `embed` PATCH op** (site 5, Decision 4) + its CLAUDE.md mention.
7. **HTTP reference provider** — `HttpEmbeddingProvider` (OpenAI-compatible): `RegenerateEmbedding` = read-compose-POST-write-constant under bounded timeout; `QueryVectorToken` = one call → constant; config-driven endpoint/model/key/dimension/timeout; fail-closed per Decision 2.
8. **Tests** — provider selection (config → right provider / null on absent); google refactor behaviour-preserving (existing tests stay green on the SQLite null path); the §6.7 parity guard; HTTP provider unit-tested against a stubbed endpoint; fail-closed dimension-mismatch startup test; query path emits identical google SQL (shape test) and a portable-cosine shape for the HTTP constant path. Load-bearing per #275.
9. **Docs** — commit this design; update CLAUDE.md (embedding section, `embed` op removal); backfill CLI help gets the migration sequence (§12).

**Code Contract #114 hot-spots:** explicit types (no `var`); no redundant `async`/`await` passthrough; `<Nullable>disable</Nullable>` in Backend (no `?` on reference types); services own logic, controllers stay thin; the new provider registration is a startup concern, not a controller one; no single-statement transactions introduced. The relocated F1–F4 SQL must keep its K&R/formatting shape (§code style).

---

## Pre-Design Checklist (#1136 §5) — self-verification

**KISS / DRY / YAGNI**
- ✅ No mirror type: `IEmbeddingProvider` has 3 impls (google, http, null) — a real polymorphic seam. `EmbeddingCompositionPolicy` is a constants holder, not a parallel type. `IEmbeddingCapability` is *removed* as a restatement (§5.6).
- ✅ No abstraction with one impl: the provider seam has two real providers + null. The policy holder is deliberately *not* an abstraction (§5.9, Decision 5) — it is shared values, avoiding an over-engineered composition layer.
- ✅ No "might need later": exactly one reference HTTP provider; no provider matrix; no async queue; no per-node/multi-provider machinery; the materialized-input fast path is explicitly *deferred* until a profile justifies it (§6).
- ✅ No deprecation window/shim: the `embed` op is *removed*, not phased.
- ⚠️ DRY math, stated honestly: unlike the prior Option C revision (which *removed* a duplication), **Option D keeps two composition paths** — this is the accepted cost of preserving PR #81. It is *contained*, not eliminated: one policy holder + one parity test (§6.7). The trade is named in Decision 1 and §6.7, not hidden.

**Existing systems first**
- ✅ Reuses `EmbeddingInputComposer` (C# path), the PR #81 server-side SQL composition (Google path, collapsed into a single CASE UPDATE — same SQL machinery, fewer round-trips), `EmbeddingBackfillService` (migration tool), pgvector storage+cosine (already portable), the `Keycloak:Audience` fail-closed idiom.
- ✅ New surface justified concretely: `IEmbeddingProvider` is a genuine runtime-polymorphic operation seam; `EmbeddingCompositionPolicy` is the minimal DRY containment for the two-path cost — not "cleanliness."

**Configurability**
- ✅ Every knob (§10) differs by environment/provider; `Dimension` gates a fail-closed check and feeds the HTTP `dimensions` param; timeout is per-deployment. Cap stays `const` in the policy holder.

**Less is better**
- ✅ `IEmbeddingCapability` deleted; `embed` op deleted; no factory interface; no composition abstraction. The one thing *not* deleted — F1–F4 — is a deliberate reversal of the prior revision per Toni's Option D, with its cost named (§6.7).

**Document discipline**
- ✅ Cites #114 and #1136 as load-bearing; scope/non-scope explicit; the DRY tension named head-on (§6.7) with a §6.7 trade-off statement, not buried; PR #81 explicitly *preserved* (§4.4) with call sites; delta from the prior Option C revision called out at the top.
