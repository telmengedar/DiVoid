# Architectural Document: Free-Workspace Graph View (PR 4)

**Author:** Sarah (software architect)
**Date:** 2026-05-12
**Status:** Proposed — sequencing two-PR roadmap, backend-then-frontend
**Umbrella:** [DiVoid node #223](https://divoid.mamgo.io/api/nodes/223)
**Backend task:** [DiVoid node #232](https://divoid.mamgo.io/api/nodes/232) — positional metadata + viewport-bounds filter
**Frontend task:** [DiVoid node #230](https://divoid.mamgo.io/api/nodes/230) — workspace graph view (PR 4)
**Prior design:** [DiVoid node #225](https://divoid.mamgo.io/api/nodes/225) — bootstrap design (§5.5, §7, §11, §14 PR-4 row); this document supersedes its PR-4 row.
**Code contracts:** [DiVoid node #114](https://divoid.mamgo.io/api/nodes/114) (mamgo) — applied here in spirit; DiVoid uses Pooshit.Ocelot via the same idioms.

---

## 1. Problem Statement

DiVoid's free-workspace mode is the next-dimension UX: instead of search results and lists, the user sees the graph **as a graph** — typed nodes at stable 2D positions, with their links drawn between them, in a viewport the user can pan and zoom. Two distinct halves of the work are required:

- **Backend (DiVoid task #232):** add persisted X / Y positional metadata to the `Node` entity and a viewport-bounds listing filter so the frontend can ask "give me the nodes inside this rectangle." Positions are shared across users — there is one canonical layout per node.
- **Frontend (DiVoid task #230, PR 4):** consume the new API, render the viewport with `@xyflow/react`, support drag-to-reposition with PATCH-on-drag-end, drop-file-to-create-node, click-empty-to-create-node, and keep render stability under interaction (drag, pan, zoom) per the load-bearing-test rule (#275) and the render-stability rule (#271).

Toni's framing (2026-05-12):

> "the workspace mode, as it brings a whole new dimension to the frontend"
> "Graph layout I want to leave free like designed now. Basically show all nodes in current viewport and the connections of them (probably needs some positional information as currently there is no 'in viewport')."

**Success criteria for PR 4 (the combined work):**

- `Node` carries persisted `X` and `Y` doubles; both are `[AllowPatch]` so `PATCH /api/nodes/{id}` with `replace /X` and `replace /Y` works without a special endpoint.
- `GET /api/nodes?bounds=<x1,y1,x2,y2>` returns nodes whose persisted position falls inside the rectangle, composing with existing `type`, `linkedto`, `query`, `status`, etc.
- The frontend `/workspace` route renders the viewport: nodes positioned at their X / Y, links between visible nodes drawn, pan + zoom local-only, drag-end persists via PATCH.
- The workspace mounts and survives drag / pan / zoom without triggering React's max-update-depth — verified by a load-bearing test that fails when the stability guard is removed.

---

## 2. Scope & Non-Scope

### In scope

- Schema additions to `Node` (X, Y) and supporting backend listing filter.
- Backend test strategy for the additive change and the new filter.
- Frontend graph-viz library decision (re-confirmed or rotated).
- Workspace component shape, interaction model, viewport / position state ownership.
- File-drop-to-node interaction (reuses existing `useUploadContent`).
- Render-stability strategy and the load-bearing-tests this PR must include.
- PR decomposition + sequencing.

### Out of scope (filed as sibling tasks where applicable)

- Width / Height on `Node` (resizable / shaped nodes) — deferred until there is a user-visible reason.
- Per-user position overlays — shared layout is the contract; per-user is a follow-up if it ever earns its weight.
- Edge labels, link creation by drag, multi-select, undo / redo, mini-map, sub-canvases, search-within-workspace.
- Real-time multi-user updates (no node-position broadcast channel).
- Mobile-touch interactions.
- The graph-viz library's *visual* polish (colours, badges, motion) — Pierre's call at implementation time per the orchestrator guidance.

### Hard non-scope (per #250)

- CI workflow files, Dockerfile, nginx.conf, deploy descriptors — handled by the separate infrastructure session.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Notes |
|---|---|---|---|
| A1 | Frontend stack is fixed: Vite + React 19 + TS + Tailwind 4 + Radix + TanStack Query + react-router-dom 7 + react-oidc-context. | Hard constraint | Theme provider landed in PR #44; workspace must respect light + dark via the same `next-themes` integration. |
| A2 | Backend stack is .NET 9 + Pooshit.Ocelot. New persisted columns flow through `SchemaService.CreateOrUpdateSchema<Node>` at startup — no migrations folder. | Hard constraint | Verified by reading `Backend/Init/DatabaseModelService.cs`. |
| A3 | `PATCH /api/nodes/{id}` already routes through `DatabasePatchExtensions.Patch` and obeys `[AllowPatch]`. Annotating the new columns is sufficient — no controller change. | High | Verified against `Pooshit.AspNetCore.Services.Patches` usage on `Node.Name` and `Node.Status` today. |
| A4 | `ArrayParameterBinderProvider` already supports `?bounds=1,2,3,4` once we declare the property as an array on the filter. The list-of-doubles bind path needs verification at implementation time. | Medium | If `double[]` binding from comma-separated query strings is not natively supported, model it as a single string and parse server-side. The architecture does not require either choice. |
| A5 | Postgres is the deployed DB; SQLite is dev-only. Positional columns are `double precision` on Postgres and `REAL` on SQLite — `double` in .NET maps to both via Ocelot. | High | Verified against the existing `float[] Embedding` column which already uses Ocelot type mapping. |
| A6 | The DiVoid graph is small today (~500 nodes, ~1000 links). 12-month target is ~5k nodes. Workspace must remain responsive at 5k with the bounds-filtered viewport typically holding ≤200 nodes. | Stated | This shapes the perf strategy: filter server-side, render client-side only what's in view. |
| A7 | All users see the same canvas state (shared layout). | Stated | Toni's framing, 2026-05-12. Per-user overlays are explicitly out of scope. |
| A8 | The `path` query parameter already composes only with `type` / `status` / `name` / `id` filters on the terminal hop (per node #8). Whether `bounds` should compose with `path` is a decision in §6.4. | Stated | Verified against existing `NodePathFilter` code. |
| A9 | The frontend's `useApiClient` ref-forwarding pattern (post-PR #40) is the canonical way to construct an ApiClient inside hooks; PR 4 must consume it, not invent a parallel pattern. | Hard constraint | Per #271 and #257 — the workspace adds many consumers of shared client surface; bypassing the canonical pattern would multiply the bug. |

---

## 4. Architectural Overview

```
 Browser (DiVoid frontend, /workspace route)
 ┌───────────────────────────────────────────────────────────────────┐
 │                                                                   │
 │  WorkspacePage (route entry, lazy-loaded)                         │
 │   │                                                               │
 │   ├── WorkspaceCanvas  ────────────────────► @xyflow/react        │
 │   │     │   - controlled nodes/edges                              │
 │   │     │   - onNodesChange / onEdgesChange handlers               │
 │   │     │   - onNodeDragStop → patch /X /Y                        │
 │   │     │   - viewport state (pan/zoom)  ── debounced ──► bounds  │
 │   │     │   - <Drop overlay>: HTML5 file drop                     │
 │   │     │                                                         │
 │   │     ├── useViewportNodes(bounds)  ────► GET /api/nodes?bounds │
 │   │     ├── useViewportLinks(nodeIds)  ───► GET /api/nodes/.../links*  │
 │   │     ├── usePatchNodePosition()    ────► PATCH /api/nodes/{id} │
 │   │     ├── useCreateNode + useUploadContent   (PR 3 hooks)       │
 │   │     │                                                         │
 │   │     └── React.memo'd NodeCardRenderer + EdgeRenderer          │
 │   │                                                               │
 │   ├── WorkspaceToolbar (zoom in/out, fit, "create here")          │
 │   └── KeyboardHandlers (Delete, Esc — others reserved)            │
 │                                                                   │
 │   Cross-cutting:                                                  │
 │    - useApiClient() — single stable client, per PR #40 pattern    │
 │    - sonner toasts for errors                                     │
 │    - next-themes for light/dark surface                           │
 └───────────────────────────────────────────────────────────────────┘
                                │
                                ▼ Bearer JWT
                ┌────────────────────────────────────────┐
                │  DiVoid Backend (.NET 9 + Ocelot)      │
                │   /api/nodes  (GET)                    │
                │     - new: ?bounds=x1,y1,x2,y2         │
                │     - composes w/ type/linkedto/query  │
                │   /api/nodes/{id}/links  (GET, NEW)    │
                │     - thin link-listing endpoint       │
                │   /api/nodes/{id}  (PATCH)             │
                │     - /X, /Y allowed via [AllowPatch]  │
                │   Node entity: + X (double) + Y (double)│
                └────────────────────────────────────────┘
```

*The `links` endpoint is a new thin GET — see §6.5.*

---

## 5. Components & Responsibilities

### 5.1 Backend — `Node` entity (extended)

- **Owns:** the persisted X and Y coordinates of every node, as part of the same row that carries `Type`, `Name`, `Status`, `ContentType`, `Content`, `Embedding`.
- **Does not own:** layout strategy. The entity stores numbers; the *meaning* of those numbers (the coordinate system, which library reads them) is the frontend's concern. The backend's only invariant is "two doubles, persisted, patchable, queryable."

### 5.2 Backend — `NodeFilter` (extended)

- **Owns:** the new `Bounds` field representing a viewport rectangle `[xMin, yMin, xMax, yMax]`.
- **Owns:** the validation contract for that field (length exactly 4 when present; xMin ≤ xMax and yMin ≤ yMax; otherwise HTTP 400 with `code=badparameter`).
- **Does not own:** the SQL predicate construction — that's `NodeService.GenerateFilter`'s job (see §5.3).

### 5.3 Backend — `NodeService.GenerateFilter` (extended)

- **Owns:** translating `filter.Bounds` into an Ocelot predicate `n => n.X >= xMin && n.X <= xMax && n.Y >= yMin && n.Y <= yMax && n.X != null && n.Y != null`.
- **Composes with:** the existing `Id`, `Type`, `Name`, `LinkedTo`, `Status`, `NoStatus` predicates via the existing `PredicateExpression<T>` AND-chain.
- **Composes with `path`:** the bounds predicate is appended to the terminal-hop predicate in `ComposeHops` (see §6.4 for the decision).

### 5.4 Backend — `Node` PATCH path (unchanged surface, extended via attributes)

- **Owns:** nothing new. The same `PATCH /api/nodes/{id}` endpoint accepts `[{"op":"replace","path":"/X","value":42.5}]` once `X` and `Y` carry the `[AllowPatch]` attribute. The `DatabasePatchExtensions.Patch` extension already enumerates `[AllowPatch]` properties and rejects others with `NotSupportedException`.
- **Surface change:** the JSON-Patch path vocabulary on `Node` grows from `{/name, /status}` to `{/name, /status, /X, /Y}`. Documented in node #8 update.

### 5.5 Backend — New thin `GET /api/nodes/{id}/links` (or batched form)

- **Owns:** returning the link adjacency for a known set of nodes — i.e., for each link where source or target is in the set, return the pair `(sourceId, targetId)`.
- **Why a new endpoint, not a filter on `/api/nodes`:** the frontend's viewport rendering needs to draw an *edge between two visible nodes*. That isn't a node query; it's a link query. Today there is no way to ask the backend "give me the links incident to these nodes" except by walking — which would require N round-trips for N viewport nodes.
- **Shape decision (§6.5):** a batched form: `GET /api/nodes/links?ids=<id1>,<id2>,...` returning a flat list of `{sourceId, targetId}` pairs. Single round-trip per viewport change.
- **Does not own:** node details (those come from the bounds-filtered `/api/nodes` call). The two endpoints are deliberately split — the frontend stitches them.

### 5.6 Frontend — `WorkspacePage` (route entry)

- **Owns:** route lifecycle (`/workspace`), top-level layout under the existing `AppShell`, error boundary scoped to the workspace.
- **Does not own:** any data fetching, any rendering — those live in `WorkspaceCanvas`.

### 5.7 Frontend — `WorkspaceCanvas`

- **Owns:** the `@xyflow/react` `ReactFlow` instance, the viewport state (local pan/zoom), the conversion between `NodeDetails[]` + `NodeLink[]` and `Node` / `Edge` objects that xyflow consumes, the drag-end handler that calls `usePatchNodePosition`, the file-drop overlay.
- **Does not own:** node-level rendering (delegated to a memoised `NodeCardRenderer` registered with xyflow), edge styling (delegated to `EdgeRenderer`), the toolbar (separate component), nor the data-fetching logic (delegated to the hooks below).

### 5.8 Frontend — `useViewportNodes(bounds)`

- **Owns:** the TanStack Query call to `GET /api/nodes?bounds=...&fields=id,type,name,status,X,Y` with a debounced bounds parameter to avoid refetching on every pan tick.
- **Cache key:** `['nodes', 'viewport', bounds]` where `bounds` is the debounced rectangle.
- **Stale time:** short (1s) — the viewport contents are the authoritative truth, and other agents might be writing concurrently. We accept the cost.

### 5.9 Frontend — `useViewportLinks(nodeIds)`

- **Owns:** the TanStack Query call to `GET /api/nodes/links?ids=...` with the visible node ids.
- **Cache key:** `['links', 'incident', sortedNodeIds]` — sorting is necessary because xyflow may surface ids in non-deterministic order.
- **Stale time:** short (1s), same reason as 5.8.

### 5.10 Frontend — `usePatchNodePosition`

- **Owns:** the mutation that PATCHes `/X` and `/Y` for a single node on drag-end.
- **Sends both** `/X` and `/Y` in one PATCH body even if only one moved (xyflow drag callbacks always carry both).
- **Optimistic update:** none. Drag is local in xyflow's controlled state; PATCH is fire-and-forget-and-toast-on-error. Position is already visible at the new location locally; we don't need optimistic cache for that. If the PATCH fails, toast + revert is handled by re-fetching the bounds query.

### 5.11 Frontend — `WorkspaceToolbar`

- **Owns:** zoom-in / zoom-out / fit-to-content buttons, a "create node here" affordance (or "create at center" if there's no click-context), the node-count and "loading" indicator.
- **Does not own:** keyboard shortcuts (those live on the canvas root).

### 5.12 Frontend — `NodeCardRenderer` (custom xyflow node type)

- **Owns:** the React component xyflow calls for each visible node. Renders the node's type badge, name, status (when set). Memoised with `React.memo` and an explicit prop-equality function so a re-render of the canvas does not cascade into every node's render.
- **Click handler:** click → navigate to `/nodes/{id}` (existing PR 2 detail page); double-click is reserved (no behaviour in this PR).
- **Does not own:** the node's *content* preview (markdown body, image, etc.) — that's the detail page's job.

---

## 6. Interactions & Data Flow

### 6.1 First open of `/workspace`

```
1. User navigates to /workspace.
2. WorkspacePage mounts, WorkspaceCanvas reads initial viewport from sessionStorage
   (if present) or defaults to viewport [-500, -500, 500, 500] centered at origin.
3. useViewportNodes([-500,-500,500,500]) fires →
     GET /api/nodes?bounds=-500,-500,500,500&fields=id,type,name,status,X,Y
4. The response carries N nodes. Those whose X/Y are null are EXCLUDED by the backend
   (the predicate requires X IS NOT NULL AND Y IS NOT NULL).
5. WorkspaceCanvas constructs xyflow Node objects, one per result, each with
   { id, position: { x, y }, data: nodeDetails }.
6. useViewportLinks(visibleIds) fires →
     GET /api/nodes/links?ids=<comma-sep-ids>
7. The response carries the link adjacency. WorkspaceCanvas filters to links where
   BOTH endpoints are in the visible set, constructs xyflow Edge objects.
8. ReactFlow renders the scene.
```

### 6.2 Pan / zoom (local only)

```
1. User drags the canvas background. xyflow updates its internal viewport state.
2. WorkspaceCanvas's onViewportChange handler computes the new bounds rectangle
   in world coordinates from xyflow's viewport (translate + scale).
3. The bounds value is debounced (250ms default).
4. After debounce, useViewportNodes refetches with the new bounds.
5. If new node ids appear, useViewportLinks refetches.
6. Session viewport is persisted to sessionStorage on every settle (no server call).
```

### 6.3 Drag a node

```
1. User starts dragging node N. xyflow updates its local node position on every tick.
2. WorkspaceCanvas's onNodeDragStop fires when the user releases.
3. usePatchNodePosition.mutate({ id: N.id, x, y }) →
     PATCH /api/nodes/{N.id}
     [{"op":"replace","path":"/X","value":x},
      {"op":"replace","path":"/Y","value":y}]
4. On success, invalidate ['nodes', 'viewport', ...] queries so any concurrent
   viewport on a different client picks up the new position on next fetch.
5. On error (network / 403 / etc.), toast the error; the bounds query refetches
   and snaps the node back to its server-side X / Y on next poll.
```

### 6.4 `bounds` composition with `path` — decision

There are three options:

- **(A)** `bounds` is always evaluated on the *terminal hop* of a `path` query.
- **(B)** `bounds` and `path` are mutually exclusive — supplying both returns 400.
- **(C)** `bounds` is silently ignored when `path` is present.

**Recommendation: (A) — terminal-hop composition.** Same model as the other terminal-hop filters (`type`, `status`, `name`, `id`). It matches the documented composition rule on node #8 ("paging / sort / fields / type / status / name / id apply to the terminal hop only") with the smallest exception surface. Cost: zero — the backend already builds the terminal-hop predicate in `ComposeHops`; we just append the bounds clause to it.

**Why not (B):** silently rejecting a valid composition would block the obvious use case of "give me tasks under DiVoid that are currently in view," which is precisely the workspace's value-add.

**Why not (C):** silent ignoring is a footgun. Either we honour it or we 400.

### 6.5 Why a new `GET /api/nodes/links` endpoint (not a node filter)

Three other shapes were considered:

- **Walk via `linkedto` for each visible node** — N round-trips for N visible nodes; obviously wrong.
- **Combined `?bounds=` endpoint that returns `{nodes, links}`** — couples node-listing pagination with link adjacency in a way that breaks the existing `Page<NodeDetails>` envelope contract. Worth doing if performance demanded a single round-trip, but it doesn't.
- **Embed link adjacency in each `NodeDetails`** — bloats the DTO with data many list callers don't need; breaks the camelCase POCO shape; reverses the entity-vs-relationship split that the schema already enforces.

The thin batched endpoint wins:

```
GET /api/nodes/links?ids=<id1>,<id2>,...
→ 200
{ "result": [{"sourceId":1,"targetId":3}, {"sourceId":1,"targetId":5}, ...] }
```

Filter semantics: return every `NodeLink` where `sourceId IN ids OR targetId IN ids`. The frontend then filters client-side to draw only edges where *both* endpoints are visible.

Size cap: same `count ≤ 500` ceiling as the node listing, applied after the SQL filter. If a viewport contains nodes with many off-screen links, the response is capped and the frontend simply doesn't draw the off-screen edges — which is fine, the user can't see them anyway.

Auth: `[Authorize(Policy = "read")]`, same as `/api/nodes`.

### 6.6 Migration of existing rows — decision

Two options were on the table:

- **(a) Synthesise a layout at migration time.** A one-shot force-directed pass over all 500 existing nodes; bulk-update X / Y.
- **(b) Leave NULL; let the frontend synthesise placement on first sight, PATCH back.**

**Recommendation: (a) — migration-time synthesis.** Reasons:

1. Toni's framing makes layout *shared*. Option (b) means the first user to open the workspace pays the synthesis cost and writes the canonical positions for everyone — that user's machine becomes the de-facto layout engine. Option (a) makes the migration the layout engine, run once, deterministically, on the backend.
2. With NULLs the bounds-filtered viewport is empty on first open — bad UX.
3. The pass is cheap at current size (~500 nodes). A simple Fruchterman-Reingold variant runs in <1s. The graph won't 10x overnight.

**Mechanics:** the synthesis runs in a one-shot CLI verb (`dotnet Backend.dll layout-nodes`), not in `DatabaseModelService` startup. Reasons:

- Startup-time layout would run on every dev machine boot.
- The verb can be invoked once against prod after the schema is rolled out.
- The verb is idempotent: it ONLY touches rows where X IS NULL AND Y IS NULL — a re-run leaves user-positioned nodes untouched.

**Newly created nodes after migration** that don't carry positions in the POST body will be inserted with X = 0, Y = 0 (the column default). The frontend will see them at origin and the user can drag them somewhere meaningful. We don't try to auto-place new nodes on the server — that's the user's job. Documented in node #8.

---

## 7. Data Model (Conceptual)

The entity model adds two fields to one entity. Nothing else changes.

| Concept | Today | After this design |
|---|---|---|
| `Node` | `Id`, `TypeId`, `Name`, `ContentType`, `Content`, `Embedding`, `Status` | + `X: double` + `Y: double` |
| `NodeDetails` (DTO) | `Id`, `Type`, `Name`, `Status`, `ContentType`, `Similarity?` | + `X?: double` + `Y?: double` (returned only when requested in `fields` or in the default field set for the viewport call) |
| `NodeLink` | `SourceId`, `TargetId` | unchanged |
| `NodeFilter` | `Id[]`, `Type[]`, `Name[]`, `LinkedTo[]`, `Status[]`, `NoStatus`, paging, `Query`, `MinSimilarity` | + `Bounds: double[]` (length 4 when present) |
| `LinkAdjacency` (new DTO) | — | `SourceId: long`, `TargetId: long` — flat POCO mirroring the row pair |
| `Page<LinkAdjacency>` (new) | — | reuses the same envelope shape as `Page<NodeDetails>` |

**Coordinate system:** an abstract world space, double-precision floats, both X and Y unbounded (`±1.7e308`). The frontend's viewport maps world coordinates to screen pixels; the backend has no opinion on units. **Y axis convention:** Y increases downward (screen-coordinate convention, matches what `@xyflow/react` uses internally). Documented in node #8 so other consumers don't invert it.

**Default value:** when a row is inserted without an explicit X / Y in the POST body, the column default is 0. The schema declares `X` and `Y` as `double` (non-nullable) with default 0 to keep the row simple — see §10 trade-off discussion below.

**Why not nullable doubles?** Considered and rejected — see the rejected-alternative documentation node linked from this design.

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 Backend — `Node` schema additive change

| Property | Type | Nullable | Default | Patchable | Indexed |
|---|---|---|---|---|---|
| `X` | `double` | no | 0 | yes (`[AllowPatch]`) | yes — composite `[Index("position")]` together with Y |
| `Y` | `double` | no | 0 | yes (`[AllowPatch]`) | yes — composite `[Index("position")]` together with X |

**Composite index rationale:** the bounds query filters on both X and Y. A composite index on `(X, Y)` lets Postgres serve typical viewport queries via an index range scan rather than a full sequence scan. Index name `"position"` matches the Ocelot convention (one shared scope name for both columns, per the same pattern as `[Index("node")]` on `TypeId` + `Name` today).

**Indexed but cheap:** the composite index on two `double precision` columns costs ~16 bytes per row plus B-tree overhead. At 5k nodes that's well under 1 MB. Worth it.

### 8.2 Backend — `GET /api/nodes` extended

New query parameter:

| Parameter | Type | Description |
|---|---|---|
| `bounds` | `double[]` (length 4) | Viewport rectangle as `[xMin, yMin, xMax, yMax]`. Filters to nodes where X ∈ [xMin, xMax] AND Y ∈ [yMin, yMax]. Always implies `X IS NOT NULL AND Y IS NOT NULL`. |

**Wire form:** `?bounds=10,20,100,200` (comma-separated, leveraging the existing `ArrayParameterBinderProvider`). Bracketed and repeated forms are also accepted by the provider but the comma form is the documented canonical.

**Validation contract:**

- Length ≠ 4 → HTTP 400 `code=badparameter` `text="bounds must be four numbers: xMin,yMin,xMax,yMax"`.
- xMin > xMax or yMin > yMax → HTTP 400 with a specific message.
- Otherwise, compose with existing predicates.

**Composition table (existing filters × bounds):**

| Combined with | Behaviour |
|---|---|
| `id`, `type`, `name`, `status`, `nostatus` | AND — all predicates must hold. |
| `linkedto` | AND — only nodes in the viewport AND linked to the given anchors. |
| `query` | AND on filter, ranks within the bounds-filtered set. |
| `path` | bounds appended to the terminal-hop predicate (decision §6.4). |

**Response shape:** unchanged. `Page<NodeDetails>`. When the caller requests `fields=...,X,Y` (or relies on the default set for the workspace, which includes X and Y), the X / Y values come back; otherwise they are omitted from the DTO per the existing field-projection contract.

### 8.3 Backend — `PATCH /api/nodes/{id}` extended path vocabulary

Body shape unchanged: `PatchOperation[]`. New permitted paths:

| Path | Op | Value | Result |
|---|---|---|---|
| `/X` | `replace` | `double` | sets the X column |
| `/Y` | `replace` | `double` | sets the Y column |

`add`, `remove`, `flag`, `unflag`, `embed` on `/X` and `/Y` return the same `NotSupportedException` as today's enforcement.

### 8.4 Backend — `GET /api/nodes/links` (new)

| Parameter | Type | Description |
|---|---|---|
| `ids` | `long[]` | Required. Returns links where SourceId ∈ ids OR TargetId ∈ ids. |
| `count` | int | Optional, max 500, default 500. |
| `continue` | long | Optional, paging cursor. |

**Response:** `Page<LinkAdjacency>` where `LinkAdjacency = { sourceId, targetId }`.

**Auth:** `[Authorize(Policy = "read")]`.

**Behaviour on empty `ids`:** HTTP 400 `code=badparameter` `text="ids parameter is required"` — same shape as other missing-required errors.

### 8.5 Frontend — public hooks

| Hook | HTTP | Cache key |
|---|---|---|
| `useViewportNodes(bounds, options?)` | `GET /api/nodes?bounds=...&fields=id,type,name,status,X,Y` | `['nodes','viewport', boundsTuple]` |
| `useViewportLinks(nodeIds, options?)` | `GET /api/nodes/links?ids=...` | `['links','incident', sortedIds]` |
| `usePatchNodePosition()` | `PATCH /api/nodes/{id}` with `[{op:'replace',path:'/X',value:x},{op:'replace',path:'/Y',value:y}]` | invalidates `['nodes','viewport']` and `['nodes', id]` |

All three call through `useApiClient()` — no inline `createApiClient` (per #271 / #257).

---

## 9. Cross-Cutting Concerns

### 9.1 Render stability (load-bearing per #271)

This PR adds ~7 new consumers of the shared `useApiClient` surface (the three hooks above, the NodeCard, the canvas, the toolbar, the file-drop overlay) — exactly the kind of "shared invariant fans out" pattern #271 warns about.

**Rules baked into this design:**

- Every hook in §8.5 calls `useApiClient()` once and uses the returned reference. No inline `createApiClient`.
- The `params` objects passed to `client.get` are `useMemo`'d with JSON-string-stringified deps, mirroring `useNodeListLinkedTo.ts` line 40–46.
- `NodeCardRenderer` is wrapped in `React.memo` with an explicit comparator that checks `id`, `type`, `name`, `status`, `position.x`, `position.y`, `selected`. Without the comparator, a parent re-render cascades into every node.
- The xyflow `nodeTypes` and `edgeTypes` objects passed to `ReactFlow` are module-level constants (not inline object literals), because xyflow internally checks `nodeTypes` identity to re-bind renderers — passing a fresh object every render is a documented xyflow footgun.
- The xyflow `defaultEdgeOptions` (if used) is also module-level.
- Pan/zoom drives the `bounds` state through a debounce; the debounced bounds is the cache key. Without debounce, every pan tick (60/s) triggers a query.

### 9.2 Error handling

| Failure | Behaviour |
|---|---|
| Viewport query 401 → silent refresh → retry | inherited from `useApiClient` chain — no special handling here |
| Viewport query 403 | toast "You don't have permission to view the workspace" — extremely unlikely, all authenticated users have read |
| Viewport query 5xx / network | toast generic error, the canvas continues showing the last successful frame |
| PATCH position 403 | toast "You don't have permission to move nodes." The bounds query refetches and the node snaps back |
| PATCH position 4xx other | toast `error.text`; same snap-back via refetch |
| File drop oversized | client-side soft cap at 10 MB (matches design #225 §R9); toast and abort before POST |
| `bounds` malformed (programmer error) | a 400 from the backend; toast generic |

### 9.3 Observability

Backend: standard `LogInformation` on the PATCH path (already in place; X/Y patches go through the same `Patch` action method). No new logging on the bounds query (reads aren't logged per code-contracts §6).

Frontend: dev-only `console.debug` for bounds changes and PATCH dispatches via the existing `lib/api.ts` debug-log path.

### 9.4 Security

The new endpoints inherit the existing auth model. No new attack surface — same Bearer JWT or API key, same `[Authorize(Policy = "read"|"write")]` enforcement.

One worth-flagging concern: the file-drop interaction will let any `write`-permission user create nodes with arbitrary content. That is already true today via `POST /api/nodes/{id}/content` (PR 3). Workspace drop just adds a more discoverable entry point — same enforcement.

### 9.5 Theming

Workspace must respect the existing light/dark theme (per PR #44). The xyflow canvas background, node card backgrounds, and edge colours all read from CSS custom properties already provided by the theme — no hardcoded colours in the workspace components.

### 9.6 Lazy loading

Per design #225 §14.4, `/workspace` is already lazy-loaded. The xyflow dependency is large (~50KB gzipped); keeping it lazy means it never touches the landing-page bundle.

---

## 10. Quality Attributes & Trade-offs

| Attribute | Approach | Trade-off |
|---|---|---|
| **Initial-frame time** | Bounds-filtered server-side query; capped at 500 nodes per response; debounced re-fetch on pan/zoom. | At very wide zoom-out, the response may be capped — the user sees "≤500 visible nodes," which is acceptable because at that zoom the dots are not individually meaningful. |
| **Drag responsiveness** | Drag is purely local in xyflow's controlled state; PATCH only on drag-end. | The viewport query may refetch on the next bounds change and overwrite the local position briefly if the PATCH hasn't landed. Mitigation: short 1s stale time + the PATCH `onSuccess` invalidation. Worst case: the node jumps for one frame. |
| **Render stability** | All shared invariants memoised; node card under `React.memo`; canvas does not re-render unless bounds, nodes, or edges change. | Strict memoisation has a maintenance cost (forgetting to memo a new prop is easy). Mitigation: a load-bearing render-loop test (§13) that fails when stability is broken. |
| **Backend perf at 5k nodes** | Composite index on (X, Y); the typical bounds query touches O(viewport-area / total-area × 5000) rows. | At very wide zoom, the query is effectively a full scan — but at very wide zoom we cap at 500 results and the COUNT-OVER is still O(n). Acceptable until the graph is much larger. |
| **Storage cost** | 16 bytes per row for X / Y; composite index overhead. | Negligible. |
| **Schema evolution** | Additive columns with non-null + default 0; existing rows backfilled by a CLI verb. Migrating away (if X/Y ever moves to a separate table) would be a normal Ocelot table change. | None worth flagging. |

### Alternatives rejected (each filed as its own DiVoid documentation node, per Hivemind Rule 3)

| Decision | Rejected | Filed at |
|---|---|---|
| Library: `@xyflow/react` | `cytoscape.js`, `sigma.js`, `vis-network` | see linked rejected-alternative node |
| Schema: two columns `X` + `Y` (non-null, default 0) | nullable `double?`; single JSON blob `Position`; separate `NodePosition` table | see linked rejected-alternative node |
| Migration: one-shot CLI synthesis | lazy client-side synthesis with first-render PATCH | see linked rejected-alternative node |

---

## 11. Risks & Mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | xyflow + TanStack Query identity-equality mismatch causes infinite renders (the #257 / #271 pattern). | Workspace unusable. | Render-stability rules in §9.1 are enforced by a load-bearing test (§13). |
| R2 | Bounds query returns 0 nodes on first open because positions weren't backfilled. | Empty canvas. | Migration synthesis runs against prod before PR 4 frontend lands (§6.6). |
| R3 | `?bounds=` array binding fails at the model-binder level (Ocelot service expects `long[]` per `ArrayParameterBinderProvider`, but `double[]` may not be wired). | Bounds requests 400. | Implementation pre-check: try `?bounds=1.0,2.0,3.0,4.0` against a local Backend; if binding fails, define `Bounds` as `string` on the filter and parse in `NodeService`. The architecture does not depend on which form wins — this is an implementation-time detail, surfaced here so John doesn't blockerize on it. |
| R4 | Composite index on `(X, Y)` does not help in practice if Postgres prefers a different plan (e.g., GiST). | Slow viewport queries. | The cost of choosing wrong is low (~ms at current scale). The implementation should use the simple B-tree composite; if `EXPLAIN` later shows it being ignored, swap to a 2D GiST index in a follow-up. |
| R5 | The link adjacency endpoint returns too many off-screen links and the frontend wastes work filtering them. | Slight perf hit. | The 500 cap and the client-side both-endpoints-visible filter are sufficient at current scale. If profiling shows the filter as hot, add a second `bounds` parameter to the links endpoint in a follow-up. |
| R6 | Concurrent moves by two agents race: A moves N to (10,10), B moves N to (20,20) at the same time. Last write wins. | Position flickers. | Acceptable for a single-tenant graph today. Documented as a known limitation; not worth the cost of a CAS / version column. |
| R7 | Markdown / rich content nodes render heavy NodeCards. | Hitch under zoom. | NodeCard renders only `name` + `type` + `status` — no content preview in this PR. Filed as an out-of-scope task. |
| R8 | Drop-file payloads of unknown MIME types crash the type-inference logic. | Failed drop with cryptic error. | Default to `application/octet-stream` and `type=documentation` if MIME can't be inferred; user can edit afterwards. |

---

## 12. Migration / Rollout Strategy

**Two PRs, sequenced backend-first, per the orchestrator "one feature one PR" rule.**

### PR 4a — Backend (John)

Branch off `main`. Lands the schema additive change, the `bounds` filter, the new `/api/nodes/links` endpoint, the migration CLI verb, and the documentation update to node #8. Closes DiVoid task #232.

Order of operations *within* PR 4a:

1. Add `X` and `Y` to `Node` (non-null, default 0, `[AllowPatch]`, composite `[Index("position")]`).
2. Add `Bounds` to `NodeFilter` and the `GenerateFilter` predicate branch.
3. Add `ComposeHops` terminal-hop composition for `bounds` (§6.4).
4. Add `LinkAdjacency` DTO and the new `/api/nodes/links` endpoint with its service method.
5. Add the `layout-nodes` CLI verb that runs a deterministic force-directed pass over all rows where `X = 0 AND Y = 0` (i.e., rows still at the default position) and bulk-updates X / Y.
6. Tests per §13.
7. Update API reference node #8 with `bounds`, `/X` /`/Y` patchable paths, and the new `/links` endpoint.
8. Run the migration verb against prod after merge (operator action; documented in the PR body).

### PR 4b — Frontend (Pierre)

Branch off `main` *after PR 4a is merged*. Implements the workspace consuming the new API. Closes DiVoid task #230.

Order of operations within PR 4b:

1. Install `@xyflow/react` (lock to a single major version).
2. Update `NodeDetails` and `NodeFilter` types to include `x`, `y`, `bounds`.
3. Add `useViewportNodes`, `useViewportLinks`, `usePatchNodePosition` hooks.
4. Replace the `WorkspacePage` placeholder with the real implementation.
5. Add `WorkspaceCanvas`, `NodeCardRenderer`, `WorkspaceToolbar`.
6. Wire keyboard shortcuts (Delete on selected; Esc cancels drag; everything else reserved).
7. Wire file-drop overlay; reuse `useCreateNode` + `useUploadContent`.
8. Tests per §13.

### Why two PRs not one

- The backend change is independently meaningful (the new endpoints + schema can be used by any future client, including agents).
- The frontend cannot meaningfully test against a backend that doesn't have the columns yet — bundling them creates a chicken-and-egg test situation that two-PR sequencing avoids.
- The orchestrator's "one feature one PR" rule (per global CLAUDE.md) — two independently-meaningful units of work ship as two PRs.

### Sequencing diagram

```
PR 4a (backend) ───► merge ───► operator runs migration verb ───► PR 4b (frontend) ───► merge
                                                                  ▲
                                                                  │ depends on the schema + endpoints
```

---

## 13. Load-Bearing Tests (per #275)

Per node #275, each PR must ship tests that prove the change is load-bearing — negative + positive substitution must both pass.

### 13.1 Backend (PR 4a)

| # | Test | Negative proof (must FAIL if reverted) | Positive proof |
|---|---|---|---|
| BT1 | `Node` schema includes X and Y after `CreateOrUpdateSchema<Node>`; INSERT with default 0 and `Patch /X /Y` round-trips. | Remove `X` / `Y` from `Node.cs` — test fails compiling or asserting. | With X / Y present, GET returns the patched values. |
| BT2 | `PATCH /api/nodes/{id}` with `[{"op":"replace","path":"/X","value":42.5}]` succeeds; reading the row back shows X = 42.5. | Remove `[AllowPatch]` from `Node.X` — the `Patch` extension throws `NotSupportedException` and the controller test gets a 400. | With `[AllowPatch]`, the test passes. |
| BT3 | `GET /api/nodes?bounds=10,10,100,100` returns only nodes whose X / Y lie within the rectangle. Seed three nodes at (5,5), (50,50), (200,200); assert exactly one is returned. | Remove the bounds predicate in `GenerateFilter` — the test gets back all three. | With the predicate, only the (50,50) node returns. |
| BT4 | `GET /api/nodes?bounds=...&linkedto=<id>` composes — given a seeded graph where two nodes are linked to anchor A and only one is inside bounds, the response is exactly that one. | Remove the `predicate &= boundsPredicate` line — the test gets both. | Composition test passes. |
| BT5 | `GET /api/nodes?bounds=...&path=[id:X]/[type:task]` evaluates bounds on the terminal hop (§6.4). Seed five tasks under project X, three inside bounds; assert three. | Remove the terminal-hop bounds line in `ComposeHops` — all five return. | Test passes. |
| BT6 | `GET /api/nodes/links?ids=1,2,3` returns the union of links incident to those nodes. Seed link (1,2) and link (3,4); assert both are returned. | Remove the new `LinkAdjacency` controller action / wire-up — the test gets 404. | With the endpoint registered, both pairs return. |
| BT7 | `bounds=1,2,3` (length 3) → HTTP 400 with `code=badparameter`. | Remove the length-4 validation — the test gets 200 or 500. | Validation passes. |
| BT8 | `bounds=100,100,10,10` (inverted) → HTTP 400. | Remove the ordering validation — the test gets 200 with an empty result. | Validation passes. |
| BT9 | `layout-nodes` CLI verb is idempotent: run twice, second run touches zero rows. | Remove the `WHERE X = 0 AND Y = 0` guard in the verb — the test sees the same node moved twice with different positions. | Guard passes. |

All backend tests live in `Backend.tests/` following the existing NUnit conventions (`[TestFixture]`, `[Test]`, `Assert.That(...)`). Use the `TestSetup.CreateMemoryDatabase()` pattern that the existing tests use (the SQLite-backed `IEntityManager`); the only test that *needs* Postgres is BT5 if `ComposeHops` semantic-search composition is touched — but bounds composition does not touch the vector path, so SQLite is sufficient throughout.

### 13.2 Frontend (PR 4b)

| # | Test | Negative proof | Positive proof |
|---|---|---|---|
| FT1 | `useViewportNodes` calls `GET /api/nodes` with `bounds` and the workspace field set; results render as xyflow Node objects. | Remove the bounds parameter from the params object — the test sees a fetch with no `bounds`. | With bounds, the test sees the correct URL. |
| FT2 | Dragging a node and releasing dispatches a PATCH with `/X` and `/Y` values matching the drop position. | Remove the `onNodeDragStop` handler wiring — the PATCH is never sent. | Handler fires; PATCH carries both fields. |
| FT3 | Pan / zoom debounces the bounds query — exactly one fetch fires per 250ms window even when 20 viewport ticks occur. | Remove the debounce — the test sees 20 fetches. | Debounce holds the count at 1. |
| FT4 | The workspace mounts and survives an automated drag/pan/zoom sequence without React's max-update-depth warning (the #271 / #257 family). | Replace `useApiClient` with an inline `createApiClient` call in any one of the three hooks — the test catches the max-update-depth warning and fails. | With the canonical pattern, no warning is raised. |
| FT5 | `NodeCardRenderer` is memoised — moving one node does not re-render the other 49 visible nodes (assertion via spy on the renderer). | Remove `React.memo` — the spy fires N times per drag tick. | With memo, the spy fires once. |
| FT6 | File drop on blank space POSTs a new node and uploads the file content via the existing PR 3 mutations. | Disconnect `useUploadContent` from the drop handler — content is never uploaded. | Drop creates the node and uploads the bytes. |
| FT7 | `bounds` changes propagate through the cache key so a viewport refetch happens. | Hardcode the cache key to a constant — the second viewport reuses the first response. | Cache key tracks bounds; fresh fetch fires. |

**FT4 is the load-bearing render-stability test** — directly per #271, capturing the exact failure mode the May 2026 PRs #36–#42 saga produced. It must be in the PR.

All frontend tests use the existing Vitest + React Testing Library + MSW patterns from PR 1–3.

---

## 14. Open Questions

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should `bounds` arrive as comma-separated `double[]` via `ArrayParameterBinderProvider`, or as a single `string` parsed in service code? | Try the array form first (one-line model change); fall back to string if binding fails. Architecture is agnostic. |
| Q2 | Should the migration CLI verb live in `Backend/Cli/` or follow the existing `dotnet Backend.dll backfill-embeddings` pattern? | Mirror the embedding-backfill verb exactly — same parser entry point, same flag style. |
| Q3 | Default zoom level on first open? | 100% (identity transform), viewport `[-500, -500, 500, 500]` — wide enough to see most of a fresh user's neighbourhood, tight enough that the bounds query returns a usable subset. Tunable by Pierre. |
| Q4 | Should the workspace remember the last viewport across sessions (localStorage) or per-session only (sessionStorage)? | Session-only for now; revisit if Toni finds it annoying. Per-user-cross-session persistence would be a follow-up (and would re-open the "shared vs per-user state" tension — defer). |
| Q5 | When the user clicks an empty area to create a node, do we capture the click position in world coordinates and prefill that as the new node's X / Y? | Yes — the entire point of "create here" vs "create somewhere." |
| Q6 | When file-drop creates a node, where does it position? | At the drop point in world coordinates. Same logic as Q5. |
| Q7 | Should the toolbar's "fit to content" zoom run on initial mount? | No — fit-to-content on a 5k-node graph would zoom out to a blur. Keep default viewport. Fit is on-demand only. |

---

## 15. Implementation Guidance for the Next Agent

### 15.1 PR 4a — Backend (John)

Branch off `main`. Closes #232.

**Build order:**

1. **Schema columns.** Add `X` and `Y` to `Backend/Models/Nodes/Node.cs` as `double` (non-null), `[AllowPatch]` on both, `[Index("position")]` on both (composite). Add `<summary>` docs. **Do not** add Width / Height — see the rejected-alternative node.
2. **DTO update.** Add `double? X` and `double? Y` to `NodeDetails`. Nullable on the DTO so callers that don't request the fields get them omitted from JSON.
3. **Mapper update.** Add `x` and `y` field mappings to `NodeMapper.Mappings`. They are not in `DefaultListFields` — callers ask for them explicitly via `?fields=...,X,Y` (or via the workspace's default field set).
4. **Filter.** Add `double[] Bounds` to `NodeFilter` with `<summary>` doc describing the `xMin,yMin,xMax,yMax` ordering. Validate in `NodeService.ListPaged` *before* calling `GenerateFilter` — if length ≠ 4 or inverted, throw a `BadRequestException` equivalent that maps to 400.
5. **`GenerateFilter` extension.** Add the bounds branch. Predicate: `n => n.X >= xMin && n.X <= xMax && n.Y >= yMin && n.Y <= yMax`. (No `X IS NOT NULL` clause needed because the column is non-null.)
6. **`ComposeHops` extension.** The bounds clause appends to the terminal-hop predicate — same path as the existing `GenerateHopFilter` chain. Verify with BT5 that the predicate is on the terminal `LoadOperation<Node>`, not on the seed.
7. **`LinkAdjacency` DTO** in `Backend/Models/Nodes/LinkAdjacency.cs`: flat POCO, `SourceId` + `TargetId`.
8. **`INodeService.ListLinks(long[] ids, ListFilter filter, CancellationToken ct)`** returning `AsyncPageResponseWriter<LinkAdjacency>`. Implementation uses `database.Load<NodeLink>(l => l.SourceId, l => l.TargetId).Where(l => l.SourceId.In(ids) || l.TargetId.In(ids))` plus the standard `ApplyFilter` + window-count idiom.
9. **Controller action** `[HttpGet("links")]` on `NodeController` taking `[FromQuery] long[] ids, [FromQuery] ListFilter filter, CancellationToken ct`, `[Authorize(Policy = "read")]`.
10. **CLI verb `layout-nodes`** following the `backfill-embeddings` pattern. Force-directed pass implemented as a simple iterative Fruchterman-Reingold pure-CSharp routine (50 iterations, default canvas 1000×1000). Only update rows where `X = 0 AND Y = 0` AND `Id != 0` (idempotency).
11. **Tests** — BT1 through BT9. Use `TestSetup.CreateMemoryDatabase()` for the SQLite-backed integration tests.
12. **Update API reference node #8** in DiVoid (file as a follow-up node-content PATCH after the PR merges — or include the doc edit in the PR body so the next sync brings it forward).

### 15.2 PR 4b — Frontend (Pierre)

Branch off `main` *after PR 4a is merged and deployed*. Closes #230.

**Build order:**

1. `npm install @xyflow/react` (pin a single major).
2. Update `src/types/divoid.ts`: add `x?: number; y?: number` to `NodeDetails`; add `bounds?: number[]` to `NodeFilter`; export a new `LinkAdjacency` interface and `Page<LinkAdjacency>`.
3. Add `src/features/workspace/queries.ts` (or per-hook files mirroring the `useNodeListLinkedTo` pattern):
   - `useViewportNodes(bounds, options?)` — calls `client.get<Page<NodeDetails>>(API.NODES.LIST, { ...filter, bounds, fields: ['id','type','name','status','X','Y'] })`.
   - `useViewportLinks(nodeIds, options?)` — calls `client.get<Page<LinkAdjacency>>(API.NODES.LIST + '/links', { ids: nodeIds })`. (Add a new constant under `API.NODES`.)
   - `usePatchNodePosition()` — mutation that posts `[{op:'replace',path:'/X',value:x},{op:'replace',path:'/Y',value:y}]`, invalidates `['nodes','viewport']`.
4. Add `src/features/workspace/WorkspaceCanvas.tsx` — the xyflow consumer. Memoise everything per §9.1.
5. Add `src/features/workspace/NodeCardRenderer.tsx` — the custom node type. Wrap in `React.memo` with explicit equality.
6. Add `src/features/workspace/WorkspaceToolbar.tsx`.
7. Replace `WorkspacePage.tsx` content with the real implementation.
8. Wire keyboard shortcuts and the file-drop overlay.
9. Tests — FT1 through FT7. Use the existing MSW setup; reuse the render-loop harness pattern from `SearchPage.renderLoop.test.tsx`.

### 15.3 Specific anti-patterns to avoid

- **Do not** call `createApiClient(...)` inline in any of the new hooks. Always go through `useApiClient()`.
- **Do not** pass `nodeTypes={{...}}` inline to `<ReactFlow>` — that's a documented xyflow footgun. Module-level constant.
- **Do not** materialise the full graph client-side and filter by bounds in JS — the whole point of the backend filter is to avoid that.
- **Do not** introduce a Zustand / Redux store for graph state. xyflow's controlled state + TanStack Query is sufficient. Adding a third state container is over-engineering and contradicts design #225 §10.
- **Do not** add a "fit to content" call on mount — see Q7.

---

*This document is the architectural contract for the DiVoid workspace mode (backend dependency #232 + frontend PR 4 #230). It is mirrored as a DiVoid `documentation` node linked to umbrella #223, project #3, code-contracts #114, the affected tasks #230 and #232, and the prior design doc #225. The rejected-alternative rationale for the three load-bearing decisions (library, schema, migration) is filed as separate linked documentation nodes per Hivemind Rule 3.*
