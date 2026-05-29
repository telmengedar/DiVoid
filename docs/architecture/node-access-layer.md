# Architectural Document: Per-Node Access Layer

**Task:** DiVoid #1370
**Status:** Design ready for implementation
**Branch:** `feature/node-access-layer-1370` (design + implementation co-located per DiVoid #1165)
**Code Contracts:** DiVoid #114 (load-bearing)
**Design Contracts:** DiVoid #1136 (load-bearing, §1 KISS/DRY/YAGNI especially)
**Precedent:** DiVoid #24 (Status field design, the canonical "add column + filter + service predicate" pattern), DiVoid #26 (Status impl), DiVoid #91 (Ocelot SchemaService handles column adds in place), DiVoid #297 (`[DefaultValue]` required for non-null value-type columns), DiVoid #77 (existing User.Permission auth landscape).

---

## 1. Problem Statement

DiVoid nodes today are universally world-visible and world-mutable. There is no concept of "this node belongs to me" or "this node is private." When the upcoming web frontend lands, every authenticated user will see every node — including draft documents, in-flight private sessions, half-written experiments — and any `write` caller can mutate any node.

The user has asked for a simple per-node access layer with three fixed pieces:

1. An `OwnerId` column naming the creator.
2. An `Access` `[Flags]` enum naming what non-owner non-admin callers may do (`None=0`, `Read=1`, `Write=2`).
3. Owner-always-everything plus admin-always-everything overrides.

Verbatim user ask (quoted to anchor every design decision below):

> *"i want a simple access layer in divoid: all nodes have an ownerid filled with the userid who created the node. if it works existing nodes may get 0 as ownerid, otherwise give it ownerid 1 (should be me, the admin). nodes have a flags indicator what access to allow (owner may always do everything, admin may also always to everything to every node) - the indicator shall be a c# integer enum (to have flat fast db access when listing) - 0 = nothing allowed (basically a private node), 1 = read (all may see and read the node and content), 2 = write (all may change node metadata and content) -> thus 3 is full access of everyone to this node currently as long as we have no more flags. write also implies delete and everything."*

Success looks like:
- Every node has an owner (existing rows backfill to sentinel `0`).
- A non-owner non-admin sees a node in list / get / content GETs only if `Access & Read` is set.
- A non-owner non-admin can PATCH metadata, POST new content, embed-op, or DELETE a node only if `Access & Write` is set.
- Owner and admin override unconditionally.
- The listing predicate ANDs into the existing `NodeService.GenerateFilter` shape without breaking paging, sort, semantic search, or path-query.

## 2. Scope & Non-Scope

**In scope.**
- `Node.OwnerId` column (non-null `long`, defaults to `0`, indexed).
- `Node.Access` column (non-null `int`-stored `[Flags]` enum, defaults to `0`, indexed).
- New `[Flags]` enum `NodeAccess { None=0, Read=1, Write=2 }` in its own file.
- Owner identity capture at `POST /api/nodes` — read `divoid.user_id` from the principal, write to the new row's `OwnerId`.
- Authorization checks at GET (single), GET content, GET list (filter), PATCH, POST content, DELETE.
- Listing visibility predicate composed into the existing `GenerateFilter`.
- `[AllowPatch]` on `OwnerId` and `Access`, with per-property authorization rules.
- Backfill posture for existing rows (sentinel `0`, admin override means nothing breaks).
- Tests covering owner/admin/stranger paths at every endpoint.

**Out of scope (call out explicitly to lock the scope — §6.4 audit hook).**
- Group-based access, ACL entries beyond owner + flat flags. YAGNI.
- Audit log of who-changed-Access-when. YAGNI per Design Contracts §6 ("audit columns without a named decision").
- "Created/UpdatedBy" — not asked. (Audit-timestamps work is the sibling task #1371; it lives in its own PR.)
- Changing `User.Permission` shape or semantics.
- A new symbol for `3 = Read | Write` (e.g. `Full`, `ReadWrite`). The user said *"thus 3 is full access"* descriptively; that is the natural composition of the two flags, not a request for a named symbol. A named symbol would be Design Contracts §6 anti-pattern "mirror enum with different default" turned inwards — the same value rephrased.
- Nullable `OwnerId`. Existing rows backfill to the sentinel `0`, not "unknown". User explicitly said *"may get 0 as ownerid"*.
- Keycloak realm-role layering. The open question at DiVoid #197 is about the user-level layer (per-user permissions); this design is about the per-node layer. The two are orthogonal. Boundary is documented in §10.
- UI surface for setting `Access`. The PATCH endpoint supports it; the frontend wiring is a separate task.
- Sharing-link or token-based read access ("anyone with the link"). Not asked.
- Transition rules / "owner cannot reduce Access while content is referenced by a published node" — speculative.

## 3. Assumptions & Constraints

**Hard constraints (verified from the codebase).**

- Every `Node` row has the same shape. No per-type columns. (CLAUDE.md "Big picture".)
- Schema is created at startup by `Backend/Init/DatabaseModelService.cs` calling `SchemaService.CreateOrUpdateSchema<Node>` — adding columns is a property-edit + boot, no migration file (CLAUDE.md "Data layer"; DiVoid #91).
- Non-null value-type columns require `[DefaultValue(...)]` or `INSERT` will fail on SQLite (DiVoid #297; `Node.X`/`Y` are the live precedent).
- Only `[AllowPatch]`-marked properties are patchable; the property tag matters (CLAUDE.md "Filtering, paging, patching").
- Wildcard handling in string-array filters switches IN → OR-chained LIKE — applies to `Name` and `Status` today; not relevant for the numeric `Access` filter and not exposed for `OwnerId` (see §5).
- Both auth schemes (ApiKey + JwtBearer/Keycloak) emit the `divoid.user_id` claim — verified at `Backend/Auth/ApiKeyAuthenticationHandler.cs:76` and `Backend/Auth/KeycloakClaimsTransformation.cs:96`. The accessor is `ClaimsExtensions.GetDivoidUserId(this ClaimsPrincipal)` (`Backend/Auth/ClaimsExtensions.cs:41`).
- Admin is encoded as a `permission` claim with value `"admin"`. `PermissionAuthorizationHandler` (`Backend/Auth/PermissionAuthorizationHandler.cs`) already implements `admin ⇒ write ⇒ read` for policy gates. Direct check: `User.HasClaim("permission", "admin")`.
- When `Auth:Enabled=false` (the dev profile and the existing test harness — `Backend/Startup.cs:222-228`), all policy gates auto-succeed and there is no authenticated principal. The design must remain correct under that posture too — see §9.

**Working assumptions.**

- The user-id space is small (single-digit users today; tens at most in the foreseeable future). Indexing `OwnerId` is cheap and the filter `OwnerId = <caller>` is selective.
- All existing nodes belong conceptually to Toni (admin). The sentinel `0` does not match Toni's actual `User.Id` (whatever that is — probably `1`), but admin override means Toni still sees and edits everything regardless. The user explicitly accepted this: *"may get 0 as ownerid, otherwise give it ownerid 1 (should be me, the admin)"*. We pick `0` because it is the natural `[DefaultValue]`.
- `Access` defaults to `3 = Read | Write` for new nodes (revised after PR #130 review — see §10). This preserves the pre-access-layer posture where all nodes were world-visible and world-mutable. Callers who want private nodes must set `Access = None` explicitly on create or via PATCH.

## 4. Architectural Overview

Two columns on `Node`, one enum, one visibility predicate composed into the existing filter, one per-operation authorization helper called from the controller (or from the service, see §6). That is the entire change.

```
                                   +---------------------------------------+
                                   |  Node (entity)                        |
                                   |---------------------------------------|
                                   |  Id           long                    |
                                   |  TypeId       long                    |
                                   |  Name         string                  |
                                   |  ContentType  string                  |
                                   |  Content      byte[]                  |
                                   |  Embedding    float[]                 |
                                   |  Status       string                  |
                                   |  X, Y         double                  |
                                   |  OwnerId      long      <-- NEW       |
                                   |  Access       NodeAccess <-- NEW      |
                                   +---------------------------------------+

  NodeAccess (new [Flags] enum):
      None  = 0      private — only owner + admin
      Read  = 1      everyone may GET / GET content / appear in lists
      Write = 2      everyone may PATCH / POST content / embed / DELETE
                     (3 = Read | Write is the natural composition;
                      no named symbol per §2 out-of-scope.)

  Authorization check (one helper, used by every node-touching path):

      principal -> { callerId, isAdmin }

      operation = read | write
      visible(node) iff:
            isAdmin
         OR node.OwnerId == callerId
         OR (operation == read  AND node.Access has Read)
         OR (operation == write AND node.Access has Write)

  List-mode predicate (composed into existing GenerateFilter):

      isAdmin                              -> no extra predicate
      otherwise                            -> AND (OwnerId == callerId
                                                   OR Access & 1 != 0)
```

The check itself is fewer than two dozen lines; it appears in one helper called from every entry point that touches a single node (GET, GET content, PATCH, POST content, DELETE), and as a list-predicate in `GenerateFilter`. Path-query mode reuses the same predicate at the terminal hop (because it routes through `GenerateFilter` already, per `NodeService.ComposeHops:454`).

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|-|-|-|
| `Node.OwnerId` (entity column) | Who created the node. Set once on insert; mutable only by owner or admin via PATCH. | The fact that an owner is "valid" (no FK constraint). The convention that admin override holds even when `OwnerId == 0`. |
| `Node.Access` (entity column) | What non-owner non-admin callers may do with this node. Defaults to `None` (private). Mutable only by owner or admin via PATCH. | Transition rules, history, group/role layering. |
| `NodeAccess` (new `[Flags]` enum, single file) | The bit semantics: `None=0`, `Read=1`, `Write=2`. Composition `3 = Read | Write` is implicit. | Per-type defaults; vocabulary discovery in the graph (not asked). |
| `NodeAuthorization` (new internal helper, see §8) | `BuildVisibilityPredicate(callerId, isAdmin, write)` — the single SQL predicate builder used by every node-touching op (list, get, content, patch, delete, link/unlink). Returns null for admin (no extra predicate). `BuildOwnerPredicate(callerId, isAdmin)` — owner-or-admin variant used for the `/access` PATCH gate. No `IsAuthorized` boolean; single-node ops compose the predicate into the operation's WHERE directly (revised after PR #130 — see §10). | Identity extraction (`ClaimsExtensions` already does that). Policy registration (`Startup.cs` already does that). |
| `NodeService` (existing) | Calling the helper at every entry point. Composing the list predicate into `GenerateFilter`. Setting `OwnerId` on insert from the authenticated caller. Enforcing per-property PATCH authorization on `OwnerId` and `Access`. | Anything in §2 out-of-scope. |
| `NodeController` (existing) | Forwarding the `ClaimsPrincipal` (or pre-extracted `callerId`+`isAdmin` tuple) into the service. (Auth-policy `[Authorize(Policy="...")]` attributes are unchanged.) | The actual per-node decision (lives in the service so it can compose into the SQL predicate). |

**Single-responsibility check.** No component owns more than one of the four concerns: data shape, semantics enum, decision logic, plumbing. Auth identity remains in `ClaimsExtensions` and `PermissionAuthorizationHandler`; this design adds **nothing** to those files.

## 6. Interactions & Data Flow

### Where the decision is made

Two layers, one decision:

1. **Policy gate (unchanged):** `[Authorize(Policy="read")]` / `[Authorize(Policy="write")]` on the controller endpoint checks that the caller has *any* read or write permission at all. Anonymous callers (when auth is on) are rejected at this layer with `401`. Callers without the right policy are rejected with `403`. This layer is coarse — "may this caller, in principle, do reads / writes".
2. **Per-node gate (new):** Inside the service, every entry point that loads or mutates a specific node consults the new helper. The helper is the *fine-grained* check that combines owner + admin + flags. A caller may pass the policy gate yet fail the per-node gate — that produces `404` (not `403`, see §9 "leak avoidance").

Placing the per-node decision in the service (not the controller) is deliberate:
- The same logic must compile into the list-mode SQL predicate. The service owns SQL; the controller does not.
- The single-node and list-mode forms share a truth table — same helper, two surfaces.
- Per Code Contracts §7 ("controllers carry minimal business logic — delegate to services"), authorization decisions that depend on row state belong below the controller.

### Identity & flags read

`NodeController` reads `User` (the `ClaimsPrincipal` property of `ControllerBase`) and extracts a `(callerId, isAdmin)` tuple. When `Auth:Enabled=false` the principal carries no claims; the controller treats this as "no caller identity, treat as admin-equivalent" (see §9 trade-off). The tuple is passed to the service as method parameters — explicit and testable, no service-locating `IHttpContextAccessor`.

### Sequence: `GET /api/nodes/{id}`

```
caller                  NodeController                NodeService                DB
  | GET /api/nodes/13       |                              |                      |
  |----------------------->|                              |                      |
  |                         | [Authorize(Policy="read")]   |                      |
  |                         | -> passes (caller has read)  |                      |
  |                         |                              |                      |
  |                         | callerId, isAdmin            |                      |
  |                         | = ResolveCaller(User)        |                      |
  |                         |                              |                      |
  |                         | GetNodeById(13, callerId, isAdmin)                  |
  |                         |----------------------------->|                      |
  |                         |                              | SELECT ... WHERE Id=13|
  |                         |                              |--------------------->|
  |                         |                              |<---------------------|
  |                         |                              | IsAuthorized(node,   |
  |                         |                              |   callerId, isAdmin, |
  |                         |                              |   Read)              |
  |                         |                              |   -> false           |
  |                         |                              | throw NotFoundException<Node>(13)
  |                         |<-----------------------------|                      |
  |  HTTP 404 (canonical    |                              |                      |
  |  not-found envelope)   |                              |                      |
  |<------------------------|                              |                      |
```

Note the `404`, not `403`. Surfacing "exists but you can't see it" leaks node existence to non-readers. Same posture as `GET /api/nodes/{id}/user` (controller comment at `NodeController.cs:117-119`).

### Sequence: list

For `GET /api/nodes`, the per-node check becomes a predicate ANDed into `GenerateFilter`:

```
caller                  NodeController                NodeService                DB
  | GET /api/nodes          |                              |                      |
  |----------------------->|                              |                      |
  |                         | callerId, isAdmin            |                      |
  |                         | = ResolveCaller(User)        |                      |
  |                         | ListPaged(filter,            |                      |
  |                         |          callerId, isAdmin)  |                      |
  |                         |----------------------------->|                      |
  |                         |                              | GenerateFilter +     |
  |                         |                              | visibility predicate |
  |                         |                              |--------------------->|
  |                         |                              |    SELECT ...        |
  |                         |                              |    WHERE (existing)  |
  |                         |                              |      AND (isAdmin    |
  |                         |                              |        OR OwnerId=cid|
  |                         |                              |        OR Access&1<>0)|
  |                         |                              |<---------------------|
  |  HTTP 200 page          |                              |                      |
  |<------------------------|                              |                      |
```

The page envelope is unchanged. Total count reflects only visible rows (because the count uses the same predicate via `CountOver()`).

### Sequence: PATCH owner-only field (e.g. `/access`, `/ownerId`)

A non-owner non-admin caller patches `/access` on a node whose `Access` includes `Write`. They pass the policy gate (they have write), they pass the per-node gate (the node is `Write`-public), but they fail the per-property gate — `OwnerId` and `Access` may only be patched by owner or admin. See §8 "PATCH authorization rules" for the full table.

### Sequence: `POST /api/nodes` (create)

```
caller                  NodeController                NodeService                DB
  | POST /api/nodes         |                              |                      |
  | { type:"task", ... }    |                              |                      |
  |----------------------->|                              |                      |
  |                         | [Authorize(Policy="write")]  |                      |
  |                         | callerId =                   |                      |
  |                         |   ResolveCallerId(User)      |                      |
  |                         | CreateNode(node, callerId)   |                      |
  |                         |----------------------------->|                      |
  |                         |                              | INSERT INTO node     |
  |                         |                              |   (TypeId, Name,     |
  |                         |                              |    Status, X, Y,     |
  |                         |                              |    OwnerId, Access)  |
  |                         |                              |   VALUES (...,       |
  |                         |                              |    callerId, 0)      |
  |                         |                              |--------------------->|
```

Default `Access = 0` (private). Owner is the authenticated caller — incoming `OwnerId` from the body is **ignored** (admins use PATCH to transfer ownership).

If the caller specifies `Access` in the create body, honour it — that lets a caller create a public node in one round-trip. Anything in `OwnerId` from the body is ignored.

## 7. Data Model (Conceptual)

### Node — extended

| attribute | semantics |
|-|-|
| `id` | (existing) identity |
| `type`, `name`, `contentType`, `content`, `embedding`, `status`, `x`, `y` | (existing) unchanged |
| `ownerId` | NEW. Non-null `long`. The DiVoid user-id of the creator, captured on insert from the authenticated caller. Defaults to `0` (sentinel — exists only on the rows that pre-date this feature). Indexed so `OwnerId = <callerId>` is a cheap point-membership check. |
| `access` | NEW. Non-null `int`-stored `[Flags]` enum (`NodeAccess`). Defaults to `3` (`Read | Write`, fully public). Indexed so the list visibility predicate `Access & 1 != 0` is index-eligible. Revised after PR #130 — see §10. |

### NodeAccess (new enum, separate file per Code Contracts §1)

| value | name | semantics |
|-|-|-|
| `0` | `None` | Private — only owner and admin may read or write. |
| `1` | `Read` | Everyone authenticated for `read` may GET, GET content, see in lists. |
| `2` | `Write` | Everyone authenticated for `write` may PATCH (subject to per-property rules), POST content, embed-op, DELETE. |
| `3` | `Read | Write` (implicit, no named symbol) | Both. |

The values are explicitly powers of two so flag composition is natural. The enum carries `[Flags]`.

### What is intentionally not modelled

- `OwnerId` has **no FK** to `User`. Sentinel `0` does not point at a real row; introducing an FK would force a backfill choice between `1` (admin) and a synthetic "system" row. The user said *"if it works existing nodes may get 0 as ownerid"* — that explicitly authorizes the sentinel-without-FK shape. Lookup of the owner's display name is a `JOIN` on `OwnerId = User.Id` performed only when a UI consumer needs it (out of scope for this PR).
- No history of past `Access` values or past owners. YAGNI per Design Contracts §1.
- No per-type default `Access`. The user said `None=0` is the default; that holds for every type. If a future task wants different defaults per type, that is an additive change.

## 8. Contracts & Interfaces (Abstract)

This section names the contracts in prose. No code.

### Node read contract — extended

`NodeDetails` gains two fields:
- `ownerId` (`long`) — always present.
- `access` (`NodeAccess` serialized as the enum's string name per the global `JsonStringEnumConverter` registration in `Startup.cs`).

Both are part of `DefaultListFields` (so they appear by default in list responses; existing test fixtures must accept the new fields, but Pooshit.Json's "unknown fields tolerated" posture means external consumers do not break).

### Node list filter contract — extended (visibility predicate)

The list endpoint's behaviour gains an *implicit* visibility filter, applied unconditionally:

- If the caller is admin (or auth is disabled), no additional predicate.
- Otherwise: `(OwnerId = <callerId> OR (Access & 1) != 0)` ANDed into the existing predicate built by `GenerateFilter`.

This visibility predicate composes with:
- `id`, `type`, `name`, `status`, `bounds`, `linkedto` filters (AND).
- Semantic search (`query`, `minSimilarity`) — invisible rows are filtered out before similarity ranking.
- Path-query mode (`?path=...`) — the predicate ANDs into the terminal hop only, mirroring `ComposeHops` at `NodeService.cs:454`. Intermediate hops are **not** visibility-filtered: the design follows existing behaviour where intermediate hops can reference any node by id/type/name. A future refinement may tighten this, but that is YAGNI today (intermediate hops are not surfaced in the response; only the terminal-hop ids are).

**No new filter query parameter is added.** The visibility check is implicit per request, derived from the principal. There is no `?ownerId=` or `?access=` query parameter (YAGNI; admins who need to inspect ownership use a path query against the graph).

### Node patch contract — extended (per-property authorization)

`OwnerId` and `Access` are `[AllowPatch]`. The infrastructure-level patch is permitted; the **service-level per-property check** rejects patches by callers who are not owner/admin on those two paths.

| patch path | who may patch | semantics |
|-|-|-|
| `/name` | owner OR admin OR (`Access` has `Write`) | (existing — gains the per-node gate) |
| `/status` | owner OR admin OR (`Access` has `Write`) | (existing — gains the per-node gate) |
| `/x`, `/y` | owner OR admin OR (`Access` has `Write`) | (existing — gains the per-node gate) |
| `/access` | owner OR admin ONLY | Changing publicity is a privileged op. A `Write`-public node does NOT let strangers flip `Access`. Failure: 404 (revised after PR #130 — see §10). |
| `/ownerId` | admin ONLY | Ownership transfer is admin-only. Non-admin (including owner): 404 immediately, no DB query (admin is a request-scoped constant). |

The service classifies the patch array once before issuing any UPDATE: if any op touches `/ownerId`, the admin gate fires first (non-admin → 404 with no DB query); if any op touches `/access`, the owner-or-admin WHERE predicate applies; otherwise the write-visibility predicate applies. One UPDATE, one WHERE variant per classification. 0 affected → 404.

All failures across all three classifications return 404 (revised after PR #130 — see §10). There is no 403 surface in the per-node access layer.

### Single-node read / GET-content contract — extended

`GET /api/nodes/{id}` and `GET /api/nodes/{id}/content`: the read visibility predicate is composed into the mapper fetch / content SELECT WHERE clause. If no row matches (node absent OR caller cannot read it), `NotFoundException<Node>(id)` — `404`, not `403`. Existence is not leaked. One query, not two. (Revised after PR #130 — see §10.)

### Single-node write contract — extended

`DELETE /api/nodes/{id}`, `POST /api/nodes/{id}/content`, the deprecated `embed` op via PATCH: the access predicate is composed directly into the operation's WHERE clause. A 0-affected-rows result maps to `NotFoundException<Node>` (404) — same leak-avoidance reasoning, one fewer round-trip than the prior load-then-check pattern (revised after PR #130 — see §10).

For `DELETE`: `DELETE FROM node WHERE id=? AND (isAdmin OR OwnerId=callerId OR (Access & Write)!=0)`. 0 affected → 404.

`POST /api/nodes/{id}/content`: `UPDATE ... SET content=? WHERE id=? AND visibility(write)`. 0 affected → 404; then embedding pipeline proceeds within the same transaction.

### Linking contract — relationship to per-node access

`POST /api/nodes/{id}/links` and `DELETE /api/nodes/{id}/links/{otherId}` operate on **two** nodes. The user did not say anything about link operations. Default to: `Write` is required on the source endpoint (the URL-path node). The target endpoint is treated as a referent, not a mutated entity — the convention is the same as "I can mention any public node from my own node," which is already how the graph behaves.

This is the only place where the design extrapolates from the user's words. The alternative — requiring `Write` on both endpoints — would prevent legitimate "link my private note to a public topic" workflows and would break every existing test fixture. If Toni wants stricter link semantics, that is a follow-up.

### Node create contract — extended

`POST /api/nodes`:
- `OwnerId` from the request body is **ignored**; the service sets it from the authenticated caller's `divoid.user_id`.
- `Access` from the request body is honoured (caller may create a node already-public in one round-trip). Defaults to `None`.
- When `Auth:Enabled=false` and the caller has no identity, `OwnerId = 0` (the sentinel). The dev posture is "no auth = everyone is admin"; nodes still get an owner field, but it points at the sentinel.

### Invariants

- A node has exactly one owner at any moment. Ownership transfer is atomic (single PATCH).
- A node's `Access` flags are settable only via PATCH (no separate endpoint).
- Backfill rows have `OwnerId = 0` AND `Access = 0`. The admin override means Toni still has full access; non-admins see nothing they did not create.

## 9. Cross-Cutting Concerns

**Auth on/off posture.** When `Auth:Enabled=false` (the dev profile and most tests), there is no authenticated principal. The design treats this as "admin-equivalent" — visibility predicate is no-op, all per-node checks succeed, `OwnerId` of new rows is `0` (no identity to capture). This keeps the existing test suite green without explosive rewiring. The trade-off: tests that need to verify the per-node gate must enable auth (or inject a fake principal via the test factory). The `Backend.tests` infrastructure already supports this (the test factory disables `IHostedService` and can register a mock auth handler — see existing `NodeUserLookupHttpTests.cs` and `Backend.tests/TestSetup.cs`).

**Leak avoidance.** Per-node gate failures map to `404`, not `403`. Existence of a private node must not be observable to non-readers. Same posture as the existing `GET /api/nodes/{id}/user` endpoint.

**Indexing.** Two new single-column indexes (`OwnerId`, `Access`). The dominant non-admin list-mode query becomes:
```
WHERE (existing predicate) AND (OwnerId = <callerId> OR (Access & 1) != 0)
```
The DB can use either index — in practice it uses `OwnerId` for selective owners (rare admin / common author) and the `Access` index for public-content queries. A composite index `(OwnerId, Access)` is *not* added today; single-column indexes are sufficient for the OR predicate. If the query plan proves bad in practice (post-deploy observation), the composite can be added — that is one boot-cycle, not a migration.

**Concurrency.** No multi-row coordination. `Access` and `OwnerId` are single-column writes via PATCH. Last-writer-wins on simultaneous PATCHes is acceptable (matches `Status`'s posture per DiVoid #24 §"Concurrency").

**Idempotency.** `PATCH /access` with the same value is a no-op at the row level; the affected-row count check returns 1 (the row exists; no value change is fine).

**Observability.** No specific logging required by this design. The existing PATCH controller already emits `LogInformation("Patching node '{nodeId}'", nodeId)`. If `/access` and `/ownerId` patches deserve a higher-signal log line later (security audit), that is a small additive change — not in scope.

**Error handling.** All per-node gate failures — including PATCH per-property gates — map to `404` via `NotFoundException<Node>(id)`. The prior distinction (per-property failures on `/access` and `/ownerId` gave `403`) is dropped in favour of 404 across the board (revised after PR #130 — see §10). No `AuthorizationFailedException` is thrown. No new exception types are introduced.

**Consistency model.** A `PATCH` array containing a mix of allowed and disallowed paths fails atomically — the whole transaction rolls back. Same shape as the existing PATCH transaction at `NodeService.cs:746-761`.

**Caching.** None today. If a future caching layer materializes, visibility-filtered list responses cache per-caller (or per-permission-class), not globally. Out of scope.

## 10. Quality Attributes & Trade-offs

**Scalability.** O(1) per single-node check (point lookup + in-process compare). O(log n) per list query given the index. No new joins on the hot path. Visibility predicate is one indexed predicate ANDed in.

**Performance.** Two `long`/`int` columns per row (~12 bytes); two indexes. The list-mode predicate adds one `OR` to the WHERE clause. At the 30-node scale of today and the 10K-node scale of next year, both are noise.

**Maintainability.** One enum file, one helper class, mechanical edits to `NodeService` entry points, one column-add in `Node.cs`. The PATCH per-property check is the one piece that takes thought; it is centralized in the helper (single source of truth — Code Contracts §0 KISS).

**Evolvability.** New flags slot into `NodeAccess` (e.g. `Delete = 4` if we ever decide delete should be separable). Owner-transfer rules can tighten (e.g. require a co-signing admin) without changing the column shape. Group-based access can layer on top — a future `NodeAccessList` table joined into the predicate — without disturbing the column-based fast path.

### Trade-offs explicitly accepted

| Trade-off | Why we accept it |
|-|-|
| Sentinel `OwnerId = 0` instead of nullable | User explicitly authorized it; nullable adds a third state ("unknown") that has no semantic meaning beyond "pre-feature row". Admin override means the sentinel is never user-visible as a problem. Per Design Contracts §6 ("audit columns without a 4-week-named-decision"), the nullable case carries no near-term decision. |
| `/ownerId` patch is admin-only (not owner-or-admin) | Owners cannot reassign their own nodes to evade scrutiny. Admins reassign when users leave. The user did not name a rule here; we pick the safer side. If owners need to "donate" a node, they can ask an admin or set `Access` to public. |
| Linking does not require `Write` on the target | Existing graph posture is "anyone can reference any node by id"; tightening here would break test fixtures and legitimate referencing. The user did not raise it. |
| Auth-disabled mode treats everyone as admin-equivalent | Keeps the existing test suite + dev profile working. Production is auth-enabled (see `Startup.cs:115`). |
| Per-node gate maps to `404` on failure | Existence-leak avoidance. Matches `GET /user` posture. The trade-off: an admin debugging "why does the user see no nodes" cannot distinguish "node doesn't exist" from "node not visible" from the API surface alone (they must consult logs or query as admin). |
| No `ReadWrite` / `Full` named symbol for `3` | User's "thus 3 is full access" was descriptive. Adding a symbol invites Design Contracts §6 anti-pattern ("mirror enum with different default"). |
| No FK constraint on `OwnerId` | Allows the sentinel `0` without a "system" user row. Lookup of the owner display name is a join performed only when consumers need it. |

### Alternatives considered and rejected

| Alternative | Why rejected |
|-|-|
| **Per-role ACL table** (`NodeAccessRole`, mapping `nodeId → roleId → {read,write}`) | The user explicitly said *"a simple access layer"* and named the enum shape. ACL is the over-engineered version of this. YAGNI. |
| **Visibility as a graph link** (`node ─linked-as-readable→ user`) | Filtering requires triple joins through the link table; mutation requires multi-step link/unlink. The column form fits the existing `GenerateFilter` shape and benefits from the index. Same reasoning as DiVoid #24 §"Status as a link" rejection. |
| **One `Visibility` enum with values `Private`, `Public`, `WorldWritable`** | Loses the flag semantics. The user explicitly named the `[Flags]` shape (*"flags indicator"*, *"3 is full access ... as long as we have no more flags"*). |
| **Nullable `OwnerId`** | Adds an "unknown" state. User explicitly said sentinel-or-admin, not unknown. |
| **Owner cannot patch `/ownerId`** | Considered; admin-only is the tighter version. Owners-can-donate would let a departing user assign nodes to a colleague before leaving, but it also lets a user evade an audit. The admin-only rule scales better. |
| **Skip the design and brief John directly** | Per DiVoid #1220 ("Skip-the-architect rule") this would apply to features under ~50 LoC mirroring a precedent. This feature touches authorization (genuinely-new architectural decision: per-node vs per-user, predicate-in-WHERE composition with two existing modes, per-property PATCH gates) — beyond the threshold. |

### Relation to DiVoid #197 (Keycloak realm roles vs DiVoid permissions)

**This task is orthogonal to #197.** #197 is about which authority owns the *user-level* permission set — Keycloak realm roles or `User.Permissions` in the DB. This design uses the existing `permission` claim regardless of which authority populates it; whichever way #197 resolves, the per-node layer is unaffected. The boundary is: this design touches only `Node.OwnerId`, `Node.Access`, and the visibility predicate — not the `User` table, not the claim emission, not `PermissionAuthorizationHandler`.

## 11. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|-|-|-|-|
| `[DefaultValue(0)]` is omitted on either new column → `INSERT` fails on SQLite for existing rows | Medium (the trap is repeat-observed — DiVoid #297) | Service refuses to start cleanly | Both new columns have `[DefaultValue(0)]`. Test suite includes a round-trip insert against the SQLite test fixture. The §13 tests catch this on the first run. |
| Schema reconciliation fails when adding two non-null columns to a populated table | Low (DiVoid #91 says Ocelot handles column adds in place when `[DefaultValue]` is set) | Service refuses to start | `[DefaultValue]` is the mitigation; a backup of the dev DB (`DiVoid.db3`) is the fallback per DiVoid #91. |
| List queries get materially slower under high cardinality | Very low at current scale (30 nodes) | Low | Single-column indexes on `OwnerId` and `Access`. If a Postgres EXPLAIN ever shows a sequential scan, add the composite — one boot. |
| `404`-leak posture confuses operators ("the row exists, why doesn't it show?") | Medium | Low | Documented in §9. Admin queries with `permission=admin` see everything; that is the operator's debugging tool. |
| A `[AllowPatch]` slip lets a non-owner patch `/ownerId` or `/access` | Medium without test coverage | High (privilege escalation) | The per-property check in the service is the gate. §13 tests cover stranger-patches-access-fails and stranger-patches-ownerId-fails as load-bearing assertions. |
| Auth-off test fixture masks per-node gate behaviour | Medium | Low — tests for the gate explicitly enable auth or inject a principal | §13 explicitly names the auth-on tests as a separate fixture. |
| Backfill row with `OwnerId = 0` is visible only to admin and to no one else; if a stranger had been bookmarking a public link, it becomes a `404` | Low (no public consumers today) | Low | Admin can flip `Access` to `Read` on any rows that were genuinely public. The migration plan (§12) calls this out. |
| Linking lets a stranger discover the existence of a private node by attempting to link to it and observing the response | Low (linking requires `Write` on the source; the target id is a known input) | Low | Linking does not reveal target state beyond what the source node already knows. A stranger who can guess a node id can already probe `GET /api/nodes/{id}` for the same answer (which now correctly `404`s). |

## 12. Migration / Rollout Strategy

The graph has tens of nodes and is dev-local. Migration is small.

**Phase 1 — schema and code.** Add `OwnerId` and `Access` to `Node` with `[DefaultValue(0)]`. `Init/DatabaseModelService.CreateOrUpdateSchema<Node>` reconciles the schema on boot (DiVoid #91). Add the `NodeAccess` enum file. Add the per-node helper. Wire the per-node check into `NodeService` entry points. Compose the visibility predicate into `GenerateFilter`. Add the per-property PATCH gates. Update `NodeMapper` + `NodeDetails` + `NodeFilter` (the last receives **no** new filter property, but its `Fields` defaulting will surface the new columns).

**Phase 2 — backfill.** Existing rows get `OwnerId = 0`, `Access = 0` by virtue of `[DefaultValue]`. No explicit `UPDATE` is required — the admin override means Toni still sees everything. If any nodes need to remain publicly readable (e.g. the agent-onboarding doc #9, the API reference #8, the operating contract #190 — DiVoid's documentation public surface), an admin runs `PATCH /access -> Read` on those after deploy. Toni's call which ones to publicize; the design does not pre-list them.

**Phase 3 — tests + verification.** Tests (per §13) cover owner / admin / stranger behaviour at GET, list, PATCH, content GET/POST, DELETE. Test suite passes both with `Auth:Enabled=true` (per-node gate) and `Auth:Enabled=false` (admin-equivalent fall-through).

**Phase 4 — docs.** Update `docs/architecture/auth-and-bootstrap.md` with a one-paragraph "Per-node access" section pointing at this document. No API doc surface changes (no new endpoint, no new query parameter); the response shape gains two fields but Pooshit.Json's tolerant-unknown-fields posture means consumers are not forced to change.

The Phase 2 backfill of "make documentation nodes publicly readable" is the only post-deploy operator step. It is a handful of `PATCH /access -> Read` calls and is naturally Toni's; not part of this PR.

## 13. Open Questions (for the orchestrator, if any)

None. Every architectural question is settled by the user's words, the precedents (#24, #91, #297), or the explicit out-of-scope list in §2. The §6.1 boundary on "where the per-node decision lives" is settled (service, per Code Contracts §7). The §9 `404` vs `403` choice is settled (leak avoidance, matches `/user` precedent). The §10 owner-vs-admin choice on `/ownerId` is settled (admin-only, safer side). The §13 auth-disabled posture is settled (admin-equivalent, matches existing test posture).

Two items the user may want to clarify *after* implementation, but neither is blocking:

1. **Public documentation rows** — which existing nodes (e.g. #8, #9, #190) should be publicly readable after Phase 2? Toni's call; one-line PATCH each. The default of "private to admin" is safe.
2. **Future link-tightening** — does the user want to require `Write` on the target endpoint of a link? Default today is "no" (status quo); follow-up if needed.

## 14. Implementation Guidance for the Next Agent

Implementation work is one PR (DiVoid #1165), co-located with this design doc. Suggested milestone order — each milestone leaves the build in a green state.

### M1 — Schema and enum

1. Create `Backend/Models/Nodes/NodeAccess.cs` with the `[Flags]` enum (`None=0`, `Read=1`, `Write=2`). One top-level type per file per Code Contracts §1.
2. Edit `Backend/Models/Nodes/Node.cs`: add `OwnerId` (`long`, `[Index("owner")]`, `[DefaultValue(0L)]`, `[AllowPatch]`) and `Access` (`NodeAccess`, `[Index("access")]`, `[DefaultValue((int)NodeAccess.None)]`, `[AllowPatch]`). Mirror the existing `[AllowPatch] + [Index] + [DefaultValue]` shape that `X`/`Y` use.
3. `Init/DatabaseModelService` requires no change — `CreateOrUpdateSchema<Node>` already runs.

### M2 — DTO and mapper

1. Edit `Backend/Models/Nodes/NodeDetails.cs`: add `OwnerId` (`long`) and `Access` (`NodeAccess`).
2. Edit `Backend/Models/Nodes/NodeMapper.cs`: register `"ownerId"` and `"access"` field-mappings (lowercase per the existing convention — `"id"`, `"status"`, `"contentType"`). Add both to `DefaultListFields`.

### M3 — Per-node decision helper

1. Create `Backend/Services/Nodes/NodeAuthorization.cs` (or a similarly-named file under `Backend/Services/Nodes/`). Single static-utility class exposing two methods:
   - One that returns `bool` given `(OwnerId, Access, callerId, isAdmin, operation)` — used by single-node entry points.
   - One that returns a `PredicateExpression<Node>` given `(callerId, isAdmin)` — used by the list filter composer.
   Both methods share the same truth table internally so a single edit moves both surfaces in lockstep. Per Code Contracts §0 DRY, the truth table is named (e.g. `IsVisibleForRead`, `IsVisibleForWrite`) — one helper per operation, not a switch.
2. The per-property PATCH gate (`/ownerId` admin-only, `/access` owner-or-admin) is a separate small helper in the same file — `CanPatchPath(path, isOwner, isAdmin)` is a clean fit.

### M4 — Identity extraction in the controller

1. Edit `Backend/Controllers/V1/NodeController.cs`: at every endpoint that touches a node, extract `(callerId, isAdmin)` from `User` (the `ControllerBase` principal) and pass to the service. Use the existing `ClaimsExtensions.GetDivoidUserId` for the id; for the admin flag use `User.HasClaim("permission", "admin")` directly (no new helper needed — the check is one line).
2. Handle the auth-disabled posture: when `User.Identity?.IsAuthenticated != true`, treat as admin. The check is a one-line branch in the controller's caller-resolver.

### M5 — Service wiring

1. Edit `Backend/Services/Nodes/INodeService.cs` and `NodeService.cs`: add `(long callerId, bool isAdmin)` parameters to `GetNodeById`, `ListPaged`, `ListPagedByPath`, `GetNodeData`, `Patch`, `UploadContent`, `Delete`, and `CreateNode`. (`LinkNodes`/`UnlinkNodes`: see §8.)
2. `CreateNode`: ignore `node.OwnerId` from the input; insert `callerId` (or `0` if auth-disabled-equivalent) into the new `OwnerId` column.
3. Single-node ops: load `(OwnerId, Access)` (cheap two-column scalar load), call the helper, throw `NotFoundException<Node>` on failure.
4. `Patch`: per-operation per-property check via the helper; on failure throw `AuthorizationFailedException` (existing exception, see `Backend.Errors.Exceptions`).
5. `GenerateFilter`: AND in the list-mode visibility predicate when `!isAdmin`. The signature gains `(long callerId, bool isAdmin)`. The two callers (`ListPaged` and `ComposeHops`/`ListPagedByPath`) both forward the values.
6. `LinkNodes`: require write on the source node per §8. `UnlinkNodes`: same.

### M6 — Tests

Add the following test cases (NUnit, parallelizable, `Assert.That(value, Is.EqualTo(...))` per Code Contracts §13). Use the existing HTTP-fixture style (`WebApplicationFactory<Program>` + `TestSetup.CreateTestFactory`). A new fixture `NodeAccessHttpTests` collects them; auth-on fixtures inject a fake principal via the test factory.

Auth-disabled coverage (existing posture preserved):
- `Create_NoAuth_OwnerIdIs0` — `POST /api/nodes` writes `OwnerId = 0` when no principal.
- `List_NoAuth_ReturnsAll` — list visibility predicate is no-op when auth is off.

Auth-enabled coverage (new fixture):
- `Get_PrivateNodeAsStranger_Returns404` — owner=A, Access=None, caller=B (non-admin) → `404`.
- `Get_PrivateNodeAsOwner_Returns200`.
- `Get_PrivateNodeAsAdmin_Returns200`.
- `Get_ReadablePublicNodeAsStranger_Returns200`.
- `GetContent_PrivateNodeAsStranger_Returns404`.
- `List_PrivateNodeNotVisibleToStranger` — page total reflects only visible rows.
- `List_OwnerSeesOwnPrivate` — caller=A sees own private node in the list.
- `List_AdminSeesAll`.
- `List_BothOwnerAndPublic` — caller=A, Access=Read on B's node, Access=None on C's node → page includes A's nodes and B's.
- `Patch_AccessAsStranger_Returns403_OnWritePublicNode` — write-public node, stranger may patch `/name` but not `/access`. The PATCH transaction rolls back.
- `Patch_AccessAsOwner_Returns200`.
- `Patch_OwnerIdAsOwner_Returns403` — owners may NOT transfer ownership.
- `Patch_OwnerIdAsAdmin_Returns200`.
- `Delete_PrivateNodeAsStranger_Returns404`.
- `Delete_WritePublicNodeAsStranger_Returns200`.
- `PostContent_PrivateNodeAsStranger_Returns404`.
- `Create_OwnerIdInBodyIsIgnored` — caller=A, body says `ownerId=B` → row gets `OwnerId=A`.
- `Create_AccessInBodyIsHonoured` — body says `access=Read` → row gets `Access=Read`.
- `Backfill_ExistingRow_OwnerIdIs0_AccessIsNone` — schema-reconciliation test: insert a row directly via Ocelot bypassing the service, then read via the service as admin (should be visible) and as stranger (should be invisible). Sanity test for the `[DefaultValue]` setup.

The semantic-search and path-query paths inherit the predicate via `GenerateFilter`; one sanity test each is sufficient — exhaustive coverage of every combinator is already provided by existing tests of those modes, and the visibility predicate is the same predicate everywhere.

### M7 — Docs

1. Append a "Per-node access" subsection to `docs/architecture/auth-and-bootstrap.md` (3-4 lines pointing at this design doc).
2. No API doc node (#8) change required — no new endpoint, no new query parameter, response shape gains two fields with Pooshit.Json tolerance.

### Pre-PR self-check (Code Contracts §16)

- [ ] One public type per file: `NodeAccess` in its own file (M1), helper in its own file (M3).
- [ ] `[AllowPatch]` on both class and property (`Node` is already `[AllowPatch]` via members; new properties carry the per-property attribute too).
- [ ] Domain-object return types (controllers do not wrap in `IActionResult` for new code paths).
- [ ] No `var` (explicit types).
- [ ] No `private` modifier on fields.
- [ ] `[DefaultValue]` on both new value-type columns.
- [ ] Tests `[TestFixture, Parallelizable]` + `[Test, Parallelizable]`.
- [ ] No try/catch in controller paths.
- [ ] No new exception types invented (using `NotFoundException<Node>`; `AuthorizationFailedException` is no longer used in the per-node layer after PR #130 revision).

---

## 10. Trade-offs revised by Toni (PR #130 bounce)

### Revised after PR #130 review

The following two design decisions were overridden by Toni's verbatim feedback after the first implementation round. The original design reasoning is preserved below each revision for traceability.

**Revision 1 — Access default changed from `None` (0) to `Read | Write` (3)**

Toni's verbatim words:

> *"A nodeaccess default of None would lead to current nodes not being accessible by anyone but the admin - also if anyone from then on uses it without the tools being updated they would create private nodes by default. I want the current system to behave as is, so a default of 3 would be correct here"*

**Original design:** §3 said `Access` defaults to `None = 0` because "opt-in to publicity, not opt-out" is the safe default for a new access layer. The reasoning was that unknown/unspecified callers should not accidentally expose private data.

**Revised:** `[DefaultValue((int)(NodeAccess.Read | NodeAccess.Write))]` (value 3). This preserves the pre-access-layer posture where every node was world-visible and world-mutable. Existing rows backfill to `Read|Write` via the column default, remaining fully accessible to all authenticated callers. New nodes created without an explicit `Access` in the body default to `Read|Write`. Callers who want private nodes must set `Access = None` explicitly.

---

**Revision 2 — Single-node ops use query-level access checks (no load-then-check)**

Toni's verbatim words:

> *"Delete -> load node before deletion for access check is a smell - you can just add the accessibility criterias to the where of the delete, especially since you throw a notfound on fail anyways."*
>
> *"Same for basically all methods -> load then check is inefficient. You can check all of that right at query level."*

**Original design:** §8 described a two-step pattern for single-node write ops: `Load<Node>(OwnerId, Access)` to fetch the two authorization columns, then an in-process `IsAuthorized(...)` call, then the actual operation. This was motivated by Code Contracts §6.3.1 reasoning ("entity fields are consumed for the authorization decision").

**Revised:** Every single-node op composes `NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write)` (or `BuildOwnerPredicate` for the `/access` gate) directly into the operation's WHERE clause. A 0-affected-rows result maps to `NotFoundException<Node>` (404) — this is both the "node not found" and "you can't touch it" case, unified. One DB round-trip per op instead of two. The `IsAuthorized(...)` boolean method is removed. `BuildVisibilityPredicate` is the single source of truth for the truth table, shared by list and single-node ops.

---

**Revision 3 — 404 across the board for all access failures (no 403 from per-property gates)**

Toni's verbatim words (on W1, as a principle):

> *"for W1: i want for jenny to treat comment fencing (or such matters in general) more seriously. Its better to deliver it clean right away than to have to task it for cleanup later. Behavior like this leads to technical debts (even though here its just comments), but its the principle which matters, do it right immediately instead of having to invest time later."*

The original design returned `403` for per-property PATCH gate failures (`/access` and `/ownerId`) on the reasoning that the caller had already proven they could see the node (read check passed) so the failure was at the per-property layer — not an existence-leak. With Revision 2's query-level approach, there is no longer a separate read pre-check before write ops — the read and write paths are independent. Returning `403` here would require a separate SELECT to confirm the node exists before returning the property-gate error, reintroducing the load-then-check smell. **404 across the board** is simpler: every node-touching op that fails (for any access reason) returns 404.

---

*End of design document.*
