# Architectural Document: Embeddings v2 — Name-Inclusive Composition, Name-Only Fallback, Regen on Name PATCH

Resolves DiVoid task **#437**. Extends and partially supersedes the v1 design at `docs/architecture/embeddings.md` (task #180). Search-side semantics (#183) are untouched by this design — the search query path keeps using the same model and the same `DB.CustomFunction("embedding", …)` call shape, only the indexed value changes.

---

## 1. Problem Statement

The v1 embedding pipeline embeds **content only**, on **content writes only**. Two consequences shape the user experience:

1. **Group / project / person / session-log-stub nodes have no semantic surface.** Any node whose only meaningful field is its `Name` is invisible to semantic search — the `WHERE n.Embedding IS NOT NULL` predicate at `Backend/Services/Nodes/NodeService.cs:409` filters them out. For a node graph in which names like "Hivemind Protocol", "Embeddings v2", "Pooshit" are the primary discriminator, this is a sharp loss of recall.
2. **Names are silent signal.** Even for content-bearing nodes, the name is often the densest, most user-curated descriptor. Embedding only the content discards it.

Embeddings v2 brings the **name into the indexed signal** and ensures **every node** carries an embedding whenever it has any text surface at all (name OR content). It also closes the silent-staleness gap on `PATCH /name` by triggering regeneration.

**Success criteria.**

- Every node with a non-empty name has an embedding after deploy. Steady-state `Embedding IS NULL` happens only when *both* name and content are empty/non-text.
- The indexed value derives from `name + content` when content is text; `name` alone otherwise.
- `PATCH /api/nodes/{id}` `replace`/`add`/`remove` on `/name` regenerates the embedding inside the same write.
- `POST /api/nodes/{id}/content` continues to regenerate; the composition now includes the *current* name.
- `POST /api/nodes` (create) writes a name-only embedding when the new row has a name.
- Search recall improves on group/project/person nodes (manual smoke). No regression on content-bearing nodes (the v1 signal is a subset of v2).
- Tests run on SQLite as before; the gating remains the singleton capability flag.

## 2. Scope & Non-Scope

**In scope.**

- Composition rules: what text feeds `embedding(model, ?)` in each shape.
- Trigger surface: every code path that must write/overwrite `Node.Embedding`.
- Empty/non-text/null-content semantics; the surviving meaning of `Embedding IS NULL`.
- The `embed` custom JSON-Patch op's semantics under v2.
- Migration: backfill strategy for existing rows whose embeddings reflect v1's content-only shape.
- Test strategy that fits the existing SQLite-bound fixture.
- Doc-comment / CLAUDE.md drift cleanup.

**Out of scope.**

