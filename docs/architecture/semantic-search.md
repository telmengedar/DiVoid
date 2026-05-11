# Architectural Document: Semantic Search on `GET /api/nodes`

Resolves DiVoid task **#183** ("Fold embedding-based semantic search into the existing `GET /api/nodes` listing endpoint"). This document lands the open architectural questions; the implementing agent (John) builds from here.

This design is a clean extension of [`embeddings.md`](./embeddings.md) (task #180) and reuses the listing surface defined in [`graph-query.md`](./graph-query.md) (task #4). Both documents must be consulted before implementation begins.

---

## Goals

A caller — agent or human — supplies a natural-language question on the existing list endpoint. The server embeds the question text, ranks `Node`s by vector similarity against `Node.Embedding`, and returns them in the same response envelope as every other list call. No new route, no new envelope. The feature is **purely additive**: when the semantic-search field is absent the endpoint behaves exactly as it does today.

The endpoint must:

- Accept a question string on an existing-or-new query parameter and treat it as the search seed.
- Embed the question **server-side** using the same `embedding('gemini-embedding-001', ...)` Postgres function that populated `Node.Embedding` (see [`embeddings.md`](./embeddings.md) Decision 1). The query embedding never crosses the .NET boundary.
- Compose cleanly with the existing `type`, `name`, `status`, `linkedto`, `id`, and `nostatus` filters, with pagination, and with `fields`.
- Order results by similarity descending when the field is present; preserve existing sort semantics when it is absent.
- Return the similarity score on every result so callers can self-threshold.
- Fail loudly (HTTP 400) when the SQLite test fixture is asked to do semantic search; refuse to silently fall back.

## Non-goals

- A new endpoint or response envelope. Toni explicitly wants this folded into `GET /api/nodes`.
- Hybrid retrieval (BM25 + vector), reranking, query expansion. MVP is single-pass kNN.
- Multi-vector or multi-modal embeddings. DiVoid has one embedding column per node.
- Multi-turn / conversational search. Single-shot, stateless.
- Per-user / per-tenant scoping. DiVoid has no tenant model.
- Schema changes. `Node.Embedding` is already provisioned at the right dimension.
- Changes to the embedding write path. PR #23 / #24 are done.
- An ANN index. The current corpus is small enough that an exact kNN scan is acceptable; the index migration is a perf follow-up and the search-side query already uses the right operator for `ivfflat` / `hnsw` to slot in later.

## Current state

- `Node.Embedding` is a `float[]` `[Size(3072)]` populated for every text-content node on Postgres deployments by the path landed in PR #23. SQLite fixtures leave the column at `null`.
- `IEmbeddingCapability.IsEnabled` is a singleton boolean fixed at startup, `true` on Postgres and `false` on SQLite. It is the only gate the system needs to consult before invoking `embedding(...)`.
- `NodeService.ListPaged` and `NodeService.ListPagedByPath` already share the controller action `NodeController.ListPaged` via the unified `NodePathFilter` — the dispatch is on `Path` being present. The same multiplex is the natural home for a third mode "semantic search."
- `mamgo-backend` runs the exact pattern this feature needs, on `CampaignItemTargetMapper`, gated by `filter.FindSemantically`. The SQL shape, the cast strategy, the no-default-threshold posture, and the conditional similarity field-mapping are precedents we adopt directly.

---

## 1. The query parameter slot

### Decision

**Reuse the `Query` property defined on `Pooshit.AspNetCore.Services.Data.ListFilter`.**

That property is inherited today by `NodeFilter` (and therefore `NodePathFilter`) and is never consumed anywhere in DiVoid — confirmed by reading every `filter.*` reference in `NodeService.cs`, `UserService.cs`, `ApiKeyService.cs`, and `FilterExtensions.cs`. It is Toni's hinted unused slot.

The semantic meaning *"a natural-language search string"* is a reasonable fit for a property called `Query`: it is the user's query expressed as text. No new property on `NodeFilter` or `NodePathFilter`.

### Rationale and trade-off

- The Pooshit base class already defines `Query`; introducing a sibling like `Search` or `Q` would shadow it and would confuse anyone reading the inherited filter type.
- The literal word *"query"* is overloaded inside this codebase (we use it for path-query traversal), but path-query lives on a separate property (`Path`) and the `Query` slot is free. The two never collide.
- The trade-off accepted: a future reader who sees `?query=...` in a URL has to learn that on this endpoint it means "semantic search." This is documentation work, not a design flaw. The endpoint's composable surface (standard filters + optional `path` + optional `query`) is explained as a unified table in the controller XML doc, in the API reference at graph node 8, and in the openapi description.

Rejected alternatives:

- **A new `Semantic` / `Search` property on `NodeFilter`.** Adds schema. Coexists awkwardly with `Query`. Burns a clearer name on a property that already has a base-class home.
- **Reusing `NodePathFilter.Path` with a discriminator prefix** (e.g. `?path=semantic:foo`). Conflates two different query languages in one field. Hard to evolve.
- **A new endpoint `GET /api/nodes/search`.** Explicitly forbidden by the brief.

---

## 2. Model and embed-the-query call

### Decision

The query text is embedded **server-side, in the same SQL statement that produces the page**, using the same primitive that populated the column:

```
embedding('gemini-embedding-001', :queryText)
```

Composed in Ocelot terms exactly as the mamgo precedent at `mamgo-backend/netcore/services/Models/Campaigns/Targets/CampaignItemTargetMapper.cs:171-174`:

> `DB.Cast(DB.CustomFunction("embedding", DB.Constant("gemini-embedding-001"), DB.Constant(queryText)), CastType.Vector)`

The `gemini-embedding-001` literal must come from `TextContentTypePredicate.EmbeddingModel` — the single shared constant introduced by PR #23 — not be re-stringified at this call site. That is the same drift class flagged in [`embeddings.md`](./embeddings.md) §13.1 and resolved by reusing the constant.

### Rationale

- The query embedding is computed **once per request**, inside the Postgres planner, alongside the page query and `COUNT(*) OVER ()` window — one round trip. No `float[]` is shipped to .NET in either direction; the query string is the only input bind.
- Model identity is the hard rule from [`embeddings.md`](./embeddings.md) Decision 1: the column was populated with `gemini-embedding-001` 3072-dim vectors; the query must use the same model or the cosine numbers are noise.
- mamgo-backend uses this exact call shape on the read side (`CampaignItemTargetMapper.cs`) and on the write side (`JobService.cs`). DiVoid's write side already adopted it (PR #23). The read side adopts it now for symmetry.

### Trade-off

Each request pays one Vertex AI roundtrip on the database side. Typical cost is ~200–800 ms for short query strings (per the empirical figures in [`embeddings.md`](./embeddings.md) §3). For the interactive use case this is acceptable; agents are tolerant of sub-second latency. If higher QPS is ever needed we revisit with a query-side cache keyed on the verbatim query string — explicitly out of scope here.

---

## 3. Similarity operator

### Decision

**Cosine similarity, expressed through Ocelot's `DB.VCos`** — which compiles to pgvector's `<=>` (cosine distance) — and exposed to callers as `1.0 - <cosineDistance>` so larger means more similar.

This matches the mamgo precedent verbatim:

> `1.0f - DB.Cast(DB.VCos(DB.Cast(<queryEmbedding>, CastType.Vector), DB.Cast(<nodeEmbedding>, CastType.Vector)), CastType.Float).Single`

### Rationale

- `gemini-embedding-001` embeddings are unit-normalised on the model side; cosine is the canonical and recommended distance for it. This is consistent with the Gemini embedding model card and with the mamgo embedding-comparison script.
- pgvector's `<=>` is widely supported across pgvector versions and is the operator that `ivfflat` / `hnsw` cosine indices serve, so this choice does not paint a future ANN migration into a corner.
- mamgo runs this in production with no observed quality issues; matching that choice keeps DiVoid free of a per-codebase tuning surface.

### Trade-off

`<=>` returns distance (smaller is closer), but exposing it as similarity (larger is closer) needs the `1 - x` flip on the mapped field. This is a one-liner and matches what mamgo already does, but it is worth flagging so John does not forget the flip: an unflipped similarity sort would return the *least* relevant nodes first.

Rejected alternatives: `<->` (L2) is meaningless for cosine-normalised vectors; `<#>` (negative inner product) is equivalent to cosine on normalised vectors but is harder to reason about for callers ("less negative is better").

---

## 4. Threshold vs top-N vs return-the-score

### Decision

**Top-N + return-the-score; callers self-threshold.** No server-side default cosine floor in v1.

Concretely:

- `count` (already clamped to ≤500 by `ApplyFilter`) is the N.
- The response carries a new `similarity` field per result (a float in roughly the `[-1, 1]` range, in practice `[0, 1]` for this model).
- An optional `minSimilarity` query parameter (a float, default null) is recognised: when present it becomes a `WHERE similarity >= :minSimilarity` predicate. When absent no floor is applied. mamgo's `Similarity` filter field is the precedent.

### Rationale

The brief recommends this default and the mamgo precedent backs it. Three reasons it is the right call:

1. **Cosines from `gemini-embedding-001` are not cross-corpus calibrated.** mamgo's `applicant-rematching.md:470` explicitly observes that a floor tuned against one applicant's distribution misfires on another's. The exact same dynamic applies to DiVoid: a query about "Postgres index tuning" produces a different similarity distribution from "tomorrow's groceries." A single server-side floor cannot satisfy both.
2. **Callers know what they want.** An agent asking *"what nodes are about authentication"* wants the top 10 regardless of absolute score; a human filtering for noise wants to see scores and pick a cutoff. Returning the score makes both shapes possible without server-side policy.
3. **The corner the design avoids.** Threshold-only-with-no-score is a one-way door: if the floor is too tight the user gets empty pages with no signal why; if it is too loose they get noise with no signal which is which. Exposing the score is the cheapest insurance against either failure mode.

### Trade-off

A `similarity` field is added to `NodeDetails` (as `float?`, nullable so it stays `null` outside semantic-search mode). The field appears only in the response, never in `POST` / `PATCH` shapes — it is a derived value. The mamgo `CampaignItemTargetDetails.Similarity` pattern is the precedent.

`minSimilarity` is a new query parameter on `NodeFilter`. It is ignored when `Query` is absent (a `minSimilarity` floor without a query has no defined meaning); John surfaces this as a 400 with `code=badparameter` and message *"minSimilarity requires query"* rather than silently ignoring it.

---

## 5. Result ordering and paging

### Decision

When `Query` is present, similarity ordering wins. Specifically:

- `ORDER BY similarity DESC` is appended automatically.
- `sort` and `descending` are **ignored** with no error (parity with mamgo's commented-out compound-sort attempt at `CampaignItemTargetService.cs:354-357`, which was never enabled — Toni's standing posture is that compound sort with similarity is more confusing than useful).
- `count` (top-N) and `continue` (row offset) work normally. Paging through a similarity-ordered result set is by row offset on the deterministically-ordered stream.

When `Query` is absent the existing behaviour is preserved exactly — `sort` and `descending` apply, `KeyNotFoundException` on unknown sort keys still surfaces as HTTP 400. Nothing in the standard list path changes.

### Edge cases spelt out

| Case | Behaviour |
|---|---|
| `?query=X&sort=name` | `sort` silently ignored; results are ordered by similarity desc. Documented in controller XML doc. |
| `?query=X&descending=true` | `descending` silently ignored; similarity desc is the only sort. |
| Two nodes with identical cosine | Tie-breaker is `Node.Id ASC` for stable pagination. Added as a secondary `OrderBy`. |
| `?query=X&continue=100` | Row offset 100 against the similarity-ordered stream. The cosine numbers may "jump" between pages because similarity is continuous; this is acceptable, callers who want stable pages can stash the score and resume below it. |
| `?query=X&count=10000` | Clamped to 500 by `ApplyFilter` exactly as today. |

### Rationale

Compound sorts with a derived similarity column are *theoretically* expressive — *"top-similarity items, then alphabetical within ties"* — but they are confusing to specify, ambiguous in semantics (what does it mean to order by `name` after a continuous similarity score?), and mamgo's own attempt at it was left commented out as unused. Silently overriding `sort` when `Query` is present is the simpler contract: a semantic search is a similarity query, and that is the only sort that makes sense.

### Trade-off

A caller who supplies both `?query=...` and `?sort=...` gets a result they did not literally ask for. We accept this because the alternative (400 on conflict) makes the most common copy-paste mistake — leaving `sort=name` from a previous request — fatal instead of warning. We will document the silent override on the endpoint and in the API reference.

---

## 6. Composition with non-similarity filters

### Decision

`?type=task&status=open&query=X` ANDs the predicates exactly as the standard list endpoint already does for `?type=task&status=open`. The semantic-search step layers on top:

```
SELECT n.*, similarity, COUNT(*) OVER ()
FROM   Node n
JOIN   NodeType t ON n.TypeId = t.Id
WHERE  <existing NodeFilter predicates: type, name, status, linkedto, id, nostatus>
  AND  n.Embedding IS NOT NULL                       -- §7 below
  AND  similarity >= :minSimilarity                  -- only if supplied
ORDER  BY similarity DESC, n.Id ASC
LIMIT  :count OFFSET :continue
```

In Ocelot terms: the existing `GenerateFilter(filter)` runs unchanged, producing the predicate; an additional `n.Embedding != null` clause is appended (§7); the mapper is constructed with the filter so the conditional `similarity` field-mapping yields; `OrderBy(mapper["similarity"].Field, descending: true)` is appended to the operation before `WindowedFromOperation`.

### What changes structurally

`NodeMapper` becomes filter-aware — its constructor accepts the `NodeFilter` (or null), and `Mappings()` conditionally yields the `similarity` field when `filter?.Query` is non-empty. This matches `CampaignItemTargetMapper`'s pattern verbatim. `NodeMapper.CreateOperation` continues to alias `node` and join `NodeType` as `type`; no extra join is needed because the query-side embedding is computed in the `Node` row scope (each `Node.Embedding` is a per-row column, not a foreign table).

The mapper instance is constructed inside `NodeService.ListPaged` (and `ListPagedByPath`) once per request from the inbound filter. Both modes support semantic search uniformly — `Path` produces the candidate set, `Query` ranks within it. See §10 below for the composition rule.

### Rationale and trade-off

- The join graph is `Node + NodeType`. Adding the similarity field-mapping on the mapper is the canonical Ocelot extension point and keeps the SQL composable with `WindowedFromOperation<long, Node>` for `COUNT(*) OVER ()`.
- Making `NodeMapper` filter-aware is a small breaking shape change to its public ctor. All call sites in `NodeService` construct the mapper fresh per request; the refactor is mechanical (every `new NodeMapper()` becomes `new NodeMapper(filter)`, with a `null` overload kept for tests that do not exercise semantic search).
- The trade-off: `NodeMapper` is no longer truly stateless. Acceptable, as long as nobody caches the mapper instance — and nothing does.

---

## 7. Nodes without embeddings

### Decision

**Exclude.** When `Query` is present, append `WHERE n.Embedding IS NOT NULL` to the operation's predicate.

### Rationale

- A node without an embedding has no vector signal to rank on. Its cosine against the query is undefined; the database would return `null` or a sentinel for `VCos(null, ...)`, polluting both the result set and the score.
- Three cases produce `Embedding IS NULL` today: (a) the node never had text content (binary blob, never embedded by construction), (b) the node predates PR #23 and is awaiting backfill, (c) the node is on a SQLite deployment — but in case (c) the request is already rejected at the capability gate (§8) so we never get here.
- For case (a) the node genuinely has no semantic content; excluding it is correct. For case (b) the backfill CLI exists for exactly that reason and the gap closes once it runs. Neither is a regression.

### Trade-off

A node whose embedding generation transiently failed (e.g. a past Vertex AI outage during upload, before PR #23's strict-consistency model landed) is invisible to semantic search until re-uploaded. We accept this; the alternative (sentinel-distance inclusion) would mix unembedded nodes into the result set with no defensible position in the ranking.

---

## 8. Capability gating

### Decision

When `embeddingCapability.IsEnabled == false` and `Query` is non-empty, return **HTTP 400** with the body shape produced by the existing `Pooshit.AspNetCore.Services` error pipeline:

| Field | Value |
|---|---|
| HTTP status | `400` |
| `code` | `badparameter` |
| `text` | `"Semantic search requires Postgres; this deployment does not support the embedding function."` |

The gate sits at the top of `NodeService.ListPaged` (and any future path-mode adoption per §10): before any SQL is composed, check the capability flag; if disabled and `Query` is non-empty, throw a parameter-validation exception that the existing middleware turns into the 400 above.

When `Query` is empty (or null), the capability flag is irrelevant — the call routes through the standard list path exactly as today.

### Rationale

- Silent fallback to non-similarity listing was rejected on the same grounds as PR #23 rejected silent embedding-failure-swallowing: a misconfigured caller would silently get the wrong kind of results.
- 503 / 501 was considered but the failure mode is *not* an outage — it is a deliberately-not-supported configuration. 400 is the correct semantic ("you sent something this endpoint can't process") and aligns with the codebase's existing `badparameter` precedent.
- Tests on the SQLite fixture can exercise this branch directly: send `?query=foo`, assert 400 with the right error code. No mocking required.

### Trade-off

A caller who switches between Postgres and SQLite environments (e.g. running integration tests locally) sees different behaviour. We accept this because the capability flag is fixed per environment and the difference is exactly what the flag is for. The 400 message names the constraint explicitly so the failure is self-diagnosing.

---

## 9. Test strategy

The constraint from PR #23 is unchanged: **the Postgres `embedding(...)` function cannot be invoked on SQLite**, so end-to-end similarity computation cannot be exercised in CI. Tests cover the seams that *are* exercisable:

| Seam | Test substrate | Asserts |
|---|---|---|
| Capability-disabled path | `WebApplicationFactory<Program>` on SQLite fixture | `GET /api/nodes?query=foo` returns 400 with `code=badparameter`, message references "Postgres" / "embedding function." |
| `Query` absent → unchanged list | SQLite fixture | `GET /api/nodes?type=task` returns the same shape and ordering as today (regression test against existing fixtures). |
| `minSimilarity` without `Query` | SQLite fixture | `GET /api/nodes?minSimilarity=0.5` returns 400 with message referencing the required `query` parameter. |
| `sort` silently overridden | SQLite fixture | Not directly testable end-to-end (the SQLite path 400s before reaching the sort layer). Verified instead via a focused unit test on whatever helper composes the operation, asserting the `OrderBy` it sets when `Query` is present. |
| Predicate composition shape | Unit test on the operation-composition helper | Given a `NodeFilter` with `Type`, `Status`, and `Query`, the assembled `LoadOperation<Node>` has all three predicates ANDed, plus the `Embedding IS NOT NULL` predicate, plus the similarity OrderBy. Assertion is at the operation level, not the SQL string. |
| Path + `Query` composition | Unit test on the operation-composition helper | Given a `NodePathFilter` with `Path` and `Query`, the terminal `LoadOperation<Node>` returned by `ComposeHops` carries both the path predicate (IN-chain) and the similarity OrderBy + `Embedding IS NOT NULL` predicate + (if supplied) `MinSimilarity` floor. The `similarity` field-mapping is yielded. Capability gate fires on `Query` regardless of `Path`. Assertion at the operation level. |
| Tie-break ordering | Unit test on the operation helper | When `Query` is present, the operation's OrderBy criteria are `(similarity DESC, Id ASC)` in that order. Applies in both plain and path modes. |
| Production smoke (manual) | Real Postgres | `POST` a markdown node, then `GET /api/nodes?query=<excerpt>&count=5` returns the expected hit at rank 1 with a sensible similarity (>0.6 for a near-duplicate string). John runs this once before opening the PR; not part of CI. |

The new `similarity` field on `NodeDetails` is verified at the JSON serialization layer: a fixture round-trip ensures it serialises as a nullable float (omitted when null) and is never accepted on inbound payloads.

### Seams John needs

1. **The operation-composition helper.** Whatever method ends up building the `LoadOperation<Node>` from a `NodeFilter` must be unit-testable in isolation — i.e. it should not require an actual `IEntityManager` to execute. Asserting at the operation tree level (Ocelot's `LoadOperation`'s `Predicate` / `OrderBy` accessors) is sufficient. mamgo's tests of `CampaignItemTargetService.List` are the structural precedent.
2. **The mapper-with-filter ctor.** `NodeMapper` accepts `NodeFilter` (or null); the conditional `similarity` mapping is yielded when `filter?.Query` is non-empty. Unit-test: build the mapper with and without a `Query` value, assert the mapped field set differs only by `similarity`.
3. **`NodeDetails.Similarity`.** Nullable float, omitted on POST/PATCH/standard-list responses; set on semantic-search responses. Serialisation test asserts both shapes.

---

## 10. Cross-check against `embeddings.md` Decision 10 (Search-side guardrails)

[`embeddings.md`](./embeddings.md) Decision 11 ("Search-side guardrails — what NOT to constrain"; the numbering shifted from the original Decision 10 to 11 during PR #23 review, but the content is the same) names three constraints search must honour. This design honours all three:

1. **Model and dimension.** This design fixes the query embedding to `gemini-embedding-001` 3072-dim via the single `TextContentTypePredicate.EmbeddingModel` constant. ✓
2. **No metadata in the input.** This design passes the raw user query string to `embedding(...)` with no `Type:` / `Name:` prefix. The query is a question; the indexed vectors do not carry that signal; symmetry holds. ✓
3. **No normalization.** This design passes the raw `Query` string verbatim. mamgo's read-side also passes the raw `FindSemantically` string. Symmetric with the write side which passes raw `Encoding.UTF8.GetString(blob)`. ✓

**No blockers.** The search-side guardrails do not constrain this design beyond what is already adopted.

### Path-mode and `Query` compose

`NodePathFilter.Path` and `NodeFilter.Query` **compose**: `Path` produces a candidate set; `Query` ranks within it by similarity. A path-query result is a legitimate set of nodes, exactly the kind of set semantic search ranks against. There is no conflict to resolve.

The rule is symmetric with the plain-list path: the terminal `LoadOperation<Node>` produced by `ComposeHops` receives the same similarity `OrderBy(DB.VCos(...))`, the same `n.Embedding IS NOT NULL` predicate, the same `MinSimilarity` floor, and is built against the same filter-aware `NodeMapper` (§6) so the `similarity` field-projection yields identically. The response shape — including the per-item `similarity` field — is identical in both modes. Whether John factors the shared work as one helper invoked by both paths or as one branch reachable from both code paths is mechanical; the semantic is one rule.

Other interactions are parity with plain mode (§5, §8): `sort` and `descending` are silently overridden when `Query` is present; the capability gate raises 400/`badparameter` on SQLite whenever `Query` is present, regardless of `Path`. Paging through a path+query result is row offset over the similarity-ordered stream of the path-restricted set; cosine "jumps" between pages are accepted on the same terms.

---

## Out of scope

- Hybrid retrieval (BM25 + cosine), query expansion, reranking.
- Approximate-nearest-neighbour indices (`ivfflat`, `hnsw`). The exact scan is fine at current corpus size; the chosen operator is index-ready when needed.
- Query-side caching of `embedding(...)` results. The function caches Vertex AI roundtrips at its own layer; an application-side cache adds invalidation complexity for negligible win.
- A `Pooshit.Ocelot`-side abstraction for vector-similarity operators. Use `DB.VCos` directly; mamgo does.

---

## Risks & mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Vertex AI outage during a semantic-search request | Low | Request fails 500 | Same posture as PR #23's write path: the loud failure is the alert. Caller retries when Vertex AI is back. |
| Embedding column type mismatch (column is `real[]` not `vector(3072)`) blocks `DB.VCos` | Low | First semantic-search request fails with a Postgres cast error | The mamgo precedent already runs the `DB.Cast(..., CastType.Vector)` wrapper on both sides of `VCos`, which handles the implicit conversion. John verifies on the real Postgres in the production smoke check (§9). Falls under the same Decision-7 follow-up flagged in [`embeddings.md`](./embeddings.md). |
| `NodeMapper` ctor change ripples through tests | Low | Mechanical churn | The change is small and additive (overload with `NodeFilter` parameter, keep a null-default overload for tests). Tracked as part of the same PR. |
| A consumer accidentally sends `?query=` with empty string | Low | Wasted work / surprising 400 | `string.IsNullOrWhiteSpace(filter.Query)` is the gate, not `IsNullOrEmpty`. Empty-string queries are treated identically to absent ones (route to standard list). |
| Cosine on `gemini-embedding-001` is not as well-calibrated as expected for DiVoid's mixed-content corpus | Low | Top-N rankings are noisier than hoped | Returning the score lets callers self-tune. mamgo runs this model on much larger, less-curated corpora successfully. We adopt their posture (no default floor) and revisit only if signal-to-noise is observably bad. |

---

## Open questions (non-blocking)

These do not gate implementation; they are choices John can make from the code.

1. **`minSimilarity` default value.** Confirmed null (no floor). The mamgo equivalent has a sibling `Similarity` filter and treats missing as "no floor"; we mirror.
2. **Exposing the score field name as `similarity` vs `score` vs `match`.** Use `similarity` — matches the mamgo precedent and is the most precise term.
3. **Position of the `similarity` field in `NodeDetails`.** Append at the end; nullable; `[JsonIgnore]` when null so the standard list path's response shape is byte-identical to today.
4. **Wiring the existing `EmbeddingBackfillService` into the integration story.** Search depends on `Node.Embedding` being populated; the backfill CLI is the operational step that makes search useful on legacy data. Not a code change here, just a note for the rollout: run the CLI on the production Postgres before announcing the feature.

---

## Implementation guidance for John

Build in this order. Each step is independently reviewable.

1. **Add the `similarity` field to `NodeDetails`** as a nullable float, `[JsonIgnore]` when null. Add the `MinSimilarity` property to `NodeFilter` as `float?`. Do **not** add a new `Query`/`Search`/`Semantic` property — `ListFilter.Query` is the existing slot.
2. **Make `NodeMapper` filter-aware.** Constructor accepts `NodeFilter` (or null for the standard list path's default). `Mappings()` conditionally yields the `similarity` field-mapping using the `1.0f - DB.Cast(DB.VCos(DB.Cast(<query-embedding>, Vector), DB.Cast(Node.Embedding, Vector)), Float).Single` shape per §3. The query-embedding subexpression is `DB.Cast(DB.CustomFunction("embedding", DB.Constant(TextContentTypePredicate.EmbeddingModel), DB.Constant(filter.Query)), CastType.Vector)`. Keep `new NodeMapper()` (no-arg) working as a thin overload that passes `null`.
3. **Modify `NodeService.ListPaged`** to:
   - At entry, when `!string.IsNullOrWhiteSpace(filter.Query)`, check `embeddingCapability.IsEnabled`. If false, throw a parameter-validation exception that surfaces as 400/`badparameter` (§8).
   - When `Query` is empty, behave exactly as today (the rest of this list is gated on `Query`).
   - When `Query` is present and the capability is enabled:
     - Construct `NodeMapper` with the filter.
     - Append `n.Embedding != null` to the predicate.
     - Append `similarity >= filter.MinSimilarity` to the predicate when `MinSimilarity.HasValue`.
     - Append `OrderBy(mapper["similarity"].Field, descending: true)` and `OrderBy(mapper["id"].Field, descending: false)` to the operation, **replacing** any sort `ApplyFilter` may have added.
4. **Reject `MinSimilarity` without `Query`** (§4) with `code=badparameter`.
5. **Apply the same similarity treatment to `ListPagedByPath`'s terminal.** The terminal `LoadOperation<Node>` produced by `ComposeHops` receives the same similarity OrderBy, the same `Embedding IS NOT NULL` predicate, the same `MinSimilarity` floor, and is built against a filter-aware `NodeMapper` that yields the `similarity` field-mapping — identical to the plain-list path. The capability gate (§8) runs on `Query` regardless of `Path`. Whether you factor the shared work as one helper invoked by both paths or as one branch reachable from both code paths is your call; the semantic is one rule. Sort/descending overrides apply in path mode for the same reason as plain mode.
6. **Tests** per §9. Pay particular attention to the operation-composition assertions at the unit level — these are the only place the Postgres-only paths get verified in CI. Include the path + `Query` combination in the matrix.
7. **Production smoke** on a real Postgres before merging: upload a markdown node with distinctive content, `GET /api/nodes?query=<paraphrase>&count=5`, assert the node is rank 1 with similarity > 0.6.
8. **Update the API reference at graph node 8** to describe `GET /api/nodes` as a single composable surface: the standard filters always apply; `path` optionally restricts to a graph-path-defined set; `query` optionally ranks (any candidate set, with or without `path`) by similarity. Document the `query` parameter, the `minSimilarity` parameter, the `similarity` response field, and the silent override of `sort` / `descending` when `query` is present.
9. **Update the onboarding doc at graph node 9** with one paragraph and one example showing how an agent calls semantic search.

---

## Precedent: alignment with mamgo-backend

The mamgo `CampaignItemTargetMapper.cs` is the canonical read-side for the `embedding(...)` Postgres function across Toni's stack. This design adopts mamgo's choices wholesale: same model, same `DB.VCos` operator, same `DB.Cast(..., CastType.Vector)` cast strategy, same `1.0 - cosineDistance` exposed-as-similarity convention, same conditional-mapping pattern (the field-mapping is yielded only when the search field is set), same no-default-threshold posture, same `Similarity` floor field. The only mamgo-specific concept we deliberately do not adopt is `RecommendedApplicant` (vector-to-vector matching by referenced entity id) — DiVoid has no equivalent use case, and adding it now would inflate the v1 surface for no current caller.

When future search-side features land (ANN index, hybrid retrieval, cross-entity matching), the mamgo equivalents will be the first reference for shape and posture.
