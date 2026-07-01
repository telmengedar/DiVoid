# Architectural Document: Provider-Pluggable Embedding Generation

Resolves DiVoid task **#2596**. Discussion-first design (per brief §5 / DiVoid #1165): the doc is committed, **no implementation PR is opened**, and the interface-shape fork is returned to the orchestrator for Toni's decision before any code lands.

Load-bearing contracts: **Code Contracts #114** (§0 principles, Ocelot idioms, `[AllowPatch]` discipline) and **Design Contracts #1136** (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §3 configurability-is-not-free, §5 Pre-Design Checklist). This design applies every rule in #1136, not only the ones the brief enumerated.

Precedent this design builds on / partially unwinds: **#440** (embeddings-v2 composition), **#626 + #627 / PR #81** (SQL-side branch-by-WHERE composition — this design **removes** it; see §4.4 and §11), **#180** (v1 failure-tolerance constraint), **#786** (dimension/vector-space incompatibility — PARKED, but authoritative on migration mechanics).

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
- The `google_ml_integration` provider produces identical behaviour to today (default, first-class).
- One reference app-side (HTTP) provider proves the seam is real — an OpenAI-compatible `/v1/embeddings` client, which simultaneously covers OpenAI, Ollama, LM Studio, and Hugging Face text-embeddings-inference (they share the wire shape). **No further providers are designed** (YAGNI — §2, brief §4b).
- The query (search) path uses the same configured provider to produce the query vector, then a **portable pgvector cosine** expression that needs no google extension.
- Dimension mismatch between the configured provider and the `Node.Embedding` column is a **fail-closed** startup error, not a silent corruption (§7, §11 Decision 3).

## 2. Scope & Non-Scope

**In scope.**

- The `IEmbeddingProvider` seam and its two concrete implementations (google, one HTTP reference).
- The configuration shape that selects and parameterises a provider.
- Refactoring all six coupling sites (§4.1) to route through the seam.
- Evolving `IEmbeddingCapability` from an is-Postgres boolean to a provider-derived capability, with a `NullEmbeddingProvider` replacing the SQLite special-case.
- The failure-tolerance posture for the app-side network hop (§9, Decision 2).
- The dimension/vector-space migration story (fail-closed gate; the actual re-backfill is existing machinery — §7, §12).

**Out of scope (explicit).**

- Implementing the model upgrade #786 (stays PARKED).
- The 8000-char cap reconciliation #690 (independent).
- Any provider beyond google + one HTTP reference — no OpenAI-vs-Cohere-vs-Azure matrix (YAGNI).
- Changing the composition policy (name + content rules, separator, cap) — locked by #440 Decision 1/3.
- Changing the similarity operator (cosine via `DB.VCos` / pgvector `<=>`).
- Async/queued embedding generation (still synchronous-in-transaction).
- Multi-provider-at-once, per-node provider selection, or runtime provider switching without redeploy (YAGNI — one provider per deployment).

## 3. Assumptions & Constraints

