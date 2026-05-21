# Architectural Document: embeddings-v2 SQL-side composition (task #444 follow-up to #440 §5.5)

Resolves DiVoid task **#444**. Decision document for the SQL-side composition path that #440 §5.5 left as the architecturally-preferred option but #437/PR #68 deferred via read-compose-write. The deferral was gated on Ocelot v0.22 shipping `DB.Substring` / `DB.Left` / `DB.ConvertFrom` as typed helpers — that landed in PR #80, which makes this revisit actionable.

This is a *delta document*. The base architecture (composition rules, trigger surface, transaction shape, capability gating, error posture) is unchanged from #440. Only the **§5.5 PATCH-name regen mechanism** is revised here. Nothing in §5.1, §5.2, §5.3, §5.4, §5.6, §5.7 of #440 changes.

---

## 1. Problem Statement

PR #68 shipped name-PATCH embedding regen via **read-compose-write** in `NodeService.Patch`:

1. `UPDATE Nodes SET name=… WHERE id=?` — the patch UPDATE.
2. `SELECT name, content, contentType FROM Nodes WHERE id=?` — pull the row back for composition.
3. `UPDATE Nodes SET embedding = embedding(model, $composed) WHERE id=?` — composed in .NET and bound as a `DB.Constant`.

Step 2 pulls the entire `Content` bytea across the wire for every name-PATCH, even though only ~8 KB of decoded prefix will be used. The current DiVoid corpus already contains several documentation nodes in the 20–57 KB range (sampled: node #9 ≈36 KB, #114 ≈40 KB, #190 ≈32 KB, #440 ≈58 KB, #420 ≈21 KB, #493 ≈19 KB). The corpus is no longer "all small markdown" — it's "most small markdown, a long tail of mid-size architecture docs." Long session-logs, embedded code excerpts, and the documentation nodes that have grown over time push this further. At ~600 nodes today and trending up, name-PATCH on a large doc transfers tens of KB across the wire for no semantic gain.

**Success criteria.**

- Name-PATCH regen no longer fetches the `Content` blob into .NET.
- All five rows of the §5.1 composition matrix (#440 Decision 3) are honoured by the SQL-side form, including the both-empty → `null` case.
- UTF-8 boundary safety is preserved: no multi-byte character is ever split mid-sequence by truncation.
- The change is contained to `NodeService.Patch`; `UploadContent` and `CreateNode` continue using their existing .NET-side composer call (the bytes are already in memory there, see §3 below).
- Test parity with `EmbeddingInputComposer` for the five composition cases plus a multi-byte UTF-8 boundary case.

## 2. Scope & Non-Scope

**In scope.**
- The SQL expression shape for the name-PATCH regen UPDATE(s).
- The truncation-semantics decision (Option A / B / C / D / E in the task brief).
- Whether the C# `EmbeddingInputComposer` stays as a fallback or is removed for the PATCH path.
- The fixture-row set that proves SQL output equals composer output.
- Code-contract smells to pre-empt during implementation.

**Out of scope.**
- The composition policy itself (locked by #440 Decision 1 and #440 Decision 3).
- `UploadContent` and `CreateNode` paths — they keep the .NET-side composer; the blob is already in memory there and the SQL form has no win.
- `EmbeddingBackfillService` — same reason as `UploadContent`; the backfill loads the row anyway and the per-row composition cost is dominated by Vertex AI roundtrip.
- The `embed` PATCH op deprecation (covered by #440 Decision 5; orthogonal to this delta).
- Any change to the search side (#183), the model (`gemini-embedding-001`), the cap value (8000 chars), or the separator (`\n\n`).
- Introducing an `EmbeddingVersion` column (deferred per #440 Decision 6).
- Changing `INodeService.Patch`'s signature (already plumbed in PR #68).

## 3. Assumptions & Constraints

- **Pooshit.Ocelot v0.22.0-preview** is on the build (`Backend.csproj` confirmed). The reflection-probe in `.claude/reflect_ocelot/` against the on-disk DLL confirmed:
  - `DB.Substring(ISqlToken expr, int|ISqlToken start, int|ISqlToken length) → SubstringToken` — typed helper.
  - `DB.Left(ISqlToken expr, int|ISqlToken length) → LeftToken` — typed helper.
  - `DB.ConvertFrom(ISqlToken bytes, string|ISqlToken encoding) → ConvertFromToken` — typed helper.
  - All three accept `ISqlToken` operands; they compose into `LEFT(convert_from(…))` and `convert_from(LEFT(…))` cleanly on `PostgreInfo`. SQL emitted is exactly the canonical Postgres form.
- **`DB.If(bool, ISqlToken, ISqlToken)` is NOT cleanly expressible against column predicates inside `Update<>().Set(n => …)`.** The probe confirmed: the C# compiler picks the `bool`-condition overload (because `n.Content != null && n.ContentType.Like("text/%")` has type `bool` at compile time), and Ocelot's expression-tree walker then chokes on the bool sub-expression in value-position with `"'System.Boolean' not supported to get host of: …"`. The `DB.Predicate(Expression<>)` helper is the documented bridge from C#-lambda-predicate to `ISqlToken`, but its runtime guard ("Only to be used in expressions") fires when it is called outside a top-level predicate position. Conclusion: **a single-UPDATE CASE-WHEN over column predicates is not architecturally available in v0.22 without raw-SQL escape hatches.** Using `DB.CustomFunction("CASE WHEN …", …)` raw-strings would re-introduce exactly the maintainability concern (#440 §5.2 fallback) that motivated the deferral.
- **Branch-by-WHERE IS cleanly expressible.** The probe confirmed all four mutually-exclusive single-UPDATE forms (name+text-content, name-only, content-only, both-empty) prepare to valid Postgres SQL with no raw-string escape hatches.
- **Postgres `convert_from(bytea, 'UTF8')` errors on invalid UTF-8 byte sequences.** Confirmed by the Postgres docs: it raises `ERROR: invalid byte sequence for encoding "UTF8"` on a split multi-byte boundary. This rules out the byte-aware truncation form (`convert_from(LEFT(content, 8000), 'UTF8')`) for any node whose first 8000 bytes happen to end mid-character.
- **`embedding(model, NULL)` semantics on the production Postgres function are not verified in this design.** #440 §13.1 already flagged this open question. The branch-by-WHERE approach below sidesteps the question entirely — the null branch issues `SET Embedding = NULL` directly rather than relying on `embedding()` to propagate NULL.
- **The corpus is small (~600 nodes today; "low thousands" projected).** Round-trip count per name-PATCH is the constraint that matters most; bandwidth-per-row is secondary; CPU on Postgres is irrelevant at this scale. A name-PATCH is a low-frequency event — a few extra UPDATEs per PATCH cost nothing measurable.
- **No `CancellationToken` re-plumbing.** PR #68 already added `ct` to `INodeService.Patch` and threads it through to `ExecuteAsync(transaction, ct)`. This work extends those calls but adds no new boundary.
- **No new helper class is needed.** The branch-by-WHERE forms live as four explicit UPDATE statements inside `NodeService.Patch`. A private helper method on the service is acceptable but optional.

## 4. The Decision (Option F: branch-by-WHERE, four sequential UPDATEs)

### 4.1 What was rejected and why

| Option (from brief) | Status | Reason |
|---|---|---|
| **A — `LEFT(convert_from(content, 'UTF8'), 8000)`** (decode then truncate, char-aware) | Adopted as the **truncation primitive** inside the branches that need it. | Char-aware. Matches C# `string[..budget]` semantics exactly. The "decodes the entire blob even if only the first 8000 chars are kept" worry is real but bounded — at corpus median ~3–50 KB, the worst-case decode is sub-millisecond Postgres work. The cost it replaces (sending the blob across the wire to .NET) is many times larger. |
| **B — `convert_from(LEFT(content, 8000), 'UTF8')`** (truncate then decode, byte-aware) | Rejected | Postgres `convert_from` raises a runtime error on a split multi-byte boundary. A node whose UTF-8 content happens to have a 2/3/4-byte character spanning byte 8000 would fail PATCH at runtime with `ERROR: invalid byte sequence for encoding "UTF8"`. Cannot ship a write path that can crash on otherwise-valid user content. |
| **C — mostly-ASCII byte-budget heuristic** | Rejected | Adds policy ("assume mostly ASCII") that does not match the corpus. Existing nodes contain em-dashes, smart quotes, accented characters (see DiVoid #187 for the em-dash incident history). The cap is a heuristic for the *model's* token budget, not a precision contract; making the cap further unpredictable to dodge a Postgres error is the wrong layer to fix it. |
| **D — threshold-branch (short SQL, long fallback)** | Rejected | Two-path code (SQL for ≤4 KB, read-compose-write for > 4 KB) doubles the surface that has to stay in lockstep with `EmbeddingInputComposer`. The threshold becomes a tunable in its own right. Failure modes (composer drift between paths, threshold mis-set) are silent. The win — saving the read on small nodes — is irrelevant because small nodes' reads are also nearly free. The cost — maintenance — is permanent. |
| **E — byte-aware C# composer** | Rejected | Trades a known-good C# composer (covers the entire trigger surface — `UploadContent`, `CreateNode`, `EmbeddingBackfillService`, plus what was the read-compose-write fallback in `Patch`) for byte-semantics that match a SQL form that we are no longer going to use anyway. Net loss; the C# composer already works and the validation harness around it is mature (#440 Phase 1, Phase 10). |

### 4.2 The chosen shape — Option F (branch-by-WHERE)

`NodeService.Patch`, after the patch UPDATE succeeds and inside the same transaction, fires **up to four sequential single-target UPDATEs**, each guarded by a mutually-exclusive WHERE that pins both the row id and the branch's composition-policy precondition. At most one branch matches any given row; the planner short-circuits the others on the `id = ?` index lookup.

Truth table — each row of the composition matrix becomes one branch (composition matrix is #440 Decision 3):

| Branch | Match predicate (after the row id) | UPDATE expression |
|---|---|---|
| **F1** — name + text content | `name <> '' AND contentType ILIKE 'text/%' AND content IS NOT NULL` | `Embedding = embedding(model, concat(name, E'\n\n', LEFT(convert_from(content, 'UTF8'), 8000)))` |
| **F2** — name only (no text content) | `name <> '' AND (contentType NOT ILIKE 'text/%' OR content IS NULL)` | `Embedding = embedding(model, name)` |
| **F3** — content only (empty/null name) | `(name IS NULL OR name = '') AND contentType ILIKE 'text/%' AND content IS NOT NULL` | `Embedding = embedding(model, LEFT(convert_from(content, 'UTF8'), 8000))` |
| **F4** — both empty/non-text → null | `(name IS NULL OR name = '') AND (contentType NOT ILIKE 'text/%' OR content IS NULL)` | `Embedding = NULL` |

#440 Decision 3's allowlist (`TextContentTypePredicate.ApplicationTextTypes`) is what the predicate translates as — the actual production predicate uses `TextContentTypePredicate.IsText(contentType)`, which combines the `text/%` LIKE with the allowlist `IN`. The implementer expresses the SQL form as the same composition: `(contentType ILIKE 'text/%' OR contentType IN (allowlist))`. The probe in §4.4 showed only the LIKE portion for compactness — the implementation must include the IN clause to match the composer exactly. Otherwise content-types in the allowlist (`application/json`, `application/xml`, etc.) would silently drop into the name-only branch.

#440 Decision 3 also treats whitespace-only names as empty (`string.IsNullOrWhiteSpace`). The SQL form approximates this with `name IS NULL OR name = ''`. **This is a small accepted divergence** for the PATCH path: a `/name` patched to `"   "` would be treated as "name present" by the SQL form but "name empty" by the C# composer. Acceptable because (a) the controller layer should reject pure-whitespace names anyway, (b) the v1 corpus contains no such row, (c) closing this gap requires `trim(coalesce(name, ''))` which adds noise without a real-world payoff. Document the divergence in the implementation PR.

### 4.3 Why F over a single-UPDATE CASE WHEN

A single UPDATE with `SET Embedding = embedding(model, CASE WHEN … THEN … ELSE … END)` would be tighter (one round-trip vs up to four). The probe demonstrated that **this is not cleanly expressible** in Ocelot v0.22 without falling back to `DB.CustomFunction("CASE WHEN …", …)` raw strings. That raw-string fallback is exactly what #440 §5.2 already named as the maintainability concern blocking the SQL-side path. We are not buying anything by switching from "read-compose-write" to "raw-string CASE WHEN" — we are trading one stretched abstraction for another.

The branch-by-WHERE form expresses each truth-table row through the *intended* Ocelot surface: typed `DB.Left`, `DB.ConvertFrom`, `DB.CustomFunction` for the embedding/concat calls, and entity-direct column predicates inside the `Where(...)` lambda. The expressions read like the composition rules; a contributor unfamiliar with the codebase can map each UPDATE to one row of the matrix in #440 §11 Decision 3 without leaving the file.

**The round-trip-count cost is illusory at this scale.** A name-PATCH is a low-frequency operation (an agent edits a name, a human renames a project). The Postgres planner short-circuits the three non-matching branches on the `id = ?` index lookup — each non-matching UPDATE is a ~50 µs round-trip with no row work. The matching branch does the real work (calls Vertex AI, which is the 200–500 ms cost dwarfing everything else). Four 50 µs probes around a 500 ms Vertex AI call are not the bottleneck.

### 4.4 SQL the probe emitted (canonical form on Postgres)

Verbatim from the v0.22 reflect harness against `PostgreInfo`:

```
UPDATE nodes
   SET "embedding" = embedding ( @1 , concat ( "name" , @2 , convert_from( LEFT( "content" , @3 ) , @4 ) ) )
 WHERE "id" = @5 AND "name" IS NOT NULL AND "name" <> @6
       AND "contenttype" ILIKE @7 AND "content" IS NOT NULL
```

```
UPDATE nodes
   SET "embedding" = embedding ( @1 , "name" )
 WHERE "id" = @2 AND "name" IS NOT NULL AND "name" <> @3
       AND ( NOT "contenttype" ILIKE @4 OR "content" IS NULL )
```

```
UPDATE nodes
   SET "embedding" = embedding ( @1 , convert_from( LEFT( "content" , @2 ) , @3 ) )
 WHERE "id" = @4 AND ( "name" IS NULL OR "name" = @5 )
       AND "contenttype" ILIKE @6 AND "content" IS NOT NULL
```

```
UPDATE nodes
   SET "embedding" = NULL
 WHERE "id" = @1 AND ( "name" IS NULL OR "name" = @2 )
       AND ( NOT "contenttype" ILIKE @3 OR "content" IS NULL )
```

(The allowlist `IN (…)` portion of `TextContentTypePredicate.IsText` is omitted from the probe output for brevity — the implementer adds it as an `OR` clause inside the contentType predicate of branches F1, F2, F3 and F4 to match the composer's text-detection.)

## 5. Components & Responsibilities

### 5.1 NodeService.Patch — revised

**Owns.** Detect name-touch (existing `TouchesName(patches)` helper unchanged). Open transaction. Issue patch UPDATE. If name was touched and capability is enabled, issue **the four branch UPDATEs** from §4.2 sequentially. Commit.

**Does not own.** The composition policy (still #440 §5.1 / `EmbeddingInputComposer`'s rules — the SQL form is a *projection* of those rules into Postgres, not a redefinition).

**Removes from this method.** The `SELECT name, content, contentType FROM Nodes WHERE id=?` step. The `EmbeddingInputComposer.Compose(...)` call. The `(float[]) null` conditional write — F4 takes over that role.

**Keeps in this method.** Everything else: the transaction shape, the `NotFoundException<Node>` on zero-affected patch UPDATE, the post-commit `GetNodeById(nodeId)` refresh, the capability gate.

### 5.2 EmbeddingInputComposer — retained for the other trigger paths

`UploadContent` and `CreateNode` continue to use the C# composer because the relevant bytes (content blob, name) are already in .NET memory at that point. Round-tripping them to Postgres just to pull them back into the composition is not a win.

`EmbeddingBackfillService` also continues to use the C# composer (the backfill predicate already loads rows; the composer call inside the loop is the cheap part). Branch-by-WHERE in the backfill path would be six UPDATEs per row (the four branches × the additional cost of running the row-load logic anyway) — net loss.

The C# composer is **not** a fallback for the Patch path. The Patch path uses SQL-only after this change; there is no "if SQL fails, fall back to C#" code branch. The composer remains the canonical source of truth for the composition rules; the SQL form is the projection of those rules. Tests pin the two together (see §10).

### 5.3 No new helper, no new file

The four UPDATEs go inline in `Patch`. They could be factored into private static methods (e.g. `BuildNamePlusTextBranch(IEntityManager db, long nodeId)`) for readability, but the architecture neither requires nor forbids it. The implementer picks based on whether the inline form crosses a readability threshold. Recommended: one helper method on `NodeService` named `RegenerateEmbeddingViaBranches(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct)` that contains all four UPDATEs; the `Patch` method calls it. This keeps `Patch` itself short.

## 6. Interactions & Data Flow

Sequence (path: `PATCH /api/nodes/{id}` touching `/name`, on Postgres, capability enabled):

1. Controller binds patch ops, calls `nodeService.Patch(id, ops, ct)`.
2. Service computes `nameTouched` via existing `TouchesName(patches)`.
3. Service opens a transaction (existing).
4. Service issues the patch UPDATE. If zero rows affected → `throw NotFoundException<Node>` (rolls back, existing behaviour).
5. If `nameTouched`:
   - Issue **F1** UPDATE bound to (id, model, separator, cap=8000, encoding="UTF8", and the empty-name comparator).
   - Issue **F2** UPDATE (id, model, empty-name comparator, allowlist+like predicate).
   - Issue **F3** UPDATE (id, model, cap, encoding, empty-name comparator, allowlist+like predicate).
   - Issue **F4** UPDATE (id, empty-name comparator, allowlist+like predicate).
   - Each UPDATE is `ExecuteAsync(transaction, ct)`. Order is F1 → F2 → F3 → F4; correctness holds for any order because the WHEREs are mutually exclusive, but the documented order matches the composition table for review.
6. Commit (existing).
7. Return refreshed `NodeDetails` via `GetNodeById(nodeId)` (existing).

Sequence on SQLite (capability disabled):

1–4 as above.
5. `nameTouched` is forced to `false` by the existing `capability.IsEnabled` gate — skipped entirely.
6. Commit.
7. Return.

The number of branches affecting the row at step 5 is exactly zero or one. Each non-matching branch is a no-op write returning 0 affected rows; that is acceptable. The implementer does **not** check the affected-row count on F1/F2/F3/F4 — checking would couple the four UPDATEs to each other's outcomes; the truth table guarantees mutual exclusion.

## 7. Data Model (Conceptual)

No schema change.

## 8. Contracts & Interfaces (Abstract)

### 8.1 Branch-truth-table contract (the load-bearing one)

Given the four branch-conditions in §4.2, applied to a row whose final state is (Name, Content, ContentType) after the patch UPDATE:

- Exactly one branch's WHERE matches.
- The composition output of the matching branch equals `embedding(model, EmbeddingInputComposer.Compose(Name, Content, ContentType))` for that row (or `NULL` when the composer returns the null sentinel and F4 matches).
- The pure-whitespace name divergence noted in §4.2 is acknowledged.

Violation of any of the three above is a contract bug; tests pin them (§10).

### 8.2 SQL-form expression shape

The text-detection predicate that the implementer writes for branches F1/F2/F3/F4 must include both:
- `contentType ILIKE 'text/%'` (the LIKE clause).
- `contentType IN (TextContentTypePredicate.ApplicationTextTypes)` (the allowlist).

Combined as: `(contentType ILIKE 'text/%' OR contentType IN (…))`. F2 and F4 negate the combined predicate (using `NOT (combined) OR content IS NULL`); F1 and F3 affirm it.

The allowlist must be sourced from the same constant — `TextContentTypePredicate.ApplicationTextTypes` — that the C# composer references. If a future PR adds a new entry to the allowlist, the SQL branches automatically pick it up because they reference the same array. (Verify this is true in the implementation — if Ocelot's `In(constant)` evaluates the array at expression-build time and bakes it into the parameter list, the property is preserved; if it captures by reference, even better.)

### 8.3 `NodeService.Patch` signature

Unchanged from PR #68: `Task<NodeDetails> Patch(long nodeId, PatchOperation[] patches, CancellationToken ct)`. The CT is threaded into all four branch UPDATEs.

## 9. Cross-Cutting Concerns

**Transactions.** All four branch UPDATEs share the patch transaction. The five UPDATEs (1 patch + 4 branch) commit atomically. A Vertex AI failure (timeout, error from the `embedding(...)` function) in the matching branch rolls back the entire transaction — including the patch UPDATE that wrote the new name. This is identical to the current PR-#68 behaviour and matches #440's posture (strict consistency over partial availability).

**Cancellation.** The `ct` token reaches each of the four UPDATEs. A CT cancellation aborts the transaction; the patch is rolled back. Identical to current.

**Error handling.** No try/catch. The branch UPDATEs propagate Vertex AI failures upward as `Pooshit.Ocelot` exceptions, which the middleware in `Pooshit.AspNetCore.Services` translates into HTTP 500.

**Idempotency.** A name-PATCH that lands on a row already in the "correct" state (e.g. re-patching `/name` to its current value) still issues four UPDATEs. The matching one re-calls `embedding(...)` and overwrites the column with a fresh (identical) embedding. Wasteful but harmless, matches current. #440 §13.4 deferred suppression of no-op name patches.

**Concurrency.** Two concurrent name patches to the same row serialize on the row lock acquired by the patch UPDATE. The four branch UPDATEs all run inside the same transaction, so MVCC sees the row at the post-patch snapshot — the snapshot the branches' WHEREs match against. No tearing possible.

**Bandwidth.** Per name-PATCH: ~4 small UPDATE statements (each round-trip is on the order of the WHERE clause's parameter list — tens of bytes), plus the Vertex AI roundtrip dominated by the model API call. Compared to current: 1 patch UPDATE + 1 row SELECT (pulls up to the full Content blob — tens of KB for current corpus, potentially MB for future) + 1 embedding UPDATE. The win is the eliminated SELECT.

**Observability.** No logging changes. The existing posture (silent on success, exceptions propagate) is preserved. If post-deploy telemetry shows the four-UPDATE pattern is creating visible noise in the Postgres slow-query log, the implementer can collapse non-matching branches by checking `if (affectedRows == 0)` and skipping subsequent branches — but the architecture **rejects** this optimisation up front because it couples the four branches' outcomes and produces order-dependent code. Add the early-exit only if measured cost forces it.

## 10. Validation Strategy

### 10.1 Fixture rows (must each be covered)

Each fixture exercises one branch and proves the SQL output equals the C# composer output for the same inputs.

| Fixture | Name | Content | ContentType | Expected branch | Expected embedding input |
|---|---|---|---|---|---|
| **R1** | `"Hivemind Protocol"` | `# foo\n\nsome markdown body` | `text/markdown` | F1 | `"Hivemind Protocol\n\n# foo\n\nsome markdown body"` |
| **R2** | `"Project: DiVoid"` | a PNG blob (non-text) | `image/png` | F2 | `"Project: DiVoid"` |
| **R3** | `"Group node"` | null/empty content | (null) | F2 | `"Group node"` |
| **R4** | empty/null name | `# untitled doc\n\nbody` | `text/markdown` | F3 | `"# untitled doc\n\nbody"` |
| **R5** | empty/null name | null/empty content | (null) | F4 | (NULL — `Embedding` set to NULL) |
| **R6** *(load-bearing for cap)* | `"X"` (1 char) | text content of length 10000 chars, with multi-byte UTF-8 characters (em-dashes, accents) **straddling the 8000-char boundary** | `text/markdown` | F1 | `"X\n\n" + content[..7997]` (cap leaves room for name + separator; truncated at a char boundary, NOT a byte boundary; multi-byte char near boundary is preserved or fully truncated, never split) |
| **R7** *(non-`text/*` allowlist match)* | `"JSON doc"` | `{"key":"value"}` | `application/json` | F1 | `"JSON doc\n\n{\"key\":\"value\"}"` (proves the allowlist branch of TextContentTypePredicate is honoured) |

**R6 is the load-bearing fixture.** It proves Option A (char-aware truncation via `LEFT(convert_from(...))`) over Option B (which would error). The fixture must construct content whose 8000th char position is in the middle of a multi-byte UTF-8 character — e.g. 7999 ASCII chars followed by an em-dash (`—`, U+2014, three UTF-8 bytes). Postgres decodes the entire blob to text, then `LEFT(text, 8000)` slices at the *char* boundary — the em-dash is either fully included (positions 7999..8001 in the source map to char 8000, included) or fully excluded depending on where the cap lands. It is **never** split into 1 or 2 raw bytes. Compare the SQL form's output byte-for-byte against `EmbeddingInputComposer.Compose(...)` on the same inputs.

### 10.2 How the parity test runs

There are two flavors:

1. **Composer-vs-SQL parity (Postgres, manual or environment-flag-gated).** For each fixture, insert the row, run the four-branch sequence (no patch UPDATE — just the regen step), then SELECT the `Embedding` column. Separately compute `EmbeddingInputComposer.Compose(name, content, contentType)` in .NET and bind it to a direct `embedding(model, $composed)` call. Compare the two `float[]` results — they must be byte-identical because Vertex AI is deterministic for fixed model+input. If they differ, the SQL form has drifted from the composer.

2. **Composer-vs-SQL parity (SQLite, capability disabled).** Same fixtures, but the capability gate skips both the composer call and the four-branch UPDATE. The test asserts that the column stays NULL (existing behavior). This is regression-only — it catches the case where someone removes the capability gate.

The Postgres parity tests are not in CI (no Vertex AI in the test environment). They run as a manual smoke during the implementation PR review, same posture as #440 §11 Decision 9 / §14 Phase 11.

### 10.3 What is NOT validated by this strategy

- The exact embedding vector values (we only assert "same bytes"). Vertex AI's output is treated as a black box.
- The semantic quality of the embedding (covered separately by search-side smoke tests against the new corpus after backfill).
- Behavior on > 1 MB Content blobs (the largest realistic node today is ~60 KB; speculative ranges are #440 §13.5's territory).

## 11. Decisions Summary (resolving the brief's bullet questions)

### Decision 1: Which option?

**Option F (branch-by-WHERE, four sequential UPDATEs), with Option A's truncation primitive (`LEFT(convert_from(content, 'UTF8'), 8000)`, decode-then-truncate, char-aware).**

Justification recap:
- The embedding model tokenizes its input again on the Vertex AI side. The 8000-char cap is a heuristic for the *model's* context budget, not a precision contract. Whether we cap at chars or bytes is **observably indistinguishable** in the embedding-quality dimension — but the SQL byte-aware form (Option B) is **observably worse** in the failure-mode dimension because it can crash a write.
- Large-content nodes do exist in the corpus today (multi-tens-of-KB documentation nodes; node #440 itself is ~58 KB). They are not hypothetical. Saving the read-of-Content on each name-PATCH is a real (if not urgent) bandwidth win.
- A dual-path C# composer + threshold (Option D) doubles the maintenance surface for no clear win at this scale. Rejected.
- Changing the C# composer to byte-aware (Option E) trades a working surface for no gain. Rejected.
- A single-UPDATE CASE WHEN over column predicates is not cleanly expressible in Ocelot v0.22 (probe-confirmed). The branch-by-WHERE shape preserves the typed-helper surface and is mechanically readable.

### Decision 2: The exact expression shape

For each of the four branches, an `UPDATE<Node>().Set(n => n.Embedding == DB.CustomFunction("embedding", DB.Constant(model), <text-expression>).Type<float[]>()).Where(<branch-predicate>).ExecuteAsync(transaction, ct)`:

- **F1** (`<text-expression>`): `DB.CustomFunction("concat", DB.Property<Node>(x => x.Name), DB.Constant("\n\n"), DB.ConvertFrom(DB.Left(DB.Property<Node>(x => x.Content), 8000), "UTF8"))`.
- **F2** (`<text-expression>`): `DB.Property<Node>(x => x.Name)`.
- **F3** (`<text-expression>`): `DB.ConvertFrom(DB.Left(DB.Property<Node>(x => x.Content), 8000), "UTF8")`.
- **F4**: the SET is `n.Embedding == (float[]) null` (no expression; direct NULL assignment).

For all four branches the WHERE composes the truth-table predicate against entity-direct property references inside the lambda: `n.Id == nodeId && <name-condition> && <text-content-condition>`. The probe in §4.4 confirmed each prepares to canonical Postgres SQL.

The implementer derives the allowlist-inclusive text-content predicate (`contentType ILIKE 'text/%' OR contentType IN (…)`) from the existing `TextContentTypePredicate.IsText` shape. If a `BuildTextContentPredicate(LoadOperation-or-Update)` private static helper is useful for readability, factor it out; if the inline form fits in one line per branch, leave it inline.

### Decision 3: Validation fixtures

The seven fixtures in §10.1. R6 is the load-bearing one (multi-byte UTF-8 char straddling the cap boundary). R7 is the load-bearing one for the allowlist branch. R1–R5 cover the matrix rows. R5 covers the both-empty → NULL case.

The fixtures are wired into the SQLite integration-test fixture (`WebApplicationFactory<Program>`) for the regression assertion ("PATCH succeeds, embedding column unchanged because capability is disabled") and into a Postgres manual smoke for the parity assertion ("PATCH writes an embedding byte-identical to the C# composer's output").

### Decision 4: Does the C# composer stay?

**Yes, for all paths except `NodeService.Patch`.** `UploadContent`, `CreateNode`, `EmbeddingBackfillService` continue to call `EmbeddingInputComposer.Compose(...)` because the bytes are already in memory there. The composer is **not** a fallback for the Patch path — the four-branch SQL form is the sole mechanism. The composer remains the canonical source of truth for the composition rules; the SQL branches are the *projection* of those rules into Postgres. Tests pin the two together.

If the SQL form proves brittle in practice (a Postgres version change, a new content-type that crosses the LIKE/allowlist boundary in an unexpected way), the orchestrator can roll back the Patch path to read-compose-write by reverting the diff — the composer is right there, unchanged.

### Decision 5: §3 / §4 smell guards for the implementer

- **§3 "Explicit types — never `var`".** The four UPDATE blocks chain `database.Update<Node>().Set(...).Where(...).ExecuteAsync(transaction, ct)` — there is nothing to declare a local for. If a private helper method is extracted, its parameters use explicit types (`IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct`).
- **§3 "Async / Task — drop redundant async-await wraps".** Each of the four branch UPDATEs is `await`-ed individually for clarity (their order in the transaction matters for review/traceability — see §6). They cannot be collapsed into a `Task.WhenAll(...)` because they share a transaction. Do **not** wrap the existing `Patch` method body in additional `async` machinery.
- **§3 (corollary).** The `composed != null ? UPDATE-with-composed : UPDATE-with-NULL` branching that exists today in `Patch` lines 651–667 is **removed** by this change — F1/F2/F3 handle the "composed exists" cases, F4 handles the NULL case. The conditional disappears.
- **§4 "Inside method bodies — only when not obvious".** Each branch UPDATE deserves *one* short comment naming the truth-table row it implements (e.g. `// F1: name + text content`). The composition rules themselves are not re-narrated in comments — they live in #440 §11 Decision 3 and `EmbeddingInputComposer`'s XML doc.
- **§4 "Don't announce sections".** Do **not** put `// ── F1 ──` banners between the branches.
- **§6.3 "Use `.Like()` only inside `PredicateExpression<T>` lambdas".** The probe used `n.ContentType.Like("text/%")` directly inside `.Where(...)` and it worked. The §6.3 caveat is real for *plain* `Where(...)` lambdas in some Ocelot versions; on v0.22 against the `Update<>().Where(...)` shape it generated valid SQL. The implementer should confirm at build time. If it fails to compile, hoist the predicate into a `PredicateExpression<Node>` and pass `predicate.Content` to `Where(...)`.
- **No single-statement transactions.** The transaction wraps 5 UPDATEs (1 patch + 4 branch) — not a single-statement transaction. Rule naturally satisfied.
- **No new try/catch.** Failures propagate. The five UPDATEs roll back together on any failure.
- **No `var` in helper-method signatures.** If `RegenerateEmbeddingViaBranches(...)` is extracted, all parameters are explicit types; the method is `private static async Task` returning Task.
- **No async-await on pure passthrough.** The helper's body is four awaits in sequence — `async` is required because of the sequence; no smell here.

## 12. Risks & Mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | Postgres planner doesn't short-circuit the non-matching branches as cheaply as expected (full table scan on a missing index?) | Very low | Up to 3 extra wasted UPDATEs per name-PATCH | The primary-key index on `Id` is canonical Postgres behavior. Verify EXPLAIN ANALYZE on a sample row before merge if paranoid. |
| 2 | The pure-whitespace name divergence between SQL form and C# composer surfaces in user content | Low | A row PATCH'd to whitespace name gets F1/F2 not F3/F4 — minor recall miss | Documented in §4.2. If it ever matters: the SQL form's empty-name comparator becomes `name IS NULL OR trim(name) = ''`. Defer until measured. |
| 3 | `TextContentTypePredicate.ApplicationTextTypes` is bound at JIT time, not transaction time | Low | New allowlist entry needs a restart to take effect | True today for the C# composer too. Restart is part of the deploy. No new failure mode. |
| 4 | `embedding(model, '')` semantics differ from `embedding(model, NULL)` | Low | An empty-string composition (zero-byte content concatenated with empty name) goes through `embedding('')` rather than `NULL` | F4's WHERE catches the both-empty case before the embedding call; we never call `embedding('')`. R5 fixture proves this. |
| 5 | An ALLOWLIST content-type with non-UTF-8 bytes (e.g. `application/json` with Latin-1 payload) causes `convert_from(content, 'UTF8')` to error | Medium-low | Runtime 500 on PATCH for an exotic-encoding text node | Same risk class as the existing C# composer's `Encoding.UTF8.GetString(content)` which also mojibakes on non-UTF-8. Acceptable parity — both paths produce/skip the same result on the same input. |
| 6 | Branch UPDATE count grows if #440 Decision 3 adds a new row to the composition matrix | Medium long-term | New branch must be added to `NodeService.Patch` AND `EmbeddingInputComposer` in lockstep | Document the matrix→branches mapping in the implementation's XML doc on the helper. R1–R7 fixture coverage catches drift if a branch is forgotten. |

## 13. Open Questions (non-blocking)

1. **Should we add EXPLAIN ANALYZE proof to the implementation PR?** The "Postgres short-circuits non-matching branches on id index" assumption is canonical but not formally verified. Worth a one-line confirmation step in the implementation PR description before merge. Cheap to do, cheap to skip.
2. **Should the four branches live in a private helper method or inline in `Patch`?** Architecturally either is fine. Recommended: helper named `RegenerateEmbeddingViaBranches`. Final call: implementer's, based on whether the inline form crosses the readability threshold for them.
3. **Should the helper-name comment block reference the truth table in #440 Decision 3 by section number?** Yes — `/// <remarks>implements the four branches of #440 Decision 3 (composition matrix). Each branch corresponds to one row of the table.</remarks>`. Cross-reference, not narration.
4. **Should we add a Postgres-only integration test that asserts the SQL form's bytes equal the composer's bytes?** Yes if the CI environment can be plumbed to Vertex AI. The architecture allows but does not require it — the manual smoke is the contract.

## 14. Implementation Guidance for the Next Agent

Ordered build phases. This is one PR — branched off the current main (which already has #80's Ocelot 0.22 bump). Do **not** bundle with unrelated changes.

**Phase 0 — verify the probe locally.** Run the existing `.claude/reflect_ocelot/` probe (or write a one-off): assert all four branch SQL forms generate clean Postgres SQL against `PostgreInfo`. If the SQL doesn't match what's documented in §4.4, **stop and surface to Sarah** before continuing — the design rests on that probe's output.

**Phase 1 — extract the helper.** Add a private static method on `NodeService`: `RegenerateEmbeddingViaBranches(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct)`. Body: four awaits in order F1 → F2 → F3 → F4 per §4.2 / §11 Decision 2. XML doc names the matrix rows. No comments inside the method beyond one `// F1`, `// F2`, … per branch.

**Phase 2 — rewire `Patch`.** Replace lines 645–667 of `NodeService.cs` (the existing `if (nameTouched) { … SELECT row … Compose … if (composed != null) UPDATE-with-composed else UPDATE-with-NULL }`) with a single `if (nameTouched) await RegenerateEmbeddingViaBranches(database, transaction, nodeId, ct);`. The `using Transaction transaction` and the patch UPDATE and the `transaction.Commit()` and the `GetNodeById` call are unchanged.

**Phase 3 — `EmbeddingInputComposer` is unchanged.** Do not edit `Backend/Services/Embeddings/EmbeddingInputComposer.cs`. It is still the source of truth for `UploadContent`, `CreateNode`, `EmbeddingBackfillService`, and the test parity assertion in Phase 4.

**Phase 4 — tests.**
- **Unit (no DB):** none needed for the SQL form (it's a pure SQL builder; testing it is testing Ocelot). Composer matrix unit tests are unchanged from PR #68.
- **Integration (SQLite via the existing `WebApplicationFactory<Program>` fixture):** the existing "PATCH /name on SQLite leaves Embedding NULL" assertion still holds. Add a regression test for each of R1–R7 that exercises the same matrix entries via PATCH — assert HTTP 200, no crash. Embedding column verification on SQLite is impossible (capability disabled); the assertion is "the code path was taken without throwing".
- **Postgres parity (manual, pre-merge):** for each of R1–R7, set up the row in dev Postgres, call PATCH /name with a one-character rename, then SELECT the Embedding column. In parallel, compute `EmbeddingInputComposer.Compose(updated_name, content, contentType)` in .NET and bind directly to `embedding(model, $)` via a separate ad-hoc call. Compare the two `float[]` results — byte-identical. R6 (the multi-byte boundary fixture) is the load-bearing one; if it fails, Option B (byte-aware) is masquerading somewhere in the implementation.
- **Documentation:** add a one-line note to `Backend/Controllers/V1/NodeController.cs`'s `Patch` XML doc clarifying "name-PATCH regen uses SQL-side composition; the C# composer rules are mirrored via four branch UPDATEs". Update `docs/architecture/embeddings-v2.md` (the v2 design doc that PR #68 committed) — add a follow-up note pointing to this doc.

**Phase 5 — commit the design doc.** Per the CLAUDE.md design-doc rule, copy this DiVoid documentation node into `docs/architecture/embeddings-v2-sql-composition.md` on the implementation branch. The repo file is the deliverable; the DiVoid node is the durable reference.

**Phase 6 — open the PR.** Single PR, branched off main post-#80. PR title: `refactor(embeddings): SQL-side name-PATCH regen via branch-by-WHERE (task #444)`. PR body: link this DiVoid node (#444 follow-up), summarize the truncation-semantics decision (Option A char-aware via `LEFT(convert_from(...))`), and list the R1–R7 manual smoke results.

**Phase 7 — Jenny review.** Standard `jenny-qa-reviewer` cycle. Pre-empt smells: explicit types only, no decorative comments between branches, no try/catch added to Patch, no single-statement transactions (the surrounding transaction wraps 5 UPDATEs).

**Phase 8 — Toni reviews/merges.** Sarah/orchestrator step down at PR boundary per the global PR boundary rule.