- Changing the model (`gemini-embedding-001` stays).
- Changing the similarity operator (cosine, via `DB.VCos`, stays — see `NodeMapper.cs:71-78`).
- Search-side changes (#183 is done, post-PR-#67).
- Reintroducing async/queued embedding generation.
- pgvector column-type migration (still owned by search-side follow-up per v1 Decision 7).
- Token-budget-aware truncation beyond a single coarse cap (see Decision 1).
- Suppressing redundant re-embeds when text bytes are unchanged (deferred; cf. v1 §13.4).

## 3. Assumptions & Constraints

- Postgres is the only production database; SQLite remains the test fixture (`DatabaseFixture`). The capability flag at `Backend/Services/Embeddings/EmbeddingCapability.cs` already encodes this and is the only branch point.
- `gemini-embedding-001` accepts an input token budget on the order of ~2048 tokens (Vertex AI documents this for the text-embedding family; the exact ceiling has historically shifted by minor version, so the design treats the limit as a soft heuristic rather than a hard contract). Average DiVoid content fits comfortably; the long-tail is a multi-thousand-token markdown blob.
- The Pooshit.Ocelot primitive for SQL-side string concatenation is `DB.Concat(new object[] { … })` — confirmed in `mamgo-backend` at `clickservice/Services/Mappers/ClickParameterMapper.cs:33-37` and `services/Models/Campaigns/Targets/CampaignItemTargetMapper.cs:191`. Two-argument fixed forms exist via the same `object[]` overload; the design uses this.
- `DB.CustomFunction("embedding", DB.Constant(model), DB.Constant(text)).Type<float[]>()` is the primitive used in three existing call sites (`NodeService.cs:569`, `EmbeddingBackfillService.cs:79`, `DatabasePatchExtensions.cs:75`). The model identifier lives at `TextContentTypePredicate.EmbeddingModel` for the auto-flow; the PATCH op still hardcodes the literal in-line.
- `Node.Name` is already `[AllowPatch]` (verified at `Backend/Models/Nodes/Node.cs:29-31`). No prereq schema change.
- `INodeService.Patch` is currently `Task<NodeDetails> Patch(long nodeId, params PatchOperation[] patches)` — **no `CancellationToken`**. The v2 work threads one through. The existing controller path can supply `HttpContext.RequestAborted` exactly as the content controller already does.
- v1 design rationale still holds for: sync vs async (Decision 4), failure semantics (Decision 5), DI shape (Decision 9). v2 preserves all three.
- The DiVoid graph is small (~200 nodes today; "low thousands" projected). All cost/throughput trade-offs are calibrated against that scale, not against a public-API workload.
- The Postgres `embedding(...)` server-side function is configured to dispatch to Vertex AI synchronously, returning a `float[3072]` matching `Node.Embedding`'s `[Size(3072)]`. Confirmed by the v1 design's Decision 7 and the production deploy that powers #183.

## 4. Architectural Overview

v2 keeps v1's structural shape (two-UPDATE sequence inside one transaction, capability-gated, no try/catch on the embedding write) and replaces v1's **input expression** plus **trigger set**. Every embedding write — auto on content, regen on name PATCH, regen on create-with-name, backfill — funnels through one logical operation:

> *"For node id, set Embedding to `embedding(model, compose(currentName, currentContent, currentContentType))` inside the active transaction, evaluated server-side, no .NET-side text round-trip if avoidable."*

The composition is one function (§5 "Composition policy"). The trigger surface is enumerated in §6. The PATCH path gets a new hook that fires when, and only when, the patch list touches `/name`. The CLI backfill verb (`backfill-embeddings`) sweeps **all** rows on the Postgres path, not only `Embedding IS NULL`, because v1 embeddings are now considered stale by composition (see §11 Decision 4).

```
                          +---------------------------+
                          |  Composition policy       |
                          |  (single source of truth) |
                          |                           |
                          |  IN:  name, content?,     |
                          |       contentType?        |
                          |  OUT: text? to embed      |
                          |       OR null (skip+null) |
                          +-------------+-------------+
                                        |
       +---------------+----------------+----------------+----------------+
       |               |                |                |                |
       v               v                v                v                v
  POST /content   POST /nodes      PATCH /name      `embed` PATCH op   CLI backfill
  (UploadContent) (CreateNode)     (Patch)          (DatabasePatch     (EmbeddingBackfill
                                                     Extensions)        Service)
       |               |                |                |                |
       +-------+-------+-------+--------+----------------+----------------+
               |               |
               v               v
       (all paths emit one logical UPDATE Embedding = embedding(model, compose(...)))
                       |
                       v
              Postgres `embedding()`
                       |
                       v
                  Vertex AI Gemini
```

Path-specific wiring (sync inside the same transaction, no try/catch, capability-gated) is detailed in §6.

## 5. Components & Responsibilities

### 5.1 Composition policy (new helper, static)

**Lives at.** A new static class beside the existing predicate, e.g. `Backend/Services/Embeddings/EmbeddingInputComposer.cs`. Pure function. No DI dependencies.

**Owns.** The single decision: "given (name, contentBlob, contentType), what string (if any) should be embedded?" Plus the literal separator and the length cap.

**Does not own.** Issuing SQL. Reading rows. Knowing about Postgres vs SQLite. Knowing about the model name.

**Interface (prose).** One classifier method: takes name, content bytes (nullable), content type (nullable). Returns either a string (the composed text) or a sentinel meaning "no embeddable text — write null." For .NET-side composition (`UploadContent`, where the just-uploaded blob is already in memory). The same policy is reflected in the SQL-side composition expression (see §5.2) used where the composer cannot be invoked because the text is server-resident.

**Composition rules (the design call).**

| Inputs | Output |
|---|---|
| name non-empty, content text and non-empty | `name + "\n\n" + contentText` (with cap, see below) |
| name non-empty, content non-text or empty | `name` |
| name empty/whitespace, content text and non-empty | `contentText` (with cap) |
| name empty/whitespace, content non-text or empty | sentinel "no embeddable text" → write `null` |

**Separator.** `"\n\n"` — double newline. Justification: markdown-natural paragraph boundary, neutral to the model (Gemini family is trained on prose), unlikely to collide with the name itself, and visibly delimits "title" from "body" if a human ever inspects the embedded text. Single space is rejected because names containing punctuation that ends a sentence (`?`, `.`) would run-on into the body; `". "` is rejected for the same fragility; markdown heading prefixes (`# {name}\n\n`) are rejected because they couple the embedding to a syntactic convention the corpus does not uniformly follow.

**No name duplication.** The "weight the title by repeating it" pattern is rejected: modern embedding models attend to position implicitly; on Gemini's small input budgets, doubling the name burns tokens for marginal weight gain and biases distance metrics in ways that depend on name length. One occurrence at the top is sufficient signal.

**No metadata prefix.** `ContentType`, node type, status are deliberately excluded. Reasons: (a) they are structural, not semantic — a node "type=task" tells the model nothing about the task's subject; (b) including them couples the embedding's invalidation triggers to those fields too, which compounds the regen surface; (c) v1 made the same call (v1 Decision 2) and it stands.

**Length cap.** Compose first, then if the resulting string length exceeds **8000 characters**, truncate the **content** portion (preserving the name + separator prefix) before passing to the model. 8000 chars is a sane heuristic for the ~2k-token Gemini embedding budget (≈4 chars/token on average English+German prose; markdown tightens the ratio further, leaving headroom). The cap is a heuristic; document it in code and treat its value as a tunable constant rather than a contract. **Where the cap is applied:** in the .NET-side composer for the paths where the composer runs in process (`UploadContent`, `EmbeddingBackfillService`). For the pure-SQL paths (`Patch` on `/name`, `CreateNode`, see §5.2) the cap is applied server-side via a `LEFT(text, 8000)` or equivalent Ocelot-expressible truncation primitive; if Pooshit.Ocelot cannot express the truncation cleanly server-side, the fallback is read-name-and-content-first, compose in .NET, then issue the UPDATE — same approach v1 already takes for `UploadContent`. The implementer picks based on what `DB.Function("left", …)` or `DB.Substring(…)` produces; the architecture allows either, both correct.

### 5.2 SQL-side composition expression

For paths where the bytes are already in the database row (a `PATCH /name`, a create where content is absent), reading the content blob into .NET only to recompose and write it back is wasteful. The architecture **prefers** an in-database composition wherever Pooshit.Ocelot allows it:

`UPDATE Node SET Embedding = embedding(model, compose(Name, Content, ContentType)) WHERE Id = ?`

where `compose(...)` is built server-side from:

- `DB.Concat(new object[] { Name, "\n\n", convert_from(Content, 'UTF8') })` when text — Postgres has `convert_from(bytea, encoding)` to decode the blob; reachable from Ocelot via `DB.CustomFunction("convert_from", …)`.
- A `CASE WHEN ContentType LIKE 'text/%' OR ContentType IN (allowlist) AND Content IS NOT NULL THEN [concat] ELSE Name END` to pick between name+content and name-alone. Ocelot expresses this through `DB.If(predicate, thenExpr, elseExpr)` (used in `CampaignItemTargetMapper.cs:191`).
- A length cap via Postgres `LEFT(text, 8000)` or `SUBSTRING(text FROM 1 FOR 8000)` — `DB.CustomFunction("left", expr, 8000)` should work.

If the SQL-side expression turns out to be intractable to compose through Ocelot's expression tree (length-cap behaviour around the `LEFT()` call, `convert_from`'s parameter order, `DB.If` interaction with the `Type<float[]>()` cast), fall back to a per-path **read-then-compose-then-write** pattern: SELECT the row's `Name`, `Content`, `ContentType` → compose in .NET via §5.1 → UPDATE with the composed text bound as a `DB.Constant`. This fallback is acceptable per v1 precedent (`UploadContent` already does in-memory composition; performance difference is negligible at DiVoid's scale).

**Decision (default):** prefer SQL-side composition where it is cleanly expressible (`PATCH /name` benefits the most — it avoids fetching potentially-large `Content` blobs across the wire for no reason). For `UploadContent` keep .NET-side composition because the blob is already in memory by the time the embedding step runs.

### 5.3 NodeService.UploadContent (modified)

**Owns.** Same transaction shape as v1. The composition input now includes the current `Name`. Two sub-shapes:

- Text content: read the current `Name` (single column SELECT inside the same transaction), call `Composer.Compose(name, contentBytes, contentType)`. If composer returns a non-null string, issue the embedding UPDATE with that string as `DB.Constant`. If composer returns "no embeddable text" (impossible when content is text and non-empty; only happens when content+name are both empty, which the controller-layer should reject anyway), write `null`.
- Non-text or empty content: **v2 change**: do NOT null out the embedding. Read the `Name` and embed it. If the name is also empty, write `null`. This replaces the v1 "non-text → clear" branch (v1 §11 Decision 8 second half).