- **pgvector is the storage + query substrate on every real deployment.** `Node.Embedding` is `float[]` `[Size(3072)]` (`Backend/Models/Nodes/Node.cs:47`); the cosine query uses `DB.VCos` → pgvector `<=>` (`NodeMapper.cs:105`). pgvector is *not* google-specific — it runs on any Postgres. Only *vector generation* is currently google-coupled; *storage and ranking are already portable.* This is the key enabler.
- **The `google_ml_integration` `embedding(model, text)` function is a synchronous in-database call** returning `float[3072]`. It blocks the transaction across Vertex AI latency (~200–500 ms). This is unchanged.
- **Composition policy is settled and lives in C#** at `EmbeddingInputComposer.Compose(name, content, contentType)` (`Backend/Services/Embeddings/EmbeddingInputComposer.cs`). It is a pure static function and the single source of truth for "what text gets embedded." This design keeps it and makes it the *only* composition path (§4.4).
- **The corpus is small** (~600 nodes today; "low thousands" projected — #626 §3). All round-trip / bandwidth trade-offs are calibrated to that, not a public-API workload. Holding a transaction open across a model call is acceptable at this QPS.
- **Model identifier is currently a single const** `TextContentTypePredicate.EmbeddingModel = "gemini-embedding-001"`, referenced at every site except the `embed` PATCH op which hardcodes the literal (`DatabasePatchExtensions.cs:75`). Under the seam the model becomes provider config.
- **Startup already uses a fail-closed pattern** for missing config (`Keycloak:Audience`, `Startup.cs:120-124`). The dimension gate reuses that idiom.
- **Ocelot can bind a `float[]` constant that Postgres accepts as a `vector`** — assumed, not yet verified. The existing code binds `float[]` in `Set` and casts columns to `CastType.Vector`; binding a literal vector for the app-side write and query paths needs a Phase-0 probe (§13 Q1), mirroring how #627 probed the v0.22 helpers before committing.

## 4. Architectural Overview

### 4.1 The current coupling (verified in code)

| # | Site | File:line | Shape today |
|---|------|-----------|-------------|
| 1 | Create (name-only embed) | `NodeService.cs:150` | inline `embedding(model, nameInput)` in `UPDATE` |
| 2 | Content upload (composed) | `NodeService.cs:1083` | C# compose → `embedding(model, DB.Constant(composed))` |
| 3 | Name-PATCH regen (F1–F4) | `NodeService.cs:1001-1034` | four SQL-side composition branches, each inline `embedding(model, …)` |
| 4 | Backfill sweep | `EmbeddingBackfillService.cs:83` | C# compose → `embedding(model, DB.Constant(composed))` |
| 5 | `embed` PATCH op | `DatabasePatchExtensions.cs:75` | hardcoded `embedding('gemini-embedding-001', value)` |
| 6 | Query vector (search) | `NodeMapper.cs:106` | inline `embedding(model, queryText)` cast to vector, VCos cosine |

Sites 1–5 are **write** paths; site 6 is the **query** path. Sites 1, 3, 6 embed a value inside the SQL statement; sites 2, 4 compose in C# and bind the text as a constant; site 3 composes *server-side* (the #626 branch-by-WHERE).

### 4.2 The seam

Introduce **one primitive** that both provider families implement and that unifies write and query:

> **Given a piece of text, produce the SQL token that represents that text's embedding vector, suitable as the right-hand side of `SET Embedding = <token>` and as the left operand of the cosine comparison.**

- **Google provider** returns a *function-call token*: `embedding(model, <text>)`. The vector is produced server-side, inside the same statement — atomic, one round-trip, the vector never travels to .NET.
- **HTTP provider** performs the network call in C#, gets a `float[]`, and returns a *constant-vector token*: `DB.Constant(vector)` (cast to `vector`). The vector is produced app-side, then bound as a literal.

Because both return a *token*, every call site is uniform: `.Set(n => n.Embedding == provider.EmbedToken(text))`. **No call site branches on provider shape** — the SQL-vs-value duality is hidden inside the token the provider returns. This is the decisive property (§11 Decision 1, Option C).

```
                     +--------------------------------------+
   compose text      |         IEmbeddingProvider           |
  (EmbeddingInput    |                                      |
   Composer, C#,     |  EmbedToken(text) -> ISqlToken       |
   single source     |    google:  embedding(model, text)   |  <- server-side vector
   of truth)         |    http:    DB.Constant(vector)      |  <- app-side vector via HTTP
        |            |    null:    (never called; gated)    |
        |            |                                      |
        v            |  Dimension : int   (fail-closed gate)|
  text string  ----> |  IsEnabled : bool  (capability)      |
                     +-------------------+------------------+
                                         |
        +-----------------+--------------+---------------+-----------------+
        v                 v                              v                 v
   Create write     Content write                  Backfill write     Query vector
   SET Emb=token    SET Emb=token                  SET Emb=token      cosine(token, col)
   (NodeService)    (NodeService)                  (Backfill)         (NodeMapper)
```

The `embed` PATCH op (site 5) is **removed**, not routed (§11 Decision 4).

### 4.3 What lives where

- **Composition** (name+content → text): always in C#, always via `EmbeddingInputComposer`. One implementation.
- **Vector generation** (text → vector): the provider. Server-side for google, app-side for HTTP.
- **Storage + ranking**: unchanged pgvector — portable already.

### 4.4 The one thing this design gives up: server-side composition (#626 / PR #81)

Today the name-PATCH path composes text *inside Postgres* (F1–F4 branch-by-WHERE, `NodeService.cs:1001-1034`) to avoid pulling the `Content` blob into .NET. That optimization only works when the vector is also generated server-side (google). An HTTP provider **must** have the text materialized in C# to POST it — so server-side composition cannot be a shared path.

Keeping F1–F4 would mean **two composition implementations** (the SQL branches for google, the C# composer for HTTP) that must stay bit-for-bit in lockstep — exactly the drift risk #440 §10 trade-off #1 and #626 §12 risk #6 already flagged, now made worse by a provider fork. Per Design Contracts §1 (DRY) and §4 (less is better), this design **collapses to the single C# composition path** and **deletes F1–F4, `BuildEmbeddingBranchOperations`, and `RegenerateEmbeddingViaBranches`**.

**Trade-off (named, per #1136 §4):** on a name-PATCH of a large node, the google path now fetches the `Content` blob into .NET to compose (as `UploadContent` already does today, `NodeService.cs:1076`). Cost: pulling up to ~60 KB (largest current node, #626 §1) across the wire on a low-frequency operation. Benefit: one composition path instead of two, deletion of ~90 lines of google-specific SQL + its dedicated SQL-shape test file, and a seam that an HTTP provider can actually share. At DiVoid's scale (#626 itself: "the round-trip-count cost is illusory at this scale… bandwidth is secondary") the cost is negligible and the DRY win is real. **This is a genuine partial revert of recent shipped work (PR #81) — surfaced as Decision 1's consequence for Toni, not buried.**

## 5. Components & Responsibilities

### 5.1 `IEmbeddingProvider` (new interface)

**Owns.** The contract: turn a materialized text string into an embedding SQL token; expose the output dimension; expose whether embeddings are enabled.

**Does not own.** Composition (that is `EmbeddingInputComposer`). The write statement / transaction (that is `NodeService`). The cosine ranking (that is `NodeMapper`). Reading rows.

**Members (prose — no signatures per architect discipline):**
- **EmbedToken(text)** → an Ocelot SQL token for the vector of `text`. Google: a `CustomFunction("embedding", model, text)` token. HTTP: performs the HTTP call, returns a constant-vector token. Null provider: not callable (gated off upstream by `IsEnabled`).
- **Dimension** → the integer output dimension the provider produces. Used by the startup fail-closed gate and, for the HTTP provider, as the `dimensions` request parameter where the API supports it.
- **IsEnabled** → whether a real provider is configured (false for the null provider). Subsumes today's `IEmbeddingCapability.IsEnabled`.

### 5.2 `GoogleMlEmbeddingProvider`

**Owns.** Returning `embedding(model, <text-token>)` for a given text; `Dimension = 3072`; `IsEnabled = true`. Model comes from config (default `gemini-embedding-001`). Preserves today's atomic single-statement, no-round-trip, no-vector-transfer behaviour.

### 5.3 `HttpEmbeddingProvider` (the one reference app-side provider)

**Owns.** Calling a configured OpenAI-compatible `/v1/embeddings` endpoint (endpoint + model + api-key + dimension from config), parsing the returned vector, and returning it as a constant-vector token. This single implementation covers OpenAI, Ollama, LM Studio, and HF text-embeddings-inference because they share the wire shape.

**Does not own.** Retry policy beyond a single attempt and the transaction's own rollback (see §9). Composition. The write.

### 5.4 `NullEmbeddingProvider`

**Owns.** Being the active provider when no provider is configured (or on SQLite/test). `IsEnabled = false`; `EmbedToken`/`Dimension` never used because every call site gates on `IsEnabled` first. Replaces the `Database:Type != "Sqlite"` special-case at `Startup.cs:106` and `CliDispatcher.cs:56`.

### 5.5 `EmbeddingInputComposer` (unchanged, now the sole composition path)

Kept exactly as-is. Every write path (create, content, backfill, name-PATCH) calls it. It becomes the *only* composition implementation once F1–F4 are deleted.

### 5.6 `IEmbeddingCapability` (folded into the provider)

`IEmbeddingCapability.IsEnabled` is subsumed by `IEmbeddingProvider.IsEnabled`. The provider *is* the capability. Recommendation: **delete `IEmbeddingCapability` / `EmbeddingCapability`** and inject `IEmbeddingProvider` where the capability is read today (`NodeService` ctor, `EmbeddingBackfillService` ctor, the search guards at `NodeService.cs:712,795`, the `Embedding IS NOT NULL` predicate at `NodeService.cs:615`). This is a DRY consolidation (§2 form-3: the capability interface becomes a pure restatement of "is the provider non-null"). If Toni prefers to keep `IEmbeddingCapability` as a thin readability alias, that is acceptable but not recommended.

### 5.7 `NodeService` (modified — writes and search guards)

**Owns.** Calling `composer.Compose(...)`, then `provider.EmbedToken(composedText)`, then the single `SET Embedding = token` UPDATE — for create, content, and name-PATCH. The name-PATCH path loses F1–F4 and instead: read current name + content in-transaction, compose in C#, embed via the provider, one UPDATE (or `SET Embedding = null` when composition yields null). Search guards (`isSemantic && !provider.IsEnabled` → `SemanticSearchUnavailableException`) unchanged in meaning.

### 5.8 `NodeMapper` (modified — query vector via provider)

**Owns.** Obtaining the query-vector token from the provider instead of inlining `embedding(model, queryText)`. For google the emitted SQL is identical to today. For HTTP, the provider makes one call to produce the query vector, bound as a constant; the `DB.VCos` cosine comparison is unchanged (pgvector, portable). The mapper (or the `ListPaged` code that constructs it) gains a provider dependency.

## 6. Interactions & Data Flow

**Write (any of create / content / name-PATCH), provider-agnostic:**

1. Field write UPDATE runs inside the transaction (unchanged).
2. If `provider.IsEnabled`: compose text in C# via `EmbeddingInputComposer` (reading name/content in-transaction where needed).
3. If composition is null → `SET Embedding = null`. Else → `token = provider.EmbedToken(text)`; `SET Embedding = token`.
   - Google: `token` is `embedding(model, text)`; the UPDATE generates the vector server-side.
   - HTTP: `provider.EmbedToken` makes the network call *now* (inside the transaction window), returns a constant vector; the UPDATE binds it.
4. Commit. A provider failure (Vertex AI error, HTTP timeout) propagates and rolls the transaction back (fail-closed — §9, Decision 2).

**Query (search):**

1. `provider.EmbedToken(queryText)` yields the query-vector token (google: inline function; HTTP: one HTTP call → constant).
2. `1 - VCos(cast(queryToken, vector), cast(Embedding, vector))` ranks rows (unchanged pgvector cosine).
3. `Embedding IS NOT NULL` predicate and `ORDER BY similarity DESC` unchanged.

**Null provider (SQLite/test/unconfigured):** every write path skips step 2–3 (gated on `IsEnabled`); search throws `SemanticSearchUnavailableException`. Same observable behaviour as today's SQLite path.

## 7. Data Model (Conceptual)

No new entities, links, or columns. `Node.Embedding` stays `float[]`.

**Dimension is a deploy-time property, not a runtime knob.** The column's `[Size(3072)]` is compile-time. An operator adopting a provider with a different dimension (e.g. a 768-dim model) must: change the `[Size(...)]` attribute, let `SchemaService.CreateOrUpdateSchema<Node>` rebuild, and run the existing backfill. **Config declares the provider's dimension; startup asserts it equals the column size and refuses to start on mismatch** (fail-closed — §11 Decision 3). This turns a silent corruption (writing a 768-vector into a 3072 column, or vector-space garbage) into an explicit, loud operator action — the #786 constraint made a startup gate.

## 8. Contracts & Interfaces (Abstract)

**`IEmbeddingProvider`**

| Aspect | Contract |
|---|---|
| EmbedToken(text) | Input: a non-null, already-composed, already-capped text string. Output: an Ocelot SQL token whose evaluated value is the `float[Dimension]` embedding of the text, usable in `SET Embedding = token` and as a `VCos` operand. Google: no side effects, server-side. HTTP: performs exactly one network call; on failure throws (no swallow). Never returns null — the null-embedding case is decided by the *caller* (composition returned null) and written as `SET Embedding = null` without calling the provider. |
| Dimension | Constant per provider. Google: 3072. HTTP: from config. Invariant: must equal the `Node.Embedding` column size or startup fails. |
| IsEnabled | False only for `NullEmbeddingProvider`. True for google and HTTP. Every write path and search guard reads this before doing embedding work. |

**Composition contract** (unchanged, `EmbeddingInputComposer`): deterministic, pure, ≤8000 chars, null sentinel = "write null." Referenced verbatim from #440 §8.1.

**Config → provider selection contract** (§10): a single `Embedding:Provider` value maps to exactly one implementation; unknown/absent → null provider; misconfiguration (missing endpoint for HTTP, dimension mismatch) → fail-closed startup error.

## 9. Cross-Cutting Concerns

- **Failure tolerance (the app-side widening — Decision 2).** #440 chose *fail-closed*: a model failure rolls back the user's write. The google function fails only if Vertex AI (a Google-internal path from Cloud SQL) is down. An HTTP provider adds a wider, external failure surface (timeouts, rate limits, unreachable self-hosted server). **This design keeps fail-closed uniformly** (simplest, matches #440, no partial-state to reconcile), and adds a **bounded HTTP timeout** so a hung endpoint fails fast rather than holding the transaction/row lock indefinitely. Fail-open (write succeeds, embedding deferred to backfill) is the alternative — presented to Toni in Decision 2. It is *not* designed here to avoid YAGNI-building a deferral/repair mechanism that may not be wanted.
- **Transactions & consistency.** Every embedding write stays in the same transaction as the field write (#440 posture). Atomicity of `(field, embedding)` holds for *both* provider families — google generates the vector during the UPDATE; HTTP generates it just before, still inside the transaction. No new tearing window.
- **Auth / secrets.** The HTTP provider's API key is a secret: config via environment variable / the existing secret mechanism, **never** committed to appsettings or logged (§10). No new principal or endpoint authorization.
- **Observability.** One startup line naming the active provider + dimension (aids debugging, replaces the implicit `Database:Type` inference). HTTP provider: log failures at warning with endpoint + status (never the key or payload). Success stays silent (#440 posture).
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

**Configurability justification (per #1136 §3):** every knob here genuinely differs across environments/providers (endpoint, model, key differ by deployment; provider selection is the entire point of the task). `Dimension` is not speculative tuning — it is the input to a fail-closed safety assertion *and* the `dimensions` request param for providers that support it. No "for future tuning" knobs are introduced. The 8000-char cap stays a `const` in `EmbeddingInputComposer` (not promoted to config — #690 owns that question).

## 11. Decisions

### Decision 1 — Interface shape (THE central fork; needs Toni)

**Three candidates:**

- **Option A — dual-shape (brief's option A).** Provider yields *either* a SQL text-expression to inline (google, keeps server-side composition + F1–F4) *or* a materialized `float[]` (HTTP). Write path branches on which. **Keeps** #626 server-side composition. **Cost:** two composition implementations to keep in lockstep; every write call site branches on provider shape; the F1–F4 apparatus stays and must grow a "materialize" sibling. Highest complexity.
- **Option B — uniform vector materialization (brief's option B).** Provider always returns `float[]`. Google implemented as a scalar `SELECT embedding(model, text)` round-trip. **Loses** google's atomicity *and* adds a round-trip *and* pulls the vector to .NET. Simplest interface, worst google runtime.
- **Option C — uniform token (RECOMMENDED).** Provider returns a SQL *token* (function-call for google, constant-vector for HTTP). Composition always in C#. Write is uniformly `SET Embedding = token`; **no call-site branching.** Google **keeps** atomicity, single round-trip, and no vector transfer (only server-side *composition* is given up — §4.4). HTTP fits naturally. F1–F4 deleted.

**Recommendation: Option C.** It dominates A on simplicity (one composition path, no call-site fork) and dominates B on google runtime (keeps the atomic in-statement vector generation B throws away). The only thing C concedes relative to today is #626's server-side composition on name-PATCH — a marginal, low-frequency bandwidth optimization whose removal is a net DRY win at DiVoid's scale (§4.4). **The consequence Toni must accept for Option C: a partial revert of PR #81 (F1–F4 branch-by-WHERE) and its SQL-shape tests.**

### Decision 2 — Failure tolerance for the app-side hop (needs Toni)

**Choice A (RECOMMENDED): keep #440 fail-closed uniformly** + a bounded HTTP timeout. A model/HTTP failure rolls back the user's write. Simplest; no partial-state; consistent with today. Consequence: on a non-GCP host, an unreachable embedding server makes writes fail — the operator's uptime is coupled to their chosen embedding backend.

**Choice B: fail-open for the HTTP provider** — the field write commits, `Embedding` is left null/stale, a backfill sweep repairs later. Honors #180's original "content POST must never fail" literally and shrinks the blast radius of a flaky self-hosted endpoint. Consequence: a consistency window where a node is searchable-stale or invisible until backfill; requires a deferral/repair trigger (new mechanism — YAGNI risk if unwanted).

This is a genuine judgment call because #180 (fail-open) and #440 (fail-closed) point different ways and the external HTTP surface is materially riskier than the in-database function. Recommend A for simplicity and consistency; flag B as available if Toni weights write-availability over search-freshness for self-hosted deployments.

### Decision 3 — Dimension handling

Dimension is **deploy-time**, gated **fail-closed** at startup (config `Embedding:Dimension` must equal the `Node.Embedding` column size). Not a runtime knob; not dynamically resized. Adopting a different-dimension provider is an explicit schema-change + backfill operation (§7, §12). Rationale: the column size is compile-time and vector spaces are incompatible across models (#786); a loud startup refusal beats silent corruption. No speculative multi-dimension machinery (YAGNI).

### Decision 4 — The `embed` PATCH op

**Remove it.** It hardcodes `embedding('gemini-embedding-001', value)` in a generic patch extension (`DatabasePatchExtensions.cs:74-76`) that structurally cannot know about providers, it is already deprecated (CLAUDE.md, #440 Decision 5), and it has no production caller. Keeping it means a google-hardcoded leak survives the very refactor meant to remove google hardcoding. Removal deletes the `case "embed":` branch and its CLAUDE.md mention. Minor API-surface change → surfaced as a decision, but the recommendation is unambiguous: remove. (If Toni wants zero API-surface churn this cycle, the fallback is route-through-provider, but that re-introduces a provider dependency into a generic extension — worse layering. Remove is cleaner.)

### Decision 5 — No new abstraction beyond the provider

No `IEmbeddingComposer`, no per-write strategy objects, no provider *factory* interface (a switch on the config string at startup registration is enough — §2, one implementation selected per deployment). The provider is the single seam the task requires; everything else stays as concrete code.

## 12. Migration / Rollout Strategy

- **Same-dimension providers (google ↔ any 3072-dim HTTP model):** config change + restart + run the existing backfill (`cli backfill-embeddings`) to re-baseline vectors into the new provider's vector space (spaces are incompatible even at equal dimension — #786). No schema change.
- **Different-dimension provider:** edit `[Size(...)]`, schema rebuild (existing `DatabaseModelService`), config `Dimension` to match, restart (fail-closed gate passes), backfill. One operator sequence, documented in the backfill CLI help.
- **Default deployment (GCP):** `Embedding:Provider = GoogleMlIntegration`, dimension 3072 — behaviourally identical to today; no migration.
- **Rollback:** revert the config to google + backfill. Symmetric.

The backfill machinery (`EmbeddingBackfillService`) already exists and already routes composition through `EmbeddingInputComposer`; under Option C it simply calls `provider.EmbedToken` per row. No new migration tooling.

## 13. Open Questions (non-blocking, resolved at implementation time)

1. **Ocelot constant-vector binding (Phase-0 probe).** Confirm Ocelot can bind a `float[]` constant that Postgres accepts as `vector` for both the HTTP write (`SET Embedding = DB.Constant(vector)`) and the HTTP query (`VCos(cast(DB.Constant(queryVector), vector), …)`). Mirror the #627 reflect-probe approach against the on-disk assembly before committing to Option C's HTTP path. If binding is intractable, the HTTP write falls back to a parameterized raw literal — still app-side, still single-statement.
2. **`embedding(model, NULL)` semantics** — already flagged in #440 §13.1; under Option C the null case never calls the provider (caller writes `SET Embedding = null` directly), so this question is sidestepped for both providers.
3. **HTTP query-vector caching.** Repeated identical search queries re-call the HTTP provider. At current search volume, not worth a cache (YAGNI). Revisit only if search QPS and provider cost make it measurable.

## 14. Implementation Guidance for the Next Agent

Ordered milestones (one PR for the whole task per #1165 default — but this is discussion-first, so **no PR until Toni rules on Decision 1 & 2**). Build order once greenlit:

1. **Phase 0 — probe** the constant-vector binding (Q1) and the fail-closed gate idiom. Gate the whole plan on Q1's result; if it fails, return to the orchestrator before writing the HTTP provider.
2. **Provider seam** — introduce `IEmbeddingProvider`, `GoogleMlEmbeddingProvider`, `NullEmbeddingProvider`. Wire selection at `Startup.cs` and `CliDispatcher.cs` from the `Embedding` config section, replacing the `Database:Type` inference. Fold/remove `IEmbeddingCapability` (Decision 5.6).
3. **Route the google write sites through the seam** — sites 1, 2, 4 (create, content, backfill) call `provider.EmbedToken`. Behaviour identical; pure refactor. Fail-closed dimension gate at startup.
4. **Collapse name-PATCH to the C# composer** — delete F1–F4 / `BuildEmbeddingBranchOperations` / `RegenerateEmbeddingViaBranches`; replace with compose-in-C#-then-`EmbedToken` (site 3). Remove the SQL-shape test file that pinned F1–F4.
5. **Route the query path through the seam** — `NodeMapper` obtains the query-vector token from the provider (site 6). Google SQL unchanged; portability now latent.
6. **Remove the `embed` PATCH op** (site 5, Decision 4) + its CLAUDE.md mention.
7. **HTTP reference provider** — `HttpEmbeddingProvider` (OpenAI-compatible), config-driven endpoint/model/key/dimension, bounded timeout, fail-closed per Decision 2A.
8. **Tests** — provider selection (config → right provider / null on absent); google refactor is behaviour-preserving (existing tests stay green on SQLite null path); HTTP provider unit-tested against a stubbed endpoint; fail-closed dimension-mismatch startup test; query path emits identical google SQL (shape test) and a portable-cosine shape for the HTTP constant path. Load-bearing per #275 — each test fails if the behaviour regresses.
9. **Docs** — commit this design; update CLAUDE.md (embedding section, `embed` op removal); backfill CLI help gets the migration sequence (§12).

**Code Contract #114 hot-spots:** explicit types (no `var`); no redundant `async`/`await` passthrough; `<Nullable>disable</Nullable>` in Backend (no `?` on reference types); services own logic, controllers stay thin; the new provider registration is a startup concern, not a controller one; no single-statement transactions introduced.

---

## Pre-Design Checklist (#1136 §5) — self-verification

**KISS / DRY / YAGNI**
- ✅ No mirror type: `IEmbeddingProvider` has 3 impls (google, http, null) — a real polymorphic seam, not a parallel restatement. `IEmbeddingCapability` is *removed* as a restatement (§5.6).
- ✅ No abstraction with one impl and no second: the seam has two real providers + null, which is the task's whole point.
- ✅ No "might need later": exactly one reference HTTP provider; no provider matrix; no async queue; no per-node/multi-provider machinery (all explicitly out of scope §2).
- ✅ No deprecation window/shim: the `embed` op is *removed*, not phased (atomic deploy).
- ✅ DRY math: this design *removes* a duplication (two composition paths → one) rather than adding one. F1–F4 (~90 lines google-only SQL) deleted.

**Existing systems first**
- ✅ Reuses `EmbeddingInputComposer` (sole composition), `EmbeddingBackfillService` (sole migration tool), pgvector storage+cosine (already portable), the `Keycloak:Audience` fail-closed idiom. New surface is only the provider seam the task demands.
- ✅ New layer justified concretely: the provider is a genuine runtime-polymorphic seam (google server-side token vs HTTP app-side token) — not "cleanliness."

**Configurability**
- ✅ Every knob (§10) differs by environment/provider; `Dimension` gates a fail-closed check and feeds the HTTP `dimensions` param — not speculative. Cap stays `const`.

**Less is better**
- ✅ `IEmbeddingCapability` deleted; F1–F4 deleted; `embed` op deleted; no factory interface. Trade-off (losing server-side composition) named with cost/benefit math (§4.4, Decision 1).

**Document discipline**
- ✅ Cites #114 and #1136 as load-bearing; scope/non-scope explicit; out-of-scope listed; trade-offs named, not hidden; no multi-paragraph "why we keep X" filler. Predecessor #626/PR #81 mechanism explicitly marked for removal (not left live) in §4.4 + Phase 4.
