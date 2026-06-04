# Architectural Document: Severity field on Node

DiVoid task #1605. Code Contracts #114 and Design Contracts #1136 are load-bearing for this design.

## 1. Problem Statement

> "Add a 'severity'-field to nodes. The usage of this field differs from type to type — task types map their priority on them, thoughts could map an importance level — for the system it's just an abstract number, the application scope fills it with meaning. This one is also mcp relevant as it's important clients can work with it." — Toni, 2026-06-04

Nodes need a uniform, queryable numeric attribute that application-scope consumers (and humans) interpret per type. The backend stays type-agnostic: it persists, exposes, filters, sorts, and patches the number, and never reasons about what the number "means." MCP clients must be able to read, write, filter, and sort by it.

Success: a caller can `GET /api/nodes?type=task&sort=severity&descending=true` and receive tasks ordered by their numeric priority, and can `PATCH /api/nodes/{id}` with `replace /severity -> 5` to set it.

## 2. Scope & Non-Scope

### In scope
- Persisted `Severity` attribute on the `Node` entity (nullable integer).
- DTO field + mapper registration so it flows in GET/list responses.
- `[AllowPatch]` so PATCH `replace`/`remove` work.
- Filter and sort surface on `NodeFilter` / `NodeMapper`.
- The MCP **wire contract** — what shape `severity` takes on `divoid_search`, `divoid_list`, `divoid_get_node`, `divoid_patch_node`, and the composite create tools. The MCP **implementation** is on the divoid-mcp project and ships as a follow-up task filed after this PR opens.

### Out of scope
- Per-type semantic validation (no "tasks must be in `[1..5]`"; backend stays abstract).
- Backfilling existing rows (existing nodes carry `Severity = null`; a backfill is a separate task if Toni later asks).
- Range or enum constraints (no `SeverityScale` table, no enum mirror, no numeric bounds).
- Frontend surface (NodeDetailView, EditNodeDialog, filter popover, sort dropdown) — frontend task filed separately once this lands.
- Bulk-patch endpoints; severity-driven notifications; severity-based RBAC; severity audit/history; severity participation in embeddings.
- Migration of `int?` → narrower types at storage; we accept the standard nullable integer column type Ocelot emits.

## 3. Assumptions & Constraints

