# Architectural Document: Link Type & Carried Context on NodeLink

> Source task: DiVoid #7119. Code map root: #5860 (NodeLink #5954, data-model concept #6107).
> Contracts applied as load-bearing: Design Contracts (#1136 — §1 KISS/DRY/YAGNI, §2 existing-systems-first, §4 less-is-better, §5 Pre-Design Checklist) and Backend Code Contracts (#114 — §0 principles, `[AllowPatch]` discipline, Ocelot idioms, controller/service boundaries).

## 1. Problem Statement

DiVoid's graph edges (`NodeLink`) are today a payload-less, two-column entity (`SourceId`, `TargetId`) with no notion of direction or meaning. Traversal (`linkedto`) treats every edge as undirected. The user wants edges to optionally carry two new pieces of substance so a *consumer reading the graph* can derive correlation:

1. **Direction** — a `LinkType` describing whether the edge is undirected (today's behavior), unidirectional (source→target), or bidirectional (both ends directed).
2. **Context** — a single optional free-text string, interpreted in the **source→target** direction (e.g. `"subtask"` on a task→task edge reads as *source is a subtask of… no — source --subtask--> target*, i.e. the edge from source to target carries the label "subtask").

**Success criteria:** both fields are optional and default to today's exact behavior; **no existing link changes meaning**; a consumer can read direction + context off an edge while traversing; traversal itself is unchanged (both-directions).

## 2. Scope & Non-Scope

**In scope (this PR — backend model + API increment):**

- `NodeLink` entity gains `LinkType` (stored enum) + `Context` (nullable string).
- Schema bootstrap (`DatabaseModelService`) already registers `NodeLink`; the additive columns flow through the existing `CreateOrUpdateSchema<NodeLink>` path — no new registration, no migrations folder.
- The link-create path accepts optional `linkType` + `context`, defaulting to `None` / `null`.
- The link **read** path (`GET /api/nodes/links`) surfaces `linkType` + `context`.
- Full test coverage of new/changed lines.

**Out of scope — named follow-on units, NOT this PR (see §12 "What does NOT go in"):**

- Directional `linkedto` traversal filtering / per-direction query semantics (YAGNI, locked decision #4).
- A per-direction context *pair* (two strings, one per direction) — locked decision is a single string read source→target.
- Enriching the flat `NodeDetails.Links` neighbor-id array (see §5 Decision D3).
- Edit-existing-edge mutation (PATCH on a link) — see §5 Decision D4.
- MCP `divoid_link_nodes` param additions (map #6013) — separate PR.
- Frontend `LinkNodeDialog` direction/context input + edge arrow/context rendering (map #6085) — separate PR.

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Ocelot's `SchemaService.CreateOrUpdateSchema<T>` performs additive `ALTER TABLE ADD COLUMN` for new entity properties on both SQLite (dev `DiVoid.db3`) and Postgres. | High — proven precedent: `Node.OwnerId`, `Node.Access`, `Node.X/Y`, `Node.Created/LastUpdate` were all added later to a populated `Node` table via this same path. |
| A2 | A new **non-nullable numeric** column added to a populated table backfills existing rows with its `[DefaultValue]`. `LinkType` backed by int `None = 0` means existing rows read as `None` = current undirected behavior. | High — `Node.Access` uses `[DefaultValue((int)(NodeAccess.Read \| NodeAccess.Write))]`; `Node.OwnerId` uses `[DefaultValue(0L)]`. Exact pattern. |
| A3 | A new **nullable string** column backfills existing rows with `NULL`. `Context` null = "no context". | High — standard nullable-column add. |
| A4 | `Backend` is `<Nullable>disable</Nullable>`. `Context` is declared `string` (no `?`); `null` is its natural absence value. | Confirmed from `Backend.csproj`. |
| A5 | `JsonStringEnumConverter` is registered globally, so `LinkType` serializes as a string (`"None"`/`"Unidirectional"`/`"Bidirectional"`) in both directions. | Confirmed in `Startup.cs`. |
| A6 | The link-create endpoint's current body is a bare JSON `long` (`[FromBody] long targetNodeId`). The MCP tool and existing tests POST a bare number. Back-compat requires this body shape to keep working after this PR (the MCP update is a *later* PR). | Confirmed — `NodeLinkIdempotencyHttpTests` posts `new StringContent($"{targetId}")`. |

No regulatory / performance / data-loss concern surfaced. The `None = 0` default aligning with the column-add-default-to-0 behavior means **no backfill script is required** and no existing row is reinterpreted. No bounce condition triggered.

## 4. Architectural Overview

```
 WRITE (create edge with optional direction + context)
   POST /api/nodes/{sourceId}/links            body: <targetId>  (bare long — unchanged)
        ?linkType=Unidirectional&context=subtask   (NEW — optional query params)
              │
              ▼
   NodeController.LinkNodes ──► INodeService.LinkNodes(source, target, callerId, isAdmin,
                                                       linkType = None, context = null)
              │  inserts NodeLink { SourceId, TargetId, LinkType, Context }
              ▼
        ┌──────────────────────────────┐
        │  NodeLink (entity + storage)  │   SourceId, TargetId, LinkType(enum,int), Context(string?)
        └──────────────────────────────┘
              ▲
              │  raw NodeLink rows serialized directly (no DTO/mapper)
   READ  GET /api/nodes/links?ids=1,2,3  ──►  NodeService.ListLinks  ──►  [{ sourceId, targetId, linkType, context }, …]
```

**Key architectural insight (drives the whole design):** there are two distinct link read-surfaces, and only one can host direction.

- **`GET /api/nodes/links`** returns *raw `NodeLink` rows* (source + target + now type + context). Because it preserves source/target orientation, it is the **only** surface on which direction is meaningful, and it needs **no DTO/mapper work** — adding fields to the `NodeLink` POCO surfaces them automatically (the projection is extended; see §8).
- **`NodeDetails.Links`** is a flat, deduped `long[]` of neighbor ids built by a secondary query (`MaterializeWithLinks`). It **discards** source/target orientation by design (it merges both directions into a neighbor list). Direction is *structurally unrepresentable* on a flat id array. Enriching it would mean changing `long[]` to an object array — a breaking change to a frontend-consumed shape, duplicating what `/api/nodes/links` already provides. This design therefore leaves `NodeDetails.Links` untouched (Decision D3).

## 5. Key Design Decisions

### D1 — `LinkType` is a stored, non-nullable enum with `None = 0` default

`enum LinkType { None = 0, Unidirectional = 1, Bidirectional = 2 }` (locked). Non-nullable, backed by int, declared on `NodeLink` with `[DefaultValue((int)LinkType.None)]`. Rationale: `None = 0` makes the "column added to existing rows" default *identical* to today's undirected behavior — back-compat falls out for free with no backfill. Mirrors the proven `Node.Access` enum-storage pattern.

**No `[Index]` on `LinkType`.** Locked decision #4: direction is metadata a consumer *reads*, never a query filter for v1. Indexes serve filtering/sorting, which is explicitly out of scope → adding one is YAGNI (Design Contracts §3).

### D2 — `Context` is a single nullable string, read source→target

`public string Context { get; set; }` — nullable (null/empty = no context). Single string, interpreted in the source→target direction (locked). No per-direction pair (out of scope, YAGNI). No `[Index]` (not queried).

### D3 — Direction + context surface on `GET /api/nodes/links` only; `NodeDetails.Links` is NOT enriched

The task brief invited confirming "is there a link DTO, or is it raw `NodeLink`?" — the answer is **raw `NodeLink`** on `/api/nodes/links`, and a **flat `long[]`** on `NodeDetails.Links`. Only the former preserves orientation. Enriching the flat array would be a §2 Form-2 parallel surface (duplicating `/links`) *and* a breaking shape change to a surface whose consumer work (frontend edge rendering) is an explicit separate PR. **Decision:** surface the new fields on `/api/nodes/links`; leave `NodeDetails.Links` as a flat id array. This is the KISS/existing-systems-first choice and keeps the shape change off the frontend's plate until its own PR.

### D4 — No edit-existing-edge (no link PATCH), no `[AllowPatch]` on the new fields

The task asked me to *decide* patchability (it is **not** a locked decision). Decision: **no** for v1.

- There is today **no** PATCH route for links (links have POST-create and DELETE-unlink only). `[AllowPatch]` is only meaningful when a field is reachable through `DatabasePatchExtensions.Patch`, which requires a *new* endpoint + service method + interface method + auth gate + tests — a whole new mutable surface.
- No follow-on names an edit-existing-edge requirement (MCP #6013 and FE #6085 are both *create-time*). Building the PATCH surface now is YAGNI (Design Contracts §1 YAGNI, §6 "abstraction for future flexibility").
- Adding `[AllowPatch]` to `NodeLink` fields *without* a patch route would be inert decoration (dead code) — Code Contracts `[AllowPatch]` discipline.
- **To change an edge's type/context today:** unlink + relink with the desired params (existing capability). If a concrete edit-existing-edge need surfaces, it is a filed follow-on with the real shape in hand.

**Trade-off named (§4 discipline):** the downside is that re-issuing a create against an *already-existing* pair is a no-op (see D5), so caller-supplied `linkType`/`context` on a re-link are silently dropped. Probability of hitting this: low for the create-a-directed-edge primary use case; the edit path is deferred deliberately. Cost of the alternative (PATCH-link surface) now: a full new mutable endpoint + tests for a use case no consumer yet requires. The simple version ships.

### D5 — Create semantics unchanged: existing-pair re-link stays an idempotent no-op

`LinkNodes` currently early-returns if the pair already exists (bug #702 idempotency, load-bearing tests `NodeLinkIdempotencyHttpTests`). This behavior is **preserved unchanged** — re-linking an existing pair remains a no-op regardless of the new params. Rationale: preserves current behavior exactly (KISS, no regression to the #702 fix); avoids the "explicitly-`None` vs absent" ambiguity that an upsert branch would introduce. Setting type/context on a *new* edge works; changing them on an existing edge is the deferred edit path (D4).

### D6 — Write params are optional query parameters; the bare-`long` body is unchanged

To keep the create endpoint back-compat (constraint A6) and decoupled from the later MCP PR, `linkType` + `context` are added as **optional query parameters** on the existing `POST /api/nodes/{sourceId}/links`; the request body stays a bare `long targetId`.

- Alternative rejected — change the body to an object `{ targetId, linkType, context }`: breaks the bare-`long` callers (MCP, existing tests) the moment this PR merges, before the MCP PR lands. In a staged multi-PR rollout that temporal breakage is a real cost. Query params are purely additive; the current MCP tool keeps working and simply doesn't send the new params yet.
- Context as a query param is URL-encoded; short label values (`"subtask"`) are unproblematic.

### D7 — Inline links at node creation (`NodeDetails.Links`) still create `None`/`null` edges

`POST /api/nodes` with an inline `Links` id-array continues to create undirected, contextless edges. Enriching inline creation would require changing the `long[] Links` read/write shape on `NodeDetails` — a breaking change out of scope (frontend concern). The explicit `POST /{source}/links` endpoint is the path that gains type/context. (KISS — don't touch the `long[]` shape.)

### D8 — Service signature: append defaulted params after the auth context (zero call-site churn)

`LinkNodes` has **28 existing call sites** (all tests) using `LinkNodes(source, target, callerId, isAdmin)`. C# forbids optional params before required ones, so the new params are **appended after `callerId, isAdmin`** with defaults:
`LinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin, LinkType linkType = LinkType.None, string context = null)`.
Every existing 4-arg caller compiles unchanged and behaves identically (defaults = current behavior). Only the controller and new tests pass the extra args. This is a deliberate DRY/KISS choice to avoid churning 28 unrelated call sites for no behavioral reason; the slightly-unconventional param order (auth before domain) is the accepted, documented trade-off.

## 6. Components & Responsibilities

| Component | Owns / Changes | Does NOT own |
|---|---|---|
| `LinkType` (new enum, `Backend/Models/Nodes/LinkType.cs`) | The three direction values and their int backing. | Any query/filter semantics. |
| `NodeLink` (entity) | Two new stored fields: `LinkType` (`[DefaultValue((int)LinkType.None)]`), `Context` (nullable). | Serialization DTO (it *is* the wire shape for `/links`). No index on the new fields. |
| `DatabaseModelService` | Nothing new — `CreateOrUpdateSchema<NodeLink>` already present; additive columns ride the existing call. | Migrations (none exist). |
| `INodeService` / `NodeService.LinkNodes` | Accept + persist optional `linkType` + `context` on **new** edges; preserve idempotent no-op for existing pairs. | Mutating existing edges (deferred). |
| `NodeService.ListLinks` | Extend the `NodeLink` projection to include `LinkType` + `Context` so `/links` returns them. | `NodeDetails.Links` (flat array, unchanged). |
| `NodeController.LinkNodes` | Bind optional `[FromQuery] LinkType linkType`, `[FromQuery] string context`; pass through. | Body shape (bare `long`, unchanged). |
| `NodeService.CreateNode` (inline links) | Unchanged — inline links stay `None`/`null`. | — |

## 7. Data Model (Conceptual)

`NodeLink` is an undirected adjacency row between two nodes. After this change it additionally carries:

- **LinkType** — direction semantics. `None`: order of `SourceId`/`TargetId` carries no meaning (today). `Unidirectional`: one arrow, `SourceId → TargetId`. `Bidirectional`: both ends directed, each direction meaningful.
- **Context** — optional label carried on the edge, read in the `SourceId → TargetId` direction.

Storage row remains a single row per pair (create stores `(SourceId, TargetId)` once; the reverse pair is treated as the same edge for idempotency and unlink). Direction is interpreted by the reader relative to the stored `SourceId`/`TargetId`.

## 8. Contracts & Interfaces (Abstract)

**`POST /api/nodes/{sourceId}/links`**
- Inputs: path `sourceId`; body bare `long targetId` (unchanged); optional query `linkType` (enum string, default `None`); optional query `context` (string, default null).
- Semantics: creates a new edge carrying the given type/context; if the pair already exists, no-op (idempotent, params ignored — D5). Self-link rejected (unchanged). Returns 2xx (void-Task → 200).
- Invariant: absent params ⇒ `None` / null ⇒ byte-for-byte current behavior.

**`GET /api/nodes/links?ids=…`**
- Output per row: `sourceId`, `targetId`, **`linkType`** (string), **`context`** (string or absent/null). Rows with legacy data report `linkType="None"`, `context=null`.
- Implementation note (load-bearing): `ListLinks` currently projects only `SourceId, TargetId`. The projection **must** be extended to include `LinkType` and `Context`, else the new fields serialize as defaults even when set.

**`INodeService.LinkNodes`** — signature per D8. `linkType`/`context` apply only to newly-inserted edges.

## 9. Cross-Cutting Concerns

- **Back-compat / consistency:** guaranteed by `None = 0` + null defaults and the additive query params; no existing row or caller reinterpreted (A2/A3/A6).
- **Auth:** unchanged — `write` policy on the endpoint, write-visibility gate on the source node.
- **Idempotency:** the #702 no-op-on-duplicate contract is preserved (D5); its load-bearing tests must stay green.
- **Serialization:** `LinkType` via global `JsonStringEnumConverter` (string both ways); `Context` plain string.
- **Error handling:** unchanged; no new failure modes introduced.
- **Observability:** existing link log lines suffice; optionally include `linkType` in the create log line (nice-to-have, not required).

## 10. Quality Attributes & Trade-offs

- **Simplicity (primary):** two fields on an existing entity, additive query params, one projection extension, one read surface. No new table, no DTO, no mapper touch, no new endpoint, no migration, no index, no backfill. This is the minimum that satisfies the requirement.
- **Maintainability:** the `None = 0` default and single-string context keep the mental model tiny.
- **Trade-off (D4/D5):** no edit-existing-edge path; changing an edge = unlink + relink. Accepted deliberately (YAGNI) vs. building a speculative PATCH-link surface.
- **Trade-off (D8):** auth params precede domain params in `LinkNodes` to avoid 28-call-site churn. Accepted.
- **Rejected alternative:** enriching `NodeDetails.Links` (D3) and object-body create (D6) — both add surface/breakage for no requirement this PR owns.

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| SQLite `ADD COLUMN` for a non-null int fails on populated `DiVoid.db3`. | Low | Proven precedent (`Node.Access`/`OwnerId` added the same way with `[DefaultValue]`). Implementer verifies by running the app / `DatabaseModelServiceTests` against a populated db and reading a legacy link back as `None`/null. |
| `ListLinks` projection not extended ⇒ new fields silently default on read. | Medium (easy to miss) | Called out explicitly in §8; covered by a test that creates a directed+context link and asserts it round-trips through `GET /api/nodes/links`. |
| An upsert expectation (re-link changes context) is assumed by a consumer. | Low | Documented no-op semantics (D5); edit path deferred with a clear "unlink+relink" workaround. |

## 12. What Does NOT Go In (explicit out-of-scope for this PR)

- **Directional traversal filtering** — `linkedto` stays both-directions; no per-direction query. (Locked decision #4, YAGNI.)
- **Per-direction context pair** — single string only. (Locked decision.)
- **Enriching `NodeDetails.Links`** — flat id array stays flat; direction/context are read from `/api/nodes/links` (D3).
- **Edit-existing-edge / link PATCH / `[AllowPatch]` on the new fields** — deferred; unlink+relink is the interim path (D4).
- **Inline-link-at-creation type/context** — inline `Links` create `None`/null edges (D7).
- **MCP `divoid_link_nodes` params** — separate PR (map #6013).
- **Frontend `LinkNodeDialog` + edge arrow/context rendering** — separate PR (map #6085).

## 13. Open Questions

None blocking. One optional confirmation for the follow-on FE PR (not this one): whether the frontend will eventually need an edit-existing-edge path (which would justify the deferred link-PATCH surface). Not required to proceed here.

## 14. Implementation Guidance for the Next Agent (john-backend-dev)

Ordered, on branch `feat/link-type-and-context` (already created off `origin/main`, design doc already committed). All code follows Code Contracts #114 (Ocelot idioms, controller/service boundaries, K&R braces, 4-space, `<Nullable>disable</Nullable>`).

1. **Enum:** add `LinkType { None = 0, Unidirectional = 1, Bidirectional = 2 }` in `Backend/Models/Nodes/LinkType.cs` (mirror `NodeAccess.cs` file/doc style; not `[Flags]`).
2. **Entity:** add to `NodeLink` — `LinkType LinkType` with `[DefaultValue((int)LinkType.None)]`, and `string Context` (nullable, no attribute). No `[Index]` on either.
3. **Schema:** no change to `DatabaseModelService` (the existing `CreateOrUpdateSchema<NodeLink>` handles the additive columns). Verify columns are added to a populated db and a legacy link reads back `None`/null.
4. **Service — create:** extend `INodeService.LinkNodes` and `NodeService.LinkNodes` per D8 (append `LinkType linkType = LinkType.None, string context = null` after `callerId, isAdmin`). Include the two new columns in the `Insert<NodeLink>()` for the new-edge branch only. Keep the existing-pair early-return no-op (D5) unchanged.
5. **Service — read:** extend the `ListLinks` projection to `Load<NodeLink>(l => l.SourceId, l => l.TargetId, l => l.LinkType, l => l.Context)` (and the mirror in the count op is unaffected).
6. **Controller:** add `[FromQuery] LinkType linkType`, `[FromQuery] string context` to `NodeController.LinkNodes`; pass through. Body stays `[FromBody] long targetNodeId`.
7. **Tests (Backend.tests, #5997) — cover every new/changed line:**
   - Default/back-compat: create a link with no params → `GET /api/nodes/links` reports `linkType="None"`, `context` null.
   - Directed + context: `POST …/links?linkType=Unidirectional&context=subtask` → round-trips through `/links` with the exact values; `Bidirectional` likewise.
   - Existing-pair re-link with params is a no-op (D5) — extend/adjacent to `NodeLinkIdempotencyHttpTests`; the #702 tests stay green.
   - Inline links at node creation still produce `None`/null edges (D7).
   - Schema: a link row is readable with the new columns after bootstrap (extend `DatabaseModelServiceTests` or a new fixture-based test).
   - Confirm all 28 existing `LinkNodes` call sites compile unchanged.
8. **PR:** open **one** PR (this design doc + the backend increment). PR body names the two staged follow-ons as separate future PRs: MCP `divoid_link_nodes` params (#6013) and FE `LinkNodeDialog` + edge rendering (#6085).