**Does not own.** The composition rules (delegated to §5.1). The SQL primitive shape (unchanged from v1).

**Reuse the existing transaction.** The name SELECT and the embedding UPDATE both run inside the existing `Transaction transaction` opened in `UploadContent`. The transaction commits atomically with the content write.

**Reads the current name vs the just-updated name.** After the content UPDATE has run, the row still has its pre-existing name (content writes do not touch `Name`). Reading the name post-content-update inside the same transaction returns the **current** committed-or-pending name (transactional consistency). This is the desired behaviour.

### 5.4 NodeService.CreateNode (modified)

**v1 behaviour.** Inserts a row with name + type + status + x/y, never touches `Embedding`. Result: every newly-created node starts with `Embedding = null` and gains an embedding only on a subsequent `POST /content`.

**v2 behaviour.** After the row is inserted and (optionally) repositioned, if `embeddingCapability.IsEnabled` and the inserted name is non-empty, fire a name-only embedding write inside the same transaction. Composition is `name` alone (no content yet). Sequence:

1. Insert row (existing).
2. Insert links and apply auto-position (existing).
3. If capability enabled and name non-empty → `UPDATE Node SET Embedding = embedding(model, name) WHERE Id = newId` — server-side, no .NET-side string round-trip needed since the name is already a constant in the create payload. Use `DB.Constant(node.Name)` for clarity, or read it back server-side via `DB.Property<Node>(n => n.Name, "node")` — either works.
4. Commit (existing).

No try/catch on the embedding step. Failures roll back the create. This couples create availability to Vertex AI availability, matching v1's content-write coupling.

**Trade-off accepted.** Create now pays one extra Vertex AI roundtrip. ~200–800 ms added per create. For a low-QPS workspace, acceptable. Mitigation if it ever bites: section §13.5.

### 5.5 NodeService.Patch (modified — name-PATCH hook)

**Owns.** Detect whether a patch list touches `/name` (case-insensitive). If yes and `embeddingCapability.IsEnabled`, after the patch UPDATE succeeds and inside the same transaction, fire a re-embedding write whose input is the composition of the **new** name (just written by the patch) and the **current** content. SQL-side composition (§5.2) is preferred here because the content blob is typically the largest column and we want to avoid pulling it across the wire.

**Does not own.** The classification of "is this patch relevant to embedding." That's a small helper, e.g. `static bool TouchesEmbedding(PatchOperation[] ops)` — returns true if any op's `Path` resolves (case-insensitive) to `/name` and its `Op` is `replace`/`add`/`remove`. `flag`/`unflag` on a string are nonsensical and don't apply. The `embed` op is handled separately (§5.6 + §11 Decision 5).

**Transaction.** Patch currently has no explicit transaction (line 533-541, a single UPDATE). v2 adds one: open a transaction, run the patch UPDATE, if name was touched run the embedding UPDATE, commit. If either UPDATE fails, both roll back. This matches the content-write transactional shape.

**Cancellation.** Plumb a `CancellationToken` through `INodeService.Patch` from the controller. The CT is passed to both `ExecuteAsync(transaction, ct)` calls. The controller already has `CancellationToken ct` available — see how `UploadContent` already plumbs `HttpContext.RequestAborted` (the controller method needs the parameter declared; ASP.NET Core binds it automatically).

**Multiple ops in one patch list.** If the patch list includes both `/name` and `/status`, the embedding fires once at the end (deduped — see the helper above). If the patch list includes `/name` and `/x`, same. The embedding write is keyed on "did any op touch /name", not the count.

### 5.6 DatabasePatchExtensions — the custom `embed` op (deprecated)

See §11 Decision 5. Architecture: the op stays in code but is documented as deprecated. The `compose` policy is now the canonical path; callers wishing to force a re-embedding should patch `/name` to its current value (a no-op write that still triggers regen) or, preferably, hit the CLI backfill verb. No production code calls `embed` today; it is removable in a follow-up if desired.

### 5.7 EmbeddingBackfillService (modified)

**v1 predicate.** "rows where Embedding IS NULL AND Content IS NOT NULL AND ContentType is text".

**v2 predicate.** "all rows where the composition would produce a non-null embeddable string" — i.e. `Name IS NOT NULL AND Name != ''` **OR** `(Content IS NOT NULL AND ContentType matches text predicate)`. The `Embedding IS NULL` filter is dropped (see §11 Decision 4). v1-embedded rows are recomputed; new no-name-no-content rows are skipped.

**Per-row write.** Same shape as the live path: prefer SQL-side composition; fall back to read-compose-write if the SQL form is intractable. Each row in its own short transaction so a transient Vertex AI hiccup does not abort the whole sweep.

**Idempotency.** Without a version marker, the predicate above is *not* idempotent — every run re-embeds every qualifying row. For DiVoid's ~200-node scale, that costs ~$0.05–$0.15 and ~1–3 minutes. For "low thousands" it scales linearly. See §11 Decision 6 for whether to introduce an `EmbeddingVersion` column.

**SQLite path unchanged.** Capability check at the top; if disabled, log and exit.

### 5.8 NodeMapper / similarity filter (unchanged at the architecture level)

The semantic-search predicate at `NodeService.cs:409` (`operation.Where(n => n.Embedding != null)`) becomes vestigial in steady state. v2 keeps it as a safety net (§11 Decision 7) and logs a debug line if a row with non-empty name/content is encountered without an embedding (i.e. backfill never reached it). No DTO or contract change.

## 6. Interactions & Data Flow (Trigger Surface)

Every code path that must write or rewrite `Node.Embedding`:

| # | Trigger | Composition | Capability gating | Transaction shape | Cancellation |
|---|---|---|---|---|---|
| 1 | `POST /api/nodes` (CreateNode) | name alone (no content yet) | yes — skip on SQLite | inside the existing create transaction, after auto-position | controller `RequestAborted` plumbed via §10.2 |
| 2 | `POST /api/nodes/{id}/content` (UploadContent) | name + content if text non-empty; name alone if non-text/empty; null only if both empty | yes — skip on SQLite | inside the existing UploadContent transaction | already plumbed |
| 3 | `PATCH /api/nodes/{id}` touching `/name` | new-name + current-content if text; new-name alone if not; null only if both empty | yes — skip on SQLite | new transaction wrapping the patch UPDATE + the embedding UPDATE | added in this work |
| 4 | `PATCH /api/nodes/{id}` with custom `embed` op | **deprecated**: writes whatever the `value` string is into the target `[AllowPatch] float[]` property. Unchanged from v1 (still calls the same SQL primitive). | yes — but the op is invokable only when capability is enabled (a SQLite caller already gets a SQL error today; that stays) | inherits ambient | inherits ambient |
| 5 | CLI `backfill-embeddings` | same as #2 logic, row by row | yes — exit immediately on SQLite | per-row short transactions | the CLI's `ct` token, defaults to `default` |

