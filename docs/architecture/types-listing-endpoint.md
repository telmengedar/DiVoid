# Architectural Document: Types-listing endpoint (task #485)

## 1. Problem Statement

The workspace frontend currently uses a **hardcoded enum of 11 node types** to build the type-filter dropdown. New types created in the live graph (e.g. `product`, currently 1 node in prod) do not appear in the dropdown, so users cannot filter on them or even see that they exist. The frontend needs to **discover the live type vocabulary from the backend** at runtime.

Success criterion: one authenticated `GET` call returns every type that is actually in use in the graph today, in a form the frontend can iterate to build the dropdown without code changes when a new type appears.

## 2. Scope & Non-Scope

**In scope:**
- A read-only catalog endpoint exposing the live node-type vocabulary.
- Per-type usage count (the frontend wants to render counts in the dropdown).
- Stable sort order suitable for a dropdown.
- Auth and error semantics consistent with the rest of the listing surface.

**Out of scope (explicit cuts, baked into the spec so the implementer does not over-build):**
- No POST/PATCH/DELETE on the new resource — types are created implicitly by node-create today, that pipeline stays.
- No paging — the catalog is tiny and will stay tiny (≤50 entries for years to come). Do **not** add `ListFilter` plumbing speculatively.
- No filter parameters (no `?name=`, `?id=`, no wildcard predicates). The endpoint is a catalog dump.
- No frontend changes — separate task (#486, blocked on this).
- No linkage to the type-documentation nodes under Types group #29 (e.g. the `type: task` documentation node at #32). The current request is the **live vocabulary**, not metadata about each type.
- No GROUP BY of orphaned `NodeType` rows. The shape "in-use" is the only definition that answers the frontend's question.

## 3. Assumptions & Constraints

- The DB is whatever `Database:Type` is configured to use (SQLite locally, PostgreSQL in prod). Both backends are supported by Ocelot's `Load<T>().Join().GroupBy()` composition; no DB-specific SQL is required.
- `NodeType` is an existing entity, joined into the standard `NodeMapper.CreateOperation` via `n.TypeId == t.Id`. The join key is well-indexed (PK on `NodeType.Id`, foreign `Node.TypeId`).
- The codebase convention for every collection endpoint is `AsyncPageResponseWriter<T>` returning `{"result":[…], "total":N, "continue":…}` via `JsonStreamOutputFormatter`. The new endpoint must conform — this is the convention shared by `/api/nodes`, `/api/nodes/links`, `/api/messages`, `/api/users`, `/api/apikeys`.
- The `AsyncPageResponseWriter` envelope works fine for non-paged data: the `continue` token is null, `total` equals `result.Length`. No special-casing required.
- The fail-closed Keycloak audience guard in `Startup` already covers this endpoint — no auth-related changes needed.
- Code Contracts (DiVoid #114) apply: especially §3 (no redundant `async`/`Task.FromResult` wraps; no list-materialising streams; single-statement transactions only) and §4 (no in-body explanatory comments).

## 4. Architectural Overview

The endpoint is a thin read-only catalog query. Shape:

```
HTTP GET /api/types
        │
        ▼
TypeController.ListTypes                  ← new controller, sibling of NodeController, MessageController, UserController
        │
        ▼
INodeService.ListTypes()                  ← new method on the existing service (types are a Node sub-concern; NodeType already joined into NodeMapper)
        │
        ▼
Ocelot:  Load<NodeType>()
         .Join<Node>(t.Id == n.TypeId)
         .GroupBy(t.Id, t.Type)
         .Select(t.Id, t.Type, COUNT(n.Id) AS count)
         .OrderBy(count DESC, type ASC)
        │
        ▼
WindowedFromOperation → AsyncPageResponseWriter<TypeListItem>
        │
        ▼
JsonStreamOutputFormatter → {"result":[{...}], "total":N, "continue":null}
```

**Why top-level `/api/types` and not nested `/api/nodes/types`:**

The codebase already shows both patterns. Nesting is appropriate when the resource is *about* a specific node:
- `/api/nodes/links` — links involving the supplied node ids (filter parameter is required).
- `/api/nodes/{id}/user` — the user-id bound to *this* node.
- `/api/nodes/{id}/content` — content *of this node*.

The types catalog is **not about a particular node** — it is a sibling catalog of the global vocabulary, the same way `/api/users`, `/api/messages`, `/api/apikeys` are sibling catalogs. Conceptually it is closer to "what entities exist" than to "facts about a given node". Top-level wins.

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|---|---|---|
| `TypeController` (new, `api/types`) | HTTP shape, route, auth attribute, logging line, delegation to service. | Query construction, data shaping, filter validation. |
| `INodeService.ListTypes` / `NodeService.ListTypes` (new method) | Single Ocelot query: join `NodeType` to `Node`, group by type, project to DTO, sort. Returns `AsyncPageResponseWriter<TypeListItem>`. | Auth, HTTP concerns, paging policy (there is no paging). |
| `TypeListItem` (new DTO, `Backend/Models/Nodes/`) | API shape of one row: `Id`, `Type`, `Count`. | Persistence — there is no entity behind it; it is a projection over the existing `NodeType` + `Node` tables. |
| `TypeListMapper : FieldMapper<TypeListItem, NodeType>` (new) | Field mappings, default field list, `CreateOperation` that aliases `NodeType` as `type` and joins `Node` as `node` with `GROUP BY` and the `COUNT` projection. | Filter logic — there are no filter parameters. |

**Why the method lives on `INodeService` and not on a new `ITypeService`:**

- `NodeType` is already a Node sub-concern in the codebase: its entity lives at `Backend/Models/Nodes/NodeType.cs`, it is joined into every Node list query by `NodeMapper`, and its rows are created/managed inside `NodeService.CreateNode` (implicit creation on first use).
- The count being returned is a count of `Node` rows.
- A new `ITypeService` would add one DI registration and one file purely to host one read-only query that fundamentally describes node-type usage. That is the "split for the sake of splitting" failure mode — and §3 of the contracts (smell-class scan) rejects it.

The controller can still be its own class (`TypeController`) because controllers are organised by HTTP route prefix, not by service ownership — `LayoutController` already calls `LayoutNodesService`, and several controllers can call the same service.

## 6. Interactions & Data Flow

Synchronous, single request, single SQL statement:

1. Client issues `GET /api/types` with Bearer token (JWT or API-key).
2. Auth pipeline resolves the caller and the `read` policy passes.
3. `TypeController.ListTypes` logs `event=type.list callerId=<id>` and awaits `nodeService.ListTypes()`.
4. `NodeService.ListTypes` builds one `LoadOperation<NodeType>` joined to `Node` with `GROUP BY (NodeType.Id, NodeType.Type)`, projects `(Id, Type, COUNT(Node.Id) AS count)`, orders by `count DESC, type ASC`, and runs it through `WindowedFromOperation` so the total comes from the same statement.
5. The returned `AsyncPageResponseWriter<TypeListItem>` is detected by `JsonStreamOutputFormatter` (sits at index 0 of `OutputFormatters`) and streamed as the canonical `{"result":[…], "total":N, "continue":null}` envelope.

No fan-out, no message bus, no caching layer. A single DB round-trip.

## 7. Data Model (Conceptual)

No new persisted entity. The endpoint is a **projection** over existing entities:

- `NodeType` — already exists. `{Id, Type}` lookup table.
- `Node` — already exists. `TypeId` references `NodeType.Id`.

`TypeListItem` is the **API DTO** for one projected row:

| Property | Source | Semantics |
|---|---|---|
| `id` (long) | `NodeType.Id` | Primary-key id of the type row. Stable across calls. Useful for the frontend if it ever wants to drive filtering by `?type=` (today the frontend filters by name string; id is included as future-proofing at zero cost). |
| `type` (string) | `NodeType.Type` | The type name as the rest of the API spells it (`task`, `documentation`, `product`, …). This is what the frontend renders and what `?type=` accepts on `/api/nodes`. |
| `count` (long) | `COUNT(Node.Id)` over the join | Number of `Node` rows currently using this type. Always ≥1 by construction (inner join filters orphans). |

Counts are a snapshot at query time. No caching is required — the table is small and the query is cheap.

## 8. Contracts & Interfaces (Abstract)

### HTTP contract

| Element | Value |
|---|---|
| Method | GET |
| Path | `/api/types` |
| Query parameters | None |
| Auth policy | `read` |
| Success | 200 with body `{"result":[{TypeListItem},…], "total":N, "continue":null}` |
| 401 | Missing/invalid bearer (handled by existing middleware) |
| 403 | Caller authenticated but lacks `read` (canonical `{code,text}` body) |
| Caching | None at the API layer. If a future browser-side cache becomes necessary, the frontend may use a short TTL — out of scope here. |

`TypeListItem` shape (JSON):
```
{
  "id": 5,
  "type": "task",
  "count": 202
}
```

Ordering invariants:
- `count DESC` primary.
- `type ASC` secondary (deterministic tie-breaker; alphabetical on the type-name string).
- No client-controlled sort. Adding `?sort=` is out of scope; the dropdown's expected order is "most-used first, alpha tie-break", which is what the endpoint returns unconditionally.

Invariants:
- Every row in the response satisfies `count ≥ 1` (orphans are filtered by the inner JOIN — see §10 on the trade-off).
- The response is exhaustive: no implicit limit. The result set is bounded by the small distinct cardinality of `NodeType` (currently 11, projected ≤50 for years).
- `continue` is always null. No paging cursor.
- `total` equals `result.Length`.

### Service-method contract (abstract)

`INodeService.ListTypes` — no parameters, returns the standard listing envelope:

- Input: none.
- Output: `Task<AsyncPageResponseWriter<TypeListItem>>`. The signature must be **properly async** (return the `Task` directly via `await` on the `WindowedFromOperation` call); §3 of #114 prohibits `Task.FromResult` wrappers when the underlying call is already a Task.
- Side effects: none. Pure read.
- Error semantics: nothing throws. There is no "not found" — an empty graph legitimately returns `{"result":[],"total":0,"continue":null}`.

## 9. Cross-Cutting Concerns

| Concern | Treatment |
|---|---|
| **AuthN** | Default pipeline (PolicyScheme dispatches JWT or API-key). No changes. |
| **AuthZ** | `[Authorize(Policy = "read")]`. Same as every other list endpoint. |
| **Logging** | One structured line on entry: `event=type.list callerId={CallerId}`. No payload logging — there is no PII and the response is the global type vocabulary. |
| **Observability** | Inherits the existing JSON logger and HTTP middleware. Nothing custom. |
| **Errors** | None expected on the happy path. Bearer/policy failures flow through the existing `ErrorHandlerMiddleware` and produce the canonical `{code,text}` envelope. |
| **Caching** | None server-side. The query is one JOIN with GROUP BY on a tiny table; sub-millisecond on Postgres. Adding cache infrastructure here would be the "over-production" smell. |
| **Concurrency** | Read-only. No locks, no isolation level concerns. The count may shift between two calls if nodes are being inserted/deleted concurrently — this is acceptable and is the same semantics every other list endpoint exposes. |
| **Idempotency** | GET; trivially idempotent. |
| **CancellationToken** | The method signature has no `CancellationToken` parameter because the query is a single statement on a tiny table; aborting mid-query has no value and would diverge from `ListLinks`/`ListUsers` which also omit it. (Adding it is harmless but unnecessary; the implementer should not add it speculatively.) |

## 10. Quality Attributes & Trade-offs

### Decision: inner JOIN ("in-use types only") vs. all-`NodeType`-rows

**Rejected:** return all `NodeType` rows with no join.

**Chosen:** inner JOIN on `Node`, `GROUP BY (NodeType.Id, NodeType.Type)`, return only types with `count ≥ 1`.

**Reasoning:**
- The frontend's question is *"what types are in use today?"*. That is what the dropdown needs to populate. The "all rows" variant would expose orphaned `NodeType` rows if any existed, polluting the dropdown.
- Today orphans are probably impossible in practice (`NodeService.CreateNode` only inserts a new `NodeType` when one is needed, and node-deletion does not garbage-collect the `NodeType` row — so in theory deleting all nodes of a type could leave an orphan). The endpoint's contract should not depend on whether the garbage-collection invariant happens to hold.
- The cost is one JOIN + GROUP BY on a table with O(thousands) of rows max. Postgres and SQLite both handle this in sub-millisecond. The marginal cost over the "all rows" plan is negligible.

Trade-off accepted: a type that has *just* been used (one node) and then *just* deleted disappears from the dropdown immediately. That is the correct UX — invisible types should not be filterable.

### Decision: include count by default vs. opt-in `?withCount=true`

**Rejected:** opt-in counts.

**Chosen:** counts always included.

**Reasoning:**
- The empirical data shows counts are useful (`task (202)` vs `product (1)` carries information).
- Once the JOIN is in the query (per the previous decision), the COUNT is **free** — same SQL plan, same scan, one extra projection column.
- An opt-in flag would add a query-string parameter, a model-binder field on a non-existent filter class, and conditional logic on the query construction — all to save zero work. That is the "speculative configurability" smell.

### Decision: standard `AsyncPageResponseWriter` envelope vs. terse `T[]`

**Rejected:** return a plain `TypeListItem[]` and let the default JSON formatter serialize it.

**Chosen:** `AsyncPageResponseWriter<TypeListItem>` with the canonical `{result,total,continue}` envelope.

**Reasoning:**
- Every collection endpoint in this codebase uses this envelope. The frontend's existing list-response handling will work unchanged.
- The cost is zero — `WindowedFromOperation` produces both the rows and the count in one statement; wrapping in the `AsyncPageResponseWriter` is mechanical.
- Diverging "because the catalog is tiny" introduces a special case the frontend has to handle, for no benefit.

### Decision: sort order

**Chosen:** `count DESC, type ASC`. No client override.

**Reasoning:** the dropdown is most useful when the high-cardinality types are at the top. Alpha tie-break for determinism. Adding `?sort=` would expand the surface area for zero current consumer benefit (task #486 is the only consumer).

### Decision: no paging

The catalog cardinality is bounded by the distinct vocabulary humans + agents use to type nodes. Today: 11. Forecast: ≤50 for the foreseeable future. Adding `?count=`/`?continue=` would introduce 20+ lines of filter plumbing that will never execute the paged branch. Hard "no" — call this out in the doc explicitly so john does not import the pattern from `ListPaged`.

### Maintainability

The endpoint is one controller method, one service method, one DTO, one mapper. Every change to the type-vocabulary semantics has exactly one place to land.

### Performance

A single SQL statement: `SELECT t.id, t.type, COUNT(n.id) FROM nodetype t INNER JOIN node n ON n.typeid = t.id GROUP BY t.id, t.type ORDER BY count DESC, t.type ASC`. Index on `Node.TypeId` (which the existing `[Index("node")]` on `Node.TypeId` provides — verify) makes this O(N) over `Node` rows in the worst case, with grouping cardinality O(distinct types). Sub-millisecond at current and projected scale.

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Implementer adds speculative paging / `ListFilter` parameter "for symmetry with `/api/nodes`". | Medium — the project's listing patterns are uniform and the temptation is real. | The spec calls out "no paging, no filter params" in §2 and §10 explicitly. Jenny's smell-scan (§3 of #114) will catch redundant `ListFilter` plumbing. |
| Implementer wraps the service call in `Task.FromResult(new AsyncPageResponseWriter(...))` à la `NodeService.ListLinks`. | Medium — `ListLinks` is the closest existing template and it has the smell. | §3 of #114 is explicit. The doc states the method must be properly async. Reviewer will reject. |
| Ocelot's `Join` + `GroupBy` + windowed-count composition has an edge case the implementer hits and works around with in-process aggregation. | Low–Medium — Ocelot's GROUP BY support is exercised less than its plain joins in this repo. | If `WindowedFromOperation` cannot be applied to a `GroupBy`-shaped query, the fallback is: run the projection query as one `ExecuteEntitiesAsync` and pass its materialised count as the `total` lambda. Document the fallback in the implementation if it is taken; **never** load all node rows and aggregate in C#. |
| Frontend changes the dropdown sort and the backend ordering is now wrong for them. | Low. | The spec is explicit; if a future consumer wants a different sort, they can sort client-side. The catalog is ≤50 entries. |
| Orphan-`NodeType` rows appear and someone wants to inspect them. | Low (no GC today exists, but the prod table has no orphans currently). | Out of scope for this endpoint. If needed, an admin-only `/api/types?includeOrphans=true` can be added later — different concern, different consumer. |
| The "in-use types" semantics surprises a future caller that expected the table dump. | Low. | XML doc on the method, swagger `<summary>` text, and the doc node on DiVoid make the semantics explicit. The phrase "types in use" should appear in the controller XML summary. |

## 12. Migration / Rollout Strategy

Not applicable — net-new endpoint, additive, no consumer migration. The frontend integration is task #486 and ships separately. The endpoint can ship behind no flag.

## 13. Open Questions

None that block implementation. The Ocelot `GroupBy` + windowed-count composition is the one place where the implementer may need a small empirical check; the fallback path is documented in §11.

## 14. Implementation Guidance for the Next Agent

In dependency order. Treat each bullet as one architectural unit; john may choose to commit them together or split, but the order is fixed.

1. **DTO** — add `TypeListItem` under `Backend/Models/Nodes/`. Three properties: `Id` (long), `Type` (string), `Count` (long). XML summaries on type and properties per §3/§4 of #114.

2. **Mapper** — add `TypeListMapper : FieldMapper<TypeListItem, NodeType>` under `Backend/Models/Nodes/`. Three `FieldMapping`s: `id`, `type`, `count`. `DefaultListFields = ["id", "type", "count"]`. `CreateOperation` aliases `NodeType` as `type`, joins `Node` as `node` on `n.TypeId == t.Id`. Look at `NodeMapper.CreateOperation` for the alias pattern.

3. **Service contract** — add `ListTypes` to `INodeService`. Returns `Task<AsyncPageResponseWriter<TypeListItem>>`. No parameters. XML doc explicitly states "returns only types with at least one node, ordered by count descending then type ascending".

4. **Service implementation** — add `ListTypes` to `NodeService`. Build the query through the mapper, apply `GroupBy` on the `NodeType.Id`+`NodeType.Type` columns, project the count, apply `OrderBy(count DESC, type ASC)`, run through `WindowedFromOperation` (or the documented fallback in §11 if Ocelot's GroupBy + windowed-count composition fails). The method body must be **properly async** — `await` the windowed result, return the `AsyncPageResponseWriter`. Do not wrap in `Task.FromResult`.

5. **Controller** — add `Backend/Controllers/V1/TypeController.cs`. Route `[Route("api/types")]`. One action `ListTypes`, `[HttpGet]`, `[Authorize(Policy = "read")]`. `[ProducesResponseType(200/401/403)]` annotations. One log line `event=type.list callerId={CallerId}`. Body: `return nodeService.ListTypes();` (a single-statement passthrough — no `await`, no `async`, return the Task directly per §3).

6. **Tests** (load-bearing per #275) — under `Backend.tests`:
   - **Positive**: seed two `NodeType` rows + matching `Node` rows (e.g., 2 nodes of type `alpha`, 1 of type `beta`). GET `/api/types`. Assert the response contains both, with counts `2` and `1`, ordered `alpha` first (count desc), and `total=2`.
   - **Negative-proof / load-bearing**: temporarily comment out the `OrderBy` or the JOIN in `NodeService.ListTypes`; assert the positive test fails with a concrete mismatch (not green-on-removal). Document the substitution in the PR body.
   - **Orphan-filter**: seed a `NodeType` row with **zero** referencing nodes; assert it does **not** appear in the response (proves the inner JOIN filters orphans).
   - **Auth**: assert 401 with no bearer; assert 403 (or canonical 401 in this codebase's flavour) with a `read`-less caller if the test rig supports it.
   - **Empty graph**: with no `NodeType` rows, GET returns `{"result":[],"total":0,"continue":null}`.

7. **Architectural doc commit** — commit this document (after john shapes it for the repo) at `docs/architecture/types-listing-endpoint.md` on the implementation branch.

8. **PR** — one PR, branched from `main` tip `2c77c17`. Push under the `pooshit` profile (telmengedar/DiVoid repo requirement per DiVoid #184). Do **not** bundle anything else into this PR. Frontend integration is task #486 and ships separately.

### Smells to actively guard against during implementation (lifted from #114 §3)

- `Task.FromResult(new AsyncPageResponseWriter(...))` — copy of the `ListLinks` template. Wrong. Make the service method properly async.
- `using Transaction transaction = database.Transaction(); … transaction.Commit();` around a single read — single-statement transactions are noise. The query is a read; no transaction wrapper.
- Materialising all `NodeType` rows and counting nodes per type in C# — this is the "in-process filtering / list-materialising stream" smell. The aggregation belongs in SQL.
- `var` anywhere. Explicit types only.
- Speculative `[FromQuery] ListFilter filter` parameter — the spec is no-paging, no-filtering. Do not add it.
- Explanatory `// ───── helpers ─────` or `// build the query` comments inside the method body — §4 forbids them. XML summaries only.
- A new `ITypeService` — the method belongs on `INodeService` per §5.
