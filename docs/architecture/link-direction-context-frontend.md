# Architectural Document: Link Direction + Context on the Workspace Graph (Frontend)

> Design-only deliverable. Implementer: **Pierre (frontend)**. No backend unit required (see §4/§10). Companion to the merged backend design (DiVoid #7120) and source task DiVoid #7142.
>
> Load-bearing contracts: **Frontend Code Contracts #420** (Hooks §6, TanStack §8, Side-effects §10, Testing §13, TSDoc §4) and **Design Contracts #1136** (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §4 less-is-better, §5 checklist). Both applied throughout; the §5 Pre-Design Checklist and §6 anti-pattern audit are walked in §14–§15.

---

## 1. Problem Statement

The `/workspace` graph currently renders every edge as an identical plain line with no orientation and no label. The backend (merged, DiVoid #7120) now carries two new pieces of metadata on each `NodeLink` edge:

- **`LinkType`** — `{ None, Unidirectional, Bidirectional }` — the direction semantics of the edge.
- **`Context`** — an optional free-text label, interpreted source → target.

The goal is to **surface both on the frontend**, in two places:

1. **Graph rendering** (`features/workspace/`): edges show direction (unidirectional = one arrowhead at the target end; bidirectional = arrowheads at both ends; none = plain line) and the context string on/near the edge.
2. **Authoring** (`features/nodes/LinkNodeDialog.tsx`): the "Add link" dialog gains a link-type selector and an optional context field, passed through the link mutation to the backend as the `linkType` + `context` write params.

**Success criteria.** A user can (a) create a link with a chosen direction and a context label from the dialog, and (b) see that direction (arrowheads) and label rendered on the workspace canvas for any edge whose both endpoints are in view — **without regressing the render-stability contract (#1261) or the single-viewport-call fold intent (#310)**.

## 2. Scope & Non-Scope

**In scope**
- Frontend consumption + rendering of `LinkType` and `Context` on the workspace canvas.
- Extending `useLinkNodes` to send `linkType` + `context` as write params.
- Adding the two inputs to `LinkNodeDialog`.
- The data-flow decision for how the canvas obtains per-edge metadata (§4 — the crux).
- The reconcile + FloatingEdge changes required for correctness and reference-stability.
- The test-contract change to `workspaceFold.test` (§13) with its justification.

**Out of scope (explicitly)**
- **Any backend change.** The merged endpoint already serves everything needed (§4, §10). No `fields=linkDetails` inline shape, no new DTO, no John unit.
- **Editing the direction/context of an *existing* edge.** Backend #7120 locked "change an edge = unlink+relink" (no link PATCH). The dialog authors *new* links only. Editing an existing edge's metadata is a named follow-on, not this task.
- **Directional filtering / traversal semantics.** `linkedto` stays both-directions (backend #7120 locked decision #4). Direction is render-metadata only.
- **Per-direction context pair.** Backend carries a single `Context` string (source → target). No second reverse-direction label.
- **Setting direction/context on drag-to-connect (`onConnect`).** Drag-to-connect stays the fast path and creates a `None`/no-context edge (back-compat, and the dialog is the deliberate authoring surface). Not a regression — matches the merged backend default.
- **Mobile/responsive canvas.** Per FE contract §14.6, out of scope until requested.

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Backend serializes `NodeLink` as camelCase JSON with fields `sourceId`, `targetId`, `linkType`, `context`. `linkType` is the **string enum name** (`"None"`/`"Unidirectional"`/`"Bidirectional"`) via `JsonStringEnumConverter`. | High — matches existing `NodeLink` FE type casing + global `JsonStringEnumConverter` registration (project CLAUDE.md). Pierre must confirm casing on first live read (FE contract §13.11). |
| A2 | `GET /api/nodes/links?ids=1,2,3` returns all edges incident to any listed id (`SourceId IN ids OR TargetId IN ids`) **including** `linkType` + `context` in the projection. | **Verified in merged code** — `NodeService.ListLinks` line 1173–1174. |
| A3 | The write path accepts `linkType` + `context` as **query params** on `POST /api/nodes/{source}/links`, body stays bare `targetId`. | High — backend #7120 write-surface decision; MCP PR #164 mirrors it. Verified `LinkNodes(..., LinkType linkType = None, string context = null)` signature exists (NodeService line 220). |
| A4 | Query params bind the enum from its string name (ASP.NET default enum model binding). | High — MCP PR #164 sends the string name. |
| A5 | `xyflow` (`@xyflow/react`) supports per-edge `markerStart`/`markerEnd` objects (`{ type: MarkerType.ArrowClosed }`) flowing through `EdgeProps`, and `EdgeLabelRenderer` for edge labels. | High — standard xyflow API; FloatingEdge already receives + forwards `markerEnd`. |

**Hard constraints inherited from the codebase**
- Render-stability (#1261): `nodes`/`edges` arrays are reconciled to preserve references; a broken reconcile causes the "scroll-blink" regression.
- Single-viewport-call fold (#310): edge **set** is derived from inline `links[]`, not from a separate adjacency call. (This design preserves that intent — see §4.)
- FE contract §6.5 stale-closure, §8 query-key shape + `enabled` gates, §10 no render-body side-effects, §13 load-bearing tests, §4 TSDoc discipline.

## 4. Architectural Overview — The Crux Decision

### The problem restated
The canvas builds its edge **set** from `NodeDetails.links[]` — a flat `long[]` neighbor array with **no orientation and no context** (`buildEdgesFromInlineLinks`). `LinkType` + `Context` live **only** on the raw `NodeLink` rows served by `GET /api/nodes/links`. So the canvas must obtain per-edge metadata from a source it does not currently read, without regressing render-stability or the fold intent.

### Options weighed

**Option (a) — Reuse the existing `/api/nodes/links?ids=<visible node ids>` endpoint as a secondary enrichment query, join client-side. → CHOSEN.**
- The endpoint **already exists and already does exactly this**: it filters by the exact node-set the canvas already holds (`SourceId.In(ids) || TargetId.In(ids)`) and **already projects `linkType` + `context`** (verified, `NodeService.ListLinks`). No backend change.
- The edge **set** still comes from inline `links[]` (fast, already there). The enrichment layers metadata on top → **progressive enhancement**: edges paint immediately as plain lines, then upgrade to arrows + labels when the second query resolves. Nothing blocks on it.
- Cost: one additional GET per visible node-set, at the **same cadence** as the (debounced) viewport query, gated so it never fires for an empty set.

**Option (b) — Backend opt-in enrichment of the viewport read path (e.g. a richer `fields=linkDetails` inline shape). → REJECTED.**
- The inline `links[]` is a flat `long[]`; direction is **structurally unrepresentable** there (backend #7120 said so explicitly). Carrying direction + context inline requires a **new parallel per-row shape** (an array of `{targetId, linkType, context}` objects) — a new DTO, a new field selector, a new projection, and a whole backend unit.
- This is a **Design Contracts §2 Form-2 "parallel layer"** + **§4 compromise-shape**: it duplicates, into the viewport read path, a capability the `/api/nodes/links` endpoint *already provides for the exact same node-set*. It exists only to save one HTTP round-trip — a YAGNI-grade optimization with no measured need.
- It also reintroduces the orientation ambiguity the flat shape was designed to avoid: an edge `{A,B}` appears in both A's and B's rows, so an inline shape must arbitrarily pick which row is canonical. The raw `NodeLink` carries true `sourceId → targetId` orientation directly.
- **Rejected on §2 (existing-systems-first) + §4 (less-is-better) + KISS.** Reusing an endpoint that already returns the exact shape beats building a second surface.

**Option (c) — Lazy per-edge fetch on hover/selection. → REJECTED.** The requirement is to render arrows + labels for *all* visible edges, so all visible edges need metadata; per-edge fetch is N requests. Fails the requirement and is slower.

### Relationship to the #310 fold (why this is not a regression)
The #310 fold removed the old adjacency call because the edge **set** was derivable from inline links, making the adjacency call *redundant for building edges*. The enrichment call here is **not redundant**: it carries metadata that exists nowhere else. The invariant #310 protected — *the edge set is derived from inline links, not from a separate adjacency call* — **still holds**. The enrichment is a new, additive concern. (`workspaceFold.test` pins the *letter* "zero `/nodes/links` calls"; that letter must change while its *intent* is preserved and re-expressed — see §13.)

### High-level shape

```
                       useNodesInViewport (existing, unchanged)
                        │  GET /nodes?bounds=..&fields=..,links   (ONE call — the edge SET)
                        ▼
   visibleDetails[]  ───┬────────────────────────────────────────────┐
                        │                                             │
        visible id set  │                                             │ buildEdgesFromInlineLinks
                        ▼                                             ▼  (edge set, plain)
              useLinkDetails(ids)  (NEW, additive)            baseEdges: Edge[]  (id, source, target)
       GET /nodes/links?ids=..  →  NodeLink[] w/ linkType,context      │
                        │                                             │
                        └──────────────►  joinLinkMetadata  ◄─────────┘
                                            (pure fn, §8)
                                                 │
                                 enriched Edge[] (source/target normalized to
                                 link orientation; markerStart/markerEnd;
                                 data:{linkType,context})
                                                 │
                                        reconcileEdges (§ change)
                                                 ▼
                                    FloatingEdge (arrows + label)
```

## 5. Components & Responsibilities

| Component | Owns (this change) | Does NOT own |
|---|---|---|
| **`useLinkDetails(ids)`** (NEW hook, `features/workspace/`) | Fetch `NodeLink[]` for a set of visible node ids via `GET /api/nodes/links?ids=..`; `enabled` gate on non-empty ids; TanStack query key `['nodes','linkDetails', ids]`; `staleTime` aligned with viewport. | Building the edge set (that stays inline). Any orientation/marker logic. |
| **`joinLinkMetadata`** (NEW pure fn, `features/workspace/`) | Given base edges (from inline links) + `NodeLink[]`, produce enriched edges: normalize `source`/`target` to the link's true orientation, attach `markerStart`/`markerEnd` per `linkType`, attach `data:{linkType,context}`. Pure + exported for unit test (§13). | Networking, rendering. |
| **`buildEdgesFromInlineLinks`** (existing, `WorkspaceCanvas.tsx`) | Unchanged — still emits the plain edge set keyed `${lo}-${hi}`. | Metadata. |
| **`reconcileEdges`** (existing, `reconcile.ts`) | EXTENDED: include edge `data` (linkType + context) in the equality decision so metadata updates propagate; preserve the reference-bail-out contract. | Fetching, marker construction. |
| **`FloatingEdge`** (existing) | EXTENDED: read `markerStart` + `data.context`; render both arrowheads when present and a midpoint label when context is non-empty; keep memo + intersection geometry intact. | Deciding *which* markers apply (that is `joinLinkMetadata`'s job). |
| **`WorkspaceCanvas`** (existing) | Wire `useLinkDetails` from the visible id set; fold `joinLinkMetadata` into the `xyEdges` memo. | New network shapes beyond the join. |
| **`LinkNodeDialog`** (existing, `features/nodes/`) | Add link-type selector + optional context input; pass through `useLinkNodes`. | Rendering on canvas. |
| **`useLinkNodes`** (existing, `mutations.ts`) | EXTENDED: accept optional `linkType` + `context`; send as query params (via `buildQueryString`) on the POST; body stays bare `targetId`. | Dialog UI. |
| **Types** (`types/divoid.ts`) | Add `LinkType` union + `linkType`/`context` to the `NodeLink` interface; define the edge `data` shape. | — |

## 6. Interactions & Data Flow

### Flow 1 — Render an enriched edge (read path)
1. `useNodesInViewport` returns `visibleDetails[]` with inline `links[]` (unchanged, ONE bounds call).
2. `WorkspaceCanvas` derives the visible id set and passes it to `useLinkDetails(ids)` (gated `enabled: ids.length > 0`).
3. `buildEdgesFromInlineLinks` produces the base edge set (plain lines) — rendered immediately.
4. When `useLinkDetails` resolves, the `xyEdges` memo re-runs and `joinLinkMetadata` merges metadata: for each base edge whose canonical pair matches a returned `NodeLink`, it normalizes `source`/`target` to `link.sourceId → link.targetId`, sets `markerEnd` (unidirectional/bidirectional), `markerStart` (bidirectional only), and `data:{linkType,context}`.
5. `reconcileEdges` detects the changed edges (source/target flip and/or data change) and updates only those; unchanged edges keep their prior reference (no blink).
6. `FloatingEdge` renders arrowheads from the markers and a midpoint label from `data.context`.

**Orientation note (load-bearing).** `buildEdgesFromInlineLinks` assigns `source`/`target` in *iteration* order (whichever endpoint's row is hit first), which is arbitrary relative to the true link direction. For a unidirectional arrow the head must sit at `NodeLink.targetId`. Therefore `joinLinkMetadata` **re-writes `source`/`target` to match the `NodeLink` orientation** for directed edges; `markerEnd` then always sits at the semantic target. The edge **id stays canonical `${lo}-${hi}`**, so reconcile keying by id is stable across a source/target flip.

### Flow 2 — Author a link with direction + context (write path)
1. User opens `LinkNodeDialog`, searches, selects a target, picks a link type (default `None`), optionally types a context string.
2. `handleConfirm` calls `useLinkNodes.mutateAsync({ sourceId, targetId, linkType, context })`.
3. `useLinkNodes` builds the POST URL as `LINKS(sourceId)` + query string for `linkType` (omitted when `None`) and `context` (omitted when empty); body remains the bare `targetId`.
4. On success it invalidates the linkedto + viewport caches (existing behavior). Viewport invalidation refreshes inline links; the enrichment query re-fetches metadata via its own key. Arrows appear on the canvas after refetch.

### Communication summary
| Interaction | Transport | Sync/Async | Notes |
|---|---|---|---|
| Viewport nodes + inline links | `GET /nodes?bounds&fields=..,links` | async (TanStack) | Unchanged. |
| Edge metadata | `GET /nodes/links?ids=..` | async (TanStack, additive) | New. Gated. Same cadence as viewport. |
| Author link | `POST /nodes/{source}/links?linkType=..&context=..` | async (mutation) | Body = bare targetId. |

## 7. Data Model (Conceptual)

- **NodeLink (edge)** — endpoints `sourceId`, `targetId`; `linkType ∈ {None, Unidirectional, Bidirectional}`; optional `context` string interpreted source → target. Owned by the backend; the frontend is a read/write consumer.
- **Enriched xyflow Edge** — identity `${min}-${max}` (canonical, orientation-independent); presentational `source`/`target` (normalized to link orientation for directed edges); `markerStart`/`markerEnd` (derived, not persisted); `data:{ linkType, context }`. Purely a view-model; nothing new is persisted client-side.

No new persisted entity. No schema. No client-side store beyond the TanStack query cache.

## 8. Contracts & Interfaces (Abstract)

**`useLinkDetails(ids: number[])`**
- Input: the visible node id set.
- Output: a query result whose data is a collection of link rows `{ sourceId, targetId, linkType, context }`.
- Semantics: `enabled` only when `ids` is non-empty; query key includes a stable serialization of `ids` (sorted) so identical sets dedupe and panning to a new set refetches. Returns edges incident to *any* id, including some with an endpoint outside the viewport — those are simply not matched during the join (harmless).
- Invariant: never fires an empty-set request (FE contract §6.6/§8.3).

**`joinLinkMetadata(baseEdges, linkRows)` → `Edge[]`**
- Input: plain base edges (canonical id, arbitrary source/target) + link rows.
- Output: enriched edges. For each base edge, look up its canonical pair `${min}-${max}` in a map built from `linkRows`. If found and directed, set `source = String(sourceId)`, `target = String(targetId)`, markers per `linkType`, `data:{linkType,context}`. If not found (metadata not yet loaded or `None`/no-context), the edge is returned as a plain line (no markers, empty/absent `data`).
- Invariants: pure (no side effects); canonical id never changes; a missing metadata row degrades to a plain edge (progressive enhancement, never an error).

**`useLinkNodes` (extended)**
- Input: `{ sourceId, targetId, linkType?, context? }`.
- Semantics: `linkType` omitted from the query string when `None`; `context` omitted when null/empty. Body stays the bare `targetId`. All existing behavior (bug #317 already-linked graceful path, cache invalidation) unchanged. Drag-to-connect callers pass neither → identical to today.

**FloatingEdge props (extended)**
- Now also consumes `markerStart` and `data` (for `context`). Behavior: render `markerEnd`/`markerStart` when present; render a midpoint label when `data.context` is a non-empty string; otherwise identical to today.

## 9. Cross-Cutting Concerns

- **Render-stability (#1261).** The whole design hinges on `reconcileEdges` including edge `data` + orientation. Enriched edges are re-allocated in the `xyEdges` memo on every metadata change, but `reconcileEdges` collapses unchanged edges back to their prior reference (including the `data` and marker sub-objects), so `FloatingEdge`'s `memo` shallow-compare skips untouched edges. **This is the single most important correctness point** and is called out again in §11.
- **Progressive enhancement / error handling.** Edges always render from inline links first. If `useLinkDetails` is slow or fails, edges stay plain — no arrows, no crash. `retry: false` for the enrichment query (a metadata read that fails is non-fatal; don't retry-storm — FE contract §8.6). No error toast for a failed enrichment (it is a non-blocking nicety, not a user action).
- **Consistency model.** Eventually consistent between the two queries within one debounce cycle. A transient window where edges are plain before metadata arrives is expected and acceptable.
- **Idempotency / concurrency.** Read-only enrichment; no idempotency concern. The write path re-uses the existing idempotent-relink backend contract (params silently dropped on a re-link, per #7120 — documented trade-off, not this design's concern).
- **Security/authorization.** Both queries go through the existing auth-aware `useApiClient`; no new surface. Link visibility follows the backend's existing gate.
- **Observability.** No new logging; the existing dev-logging in the client covers the new GET.
- **Accessibility.** The context label is decorative-adjacent; ensure it has sufficient contrast in both themes (FE contract §14.2 `dark:` variants). The dialog's new controls need labels/`aria` consistent with the existing dialog fields.

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed | Trade-off made |
|---|---|---|
| **Performance** | One additional GET per visible node-set, at the debounced viewport cadence, gated to skip empty sets. Edge set still one bounds call. | +1 request vs. option (b)'s single call. **Explicit call:** the +1 request is cheap and cache-deduped; option (b)'s cost is a permanent parallel backend surface (§2 Form-2). One request now beats a duplicated read shape forever. |
| **Simplicity (KISS)** | Reuse an endpoint that already returns the exact shape; one new pure join fn; one new thin hook; minimal edits to reconcile/FloatingEdge/dialog/mutation. | None material. |
| **Maintainability** | No backend change; no new DTO; no parallel inline shape. The join is a small pure function with a unit test. | — |
| **Render-stability** | reconcile extended to cover the new fields; FloatingEdge memo preserved. | Slightly more per-edge comparison work in reconcile (two extra field compares) — negligible. |
| **Reusability** | `useLinkDetails` + `joinLinkMetadata` are generic enough for any future edge-metadata need. | Not built *for* a future need (YAGNI-safe) — they are the minimal shape this requirement needs. |

**Rejected alternative recap:** option (b) (inline backend enrichment) — rejected as a §2 parallel layer + §4 compromise shape saving one round-trip at the cost of a permanent duplicated surface and reintroduced orientation ambiguity. Option (c) (lazy per-edge) — rejected as N-requests that fail the "all visible edges" requirement.

## 11. Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| **reconcileEdges not updated for `data`/orientation** | Arrows + labels silently never appear (metadata update dropped) — the exact crux failure. | §5/§9/§12 mandate the reconcile change; §13 requires a load-bearing test that reverts the reconcile edit and asserts the arrow/label disappears. |
| **workspaceFold test breaks on the new `/nodes/links` call** | CI red; looks like a regression. | §13: the test is deliberately re-scoped. Its *intent* (edge set from inline links, ONE bounds call, no adjacency call to **build** edges) is preserved and re-expressed; the "zero `/nodes/links`" letter is replaced by "≤1 enrichment call that does not source the edge set." Justified per #275/§13.1. |
| **Orientation wrong (arrow points backwards)** | Misleading direction. | `joinLinkMetadata` normalizes `source`/`target` to the `NodeLink` orientation; unit test asserts marker end sits at `targetId` even when the base edge was built target-first. |
| **Marker object identity churn breaks FloatingEdge memo** | Re-render storm. | Markers live inside the edge object; reconcile preserves the whole edge reference when unchanged, so marker sub-objects stay reference-stable. Verified by the render-stability harness (§13.7). |
| **JSON casing / enum-name assumption wrong (A1/A4)** | Fields read as undefined; params rejected. | Pierre confirms via a live read (FE contract §13.11) before finalizing; the `NodeLink` FE type + `JsonStringEnumConverter` make camelCase + string-name the strong default. |
| **Enrichment query-key churn on pan** | Excess refetches. | Key on the *sorted* visible id set; the set only changes when the debounced bounds change — same cadence as the viewport query. |

## 12. Migration / Rollout Strategy

Single atomic frontend change, no migration. Two independently-valuable units → **two PRs** (per PR-scope discipline), in dependency order:

- **PR 1 (authoring):** `LinkNodeDialog` inputs + `useLinkNodes` params + types. Ships and is valuable alone (users can author directed/labeled links; they just render as plain lines until PR 2).
- **PR 2 (rendering):** `useLinkDetails` + `joinLinkMetadata` + reconcile + FloatingEdge + the `workspaceFold` test re-scope. References PR 1.

No feature flag, no deprecation window (private monorepo + atomic deploy — Design Contracts §5).

## 13. Testing Strategy (load-bearing, FE contract §13 / #275)

- **`joinLinkMetadata` unit tests (pure):** (a) directed edge built target-first → output normalizes so marker end is at `targetId`; (b) bidirectional → both markers set; (c) `None` → no markers; (d) context present → `data.context` set; (e) missing metadata row → plain edge (progressive-enhancement degrade). Each is a real substitution: reverting the corresponding branch flips a concrete assertion.
- **`reconcileEdges` test:** prev edge plain, incoming edge same id but with `data.linkType='Unidirectional'` → reconcile returns a **new** reference (not the bailed-out prev). Reverting the `data`-equality addition makes reconcile return prev → the test fails. This directly pins the crux fix.
- **`FloatingEdge` test:** given an edge with `markerStart` + `data.context`, asserts both marker ends and the label text render; degrades to plain with `None`.
- **`workspaceFold.test` re-scope (justified change to a load-bearing test):** replace the "ZERO `/nodes/links` calls" assertion with: (1) the edge **set** is still built from inline links (the `fields=links` assertion stays — this is the #310 intent); (2) at most one enrichment call to `/nodes/links` fires and it does **not** source the edge set (edges render before it resolves). The intent #310 protected is preserved; the letter changes because a new metadata concern legitimately needs the endpoint. **Pierre must state this substitution explicitly in the PR body** (§15.7).
- **Dialog + mutation test:** confirming a link with `Unidirectional` + a context string asserts the POST URL carries `linkType=Unidirectional&context=...` and the body is the bare `targetId` (FE contract §13.2 — assert production call args, not spy-called-once).
- **Render-stability harness (§13.7):** unchanged; must stay green (no new render loop from the second query).

## 14. Pre-Design Checklist (Design Contracts #1136 §5)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing one — `LinkType` mirrors the backend enum 1:1 (a required contract mirror, not a parallel design), no new frontend-only enum.
- [x] No new abstraction with one impl and no second — `useLinkDetails`/`joinLinkMetadata` are the minimal shapes the requirement needs, not speculative hooks.
- [x] No element justified by "might need later."
- [x] No deprecation/flag/shim (atomic deploy).
- [x] No inline-N-sites duplication decision.

**Existing systems first**
- [x] Audited: `GET /api/nodes/links` already serves the exact shape for the exact node-set → reused, no new layer.
- [x] No new layer proposed; option (b)'s new backend surface was explicitly rejected as §2 Form-2.
- [x] No new persisted data point.
- [x] No transitive-dead-code: every new field (`linkType`, `context`) has a named consumer (FloatingEdge arrows/label + dialog authoring).

**Configurability**
- [x] No new config knobs. Marker style (`ArrowClosed`) is a fixed presentational constant, not a config value (§3).

**Less is better**
- [x] Each element passed can-it-be-deleted/merged/inlined: enrichment can't be inlined into the viewport call without option (b)'s parallel surface; the join is the minimal pure fn.
- [x] The complex alternative (b) is named + rejected with the trade-off exercise (§4, §10).
- [x] Existing surface (`/nodes/links`) reused rather than a compromise inline shape.

**Document discipline**
- [x] Cites Code Contracts (#420) + Design Contracts (#1136) as load-bearing.
- [x] Reader/scope inventories explicit (§5 table).
- [x] Out-of-scope listed explicitly (§2).
- [x] No multi-paragraph "why keep X" filler.
- [x] No predecessor design superseded (this is net-new frontend work atop merged backend #7120).

## 15. §6 Anti-Pattern Audit (Design Contracts #1136 §6)

- **Audit columns / telemetry-then-tune / config-for-future:** none — no persisted data, no knobs.
- **Abstraction-for-future-flexibility:** the two new units are the minimal shape for the concrete requirement, not speculative extensibility.
- **Parallel layer:** the one candidate (option b, a second inline link representation) is identified and rejected.
- **Compatibility shim / deprecation window:** none.
- **Defensive code for impossible scenarios:** the "missing metadata row → plain edge" branch is *not* defensive theater — it is the real progressive-enhancement path (metadata genuinely arrives after the edge set).
- **SQL/schema anti-patterns:** N/A (no data deliverable).
- **Superseded-design-left-live:** N/A.

## 16. Implementation Guidance for the Next Agent (Pierre)

Ordered, PR-decomposed. No code here — architectural units only.

**PR 1 — Authoring (dialog + mutation + types)**
1. Types: add a `LinkType` union (`'None' | 'Unidirectional' | 'Bidirectional'`) and add `linkType`/`context` to the frontend `NodeLink` interface (`types/divoid.ts`).
2. `useLinkNodes`: extend `LinkNodesInput` with optional `linkType` + `context`; build the POST URL with `buildQueryString` (omit `linkType` when `None`, omit `context` when empty); body stays the bare `targetId`. Leave all existing invalidation + bug-#317 handling intact.
3. `LinkNodeDialog`: add a link-type selector (default `None`) and an optional context text input; thread both into `handleConfirm`. Keep the existing search/select flow untouched. Extend the link schema if the context needs validation (length cap only; keep permissive per schemas.ts convention).
4. Tests: dialog confirm asserts the POST URL query params + bare body (§13).

**PR 2 — Rendering (enrichment + join + reconcile + edge)**
5. `useLinkDetails(ids)`: new TanStack hook → `GET /api/nodes/links?ids=..`; key `['nodes','linkDetails', sortedIds]`; `enabled` on non-empty; `retry:false`; `staleTime` aligned to the viewport query.
6. `joinLinkMetadata`: new exported pure fn per §8; unit-tested per §13.
7. `WorkspaceCanvas`: derive the visible id set, call `useLinkDetails`, fold `joinLinkMetadata` into the `xyEdges` memo. Keep the memo dependency list correct (`[visibleDetails, linkDetailsData]`).
8. `reconcileEdges`: include edge `data` (linkType + context) — and note that orientation changes already surface via the existing source/target compare. Preserve the reference-bail-out. Unit-test the crux (§13).
9. `FloatingEdge`: destructure + forward `markerStart`; render a midpoint context label via `EdgeLabelRenderer` when `data.context` is non-empty. Keep `memo`, the intersection geometry, and the null-guards unchanged.
10. `workspaceFold.test`: re-scope per §13 with the substitution documented in the PR body.
11. Confirm A1/A4 (JSON casing + enum-name) via a live read before finalizing (§13.11).

**Cross-cutting for both PRs:** TSDoc on every new export (FE §4), `dark:` variants on any new color-bearing UI (§14.2), no render-body side-effects (§10), exhaustive hook deps (§6.2).

## 17. Open Questions

1. **Enrichment query keying** — key on the sorted visible id set (recommended, dedupes) vs. on the debounced bounds. Recommendation: sorted id set. Confirm no pathological churn on rapid pan (the debounce should absorb it).
2. **Context label affordance at zoom-out** — should labels hide below a zoom threshold to avoid clutter on dense graphs? Not required for correctness; flag as a possible follow-up, not part of this scope.
3. **A1/A4 casing/enum-name** — a 2-minute live read resolves it; noted as an assumption, not a blocker.

---

*Applies Frontend Code Contracts #420 and Design Contracts #1136. Companion to backend design #7120; source task #7142.*