**Sequence (path #3 — PATCH /name on Postgres):**

1. Controller binds patch ops, calls `nodeService.Patch(id, ops, ct)`.
2. Service inspects ops via the `TouchesEmbedding(ops)` helper. Captures result as `bool nameTouched`.
3. Service opens a transaction.
4. Service issues the existing patch UPDATE. If 0 rows affected → throw `NotFoundException<Node>` (rolls back).
5. If `nameTouched && capability.IsEnabled`: issue the embedding UPDATE. Prefer the SQL-side composition expression (§5.2). The expression evaluates `Name`, `Content`, `ContentType` columns server-side, applies the cap, calls `embedding(...)`, stores into `Embedding`. No try/catch.
6. Commit.
7. Service returns the refreshed `NodeDetails` via the existing `GetNodeById(id)` call.

**Sequence (path #3 on SQLite):**

1–4 as above.
5. Capability disabled → skipped entirely. No call.
6. Commit.
7. Return.

**Sequence (path #1 — CreateNode on Postgres, name non-empty):**

1. Service opens transaction (existing).
2. Resolves/inserts NodeType (existing).
3. Inserts Node row, captures `newId` (existing).
4. Inserts links, applies auto-position (existing).
5. **v2 addition:** if `capability.IsEnabled && !string.IsNullOrWhiteSpace(node.Name)`, issue `UPDATE Node SET Embedding = embedding(model, LEFT(<name>, 8000)) WHERE Id = newId`. The name is short enough to pass as `DB.Constant(node.Name)` without server-side composition gymnastics. The `LEFT` cap protects against pathological names but is essentially a no-op for normal usage.
6. Commit (existing).
7. Return refreshed DTO (existing).

**Sequence (path #1 on SQLite or empty name):**

1–4 as above.
5. Skip.
6. Commit.
7. Return.

## 7. Data Model (Conceptual)

No schema change. `Node.Embedding` keeps `float[] [Size(3072)]`. The interpretation of "non-null" shifts: v2 expects non-null whenever the node has a name OR text content; v1 expected non-null only when the node had text content.

**Optional follow-up column (deferred — see §11 Decision 6).** A `byte EmbeddingVersion` column would let backfill skip rows already at v2 and let future migrations skip selectively. Architecture allows but does not require it; the implementer can defer until a third version forces the issue.

No new entities, links, or relationships.

## 8. Contracts & Interfaces (Abstract)

### 8.1 Composition policy

**Purpose.** Single source of truth for "given a node's name, content bytes, and content type, what string (if any) goes to the embedding function?"

**Inputs.** `name : string?`, `content : byte[]?`, `contentType : string?`.

**Output.** Either a non-empty string (the text to embed, already capped to ≤8000 chars), or a sentinel indicating "no embeddable text — caller should write `null`."

**Invariants.**

- Deterministic. Same inputs → same output. No clock, no randomness, no environment.
- Pure. No SQL. No I/O.
- Decoding. If content is text and non-empty, decode as UTF-8 (matches v1 Decision 2). Mojibake on non-UTF-8 bytes is acceptable.
- Cap. The output never exceeds the configured length cap. The cap truncates the **content** portion only; the name and separator are always preserved (if the name alone exceeds the cap, the name is truncated — but a 8000-char name is a pathological input that does not occur in practice).

### 8.2 SQL-side composition expression

**Purpose.** Express the same composition policy as a server-side expression that can be the right-hand side of `Update<Node>().Set(n => n.Embedding == DB.CustomFunction("embedding", DB.Constant(model), <expr>).Type<float[]>())`.

**Inputs.** Column references: `Name`, `Content`, `ContentType` (via Ocelot's `DB.Property<Node>(n => n.X, "node")`).

**Output.** A scalar `text`-typed expression suitable for the second argument to `embedding(...)`. Postgres's `embedding()` function tolerates a NULL second argument by returning NULL (verified by the v1 design's reliance on `DB.Constant(text)` where text was always a real string — but the function accepts `text NULL` and produces `vector NULL`; the design must confirm this against the production embedding function, see §13.2). If the composition resolves to NULL/empty, the surrounding UPDATE should resolve to writing `null` to the column.

**Invariants.**

- Matches §8.1's decision table.
- Cap applied via Postgres `LEFT(…, 8000)` (or equivalent).
- Text decoding via `convert_from(Content, 'UTF8')`.
- Text-content predicate inlined as a `CASE WHEN ContentType LIKE 'text/%' OR ContentType IN (allowlist) AND Content IS NOT NULL THEN ... ELSE Name END`. The allowlist is `TextContentTypePredicate.ApplicationTextTypes` and must reference that constant exactly to keep parity with §8.1.

### 8.3 PATCH-name-detection helper

**Purpose.** Given a `PatchOperation[]`, return whether at least one op writes `/name`.

**Inputs.** `PatchOperation[]`.

**Output.** `bool`.

**Invariants.**

- Case-insensitive on `Path`. Matches the existing convention in `DatabasePatchExtensions.cs:42` (`propertyname = patch.Path[1..].ToLower()`).
- Considers `Op ∈ {"replace", "add", "remove"}` as relevant. (`add`/`remove` on a string column have current operational semantics via the patch dispatcher — see lines 62-67 of `DatabasePatchExtensions.cs` — and any of them can change the stored name.)
- The `embed` op on `/name` does NOT count as a name change (it writes to whatever column is targeted, treating the value as text-to-embed — not as a new name).

### 8.4 INodeService.Patch — new signature

**Current.** `Task<NodeDetails> Patch(long nodeId, params PatchOperation[] patches)`.

**v2.** `Task<NodeDetails> Patch(long nodeId, PatchOperation[] patches, CancellationToken ct)`.

The `params` modifier is dropped; the controller passes an array directly. Any internal callers (none today outside the controller) update accordingly. The controller's `Patch` action declares `CancellationToken ct` and forwards it.

## 9. Cross-Cutting Concerns

**Logging.** Same posture as v1 — silent on success at info level; failures propagate. Add one startup line that announces "embedding v2 composition enabled" when capability is on, so operators know what version of the input shape is in effect (helps future debugging when v3 lands). Add one debug log in the search-time path if a node row with non-empty name/content is encountered without an embedding (this indicates backfill missed something or a write path is bypassing the capability check — useful signal, low volume).

**Auth.** Unchanged. `CreateNode`, `UploadContent`, `Patch` are all `[Authorize(Policy = "write")]`. The embedding writes happen inside the service, beneath the controller authorisation boundary. No new principal or permission.

**Transactions and consistency.** Every embedding write joins an existing or newly-opened transaction. There is no window where the row's `Name` or `Content` is committed without a matching `Embedding`. A Vertex AI failure rolls back the user's write. This is identical to v1's posture and is the only mechanism that prevents v1's "phantom stale-vector" race (v1 Decision 5).

**Cancellation.** `INodeService.Patch` gains a `CancellationToken` parameter (§8.4). The token is plumbed to all `ExecuteAsync(transaction, ct)` calls inside the patch path. `CreateNode` and `UploadContent` are already partly CT-aware; v2 ensures the embedding UPDATE also receives it. The token's source is `HttpContext.RequestAborted` — already bound by ASP.NET Core when declared as a controller parameter.

**Idempotency.** Re-uploading identical content with an unchanged name yields an identical embedding (Vertex AI is deterministic for fixed model+input). Re-patching `/name` to the same value still triggers a regen UPDATE — wasteful but harmless. A future optimisation (§13.5) could suppress this.

**Retries.** None at the service layer. Backfill is the deliberate re-run mechanism. A failed write returns 500 to the caller; the caller retries.

**Caching.** None. Vertex AI's own pricing/caching is the only layer.

**Concurrency.** Two concurrent name patches to the same id serialize through Postgres row-level locking on `UPDATE Node WHERE Id = ?`. Two concurrent operations where one is a name patch and the other is a content write: the second-to-commit wins for both `Name`/`Content` and `Embedding`. Tearing is impossible because the embedding write is in the same transaction as the field write — the `(field, embedding)` pair commits atomically. A pathological interleave where two transactions read different "current" names mid-flight is prevented by the row lock both UPDATEs acquire.

**Concurrency caveat: SQL-side composition reads the post-UPDATE row state.** When the v2 PATCH path issues `UPDATE … SET Name = <new>; UPDATE … SET Embedding = embedding(model, compose(Name, …))` as two statements in one transaction, the second statement sees the row at its current transaction snapshot — which, post-first-UPDATE-inside-the-same-transaction, reflects the new name. Postgres MVCC semantics make this safe. (If implemented as a single UPDATE that touches Name and Embedding atomically, even simpler — but Ocelot's expression tree may not compose name patch + embedding regen in a single UPDATE; two sequential UPDATEs in one transaction is the safe default.)

## 10. Quality Attributes & Trade-offs

**Latency.**

- Create: now pays one Vertex AI roundtrip (was zero). +~500ms p99.
- Content upload: unchanged in shape; the composition adds zero observable latency over v1.
- Name patch: now pays one Vertex AI roundtrip on `/name` patches (was zero). +~500ms p99. Patches that don't touch `/name` are unchanged.

Trade-off accepted. DiVoid's low-QPS, agent-interactive workload absorbs this. If create-on-bulk-import ever becomes a thing, §13.5 covers the escape valve.

**Throughput.** Vertex AI quota was non-binding under v1 and remains non-binding under v2 at DiVoid's scale.

**Availability.** Coupling to Vertex AI now extends to `POST /api/nodes` and `PATCH /api/nodes/{id}` (when name is touched). Vertex AI outages now cause `create` and `name patch` to return 500. This is the same posture as v1 for content writes, applied consistently. Rationale: strict consistency is more valuable than partial availability for an agent-curated graph; a silent-half-state row (name renamed, embedding stale) actively misleads search.

**Maintainability.**

- One new component (the composer, ~30 lines).
- One small helper (TouchesEmbedding, ~5 lines).
- Three call sites modified (`CreateNode`, `UploadContent`, `Patch`).
- One service modified (`EmbeddingBackfillService`'s predicate).
- Doc-comment fixes (`NodeController.cs:122-126`).
- CLAUDE.md sentence about `embed` updated (if Decision 5 is accepted as "deprecated, leave in place"; otherwise also removed).

Trade-offs:

1. **Two composition expressions** (one .NET, one SQL). Risk: they drift. Mitigation: both reference the same `TextContentTypePredicate.ApplicationTextTypes` constant and the same `EmbeddingModel`; both have unit tests covering the same matrix; the SQL form's tests are integration-level on Postgres (smoke tests, not CI-bound). Acceptable.

2. **PATCH path gains a transaction.** v1 patch was a single UPDATE with no explicit transaction. v2 wraps in a transaction. Minor overhead (one Postgres BEGIN/COMMIT roundtrip) — negligible.

3. **Create gains a second UPDATE.** Going from "one INSERT + optional auto-position UPDATE" to "+1 embedding UPDATE" in the create path. The transaction is already there; the cost is one extra statement.

4. **Tying create availability to Vertex AI availability.** New coupling. Worth it for the consistency argument; revisitable if creates become bursty.

5. **The `embed` PATCH op becomes dead weight.** Could remove it. Decision 5 leaves it in for one cycle in case any external caller (CLI script, ad-hoc agent) uses it; if no usage surfaces, the next PR can delete.

## 11. Decisions (resolving task #437 open questions)

### Decision 1: Composition shape and cap

**Choice.** `name + "\n\n" + content` when content is text and non-empty; `name` alone otherwise; `null` only when both are empty/non-text. Length cap **8000 characters** applied to the **content portion** post-concatenation. No metadata prefix. No name duplication.

**Rationale.** See §5.1. Briefly: `\n\n` is the most prose-natural separator with no name-collision footgun; no metadata because it pollutes invalidation triggers; no duplication because Gemini does not reward it at this input size. 8000 chars is a heuristic for ~2k-token budgets and a tunable constant, not a contract.

**Trade-off.** v1-embedded long-document nodes that exceeded the (lower or higher) v1 implicit cap will re-embed under the v2 cap — meaning the v2 embedding for a long document may differ from v1's even at the content portion. Acceptable because v2 is a deliberate corpus re-baseline.

### Decision 2: Trigger surface

**Choice.** Five paths trigger an embedding write (§6 table):

1. `POST /api/nodes` (Create) — name-only embedding if name is non-empty.
2. `POST /api/nodes/{id}/content` (UploadContent) — composed embedding per the rules.
3. `PATCH /api/nodes/{id}` touching `/name` — composed embedding using the new name + current content.
4. `embed` custom PATCH op — unchanged, deprecated (Decision 5).
5. CLI `backfill-embeddings` — sweep all rows on Postgres (Decision 4).

**Sync, inside the same transaction, in every path.** Matches v1 Decision 4's sync-rationale, applied uniformly.

### Decision 3: Non-text / empty / null content semantics

**Choice.**

| Name | Content | ContentType | Embedding written |
|---|---|---|---|
| non-empty | non-empty | text/* or allowlist | `embedding(model, name + "\n\n" + content)` |
| non-empty | non-empty | non-text | `embedding(model, name)` |
| non-empty | empty/null | (any) | `embedding(model, name)` |
| empty | non-empty | text/* or allowlist | `embedding(model, content)` |
| empty | non-empty | non-text | `null` |
| empty | empty/null | (any) | `null` |

`Embedding IS NULL` survives only when **both** name and content are empty/non-text. This is rare in practice but possible (a placeholder node created mid-edit). Steady-state `WHERE Embedding IS NOT NULL` at `NodeService.cs:409` filters only those nodes out.

### Decision 4: Backfill strategy

**Choice.** **Blanket re-embed** of every qualifying row. The predicate is no longer "Embedding IS NULL" — v1 embeddings are stale by composition. The predicate is now "name non-empty OR (content non-null AND content-type is text)".

**Idempotency.** Without a version column, every run re-embeds. At ~200 rows × ~500 ms each ≈ 2 minutes; cost ≪ $1. At "low thousands" the runtime is on the order of 10–20 minutes one-time. Acceptable, and the CLI is run manually.

**Rollback story.** If v2 ships and we want to roll back to v1: the same backfill verb with a v1-shaped composer recomputes v1 embeddings. The mechanism is symmetric; only the composer changes. Rolling back requires re-running the CLI against the same Postgres. Document this in the CLI's help text.

**Rejected:** introducing an `EmbeddingVersion` column now. Cleaner long-term but adds a schema migration to a doc-only PR's scope. Deferred to §13 as an optional follow-up — first time we have a third version, we add the column then.

### Decision 5: The `embed` PATCH op

**Choice.** **Option C (deprecate, leave in place).** Mark the op as deprecated in code comments and in the user-facing PatchOperation docs. Production code does not invoke it; no consumer relies on it. Leave it functional so any external caller (CLI agent, ad-hoc script) does not hard-break.

**Rationale.**

- A: re-embed with new composition, ignore `value` → confusing, requires the op to look up the node row to compose against, conflates two semantically-different operations.
- B: embed exactly `value` → today's behaviour, but the auto-flow now covers every reasonable case, and the op's escape-hatch nature has no production use.
- C: deprecate → preserves backward compat, signposts the canonical path (use name/content writes, not bespoke patches), allows clean removal in a follow-up once nothing references it.

**Action.**

- Add `[Obsolete]` or doc-comment `<remarks>deprecated…</remarks>` to the `case "embed":` branch.
- Update CLAUDE.md to remove the explicit "plus a custom `embed` op" line, or annotate it as deprecated.
- A future PR (out of scope here) deletes the op once one release cycle confirms no caller relies on it.

### Decision 6: PATCH-name plumbing — where exactly the regen call lives

**Choice.** **Inside `NodeService.Patch`**, not inside `DatabasePatchExtensions.Patch`.

**Rationale.** `DatabasePatchExtensions.Patch` is a generic Pooshit-Ocelot extension that knows nothing about `Node`, embeddings, or capability. Adding embedding logic there:

- Couples a generic patch primitive to a node-specific concern.
- Forces all patchable entities (Users? ApiKeys? Future entities?) to either inherit or excuse the embedding hook.
- Spreads the trigger surface across two files instead of one.

`NodeService.Patch` is the natural seam: it already knows about the `Node` entity, already injects `IEmbeddingCapability`, and is the single call site the controller invokes. The detection helper (`TouchesEmbedding`) is a private static method on the service or a sibling internal class. The service:

1. Computes `nameTouched` from the patch list.
2. Opens transaction.
3. Calls the generic `database.Update<Node>().Patch(patches).Where(…).ExecuteAsync(tx, ct)`.
4. If `nameTouched && capability.IsEnabled`, fires the embedding UPDATE (SQL-side composition preferred).
5. Commits.

**Read-then-update vs single combined UPDATE.** Pursuing a single combined UPDATE (`SET Name = <new>, Embedding = embedding(model, compose(<new>, Content, ContentType))`) would be tighter but requires either patching the patch dispatcher to accept "compute Embedding alongside" or constructing the UPDATE outside the dispatcher. The architecture **prefers two sequential UPDATEs in one transaction** because: (a) the patch dispatcher is generic and adding a "side effect column" hook to it is bad layering; (b) Postgres MVCC inside a transaction makes the second UPDATE see the first's effect, so correctness holds; (c) two UPDATEs in one transaction is one extra round-trip — negligible. If Pooshit.Ocelot can express the combined form cleanly, the implementer may prefer it; the architecture accepts either.

### Decision 7: The `WHERE n.Embedding IS NOT NULL` predicate

**Choice.** **Keep as a safety net.** Convert the silent skip into a debug log when the search side encounters a name-bearing node without an embedding (signals backfill missed something or a write path bypassed the capability check).

**Rationale.** In steady state the predicate filters nothing because every name-bearing or text-content-bearing node has an embedding. But:

- Transient state during deploy and before the first backfill run still produces null-embedding rows that should not surface in search.
- A future bug (forgetting to gate a new write path through the composer) could leave a row mis-embedded; the predicate prevents bad results.
- Removing the predicate would force the search query to either include null-embedding rows (returning garbage cosine values) or branch on it at runtime — net wash for cleanliness, net loss for safety.

The debug log addition is informational: "search hit an unexpectedly-unembedded row, id=N" — useful when investigating gaps.

### Decision 8: DI / SQL composition — no new abstraction

**Choice.** The composition policy is a static helper (`EmbeddingInputComposer`). The SQL-side composition is an inline expression in each call site (with a private helper method on `NodeService` to share the expression). **No `IEmbeddingComposer` interface.**

**Rationale.** Mirrors v1 Decision 9. There is no runtime polymorphism in composition — the policy is a single function. Adding an interface for testability is the wrong fix; the static helper is trivially testable with table-driven unit tests, no fixture needed. The SQL expression is a small Ocelot expression that lives at the call site (or factored into a helper on the service); wrapping it in a class would add types without saving lines.

### Decision 9: Test strategy

**Choice.** Same posture as v1 — most tests run on SQLite where the embedding step is skipped by the capability flag. Add four test cases (§12.6 below) that exercise the composer policy directly (unit, no DB) and the trigger surface (integration, on SQLite via the existing `WebApplicationFactory<Program>` fixture, asserting *that the right path was taken* rather than asserting on actual embedding bytes).

**Postgres-only assertions.** Tests that need to verify actual embedding bytes change after a name patch require a real Postgres + Vertex AI configuration. The existing `EmbeddingBackfillTests.cs` already separates SQLite tests from Postgres-needing tests via the `EnabledCapability`/`DisabledCapability` constants; v2 tests follow the same pattern. Postgres-bound assertions live in a manual smoke check, not in CI — same posture as v1 §11 Decision 10.

### Decision 10: Documentation drift

**Updates to ship with the implementation PR (not this doc PR):**

1. `Backend/Controllers/V1/NodeController.cs:122-126` — XML doc on `UploadContent` updated to: *"uploads data for a node. on Postgres: also (re)generates a vector embedding from the node's name plus the new content (or name alone if content is non-text/empty). on SQLite: content is written; embedding is not touched."*
2. `Backend/Controllers/V1/NodeController.cs` — XML doc on `Patch` annotated: *"on Postgres, a patch that touches `/name` also regenerates the node's embedding inside the same transaction (name + current content composition)."*
3. `Backend/Controllers/V1/NodeController.cs` — XML doc on `CreateNode` annotated: *"on Postgres, creates a name-only embedding for the new node if its name is non-empty."*
4. `INodeService` interface XML docs mirror the controller updates.
5. `CLAUDE.md` (project-level) — under "Filtering, paging, patching": update the `embed` op mention to flag it as deprecated; reference this v2 doc.
6. `CLAUDE.md` (project-level) — the description of `UploadContent`'s embedding behaviour, if any, updated.
7. `docs/architecture/embeddings.md` (the v1 doc) — add a header note saying "superseded in part by `docs/architecture/embeddings-v2.md`; v1 Decisions 2, 8 (clear-on-non-text), and 10 (test strategy) are revised in v2."

**No new code-contract section needed.** v2 is feature mechanics, not a style/discipline lesson. The existing Code Contracts #114 §3 (async-await drop) and §6 (services) cover the implementation discipline; cite them in review.

## 12. Risks & Mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | SQL-side composition expression not cleanly expressible in Pooshit.Ocelot | Medium | Falls back to read-compose-write (still correct, marginally slower for big content). | Architecture allows both; implementer picks per actual Ocelot behaviour. |
| 2 | `embedding(...)` function semantics on NULL second argument differ from expectation | Low | Either a crash on `embedding(model, NULL)` or a silently-wrong embedding | Verify against the production Postgres function at implementation time; if it doesn't tolerate NULL, route the null-write case through `Set(Embedding == null)` directly rather than letting the expression resolve to NULL. |
| 3 | 8000-char cap chosen too low and truncates meaningful content | Medium | Long-document nodes get truncated embeddings; search recall degrades on tails | Cap is a constant; tune empirically post-deploy. Document the cap. Consider raising or making config-driven if recall complaints surface. |
| 4 | Vertex AI outage now affects create/patch availability, not just content uploads | Low | Wider blast radius on Vertex AI outages | Accepted per Decision 4 / v1 Decision 5. Document in operator runbook. |
| 5 | Backfill blanket re-embed at "low thousands" scale takes 20+ minutes | Low | Operator inconvenience; cost ~$5–$10 | Run during low-traffic window. Backfill CLI logs progress every 25 rows already. |
| 6 | Composer and SQL-side composition expression drift | Medium | Inconsistent embeddings on different write paths | Both reference the same constants; cover both in unit tests; smoke test the live path against Postgres before merge. |
| 7 | `[AllowPatch]` evolution: a future PR adds `[AllowPatch]` to a field whose change should also invalidate the embedding (e.g. a new `Tags` field) and the team forgets | Low | New write path bypasses embedding regen | Treat any new `[AllowPatch]` on Node as a touchpoint requiring review of the trigger table in §6. Document this in the v2 doc and reference in code review. |
| 8 | Two concurrent name-patches race the embedding write | Low | Last writer wins for both name and embedding; no tearing because they share a transaction | No mitigation needed — MVCC + row lock + same-transaction co-commit. |

## 13. Open Questions (non-blocking)

1. **`embedding(model, NULL)` behaviour.** Confirm at implementation time. If the function errors on NULL, every code path that might emit a null composition must explicitly `Set(Embedding == null)` rather than letting the SQL expression resolve to NULL. This affects §5.2's `CASE WHEN … ELSE …` form: the ELSE branch may need to be a separate code path entirely. Recommended: John verifies in a one-line Postgres query against the dev instance before writing the §5.2 expression.
2. **Length cap value (8000 chars).** Picked as a heuristic. If post-deploy search shows long-tail content getting clipped meaningfully, tune. Worth a follow-up empirical pass once #183 has telemetry on result quality.
3. **`EmbeddingVersion` column.** Adding it now would make future migrations selective. Deferring until a v3 forces the issue, on the principle that one re-baselining backfill is cheap and a schema column added speculatively is bigger surface than warranted. Revisit if v3 lands.
4. **Suppress no-op name patches.** If a caller patches `/name` to the same value, the regen fires anyway. Cheap to detect (read old name in the SELECT, compare to new value), but adds complexity. Defer until measured cost matters.
5. **Bulk-create or bulk-patch endpoints?** If DiVoid ever introduces these, the per-row sync embedding write becomes a real latency hit. The escape valve is async via the existing `EmbeddingBackfillService` invoked post-bulk-write — the bulk endpoint can defer the embedding step and queue a backfill sweep. Not in scope today.
6. **Should the `embed` PATCH op be deleted in this PR rather than just deprecated?** Decision 5 picks deprecate-then-delete. The alternative (delete now) is one PR fewer but risks breaking an undiscovered consumer. Toni's call if he prefers the cleaner sweep.

## 14. Implementation Guidance for the Next Agent

Ordered build phases. Each phase is a self-contained unit that could ship and be valuable on its own; the work as a whole is one PR per task-#437 scope, *not* one PR per phase.

**Phase 0 — verify the SQL-side primitives.** Before writing the composition expression, John runs the following in `psql` against the dev Postgres:

- `SELECT embedding('gemini-embedding-001', NULL);` — observe behaviour. If it errors, every null-path needs an explicit `Set(Embedding == null)` write.
- `SELECT convert_from('\x6869'::bytea, 'UTF8');` — confirm the decode primitive name.
- `SELECT LEFT('hello world', 5);` — confirm `LEFT` is available (it is, in stock Postgres).

Result of phase 0 informs the §5.2 expression's exact shape. Document the findings inline in the implementation PR description.

**Phase 1 — composer.** Add `Backend/Services/Embeddings/EmbeddingInputComposer.cs` per §5.1. Pure static helper. Add unit tests covering the §11 Decision 3 matrix and edge cases (name-with-tail-whitespace, content-with-BOM, non-UTF-8 bytes that decode-with-replacement, name longer than cap, content longer than cap).

**Phase 2 — name-touch helper.** Add a small static (private or beside the composer) per §8.3. Unit-test the four cases: list contains `/name` replace, list contains `/Name` (case differing), list does not contain `/name`, list is empty.

**Phase 3 — INodeService.Patch signature.** Add the `CancellationToken` parameter; update the implementation and the controller call site. This is mechanical. Note Contract #114 §3: the controller method that delegates to `nodeService.Patch(id, ops, ct)` should not have `async`+`await` if the body is one passthrough — return the Task directly. Check the existing controller code; the v1 `Patch` action is already `return nodeService.Patch(...)`, just add the parameter.

**Phase 4 — modify NodeService.UploadContent.** Read the current `Name` inside the transaction (after the content UPDATE). Call the composer with `(name, contentBytes, contentType)`. If the composer returns a string, issue the embedding UPDATE with `DB.Constant(text)`. If it returns the null sentinel, issue `Set(Embedding == null)`. Both inside the existing transaction. Remove the v1 branch that special-cased non-text by nulling — composer now handles that.

**Phase 5 — modify NodeService.Patch.** Wrap in a transaction. Compute `nameTouched`. After the patch UPDATE, if `nameTouched && capability.IsEnabled`, issue the embedding UPDATE using the SQL-side composition expression (preferred) or the read-compose-write fallback. Plumb the CT.

**Phase 6 — modify NodeService.CreateNode.** After the existing insert + auto-position block, if `capability.IsEnabled && !string.IsNullOrWhiteSpace(node.Name)`, issue `UPDATE Node SET Embedding = embedding(model, LEFT(<name>, 8000)) WHERE Id = newId`. Inside the existing transaction. The name is short and a `DB.Constant(node.Name)` is fine.

**Phase 7 — modify EmbeddingBackfillService.** Replace `CandidatePredicate()` with the v2 predicate (§5.7). Update logging messages to clarify "blanket re-embed" semantics. Add a single startup log line confirming v2 composition is active. Update the existing tests (`EmbeddingBackfillTests.cs`) — the v1 predicate's "row already embedded → skipped" test will need to flip its assertion to "row was re-embedded" (or move to a new test that asserts the v2 predicate's row-set selection).

**Phase 8 — deprecate the `embed` op.** Add `/// <remarks>deprecated; auto-flow on content/name writes is canonical</remarks>` to the `case "embed":` branch. Update CLAUDE.md's mention of the op accordingly.

**Phase 9 — documentation updates.** Apply §11 Decision 10's list. Include a header in `docs/architecture/embeddings.md` pointing to this v2 doc.

**Phase 10 — tests.**

- Unit: composer matrix.
- Unit: name-touch helper.
- Integration (SQLite, `WebApplicationFactory`): PATCH `/name` succeeds and returns 200 (cannot assert embedding bytes on SQLite — assert that the capability check was honoured by injecting a fake capability with `IsEnabled=true` and a stub `IEntityManager`-friendly assertion, OR run the existing SQLite path and assert no crash). The most-honest SQLite assertion is "PATCH succeeds, embedding column unchanged because capability is disabled".
- Integration (SQLite): create with name → 200, embedding column unchanged on SQLite.
- Integration (SQLite): upload non-text content → 200, embedding column unchanged on SQLite. (v1 would also have skipped on SQLite, so this is a regression-gate test, not a new positive assertion.)
- Postgres smoke (manual, pre-merge): PATCH `/name`, observe `Embedding` differs from pre-PATCH value (assert "different bytes" or "different cosine similarity to a known query", not "exact vector"). Create a node with a name, observe `Embedding` is non-null. Upload non-text content to a name-bearing node, observe `Embedding` is non-null (was null under v1). Empty-name + empty-content node, observe `Embedding` is null.
- Load-bearing assertions per DiVoid #275: each test must fail if the corresponding behaviour regresses. The composer tests fail if the composition rule changes. The trigger tests fail if a code path skips the embedding step on Postgres. The smoke tests fail on Postgres if the embedding doesn't change after a name patch.

**Phase 11 — smoke against Postgres.** Same posture as v1 §14 Step 8. Manual, pre-merge.

**Phase 12 — run backfill in production.** After deploy. Operator step, not code.

---

## Code Contract #114 hot-spots for this work

(These are the patterns Jenny will cite if violated. Pre-empt them.)

- **§3 "Async / Task — drop redundant async-await wraps".** When a controller or service method passes a Task through unchanged, do not wrap in `async`/`await`. The current `Patch` controller method already does this correctly (`return nodeService.Patch(...)`); preserve that shape after adding the CT parameter.
- **§3 "Explicit types — never `var`".** Composer and helper code must use explicit types. The composer's signature uses `string`/`byte[]`/`string` for params.
- **§6 Services.** All v2 logic lives in `NodeService` and friends, not in controllers. Controllers stay thin pass-through. `DatabasePatchExtensions` stays generic (Decision 6).
- **Predicate composition in the service.** `EmbeddingBackfillService.CandidatePredicate` already follows the `PredicateExpression<Node>` `&=`/`|=` idiom. v2's revised predicate stays in the same style.
- **No single-statement transactions.** The new `Patch` transaction wraps two UPDATEs (patch + embedding) plus the existing GetNodeById. The `CreateNode` transaction wraps multiple statements. Neither is a single-statement transaction, so the rule (don't transact a single UPDATE) is naturally satisfied.
- **No `Task.FromResult` band-aids.** If any new method ends up returning a value synchronously, restructure rather than wrap.
- **Patch nullability.** `Backend.csproj` is `<Nullable>disable</Nullable>`. Do not annotate reference types with `?`. Test project has nullability enabled — annotate freely there.

---

## Precedent: alignment with v1 and mamgo-backend

v2 preserves v1's three load-bearing decisions: synchronous embedding inside the same transaction, no try/catch on the embedding write, no DI seam beyond the capability flag. v2 changes one decision (clear-on-non-text → name-only on non-text) and broadens the trigger surface to include create and name-PATCH. The mamgo-backend embedding pattern (single `Update<>().Set(Embedding == DB.CustomFunction("embedding", …))`) is what every v2 call site emits, in identical shape.

The `embed` PATCH op is the one piece of legacy surface this doc actively winds down. Its deprecation removes the only remaining call site where the `gemini-embedding-001` literal is hardcoded in-line rather than referenced via `TextContentTypePredicate.EmbeddingModel` — a small post-condition benefit.