- `Node` is a single shared shape across every node type — per-type tables / per-type columns are anti-patterns the existing design (DiVoid #24, Status field) explicitly rejected. Severity inherits the same uniformity constraint.
- Schema is created at startup by `Init/DatabaseModelService` calling `SchemaService.CreateOrUpdateSchema<Node>`; new columns are added on next boot. No migration folder.
- `[AllowPatch]` is the only mechanism by which a field becomes mutable through `PATCH /api/nodes/{id}`. `DatabasePatchExtensions.Patch` throws `NotSupportedException` (→ HTTP 400) otherwise.
- The patch infra supports `replace`, `add`, `remove`, `flag`, `unflag`, `embed`. Of these, `replace` and `remove` are the meaningful operations for a numeric field. `add` exists in the codebase but is implemented as `column = column + value` (numeric addition) — see `DatabasePatchExtensions.cs:62-66`; we accept that as the existing semantic and do not legislate it for severity.
- `Backend.csproj` has `<Nullable>disable</Nullable>` — `?` on reference types is forbidden. `int?` on a value type is fine and is the standard nullable-integer shape in the codebase (mirrored by `NodeDetails.Similarity` (`float?`), `NodeDetails.X`/`Y` (`double?`), `NodeDetails.Access` (`NodeAccess?` since PR #133 / DiVoid #1462), `NodeFilter.CreatedFrom` (`DateTime?`)).
- The list endpoint resolves `?sort=<key>` strictly through `NodeMapper`'s registered field-mapping keys (`FilterExtensions.ApplyFilter` mapper overload). Adding a new sort key requires the field-mapping registration to exist.
- `JsonStringEnumConverter` is globally registered (not relevant here, but it means a nullable-int field deserializes as a plain JSON number, with `null` and omitted both deserializing to `null`).

## 4. Architectural Overview

The design adds one nullable integer column to `Node`, mirrors it on `NodeDetails`, registers it in `NodeMapper`, marks it `[AllowPatch]`, extends `NodeFilter` with a small set of read predicates (value match, range, "no severity"), and exposes `severity` as a sort key.

The shape is **structurally identical to Status** (DiVoid #24) — same single-column, same DTO mirror, same mapper registration, same `[AllowPatch]` posture, same filter pattern. The only material differences are (a) integer vs string semantics, so no wildcard/LIKE branch; (b) the meaningful filter predicates are different — range matters for an integer, wildcard does not.

```
                +-----------------------+
                |  Node                 |
                |-----------------------|
                |  Id           long    |
                |  TypeId       long    |
                |  Name         string  |
                |  Status       string  |
                |  ContentType  string  |
                |  Content      byte[]  |
                |  Embedding    float[] |
                |  Severity     int?    |   <-- new, indexed, [AllowPatch], nullable
                |  X, Y         double  |
                |  OwnerId      long    |
                |  Access       NodeAccess
                |  Created, LastUpdate  |
                +-----------------------+

      MCP wire (designed here, implemented on divoid-mcp follow-up):
        divoid_search  / divoid_list  / divoid_get_node    --> returns severity as integer | null
        divoid_patch_node                                  --> accepts replace/remove ops on /severity
        divoid_create_documentation / _task                --> optional `severity: int | null` parameter
```

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|---|---|---|
| `Node.Severity` (entity field) | Persisted numeric priority/importance signal, default null. | Per-type meaning; range validation; history. |
| `NodeDetails.Severity` (DTO field) | Wire-shape representation as nullable JSON number. | Backend mutation logic. |
| `NodeMapper` registration | `severity` field-mapping (entity↔DTO) + sort-key resolution + inclusion in `DefaultListFields`. | Filter predicate generation. |
| `NodeFilter.Severity` / `SeverityMin` / `SeverityMax` / `NoSeverity` | Inbound filter shape from the URL. | Predicate construction (`NodeService` does that). |
| `NodeService.GenerateFilter` | Predicate construction for severity reads (equality, range, no-severity). | DTO/entity mapping. |
| `DatabasePatchExtensions.Patch` (existing) | Resolves `/severity` → property write via `[AllowPatch]`. | New logic; severity reuses the existing `replace`/`remove` semantics. |
| MCP wrapper (divoid-mcp follow-up) | Surfaces `severity` on every read/write/create tool per §8. | Backend persistence. |

Deliberately **not** components: a `SeverityScale` table, a per-type severity validator, a `severity-history` audit log, an event emission on severity change, a `Severity` enum, a `severity-group` (high/medium/low) abstraction.

## 6. Interactions & Data Flow

### Reading severity
- `GET /api/nodes/{id}` returns `severity` as part of `NodeDetails` (nullable integer in JSON).
- `GET /api/nodes` includes `severity` in default list fields (clients need it for sort/filter UX without an extra round trip).
- Field is included by default (`NodeMapper.DefaultListFields` extends with `"severity"`).

### Filtering by severity
The URL surface supports three predicate shapes; they compose with each other and with all existing filters:

| Query | Predicate | Notes |
|---|---|---|
| `?severity=1` | `Severity = 1` | Single exact match. |
| `?severity=1,2,3` | `Severity IN (1, 2, 3)` | Multi-value IN (mirrors existing `id`/`type` array filters). |
| `?severityMin=2` | `Severity >= 2` | Inclusive lower bound. |
| `?severityMax=5` | `Severity <= 5` | Inclusive upper bound. |
| `?severityMin=2&severityMax=5` | `Severity >= 2 AND Severity <= 5` | Range. |
| `?noSeverity=true` | `Severity IS NULL` | Match rows with no severity set. Mirrors `NodeFilter.NoStatus`. |
| `?severity=3&noSeverity=true` | `Severity = 3 OR Severity IS NULL` | OR semantics — same shape as the `Status`/`NoStatus` combiner in `NodeService.cs:334-339`. |

`severity` and `severityMin`/`severityMax` may be supplied together; their predicates AND together. The wildcard branch present for `Name` and `Status` is intentionally **not** added — wildcards have no semantic meaning for an integer column, and `_` is reserved as a SQL `LIKE` wildcard so admitting it for integer parsing would be misleading.

### Sorting by severity
- `?sort=severity` resolves through `NodeMapper`'s registered field-mappings (the `severity` mapping registers the sort key implicitly via Ocelot's `FieldMapping`).
- `?sort=severity&descending=true` for highest-first ordering (the dominant query for tasks-as-priorities).
- Null severity ordering follows the database's natural null ordering (Postgres: nulls last on ASC, nulls first on DESC; SQLite: nulls first on ASC, nulls last on DESC). No `NULLS FIRST`/`NULLS LAST` clause is added — out of scope per Toni's "abstract integer" framing and YAGNI per §10 below.

### Changing severity
PATCH with the existing JSON-Patch shape:

| Op | Result |
|---|---|
| `[{"op":"replace","path":"/severity","value":5}]` | `Severity = 5` |
| `[{"op":"replace","path":"/severity","value":null}]` | `Severity = NULL` (numeric type accepts JSON `null` via Converter) |
| `[{"op":"remove","path":"/severity","value":1}]` | `Severity = Severity - 1` (the existing `remove` numeric-decrement semantic — see §8 invariants). |

**Decision: `replace` is the canonical clear/set operation.** Callers who want to clear severity use `replace /severity -> null` (the same shape Toni's MCP `divoid_patch_node` already produces, since JSON Patch values can be `null`). We do **not** legislate the existing `remove`-as-decrement semantic away — it remains the surface of every numeric `[AllowPatch]` field today (`add` is `+`, `remove` is `−`), and re-shaping `remove` to mean "set to null" would be a cross-cutting change that touches every numeric field, not severity-specific. The follow-up MCP doc and the API doc on DiVoid #8 document `replace null` as the canonical "clear" verb.

### Creating with severity
`POST /api/nodes` accepts `severity` in the body. If absent or `null`, the column defaults to NULL. No server-side default value is applied (deliberate: per Toni, absence is meaningful — "no severity set" ≠ "severity 0").

## 7. Data Model (Conceptual)

| attribute | semantics |
|---|---|
| `id`, `typeId`, `name`, `contentType`, `content`, `embedding`, `status`, `x`, `y`, `ownerId`, `access`, `created`, `lastUpdate` | (existing — unchanged) |
| `severity` | NEW. Nullable integer; default null. Indexed standalone and composite on `(typeId, severity)` to mirror the `(typeId, status)` precedent for the dominant access pattern "list nodes of type T sorted/filtered by severity." |

What is deliberately not modelled: a severity scale per type, a severity-group lookup table, a severity-history table, a severity-change event log.

## 8. Contracts & Interfaces (Abstract)

### Node read contract — extended
`NodeDetails` gains one optional field, `severity`, with type *nullable integer* on the wire. Present (and JSON `null` when unset) on GET-by-id; included by default in list responses; selectable via `?fields=`.

### Node list filter contract — extended
`NodeFilter` gains four fields:

| Field | URL key | Semantics |
|---|---|---|
| `int[] Severity` | `?severity=1,2,3` | IN match when present. |
| `int? SeverityMin` | `?severityMin=2` | Inclusive `>=`. |
| `int? SeverityMax` | `?severityMax=5` | Inclusive `<=`. |
| `bool NoSeverity` | `?noSeverity=true` | OR with `Severity` when both present; AND with the rest. |

The array deserialization (`1,2,3`, `[1,2,3]`, repeated `?severity=1&severity=2`) is already provided by `ArrayParameterBinderProvider` registered at index 0 of `ModelBinderProviders` (CLAUDE.md MVC §2). No new binder needed.

### Node patch contract — extended
`/severity` becomes a valid `[AllowPatch]` path. `replace` and `remove` reuse the existing numeric semantics in `DatabasePatchExtensions.cs` (replace → assignment, including `replace null` → NULL; remove → numeric decrement). `add` is mechanically supported and means numeric increment. `flag` / `unflag` are not meaningful for severity and will produce an SQL error if called; same posture as the rest of the numeric fields (we do not legislate this in code — see Risks §11 #5).

### Node sort contract — extended
`severity` is added to the set of resolvable sort keys (currently `id`, `type`, `name`, `status` per CLAUDE.md "Filtering, paging, patching"). Resolution is automatic once the `FieldMapping` exists.

### MCP wire contract (designed here; implementation: divoid-mcp follow-up task)
The MCP wrapper exposes severity uniformly across all relevant tools. The contract:

| Tool | Surface |
|---|---|
| `divoid_get_node` | Return object gains `severity: int \| null`. |
| `divoid_search` | Each result row gains `severity: int \| null` (echoed from `NodeDetails`). |
| `divoid_list` | Each row gains `severity: int \| null`. Optional `severity`, `severity_min`, `severity_max`, `no_severity` query parameters threading through to the backend's URL parameters of the same name. Optional `sort: "severity"` parameter accepted (alongside the existing `id`/`type`/`name`/`status`). |
| `divoid_patch_node` | Accepts patch ops against `/severity` (`replace`, `remove`); the wrapper's patch-shape validator must whitelist the path. |
| `divoid_create_task`, `divoid_create_documentation`, `divoid_create_session_log` | Each composite create tool gains an **optional** `severity: int \| null = None` parameter that, when supplied, is included in the POST body. Absent → backend default (NULL). |

The MCP follow-up task is filed against the divoid-mcp project Tasks group (per DiVoid #190 cross-project discipline + #447) **after** this backend PR opens, linked to #1605 and to this design.

### Invariants
1. `Severity` is single-valued (one nullable integer per node).
2. `Severity = NULL` is distinct from any integer value, including 0.
3. Mutating severity does not touch any other field; embedding regeneration is *not* triggered by severity changes (severity is metadata, not part of the embedded text content).
4. The default for new rows (POST without `severity`) is NULL.

## 9. Cross-Cutting Concerns

- **Indexing.** Two indexes:
  - Standalone `Index("severity")` — for cross-type queries (`?severity=5`).
  - Composite `Index("typeseverity")` shared by `TypeId` and `Severity` — for the dominant pattern `?type=task&sort=severity` and `?type=task&severity=3`. Mirrors the existing `nodestatus` composite on `(TypeId, Status)`.
- **Concurrency.** Single-column write. Last-writer-wins under concurrent PATCH; acceptable per the existing posture (DiVoid #24 §"Concurrency").
- **Idempotency.** `replace /severity -> N` twice is a no-op at the value level.
- **Observability.** No new logging emitted by this design. The existing `Patching node '{nodeId}'` log line in `NodeController.Patch` covers it.
- **Consistency.** No transactional coupling beyond the existing PATCH transaction.
- **Security / authorization.** Inherits the existing node visibility/write gates in `NodeAuthorization` and `NodeService.Patch`. Owner / write-public / admin all gate severity exactly like they gate `status`. No new policy.
- **Error handling.** Out-of-range / non-integer values get rejected by `Converter.Convert(patch.Value, typeof(int?), true)` in the patch path → `ArgumentException` → HTTP 400 via the existing `ArgumentExceptionHandler`. Filter binding for malformed `?severity=abc` produces HTTP 400 via the model-binder's existing error path. No new exception types.
- **Embedding.** Severity is not embedded text; the patch-name branch (`TouchesName`) in `NodeService.Patch` ignores `/severity` by construction.

## 10. Quality Attributes & Trade-offs

### Pre-Design Checklist (§5 of #1136), in order

**KISS / DRY / YAGNI**
- [x] No new type whose value-space mirrors an existing type. `int?` is a primitive; no mirror enum.
- [x] No new abstraction with one implementation. `NodeFilter` is extended in place; no new filter interface.
- [x] No design element justified by "we might need X later." (See "What does NOT go in" §2.)
- [x] No deprecation window, feature flag, compatibility shim. The PR is additive; existing rows get NULL on next boot.
- [x] No `block_size × site_count > ~15-20` inline-duplication decision (see DRY math below).

**DRY math.** The filter-block for `Status` (`NodeService.cs:321-347`, ~27 lines) and the new filter-block for `Severity` are structurally distinct: Status has wildcard/LIKE handling, Severity has range handling. There are exactly **2 sites total** — the `Status`+`NoStatus` block and the new `Severity`/`SeverityMin`/`SeverityMax`+`NoSeverity` block — and the shared substructure is just the "value-list AND/OR no-value" pattern (~6 lines). Math: `block_size × site_count = 6 × 2 = 12`, **below the 15-20 threshold**. The named-helper test ("BuildValueOrNullPredicate") is also borderline at best — the wildcard branch on the Status side means the helper would have to be parameterised by predicate-shape, which makes it harder to read than two open-coded blocks. **Decision: inline, not extract.** If a third value-or-null filter lands later (e.g., a numeric `priority` field with a `noPriority` flag), the math flips above threshold and the helper becomes the right call. We extract then, not now.

**Existing systems first**
- [x] No new service / table / DTO. `Severity` is a column on `Node`; all surfaces extend in place.
- [x] No new persisted data point that requires a 4-week-named decision (DiVoid #868). The data point IS the deliverable; the named consumer is Toni's verbatim statement that MCP clients need to work with it. There is no derived/audit column.

**Configurability**
- [x] No config knob. Severity has no environment-varying behaviour; no operator tunes it.

**Less is better**
- [x] Every design element passed the can-it-be-deleted check:
  - `Severity` column: required (the deliverable).
  - `NodeDetails.Severity`: required (wire surface).
  - `NodeMapper` field-mapping: required (entity↔DTO bridge).
  - `[AllowPatch]`: required (PATCH path requirement).
  - Standalone index: required for `?severity=N` without type.
  - Composite `(TypeId, Severity)` index: required for the dominant `?type=task&sort=severity` access pattern.
  - `NodeFilter.Severity[]`: required (exact match / IN).
  - `NodeFilter.SeverityMin/Max`: required (range — Toni names "sort and filter by severity range").
  - `NodeFilter.NoSeverity`: required for distinguishing "severity = NULL" from "any severity"; mirrors `NoStatus` precedent.
  - `DefaultListFields` inclusion: required so clients sort/filter without extra round-trips.

### Trade-offs explicitly accepted
- **Looser type safety than an enum.** Severity is `int?`, not `enum Priority`. A typo in PATCH (e.g., `999`) lands as 999. Acceptable: matches `Status`'s "free string with conventions outside the code" posture; per-type interpretation is application-scope per Toni's quote.
- **No NULLS FIRST/LAST ordering control.** Mixed-DB null-ordering differs (Postgres vs SQLite). Acceptable for an alpha system with one production target (Postgres); not in Toni's ask.
- **`remove` op stays a numeric decrement.** This is the existing semantic for every numeric `[AllowPatch]` field. Changing it to mean "set to NULL" would be a cross-cutting change. The canonical clear verb is `replace null`. This is documented in the MCP follow-up + the API doc (DiVoid #8).

### Alternatives considered and rejected

| Alternative | Why rejected |
|---|---|
| **Enum `Severity` with per-type values** | Violates the user's "for the system it's just an abstract number" framing. Forces a vocabulary the user explicitly does not want enforced. |
| **Separate `SeverityScale` table per type, FK from `Node`** | Per-type schema — violates the uniformity constraint inherited from #24. Form-1 data dump per #1136 §2 — no business logic ever reads the scale. |
| **`severity` as a graph link to a `SeverityValue` node** | Multi-step mutation (unlink + link) for a single-step concept. Cannot filter cleanly with the existing `linkedto` shape. Same anti-pattern Status rejected in #24. |
| **Reuse the bitfield `flag`/`unflag` patch ops** | Severity is single-valued, not a flag set. Bit semantics are nonsense here. |
| **`int` (non-nullable) with sentinel 0 = "no severity"** | Violates Toni's verbatim "no severity set" semantic. 0 must be a meaningful value (some clients may use 0 = lowest priority). |
| **Build the MCP wire impl in this PR** | Cross-project (divoid-mcp lives in a different repo + project per DiVoid #190 §"Cross-project work" + #447). Backend ships first; MCP follow-up references this design's §8 wire contract. |
| **Backfill existing rows to some default** | Out of scope per the task body. Existing rows getting NULL is the correct semantic — they had no severity, so they have no severity. |
| **Add a `SeverityHistory` audit column** | YAGNI per #1136 §3. No named operator, no named tuning event. |
| **Helper extraction of the value-or-null filter pattern** | DRY math: `6 × 2 = 12`, below threshold. Two sites differ in predicate shape (wildcard vs range). Re-evaluate when a third value-or-null filter lands. |

## 11. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `int?` deserializes from JSON `null` differently than expected (omitted vs explicit null collapse). | Low | Low | The PR #133 / DiVoid #1462 precedent established that `int?` / nullable-value-type DTO fields deserialize JSON `null` and omitted-field both as `null`; the entity `int?` column accepts NULL. The first-increment tests cover this. |
| Patch infra's `remove`-as-decrement surprises callers. | Medium | Low | Document `replace null` as canonical clear verb in API doc (DiVoid #8) + MCP wire contract §8. Not a code change; the existing semantic stays. |
| `add` / `remove` semantics race when used concurrently. | Low | Low | Acceptable; same posture as every other numeric `[AllowPatch]` field. Use `replace` for absolute-value sets. |
| The composite index `(TypeId, Severity)` adds write cost. | Low | Low | Two indexes per write is the same cost the Status field already pays for `(TypeId, Status)`. Acceptable. |
| `flag` / `unflag` ops on `/severity` produce confusing SQL errors. | Low | Low | The patch infra's bit operations require Int64 — Postgres will error on the type cast. Acceptable; no caller does this today. We do not legislate a code-level rejection (out of scope, applies to every numeric field, not severity-specific). |
| Wildcard parsing in filter binder accidentally accepts `?severity=%`. | Low | Low | The `int[]` array binder will reject `%` as a parse error → HTTP 400 via the existing binder error path. No code change. |
| `SchemaService.CreateOrUpdateSchema<Node>` fails to add a nullable column on existing SQLite dev DBs. | Low | Low | The Status field landed via the same mechanism; the dev DB `DiVoid.db3` will accept the new nullable column at next boot. Deleting the file forces a fresh schema rebuild. |

## 12. Migration / Rollout Strategy

- **Phase 1 (this PR).** Add `Severity` column + DTO field + mapper field-mapping + `[AllowPatch]` + composite + standalone indexes. Implementation increment per §14. First load-bearing tests added.
- **Phase 2 (sibling PR, John).** Filter surface (`NodeFilter.Severity[]`, `SeverityMin`, `SeverityMax`, `NoSeverity`) + `NodeService.GenerateFilter` predicate construction + sort registration via `NodeMapper.DefaultListFields` (sort registration is automatic via the field mapping in Phase 1; this phase only adds the *filter* predicates and the *filter* tests).
- **Phase 3 (sibling PR, on divoid-mcp project).** MCP wire impl per §8.
- **Phase 4 (frontend task, separate).** UI surfaces.

No data backfill, no feature flag, no migration step.

## 13. Open Questions

1. **`?sort=severity&descending=true` null ordering.** Postgres puts NULL last on ASC, first on DESC. The dominant query is "highest-priority first" which is DESC, so nulls appear at the top of the list. This may be surprising for the task-priority use case (humans expect their unprioritized tasks last, not first). Recommendation: leave as Postgres default for Phase 2; surface as a follow-up if users complain. Not blocking.
2. **MCP `divoid_list` URL parameter names — snake_case (`severity_min`) or camelCase (`severityMin`)?** Other MCP tools use snake_case (`include_content`, `recipient_node_id`); the backend URL accepts both via `ArrayParameterBinderProvider` casing tolerance. Recommendation: snake_case on the MCP side for tool-shape consistency; backend stays camelCase per existing `NodeFilter` precedent. Resolved in the MCP follow-up task.

## 14. Implementation Guidance for the Next Agent

This PR ships **Phase 1 only** (entity + schema + DTO + mapper + `[AllowPatch]` + first load-bearing tests). Phase 2 (filter + sort impl) is a sibling task John picks up next.

### Files in Phase 1 (this PR)

| File | Change |
|---|---|
| `Backend/Models/Nodes/Node.cs` | Add `[AllowPatch] [Index("severity")] [Index("typeseverity")] public int? Severity { get; set; }` with a 1-line `<summary>`. Also tag `TypeId` with the new composite index name `"typeseverity"`. |
| `Backend/Models/Nodes/NodeDetails.cs` | Add `public int? Severity { get; set; }` with a 1-line `<summary>`. |
| `Backend/Models/Nodes/NodeMapper.cs` | Add a `FieldMapping<NodeDetails, int?>("severity", DB.Property<Node>(n => n.Severity, "node"), (n, v) => n.Severity = v)` between `status` and `contentType`. Add `"severity"` to `DefaultListFields` after `"status"`. |
| `Backend/Services/Nodes/NodeService.cs` | Extend `CreateNode`'s INSERT column/value lists to include `n => n.Severity` / `node.Severity`. This is the bug #157 trap — `Status` was originally missed in the INSERT, causing POST-with-status to silently drop the value; the Severity insert avoids the same shape. |
| `Backend.tests/Tests/NodePatchHttpTests.cs` | Two load-bearing tests added: `Patch_ValidPath_Severity_Returns200` (replace 5 → fetched.Severity == 5) and `Patch_Severity_ToNull_ClearsValue` (replace null → fetched.Severity == null). |
| `Backend.tests/Tests/NodeCreateHttpTests.cs` (or a new `NodeSeverityHttpTests.cs` if it keeps the file at one-type-per-file) | Two load-bearing tests: `Create_SeverityOmitted_DefaultsToNull` (POST without severity → GET returns Severity null) and `Create_SeverityExplicit_PreservesValue` (POST with severity=5 → GET returns 5). |

Test-file decision: keep tests in **existing** files (`NodePatchHttpTests.cs` for patch tests, `NodeCreateHttpTests.cs` for create tests) — they're the canonical homes for the surface, matches the Status precedent (`Patch_ValidPath_Status_Returns200` lives in `NodePatchHttpTests.cs` line 69). No new test file needed. This avoids the §6.10 file-naming dance.

### Files NOT in Phase 1 (Phase 2 / sibling PR)

| File | Reserved for Phase 2 |
|---|---|
| `Backend/Models/Nodes/NodeFilter.cs` | `Severity[]`, `SeverityMin`, `SeverityMax`, `NoSeverity` fields. |
| `Backend/Services/Nodes/NodeService.cs` | `GenerateFilter` predicate branch for severity + range + nostatus-style OR combine. `GenerateHopFilter` also gets a `"severity"` case for path-query hops. |
| `Backend.tests/Tests/NodeServiceTests.cs` (or new `NodeSeverityFilterTests.cs`) | Filter happy path, IN, range, NoSeverity, composition with type/linkedto, sort by severity ASC/DESC, sort = severity with type filter. |

The split aligns with the "first cohesive increment" rule (#1220 §5): the entity + schema + DTO + mapper + `[AllowPatch]` is the smallest set that ships an end-to-end working `severity` (POST with severity, GET it back, PATCH it). Filter + sort + Hop predicate is the next coherent increment.

### Order
1. `Node.cs` — entity column + indexes.
2. `NodeDetails.cs` — DTO field.
3. `NodeMapper.cs` — field-mapping + DefaultListFields.
4. Build + test run (existing tests must stay green).
5. Add load-bearing tests in `NodePatchHttpTests.cs` and `NodeCreateHttpTests.cs`.
6. Test run; verify the 4 new tests pass and fail on revert.
7. Run §6.10 grep gates (body comments = 0; XML summary count == new-decl count).
8. Commit, push, open PR titled `feat(node): add Severity field (DiVoid #1605)`.

### Follow-up tasks the orchestrator should file (per §2 cross-project discipline)
- **DiVoid project, Tasks group:** "Severity field on Node — Phase 2: filter + sort surface" (the Phase 2 work above).
- **divoid-mcp project, Tasks group:** "divoid-mcp: add severity to wire contract per DiVoid #1605 §8."
- **Frontend task** (separate, when Phase 1+2 lands): "Customer-portal: severity surface on node edit + list filters."
