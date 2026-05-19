# Architectural Document: Auto-Position on Link Creation + Radius Bump (task #500)

## 1. Problem Statement

Today, server-side auto-positioning of new nodes runs in exactly one place: `POST /api/nodes` when the request body carries `links` AND no explicit `X`/`Y` (`NodeService.CreateNode`, lines 78–108, introduced by task #311 / PR #49). Agents that take the alternative path — `POST /api/nodes` first, then `POST /api/nodes/{id}/links` to attach edges — leave the new node permanently stuck at `(0, 0)`. The workspace canvas shows the stack of latecomers at the origin, defeating the value of positional metadata for any caller that does not bundle links into the create call.

Concurrently, the existing radius constant (`80.0` at `NodeService.cs:35`) is empirically too tight: a 4–6 child cluster around an anchor visually overlaps at typical workspace zoom levels.

**Goal:** make `POST /api/nodes/{id}/links` apply the same auto-position semantics as the create-with-links path, and pick a single new radius value used by both sites.

**Success criteria:**
- Linking a `(0, 0)` node to a positioned anchor moves the `(0, 0)` node to `ComputeAutoPosition(thatNode.Id, anchor.X, anchor.Y)`.
- Linking two positioned nodes is a no-op for positions.
- Linking two `(0, 0)` nodes is a no-op for positions (no anchor exists).
- A single constant change updates both `CreateNode` and the new `LinkNodes` path.
- Existing `CreateNode` auto-position tests continue to pass (with updated numeric expectations where they assert exact post-positions).

## 2. Scope & Non-Scope

**In scope:**
- Behavioural change to `NodeService.LinkNodes(sourceNodeId, targetNodeId)` to apply auto-position after the link insert, inside the existing transaction.
- Radius constant bump applied at a single source so both call sites share it.
- Test additions mirroring the #311 / PR #49 pattern.

**Out of scope** (verbatim from task #500's "Out of scope" section, restated):
- **Collision avoidance.** If the computed position happens to coincide with another existing node, we still place there. #311 explicitly punted this; same here.
- **Re-positioning previously-`(0,0)` nodes that already have links.** That is the CLI `LayoutNodesService` job; running it on demand handles legacy backlogs.
- **Frontend changes.** The create-via-canvas flow already pre-populates X/Y; the file-drop and click-empty-space flows already send X/Y. Nothing to change there.
- **Per-user or per-zoom dynamic radius.** Radius remains a single constant.
- **Multi-anchor averaging.** `LinkNodes` only knows two endpoints; the anchor selection is whichever isn't at `(0,0)`. No averaging.
- **Controller surface changes.** `POST /api/nodes/{id}/links` semantics are unchanged from the caller's perspective; this is purely a server-side enrichment of an already-successful operation.

## 3. Assumptions & Constraints

- The semantics established in #311 are preserved unchanged: floating-point `(X == 0.0 && Y == 0.0)` is the exact predicate for "no explicit position"; no epsilon is introduced (matches the existing test substitution evidence in #313).
- `LinkNodes` already wraps its work in a single `Transaction`. The new logic lands **inside that same transaction**, not a new one — both for atomicity and per backend code contracts §3 (no redundant transaction nesting).
- The `IEntityManager` API used by the existing auto-position block (`Load`, `Update`, `ExecuteEntitiesAsync`, `ExecuteAsync`) is the same instance available in `LinkNodes`.
- The `ComputeAutoPosition` helper at lines 33–38 is the canonical position formula; both sites must call it (not reimplement it).
- Nullable reference types are disabled in `Backend.csproj`; the helper returns a value tuple, and no nullable reference annotations are needed.
- Per CLAUDE.md / `.editorconfig`: K&R braces, 4-space indent, no `var`, two blank lines between methods, XML doc on public surface (private helpers do not require XML doc).

## 4. Architectural Overview

The change is a single-file modification to `Backend/Services/Nodes/NodeService.cs`. There are three logical edits:

```
ComputeAutoPosition (existing, static)
    └── radius constant: 80.0  --> 200.0    [EDIT 1]

CreateNode (existing)
    └── lines 78–108 auto-position block
        └── replace inline logic with call to new private helper  [EDIT 2a]

LinkNodes (existing)
    └── add post-insert call to the same private helper          [EDIT 2b]

TryAnchorOrphanToPositioned (new, private, instance method)
    └── shared "examine two endpoints, move whichever is at (0,0) toward the other" logic
```

The component graph is unchanged at the service / controller / HTTP boundary. No new types, no new files, no new DI registrations, no new endpoints, no schema change.

## 5. Components & Responsibilities

### `NodeService.ComputeAutoPosition` (unchanged signature, constant bumped)

- **Owns:** the deterministic formula mapping `(nodeId, anchorX, anchorY) → (x, y)`.
- **Does not own:** the decision of whether to apply auto-position; that lives in the callers.
- **Edit:** radius constant `80.0` → `200.0`.

### `NodeService.CreateNode` (existing — refactored to delegate)

- **Owns:** node-row creation, type resolution, link insertion, embedding generation, transaction commit.
- **Delegates:** the post-insert "anchor the new node" decision and update to the new helper.
- **Does not own:** the auto-position formula; that is `ComputeAutoPosition`.

The current behaviour of `CreateNode` (described in #311) when there is exactly one inserted node and a set of candidate anchor links is a superset of `LinkNodes` (which has exactly two endpoints). The helper's contract must serve both cases. See §8 for the contract; the helper takes a *single* candidate-to-position id and a *set* of anchor candidate ids — `CreateNode` passes its full `links` array, `LinkNodes` passes a one-element array.

### `NodeService.LinkNodes` (existing — extended)

- **Owns:** validation that both endpoints exist, the link insertion, transaction commit, the new "did this link operation leave one endpoint with a clear anchor?" call.
- **Edit:** after the existing `Insert<NodeLink>` and before `transaction.Commit()`, the method calls the helper twice — once with `(source, target)` and once with `(target, source)`. The helper is a no-op if the candidate is already positioned, so the symmetric calls are safe and the four-cell behaviour table (§7) falls out naturally.

### `NodeService.TryAnchorOrphanToPositioned` (NEW — private instance method)

- **Owns:** the conditional logic ("is the candidate at `(0,0)`? is there a positioned anchor among the candidates? if both, persist the auto-position").
- **Does not own:** the formula, the link insertion, transaction lifecycle.
- **Visibility:** private. Not exposed on `INodeService`.

### `INodeService.LinkNodes` signature

- **Unchanged.** No interface-surface change. The behavioural extension is internal to the implementation.

### `NodeController.LinkNodes` (line 181)

- **Unchanged.** Same route, same parameter, same `[Authorize(Policy = "write")]`, same `Task` return. The caller observes only the side effect that positions were updated; no body shape change.

## 6. Interactions & Data Flow

### Create-with-links flow (existing, refactor-only)

```
HTTP POST /api/nodes  (body: { name, type, links, X=null, Y=null })
  └── NodeController.Create
       └── NodeService.CreateNode
             ├── (existing) resolve type, INSERT node, INSERT links
             └── if (insertX == 0.0 && insertY == 0.0 && links.Length > 0)
                   └── TryAnchorOrphanToPositioned(tx, newNodeId, links)
                          └── load anchor positions, pick first non-(0,0), UPDATE row
```

### Link-existing-nodes flow (NEW positioning step)

```
HTTP POST /api/nodes/{sourceNodeId}/links  (body: targetNodeId)
  └── NodeController.LinkNodes
       └── NodeService.LinkNodes
             ├── (existing) validate both exist, no-self-link, no-duplicate
             ├── (existing) INSERT NodeLink(sourceNodeId, targetNodeId)
             ├── TryAnchorOrphanToPositioned(tx, sourceNodeId, [targetNodeId])
             ├── TryAnchorOrphanToPositioned(tx, targetNodeId, [sourceNodeId])
             └── Commit
```

The two symmetric calls realise the four-cell table without branching: each call is a no-op unless its `candidate` is at `(0,0)` AND its `anchorCandidates` array contains a positioned node. Floating-point `(X != 0.0 || Y != 0.0)` is used for the anchor non-zero predicate, matching `CreateNode:93` exactly.

**Why two calls, not one with branching:** the helper's contract is "if `candidate` is at `(0,0)` and an anchor is found, move it." That is exactly the semantic both `LinkNodes` endpoints need, evaluated independently. Branching at the `LinkNodes` site would duplicate the "is this one positioned?" check in two places; the helper already owns it. The cost is one extra `SELECT` of anchor positions per link operation when both endpoints are already positioned — one query returning zero non-zero anchors is cheap, and the alternative (one-shot branching with a single-anchor fetch) muddies the contract.

### Transactional boundary

Both `CreateNode` and `LinkNodes` already open a `using Transaction transaction = database.Transaction();` and commit at the end. The helper accepts that `transaction` as an explicit parameter and threads it through every Ocelot operation it issues. This satisfies the existing pattern (mirror of #311 / `CreateNode:83-106`) and code contracts §3 (no fresh transaction inside another transaction).

## 7. Behaviour Table (the load-bearing spec)

The four-cell symmetric specification from task #500, restated:

| Source position | Target position | Outcome |
|---|---|---|
| `(0, 0)` | `(0, 0)` | No-op. No anchor exists; both stay at origin. Link is still inserted. |
| `(0, 0)` | non-zero | **Source moves** to `ComputeAutoPosition(sourceId, target.X, target.Y)`. Target unchanged. Link inserted. |
| non-zero | `(0, 0)` | **Target moves** to `ComputeAutoPosition(targetId, source.X, source.Y)`. Source unchanged. Link inserted. |
| non-zero | non-zero | No-op for positions. Both retain their existing positions. Link inserted. |

**Invariants:**
- "Explicit position is sacred" is preserved: a node with a non-zero position is never auto-repositioned by this code path. This is the same property guarded by `(insertX == 0.0 && insertY == 0.0)` in `CreateNode:79`.
- Auto-position never *creates* a link; it only reacts to one that was just inserted.
- The result of `ComputeAutoPosition` is fully deterministic in the candidate's id and the anchor's position; identical inputs produce identical outputs (no clock, no RNG, no concurrency-sensitive state).

## 8. Contracts & Interfaces (Abstract)

### Private helper contract: `TryAnchorOrphanToPositioned`

**Conceptual signature:** takes a transaction, a candidate node id, and an ordered list of anchor candidate ids; returns nothing.

| Aspect | Specification |
|---|---|
| **Input — transaction** | The ambient transaction from the caller. All DB reads/writes the helper issues are bound to it. |
| **Input — candidate id** | The id of the node that *might* be auto-positioned. The helper will read its position to confirm it is at `(0,0)`; if not, the helper is a no-op. |
| **Input — anchor candidates** | An ordered enumerable of node ids to consider as anchors. The helper picks the **first** in iteration order whose position is non-`(0,0)`. Matches `CreateNode:91-98`'s "links order is the tie-breaker" semantics. |
| **Behaviour — candidate already positioned** | No-op. No reads beyond the candidate position lookup, no writes. |
| **Behaviour — no positioned anchor found** | No-op. The candidate is not moved. |
| **Behaviour — both conditions satisfied** | Computes via `ComputeAutoPosition(candidateId, anchor.X, anchor.Y)`; persists the new `X` and `Y` on the candidate row via `UPDATE Node SET X=?, Y=? WHERE Id=?` inside the supplied transaction. |
| **Errors** | The helper does not throw on "no anchor" / "candidate positioned" — those are the normal no-op cases. It propagates any underlying DB error. |
| **Read pattern** | A single `Load<Node>` projecting only `Id, X, Y`, filtered to `Id IN (candidateId, anchorCandidates...)`, executed once and consumed via `await foreach` into a `Dictionary<long, Node>` — same shape as `CreateNode:82-88`. The candidate row is read in the same query as the anchor candidates to avoid a second round-trip. |
| **Write pattern** | A single conditional `Update<Node>` of `X`, `Y` filtered by `Id == candidateId`. Same shape as `CreateNode:103-106`. |
| **Side effects** | The candidate's `X` / `Y` are updated. No other column is touched. No embedding regeneration. No link mutation. |

### Public interface surfaces

- `INodeService.LinkNodes(long, long)` — signature unchanged. Documentation updated to mention the auto-position side effect.
- `INodeService.CreateNode(NodeDetails)` — signature unchanged. Behaviour preserved (the refactor is intent-preserving).
- HTTP route surface — `POST /api/nodes`, `POST /api/nodes/{id}/links`, request/response shapes — all unchanged.

## 9. Cross-Cutting Concerns

- **Authentication / authorisation:** unchanged. `LinkNodes` continues to be `[Authorize(Policy = "write")]`. No new policy.
- **Concurrency:** the helper reads the candidate's current position inside the same transaction in which it just (in the create path) inserted the candidate, or (in the link path) the candidate already exists. A concurrent writer could theoretically PATCH a node's X/Y between the SELECT and UPDATE; the existing `CreateNode` site has the same property and has been live since PR #49 without incident. We do not introduce a row lock — the cost is not justified by the failure mode (a near-simultaneous PATCH wins or loses by milliseconds; either outcome is acceptable per the existing semantics).
- **Idempotency:** the helper is idempotent in the steady state — after the first successful application, the candidate is no longer at `(0,0)` and subsequent calls are no-ops. A re-execution of the same `LinkNodes` request fails earlier on the "already linked" check (`NodeService.cs:185`), so idempotency at the HTTP layer is unchanged.
- **Observability:** the existing `LinkNodes` log line in the controller (`"Linking node '{targetNodeId}' to '{sourceNodeId}'"`) is sufficient. The helper does not log — a successful auto-position is the expected outcome of a successful link operation; logging it separately would be noise. If a future incident requires distinguishing "link inserted, no position change" from "link inserted, position moved," a single `LogDebug` line in the helper is a one-line addition; we do not add it pre-emptively.
- **Error handling:** unchanged. The link-not-found / duplicate-link checks remain at their existing positions; the helper runs after the insert and shares the transaction's rollback path.
- **Embeddings:** untouched. Auto-positioning does not affect the `name`-derived embedding stored on the node.

## 10. Quality Attributes & Trade-offs

### Decision 1 — Helper extraction vs. inline duplication

**Verdict: extract a private helper.**

**Justification grounded in the code:** the auto-position block at `CreateNode:78-108` is approximately 30 lines that read like a self-contained subroutine — it loads a set of candidate-anchor rows, picks the first non-zero, calls `ComputeAutoPosition`, persists via `UPDATE`. The `LinkNodes` site needs the same body with `candidateId = source/target` and `anchorCandidates = [the other endpoint]`. Inline duplication would:
- Place ~20 lines of near-identical logic at two call sites.
- Couple the radius bump to a single literal (no risk there) but couple future changes (collision avoidance, multi-anchor averaging, lock-on-update) to two sites that must stay in sync.
- Re-introduce the pattern of "two places that do the same thing slightly differently" that the existing single-place implementation in `CreateNode` has so far avoided.

Extraction:
- Lets `CreateNode` shrink to a clearer "create the row, link it, anchor it if needed" three-step body.
- Makes `LinkNodes` a two-line addition rather than a 20-line bulge.
- Single ownership of the "is this candidate at (0,0) AND do we have an anchor?" decision.
- Costs nothing in surface area — the helper is a private instance method, not on `INodeService`.

The trade-off is one indirection in the call stack; that is negligible compared to the readability and sync-risk wins. **Extract.**

### Decision 2 — Radius value

**Verdict: 200.0.**

**Justification:** the user's framing is "at least double it good," which makes 160 the floor. The task's own caveat warns against going so large that a child node lands closer to a different unrelated parent than to its actual parent in a dense subgraph; the architect's eyeball estimate places that threshold somewhere above 250. The middle of that band, 200, gives:
- 2.5× the current separation — comfortably above the "merely double" minimum, into territory where a 4–6 child cluster around an anchor is visually readable at typical workspace zoom.
- A round number that is easy to remember and easy to revisit if the next round of UX feedback says "still too tight" or "now too loose" — the constant is the dial.
- A single-sourced literal (the existing `const double radius = 80.0` at `NodeService.cs:35`) — change one line, both call sites pick it up.

160 is conservative and barely satisfies the brief. 250 is closer to the upper safe limit and leaves no headroom if a future bump is requested. 200 is the right answer.

### Other trade-offs

- **Two helper calls per `LinkNodes` vs. one branching call:** chose two symmetric calls (see §6 rationale). Costs one extra SELECT-with-no-anchor in the both-positioned and both-zero cases (one query each, returning at most two rows); buys a cleaner helper contract.
- **Loading the candidate row in the same query as anchor candidates vs. a separate query:** chose single query. The helper reads `Id IN (candidateId, ...anchorCandidateIds)` in one `Load<Node>` and filters in memory which row is the candidate vs. anchors — a 2-3 row result that does not justify two round-trips.
- **Floating-point exact equality (`== 0.0`) vs. epsilon:** chose exact equality. The existing `CreateNode:79` and `CreateNode:93` rely on it; introducing an epsilon would diverge two adjacent semantics in the same service for no concrete benefit. Positions are either exactly the default `0.0` or set by an explicit caller value; an epsilon would mask, not catch, the only bug class this could touch.

## 11. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Existing `CreateNode` tests assert exact post-positions tied to `radius=80`. | High | Tests fail until updated. | The radius bump is a behaviour change in those tests' assertions, not in their semantics. Update the expected values to match `radius=200`. The mental-substitution invariant in #313 stays valid. |
| A caller does `CreateNode` with explicit `X=0, Y=0` *and* links and expects to stack at origin. | Low | Caller's intent overridden — node is auto-positioned. | This is the documented behaviour from #311 and is preserved. No change. Callers wanting stack-at-origin must set an explicit non-zero value or just live with the auto-position. |
| `LinkNodes` is called between two nodes where one is at `(0,0)` and the user actually wanted it to stay at the origin. | Low | The node moves unexpectedly. | This is the new behaviour the task is asking for. The "explicit position is sacred" rule still holds: any node that has been deliberately positioned (X or Y non-zero) is immune. Origin-stickiness for `(0,0)` is exactly the misfeature being repaired. |
| Concurrent PATCH races the auto-position UPDATE. | Very low | The later writer wins. | Existing `CreateNode` has the same property; no new mitigation needed. |
| Future cleanup of legacy `(0,0)` nodes that already have links (those that pre-date this PR). | Medium | Those nodes are not touched by this PR. | Out of scope per task #500. The CLI `LayoutNodesService` is the existing tool for one-shot sweeps. |
| Test `LinkNodes_*` cases share fixture state and pollute each other. | Low | Flaky tests. | Mirror the per-test in-memory SQLite fixture pattern in the existing `NodeAutoPositionTests` (PR #49); each case starts from a fresh schema. |

## 12. Migration / Rollout Strategy

No migration required. The change is purely additive at the behaviour layer:
- No schema change.
- No new endpoint.
- No new request/response field.
- Existing `(0,0)` nodes are not retroactively moved by deploying this change; only **future** `LinkNodes` calls trigger the new behaviour.

If desired, a one-shot `LayoutNodesService` run after the deploy will sweep historic origin-stuck nodes, but that is operational, not part of this PR.

## 13. Open Questions

None blocking. The two decisions (extract; 200.0) are made in §10; the four-cell behaviour table is fixed in §7; the helper contract is specified in §8.

A non-blocking forward question, for the reviewer or for a follow-up task: should the helper also be invoked by `UnlinkNodes`? Currently no — removing a link should not move anything; an unlinked node retains its position. The asymmetry (link can position, unlink cannot) is intentional and matches the user's framing. No action needed.

## 14. Implementation Guidance for the Next Agent (John)

Build in this order; the order minimises sync-risk between intermediate states.

### Phase 1 — Helper extraction (no behaviour change yet)

1. Bump the `radius` constant at `NodeService.cs:35` from `80.0` to `200.0`. Single literal change.
2. Add a new private instance method on `NodeService`, e.g. `TryAnchorOrphanToPositioned`, with the contract specified in §8:
   - Parameters: the ambient transaction, a candidate node id, and an enumerable of anchor candidate ids (preserve iteration order — the first non-zero anchor wins, matching the existing tie-breaker).
   - Body: one `Load<Node>` projecting `Id, X, Y` filtered on `Id IN (candidateId, ...anchorCandidates)`; iterate with `await foreach` into a `Dictionary<long, Node>` (mirror `CreateNode:82-88`); look up the candidate row, return early if its position is non-`(0,0)`; iterate anchor candidates in order, pick the first whose position is non-`(0,0)`, return early if none; call `ComputeAutoPosition` and issue a single `Update<Node>` of X/Y filtered on `Id == candidateId`.
3. Refactor `CreateNode` (lines 78–108) to call the new helper. The auto-position branch becomes effectively a single helper call inside the `if (insertX == 0.0 && insertY == 0.0)` guard. Confirm the existing `NodeAutoPositionTests` still pass (with updated expected values to match `radius=200`).

### Phase 2 — Extend `LinkNodes`

4. In `LinkNodes` at `NodeService.cs:178`, after the existing `Insert<NodeLink>().ExecuteAsync(transaction)` and before `transaction.Commit()`:
   - Call the helper with `(candidate=sourceNodeId, anchorCandidates=[targetNodeId])`.
   - Call the helper with `(candidate=targetNodeId, anchorCandidates=[sourceNodeId])`.
   - Both calls thread the existing `transaction`.

### Phase 3 — Tests

5. Add four positive cases in a new `Backend.tests/Tests/NodeLinkAutoPositionTests.cs` (or extend the existing `NodeAutoPositionTests.cs` file if it makes the suite shape cleaner; per existing pattern from PR #49, a new file mirroring the existing one is cleaner). Cases:
   - **T1** — source at `(0, 0)`, target at `(100, 200)`; after `LinkNodes(sourceId, targetId)`, assert source position equals `ComputeAutoPosition(sourceId, 100, 200)` exactly.
   - **T2** — symmetric: source at `(100, 200)`, target at `(0, 0)`; assert target position equals `ComputeAutoPosition(targetId, 100, 200)`.
   - **T3** — both positioned at known coordinates; assert both remain unchanged after linking.
   - **T4** — both at `(0, 0)`; assert both remain `(0, 0)` after linking (the link itself is inserted regardless).
6. Add a radius-bump lock test: link a `(0, 0)` candidate to an anchor at `(0, 0)` — wait, that's T4 and is degenerate. Instead: link a `(0, 0)` candidate to an anchor at, say, `(0, 0)` is the no-op case; for the radius lock, use the same fixture as T1 (anchor at `(100, 200)`) and assert that the resulting candidate position's Euclidean distance from the anchor equals `200.0` (the new constant), within a small tolerance. Negative-proof: setting the constant back to `80.0` breaks this test specifically.
7. Document the negative-proof substitutions in the PR body, mirroring #313's pattern. For each positive test, state the one-line change in production code (e.g., "comment out the second helper call in LinkNodes") that causes the test to fail and quote the failure message.

### Phase 4 — Doc commit

8. Commit this architecture document to the implementation branch at `docs/architecture/auto-position-on-link.md` (CLAUDE.md design-doc rule).

### §3 smell-guards (do NOT trip these — Jenny will cite the section)

- **No `var`.** Explicit types throughout the helper, including `Dictionary<long, Node>`, `Node`, `double`, `(double X, double Y)`.
- **No redundant `async`/`Task` wraps.** The helper is genuinely async (awaits a `Load` enumerator and an `Update`); a lambda passthrough wouldn't apply. Watch the *call sites*: do not introduce `async () => await TryAnchorOrphanToPositioned(...)` or `Task.FromResult(...)` wraps when threading through.
- **No new transaction.** The helper uses the supplied `Transaction` parameter. Do not call `database.Transaction()` inside it. The existing `LinkNodes` transaction is the boundary.
- **No list-materialisation of streamed enumerables.** The `Load<Node>` results are consumed by `await foreach` into a small dictionary, as in `CreateNode:83-88` — do **not** call `.ToList()` / `.ToArrayAsync()` first. The dictionary load is a deliberate small-result-set materialisation; the streamed source is the right shape.
- **No in-process filtering of DB-side predicates.** Filter on `Id IN (...)` in the SQL, not by loading all rows and `.Where(...)`-ing in memory. (Match `CreateNode:84`'s `.Where(n => n.Id.In(links))`.)
- **Single statement per transaction is not a smell here.** `LinkNodes`'s transaction wraps the existence check, the duplicate check, the insert, and now the helper calls — that's multi-statement and correct. Do not collapse to a no-transaction form just because a single mental step (the helper call) sits inside it.
- **K&R braces, two blank lines between methods, no XML doc on private helpers** (per §3 and Backend conventions).

### What John does NOT do

- Does not change `INodeService.LinkNodes` signature.
- Does not change `NodeController.LinkNodes` (line 181) — that route is correct as is.
- Does not touch `LayoutNodesService` — out of scope.
- Does not introduce collision-avoidance — out of scope.
- Does not retroactively re-position existing `(0,0)` nodes — out of scope; that is the CLI's job if Toni asks for a sweep.
- Does not open the PR until the architecture doc is committed and the tests pass locally on both SQLite (default) and (if convenient) Postgres.
