# Architectural Document: Organizations Layer

**Task:** DiVoid #1725
**Status:** Design ready; first implementation increment bundled in the same PR (DiVoid #1165).
**Branch:** `feature/organizations-1725`
**Code Contracts:** DiVoid #114 (load-bearing).
**Design Contracts:** DiVoid #1136 (load-bearing — §1 KISS/DRY/YAGNI, §2 existing-systems-first, §4 less-is-better).
**Precedent:**
- DiVoid #1370 + session-log #1377 — per-node access layer (PR #130). Direct precedent for "auth-scoped predicate composes into `GenerateFilter` without breaking paged streaming." The patterns mirrored here: `BuildVisibilityPredicate`, threading `(callerId, isAdmin)` through every `INodeService` method, admin override, 404-not-403 to avoid existence leaks.
- DiVoid #30 — current `organization` node-type (catalog only). The graph-side concept stays; this design adds a scalar FK column for enforcement.
- DiVoid #91 — Ocelot `SchemaService` handles column adds in place when `[DefaultValue]` is set.
- DiVoid #297 — `[DefaultValue(...)]` required for non-null value-type columns to satisfy SQLite back-fill.
- DiVoid #1462 — in-flight bug on `NodeDetails.Access` create-default behaviour; orthogonal, lands independently. This design avoids further churn in that DTO area beyond the additive `OrganizationId` field.

---

## 1. Problem Statement

DiVoid today has no enforced concept of "organization membership". Every authenticated caller sees every node — the per-node access layer (#1370) gates per-row publicity, but every row sits in one shared global namespace. As DiVoid grows beyond a single-tenant graph, two callers from two different organizations must not see each other's nodes by default, even when both are authenticated and even when both have `read` permission.

The user's verbatim ask (2026-06-07):

> *"i would like an organization in divoid. nodes are linked to an organization (id field), users can be assigned to multiple organizations (can be in jwt token and apikey to not have to load linked organizations on every call) - on all calls the accessible nodes are always filtered down to accessible (linked) organizations. For ease of use a transition to an inaccessible node can be loaded, but the inaccessible nodes never appear anywhere (listing, get, patch, delete, whatever)."*

Six pieces, all required:

1. A first-class `Organization` entity.
2. Each node carries an `OrganizationId` scalar FK ("id field" in the ask).
3. Users belong to one or more organizations (many-to-many).
4. The caller's accessible-organization set is **claim-carried** on both auth paths (JWT + API key) so the membership table is not queried on every request.
5. Every node-touching call filters down to `OrganizationId ∈ caller.accessibleOrgs` as the **outermost** visibility predicate, composing with the per-node access bits from #1370.
6. **Cross-org link transition:** link adjacency rows from a visible node to an invisible far end remain in the visible node's `links` array (so the graph does not look broken). The far-end node itself never appears in any list / get / patch / delete / content response.

Success looks like:

- A non-admin caller authenticated against Org-A cannot see, fetch, patch, delete, or upload content for any node whose `OrganizationId` is Org-B — including when probing by id.
- A caller can be a member of multiple organizations simultaneously; the visibility predicate becomes `OrganizationId ∈ {orgA, orgB, ...}`.
- The membership table is read only at credential-mint time (API key) or claim-emission time (JWT), never on the request hot path.
- Existing nodes (174+ rows at design time) backfill to a default "DiVoid" organization; every current user becomes a member of that organization. No data is lost; no caller's posture changes.
- The per-node `Access` bits (#1370) still apply *within* an organization — they are not relaxed for fellow members of the same org.

## 2. Scope & Non-Scope

### In scope

- New `Organization` entity (id + name + timestamps + `OwnerId` for symmetry).
- New `UserOrganization` join entity (many-to-many `User ↔ Organization`).
- New `Node.OrganizationId` non-null scalar FK column with `[DefaultValue]` for back-fill.
- New `NodeAccess`-style `OrganizationDetails` DTO + `OrganizationMapper` + `OrganizationFilter` + `OrganizationController` at `api/organizations` (CRUD scoped to admin or to existing members).
- New `divoid.organization_ids` claim emitted on **both** auth paths (`ApiKeyAuthenticationHandler` + `KeycloakClaimsTransformation`).
- New `OrganizationAuthorization` helper that returns a `PredicateExpression<Node>` (and a small in-process tuple) for the outermost visibility gate.
- Composition of the org gate **before** the per-node gate in `NodeService.GenerateFilter` and every single-node entry point (`GetNodeById`, `GetNodeData`, `Patch`, `UploadContent`, `Delete`, `LinkNodes`, `UnlinkNodes`, `CreateNode`).
- `NodeService.CreateNode` sets `OrganizationId` from a body field if supplied (and the caller is a member); otherwise picks the caller's "primary" org per a stable rule (§7).
- Cross-org link visibility: `ListLinks` (`GET /api/nodes/links?ids=...`) and the inline `?fields=links` adjacency continue to return adjacency pairs whose *other* endpoint sits in an invisible org. The endpoints whose **own** id is visible are returned in full; the far-end id is exposed as a bare `long` only — the node itself is never expanded into the response. This is the user's "transition" rule (§6).
- Bootstrap: at first boot after this change, create a single `Organization` row named **"DiVoid"**, link every existing `User` to it via `UserOrganization`, and let the column default for `Node.OrganizationId` populate every existing node.
- Auth-disabled posture: `Auth:Enabled=false` returns `(callerId=0, isAdmin=true, accessibleOrgs=null)` — `accessibleOrgs=null` means "no filter" (admin-equivalent). Mirrors #1370.

### Out of scope (explicitly named to lock §2 audit)

- **Per-organization roles** (e.g. `org-admin` distinct from `member`). The ask says membership grants access; nothing about role distinctions inside an org. A future task can layer roles onto `UserOrganization` without disturbing the column shape.
- **UI for managing membership** — admin-only DB-level `POST /api/organizations/{id}/members/{userId}` is enough for this PR; a frontend pill / picker is a separate task.
- **Cross-org node transfer endpoint** — moving a node from Org-A to Org-B is a PATCH on `/organizationId` (admin only, see §7). No bulk-transfer surface, no transfer history, no audit log. YAGNI.
- **Organization-level `Access` flags** — Org membership itself is a binary "member or not". The user did not ask for org-level read/write distinctions; that would duplicate the per-node `Access` axis at a different layer (Design Contracts §2 form-2 anti-pattern).
- **Inviting users by email** — out of scope; future.
- **Organization deletion semantics** beyond admin-only DELETE on an empty org. Cascade rules (what happens to nodes when their org is deleted) are deferred — admin sees "cannot delete: 12 nodes reference this org" on attempt; transfer them first.
- **Folding in DiVoid #1462** (in-flight `NodeDetails.Access` create-default bug). This design is purely additive to `NodeDetails` (one new field). If #1462 merges first, this design rebases trivially; if this merges first, #1462 rebases trivially. Either order works.
- **Reworking the `organization` node-type (#30)** — graph-side `organization` nodes remain a catalog concept. This design adds a *separate*, *enforceable* `Organization` entity with its own table. The two layers may eventually merge (an organization node *is* the organization entity), but that is YAGNI today — see §10 trade-off "two organization concepts coexist".
- **Renaming an existing field** or deprecating any existing API. The change is additive.
- **A new symbol for the no-filter case.** `accessibleOrgs = null` means "admin-equivalent, no predicate" — same convention as `BuildVisibilityPredicate` returning `null` in #1370. Adding a named "all orgs" sentinel would be Design Contracts §6 anti-pattern (mirror enum with different default).

## 3. Assumptions & Constraints

**Hard constraints (verified from the codebase).**

- Schema reconciliation is in-place via `Init/DatabaseModelService` calling `SchemaService.CreateOrUpdateSchema<T>` (no migration files). New entities must be registered there.
- Non-null value-type columns require `[DefaultValue(...)]` or `INSERT` fails on SQLite (DiVoid #297; `Node.OwnerId` / `Node.X` are live precedent).
- Both auth schemes emit `divoid.user_id` (and `permission` claims). The hooks for adding a third claim already exist: `ApiKeyAuthenticationHandler:HandleAuthenticateAsync` builds the `ClaimsIdentity` directly; `KeycloakClaimsTransformation:TransformAsync` builds the augmentation identity. Both have a `User` row in hand at the point of emission; both can load (or pre-resolve) the user's org list.
- `INodeService` already accepts `(long callerId, bool isAdmin)` on every node-touching method (#1370 / PR #130). The signature gain is one more parameter (`long[] accessibleOrgs`), threaded the same way. Auth-disabled returns `null` for that parameter, meaning "no filter".
- The list-mode predicate composer is `NodeService.GenerateFilter`. Adding a second predicate before the per-node gate is mechanical — same `&=` `PredicateExpression<Node>` shape.
- `NodeMapper.DefaultListFields` is the single source for the default response shape; adding `organizationId` to it surfaces the field everywhere consistently.
- `NodeFilter` accepts URL-binding for array query parameters via `ArrayParameterBinderProvider` (`Startup.cs`). Adding `?organizationId=1,2,3` as an optional admin-scoped filter is free.
- Pooshit.Json tolerates unknown fields on the receive side; existing consumers do not break when `organizationId` appears in responses.
- All current users — at design time — sit in one logical organization (Toni's). The bootstrap "single DiVoid org with every user as a member" is therefore a no-op semantic change.

**Working assumptions.**

- Cardinality of orgs per user is low: 1–5 typical, certainly under 50. The claim payload (`int[]` serialized as a JSON array of numbers, or a CSV string) is small. JWT size is not a concern.
- Cardinality of orgs total is also low (single-digit at design time; tens within 4 weeks). Indexing `OrganizationId` is cheap and selective.
- The membership set for an API-key principal is **stable for the life of the key**. Adding or removing a user from an org does not immediately revoke an outstanding API key's claim — the next call still carries the claim from the row at mint time. Operators rotate the key (delete + re-mint) when membership changes need to take effect. This matches the existing posture for `permission` (changing a user's permission does not invalidate outstanding API keys; the keys carry the snapshot taken at mint). The fast-path saving is what the user explicitly asked for: *"to not have to load linked organizations on every call"*.

  - For the **JWT path**, the claim is computed inside `KeycloakClaimsTransformation` on every authenticated request (the transformation already runs there once per request, and already loads the `User` row). The DB hit for the user row is already paid; piggy-backing the org list on the same fetch (joining the membership table, or a single secondary query) does not introduce a new per-request round-trip. JWT principals therefore see membership changes on the next request after the change.
  - For the **API-key path**, mint-time materialization keeps `HandleAuthenticateAsync` exactly as cheap as it is today. The handler already loads the `ApiKey` row and the `User` row; the org list is denormalised onto the `ApiKey` row (`OrganizationIds` JSON column, same shape as `Permissions`).

- The cross-org "transition" rule (§6) is about *bare ids in the links array*, not full node payloads. A caller in Org-A who fetches `/api/nodes/13` (which is in Org-A) and asks for `?fields=links` may see `links: [99]` where node 99 is in Org-B. They cannot then call `GET /api/nodes/99` and get the body — that returns 404. This is the user's intended posture: "the graph does not look broken, but you cannot drill in."

## 4. Architectural Overview

Three new entities, one new claim, one composition before the existing per-node gate, one bootstrap step. The change touches the **boundary** (auth handlers) and the **predicate composer** (`NodeService.GenerateFilter` + every single-node WHERE). Nothing in the per-node access layer changes shape — `OrganizationAuthorization` is a sibling helper to `NodeAuthorization`, not a replacement.

```
                         +--------------------------------+
                         |  Organization (entity)         |
                         |--------------------------------|
                         |  Id          long              |
                         |  Name        string            |
                         |  OwnerId     long              |
                         |  Created     DateTime          |
                         |  LastUpdate  DateTime          |
                         +--------------------------------+
                                       |
                                       |  membership (many-to-many)
                                       v
                         +--------------------------------+
                         |  UserOrganization (join)       |
                         |--------------------------------|
                         |  UserId          long          |
                         |  OrganizationId  long          |
                         |  (composite primary key)       |
                         +--------------------------------+

                         +---------------------------------------+
                         |  Node (extended)                      |
                         |---------------------------------------|
                         |  Id, TypeId, Name, ...                |
                         |  OwnerId, Access  (from #1370)        |
                         |  OrganizationId    <-- NEW            |
                         +---------------------------------------+

  Claim emission (both auth paths add this alongside divoid.user_id):

      divoid.organization_ids = "1,5,12"      (CSV — single multi-valued claim, see §8)

  Visibility composition (outermost → innermost):

      principal -> { callerId, isAdmin, accessibleOrgs }

      visible(node) iff:
            isAdmin                                                # admin override (orgs + access)
         OR (node.OrganizationId IN accessibleOrgs                 # OUTER: org gate
             AND (node.OwnerId == callerId                         # INNER: per-node gate (#1370)
                  OR (node.Access & Read) != 0))

  List-mode predicate (composed into existing GenerateFilter, BEFORE the per-node predicate):

      isAdmin                                  -> no extra predicate
      accessibleOrgs is null (auth-disabled)   -> no extra predicate
      accessibleOrgs is empty (member of zero) -> WHERE 1 = 0     (returns nothing)
      otherwise                                -> AND OrganizationId IN (accessibleOrgs)
                                                  AND (per-node visibility predicate from #1370)
```

The check is identical at the single-node entry points (the same predicate ANDs into the row-level WHERE). 404 is returned for any visibility failure, never 403 — same posture as #1370 (existence leak avoidance).

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|-|-|-|
| `Organization` (new entity, `Backend/Models/Organizations/Organization.cs`) | The org identity row: id, name, owner, timestamps. PK + index on `Name`. | Membership (lives in `UserOrganization`). Nodes (linked by FK from `Node.OrganizationId`). |
| `UserOrganization` (new join entity, `Backend/Models/Organizations/UserOrganization.cs`) | The many-to-many membership row: `(UserId, OrganizationId)` composite key. No payload beyond the two ids. | Roles, invitations, expiration. |
| `Node.OrganizationId` (new column on existing `Node`) | The single org that owns this node. Non-null. Default = bootstrap "DiVoid" org id (a `const` resolved at first boot — see §7 "bootstrap"). Indexed for the visibility predicate. | The list of orgs the node is *visible to* — that is membership-derived (`Node.OrganizationId IN caller.accessibleOrgs`). A node belongs to exactly one org. |
| `OrganizationDetails` (new DTO) | API-facing representation of an org. Includes a `members: long[]` field on **single-get** responses only; list responses omit it (cardinality discipline). | The membership table itself. |
| `OrganizationMapper` (new) | The `FieldMapper<OrganizationDetails, Organization>` translating between DTO and entity. Standard pattern mirrored from `UserMapper` / `NodeMapper`. | Membership joins (separate small helper in the service). |
| `OrganizationFilter` (new) | `ListFilter` extension: `Id`, `Name` (wildcard-aware per existing convention). No `LinkedTo` (orgs are not graph nodes). | Permission gating. |
| `IOrganizationService` + `OrganizationService` (new, `Backend/Services/Organizations/`) | CRUD on `Organization`; `AddMember`, `RemoveMember`, `ListMembers`; `GetUserOrganizationIds(long userId)` used by both auth handlers at claim-emission time. | The claim emission itself (that's the auth handler's job). |
| `OrganizationController` (new, `Backend/Controllers/V1/OrganizationController.cs`) | REST surface for orgs and membership at `api/organizations`. | Per-node access — that stays in `NodeController` / `NodeService`. |
| `OrganizationAuthorization` (new sibling helper to `NodeAuthorization`, `Backend/Services/Nodes/OrganizationAuthorization.cs`) | One method: `BuildOrgVisibilityPredicate(long[] accessibleOrgs, bool isAdmin)` returning `PredicateExpression<Node>` (or `null` when admin / no-filter / unset). One method: `IsOrgAccessible(long? orgId, long[] accessibleOrgs, bool isAdmin)` — the bool form for in-process checks (used by `CreateNode` when validating a body-supplied `organizationId`). | The per-node bits (those stay in `NodeAuthorization`). |
| `INodeService` / `NodeService` (existing) | Threading `accessibleOrgs` through every node-touching method; ANDing the org gate into every WHERE clause BEFORE the per-node gate. `CreateNode` resolves the new node's `OrganizationId` from the body (validated) or from a "primary org" rule. | The membership lookup (that's the auth handler's job; the service receives the claim-derived list). |
| `NodeController` (existing) | Extending `ResolveCaller()` to return `(callerId, isAdmin, accessibleOrgs)` — read the new claim. Threading `accessibleOrgs` into every service call. | The org-CRUD endpoints (those live in the new `OrganizationController`). |
| `ApiKeyAuthenticationHandler` (existing) | Adding the `divoid.organization_ids` claim during `HandleAuthenticateAsync` from the denormalised `ApiKey.OrganizationIds` column. | The membership table (read denormalised, not queried). |
| `KeycloakClaimsTransformation` (existing) | Adding the `divoid.organization_ids` claim during `TransformAsync` from a single membership query keyed by user id. | Token issuance (Keycloak does that). |
| `ApiKey.OrganizationIds` (new column on existing `ApiKey`) | Denormalised snapshot of the user's org-membership at key-mint time. JSON-encoded `long[]` per the existing `Permissions` pattern. | Authoritative membership (that's `UserOrganization`). Re-syncing on membership change (out of scope — see §3). |
| `Init/DatabaseModelService` (existing) | Registering the two new entities (`Organization`, `UserOrganization`) for schema reconciliation. The bootstrap block — see §7 — also lives here as a small idempotent block. | The org-CRUD surface. |

**Single-responsibility check.** No component owns more than one concern. The visibility decision is in one helper; the membership snapshot lives on one column; the claim is emitted from one place per scheme; the service reads the claim and composes predicates. The per-node access layer (#1370) is untouched in shape — `NodeAuthorization` remains its own helper; `OrganizationAuthorization` sits next to it.

## 6. Interactions & Data Flow

### Identity resolution (extended `ResolveCaller`)

```
caller                  NodeController                      principal
  | Authorization: Bearer xxx   |                                |
  |---------------------------->|                                |
  |                              | ResolveCaller():               |
  |                              |   callerId      = divoid.user_id claim
  |                              |   isAdmin       = permission claim == "admin"
  |                              |   accessibleOrgs = ParseCsvLongs(divoid.organization_ids claim)
  |                              |                              null IF auth disabled
  |                              |                              empty array IF claim present but empty
  |                              |                              null IF isAdmin (admin override fast-path)
```

### Sequence: `GET /api/nodes` (list)

```
caller                  NodeController                NodeService                DB
  | GET /api/nodes               |                              |                      |
  |---------------------------->|                              |                      |
  |                              | (callerId, isAdmin, orgs)    |                      |
  |                              |  = ResolveCaller()           |                      |
  |                              | ListPaged(filter,            |                      |
  |                              |          callerId, isAdmin,  |                      |
  |                              |          accessibleOrgs)     |                      |
  |                              |----------------------------->|                      |
  |                              |                              | GenerateFilter:      |
  |                              |                              |   + BuildOrgVisibility|
  |                              |                              |     (outer)          |
  |                              |                              |   + BuildVisibility  |
  |                              |                              |     (inner, #1370)   |
  |                              |                              |   + caller filters   |
  |                              |                              |--------------------->|
  |                              |                              |  SELECT ...          |
  |                              |                              |  WHERE OrganizationId|
  |                              |                              |    IN (...)          |
  |                              |                              |    AND (OwnerId=cid  |
  |                              |                              |         OR Access&1) |
  |                              |                              |    AND (rest)        |
  |  HTTP 200 page               |                              |                      |
  |<-----------------------------|                              |                      |
```

Page total reflects only visible rows (window function uses the same predicate).

### Sequence: `GET /api/nodes/{id}` and `GET /api/nodes/{id}/content`

Both compose the org gate AND the per-node gate into the WHERE of the existing single SELECT (or the `Update<Node>().Patch().Where(...)` in PATCH / content-POST). Zero rows → `NotFoundException<Node>` (404). No separate read pre-check; no separate "you can't see this org" error class. Existence is not leaked across org boundaries — same posture as the per-node gate.

### Sequence: `POST /api/nodes` (create) — `OrganizationId` resolution

`NodeDetails.OrganizationId` is **optional** on the create body:

- **Supplied + caller is a member (or admin):** honoured. The new node gets that org.
- **Supplied + caller is not a member + not admin:** `ArgumentException` → HTTP 400 with a clean message. This is the only org-scoped 400; not a 404. The caller asked to write into a specific org they cannot access; that is a deliberate mistake worth surfacing. (Distinct from "the org id doesn't exist" — which gets a different message in the same handler.)
- **Omitted:** the service picks the caller's "primary org" using the deterministic rule in §7. If the caller has no memberships at all (mid-life user created after the bootstrap backfill ran), the service silently falls back to the bootstrap "DiVoid" org id — see §7's revised rule. No 500.
- **Auth-disabled mode:** `accessibleOrgs == null` (admin-equivalent). The body's `organizationId` is honoured if supplied; otherwise the new node gets the bootstrap "DiVoid" org id from the column default. (The default sentinel is a stable `const` — see §7 "bootstrap default sentinel" and §10 trade-off "const not config".)

### Sequence: cross-org link transition (the user's "transition" rule)

The cleanest reading of the user's words *"a transition to an inaccessible node can be loaded"* is that the **link adjacency** (a `(SourceId, TargetId)` pair) remains visible when one endpoint is visible, even if the other endpoint is invisible — but the invisible endpoint is never expanded into a node payload.

Two paths express this:

1. **`GET /api/nodes/links?ids=...`** (`NodeController.ListLinks`): returns `NodeLink` adjacency rows where either `SourceId` or `TargetId` is in the requested `ids`. The current shape (PR #130 era) does **not** filter by visibility — it returns bare `long`-pair rows. The org gate adds **one** filter: at least one endpoint of the returned pair must be in a visible org (`SourceId.In(visibleNodeIds) || TargetId.In(visibleNodeIds)`). The invisible far end's bare id stays in the response. (Without this filter, an attacker could call `ListLinks` with arbitrary ids and harvest org-B adjacency directly.) Implementation: `ListLinks` first applies the org+access predicate to its `ids` (filter `ids` down to visible-by-caller ids), then returns adjacency rows incident to *that* filtered id set. Implementation lives in the second PR; the first increment ships the `Organization` + claim + outer org predicate on `GenerateFilter` and on the most-used single-node ops (`GetNodeById`, `Delete`).

2. **Inline `?fields=links` adjacency on list/get responses** (`NodeService.FetchAdjacentIds`): the helper today fetches every neighbour of every returned row. With the org gate in place, the *containing* row is by construction visible (it passed the WHERE). Its neighbour ids are returned bare — same posture as #1: the bare `long` of the invisible neighbour stays, the neighbour's node payload does not.

The result the user sees: the graph viewer renders an arrow from node 13 (visible, Org-A) to node 99 (Org-B); clicking node 99 fetches `GET /api/nodes/99` and gets a 404. The arrow stays drawn; the destination is unreachable. That is the user's "transition to an inaccessible node can be loaded, but inaccessible nodes never appear anywhere."

### Sequence: org CRUD

`api/organizations` — single REST surface. `GET` lists organizations the caller is a member of (admin sees all). `POST` creates (admin only — the bootstrap pattern is that orgs are created administratively, not self-service). `PATCH /api/organizations/{id}` mutates name (admin or member-of-the-org). `DELETE` admin-only, and only when no nodes still reference the org (FK-style check at service layer; throws if any node still has that `OrganizationId`).

`POST /api/organizations/{id}/members/{userId}` (admin only) inserts the `UserOrganization` row. `DELETE` removes it. **Both endpoints invalidate the cached snapshot on the affected user's API keys is OUT OF SCOPE** — per §3, API-key membership snapshots are stable for the life of the key; rotation is the documented mechanism.

## 7. Data Model (Conceptual)

### Entities

| Entity | Field | Type | Notes |
|-|-|-|-|
| `Organization` | `Id` | `long` | `[PrimaryKey, AutoIncrement]`. |
| | `Name` | `string` | `[Index("name")]`, `[Size(...)]` if needed. Not unique — multiple orgs may share a display name in principle (operators can enforce uniqueness later if they want). |
| | `OwnerId` | `long` | The user who created the org. Same sentinel semantics as `Node.OwnerId` from #1370. |
| | `Created` | `DateTime` | `[DefaultValue("0001-01-01 00:00:00")]`, backfilled on first boot mirroring the `Node.Created` pattern. |
| | `LastUpdate` | `DateTime` | Same shape. |
| `UserOrganization` | `UserId` | `long` | `[Index("user")]`. Half of the composite key. |
| | `OrganizationId` | `long` | `[Index("organization")]`. The other half. |
| | (composite PK) | | Pooshit.Ocelot composite key convention: both columns indexed under one composite-index name, `[Index("user_org")]` on both. (Verify the exact attribute convention against existing composite indexes in the codebase during implementation — `Node` uses multiple `[Index("name")]` attributes on different properties to compose a single index; same shape applies here.) |
| `ApiKey.OrganizationIds` | new column | `string` | `[JsonColumn]`, JSON-encoded `long[]`. Snapshot at key-mint time. Null/empty = no orgs (caller sees nothing). |
| `Node.OrganizationId` | new column | `long` | `[Index("organization")]`, `[DefaultValue(BootstrapOrgIdConst)]`, `[AllowPatch]`. Non-null. PATCHable only by admin (similar to `OwnerId` per #1370 §8). |

### Relationships

- `Node N → 1 Organization` (each node belongs to exactly one org).
- `User N ↔ N Organization` (via `UserOrganization`).
- `Organization 1 → ?  User` (an `Organization.OwnerId` references the creating user; informational, no FK enforcement — same as `Node.OwnerId`).

### Bootstrap default sentinel

Per §3 / §10, the bootstrap "DiVoid" org gets a stable, well-known id. The cleanest path: insert the bootstrap row at first boot if it does not already exist, then expose the id as a `const long BootstrapOrgIdConst = 1` in `Backend/Models/Organizations/Organization.cs`. Reasoning for `const` over config (per Design Contracts §3): there is no operator who would re-tune this; the value does not differ across environments by design (every deployment of DiVoid has its own "DiVoid org" at id 1); no telemetry-then-tune story. A constant is the right shape.

The bootstrap block runs **inside** `Init/DatabaseModelService.StartAsync` after the schema reconciliation, inside the existing transaction:

1. If `SELECT COUNT(*) FROM organization = 0`: `INSERT INTO organization (Name, OwnerId, Created, LastUpdate) VALUES ('DiVoid', 0, now, now) RETURNING Id`. The returned id should be `1` on a fresh DB; the column default for `Node.OrganizationId` is `1` so back-fill is automatic.
2. If `SELECT COUNT(*) FROM userorganization = 0`: backfill every existing user as a member of the bootstrap org via `INSERT INTO userorganization (UserId, OrganizationId) SELECT u.Id, 1 FROM divoid_user u`.
3. Both are idempotent: re-running the bootstrap on a populated DB is a no-op (`COUNT(*) > 0` guards both inserts). No additional `IF NOT EXISTS` per row needed because the `COUNT` check brackets the whole block.

### Backfill posture for existing nodes

Per DiVoid #91, `[DefaultValue(BootstrapOrgIdConst)]` on the new `Node.OrganizationId` column lets `SchemaService.CreateOrUpdateSchema<Node>` populate the column for every existing row at the moment the column is added. The bootstrap block (step 1 above) runs **before** any caller can write to the DB, but **after** schema reconciliation. SQLite reconciliation defaults the column at the column-add step; Postgres does the same. No explicit `UPDATE node SET OrganizationId = 1` is required — the column default handles it.

**Order matters.** Schema reconciliation runs first; the bootstrap inserts run second; the existing `Node.Created` back-fill (`StartAsync` at `DatabaseModelService.cs:41–44`) runs third. All in one transaction.

### What is intentionally not modelled

- **No FK constraints** on `Node.OrganizationId → Organization.Id`, on `UserOrganization.UserId → divoid_user.Id`, or on `UserOrganization.OrganizationId → Organization.Id`. The bootstrap convention and admin-only delete guard are enough. Same reasoning as `Node.OwnerId` having no FK in #1370.
- **No "DefaultOrganizationId" per user**. The "primary org" pick rule (§7 below) is deterministic from the existing membership list; an extra column is YAGNI.
- **No timestamp on `UserOrganization`**. When was a user added to an org? The membership table is a pure set; auditing membership changes is a separate (future) concern.

### "Primary org" pick rule

When `CreateNode` is called with no `organizationId` in the body:

1. If `accessibleOrgs` is null (auth-disabled) OR empty (caller authenticated but in no orgs): use `BootstrapOrgIdConst`. The empty-set case is a no-fail fallback — without it, every user-mid-life-created-without-membership would 500 on first node create; with it, the new node lands in the bootstrap org and is visible to admin (and to the caller as the owner via the per-node `Access` bits). The user can be added to a real org afterwards and `PATCH /organizationId` (admin) the existing node into it.
2. If `accessibleOrgs` has one element: use that.
3. If `accessibleOrgs` has more than one: pick the smallest id. Deterministic; the bootstrap org is always id `1` and therefore always the default for a multi-org caller. Callers who want a non-default org explicitly supply `organizationId` in the body.

The "smallest id" rule keeps the contract local — no per-user "default org" column, no opaque server-side state. If the user later wants a per-user default, that becomes a column on `User`; today it is YAGNI.

## 8. Contracts & Interfaces (Abstract)

### Claim — `divoid.organization_ids`

- **Type:** comma-separated list of `long`s (`"1,5,12"`). No spaces, no brackets, no JSON encoding.
- **Reason for CSV over JSON-array-as-string:** simplest possible parser at read time (`value.Split(',') -> long.Parse`); the `permission` claim is already emitted as repeated single-valued claims rather than a serialized array, and reading "all values for claim type X" is one-line via `principal.FindAll(claimType)`. The CSV form lets us emit one claim entry containing all ids — slightly more compact JWT payload, slightly cheaper claim enumeration. Either shape works; the CSV form is the one this design commits to so both auth paths emit identically.
- **Absent claim:** treated as `accessibleOrgs = null` → admin-equivalent fall-through (matches `Auth:Enabled=false` posture). A principal whose membership is genuinely empty is implicitly treated as a member of the bootstrap "DiVoid" org — `IOrganizationService.GetUserOrganizationIds` returns `[BootstrapOrgIdConst]` for users with no explicit membership rows. This matches the bootstrap intent ("every user starts in DiVoid") and avoids the trap where a mid-life-created user with no explicit org assignment becomes invisible to themselves. The empty-array emission (visibility predicate `WHERE 1=0`) is reserved for a future strict-mode toggle if operators ever want it; today there is no such mode.
- **Admin shortcut:** when `permission == "admin"`, the org claim is emitted but ignored by the predicate composer (`isAdmin` short-circuits `accessibleOrgs`).
- **Caller-side parse:** `NodeController.ResolveCaller()` reads `principal.FindFirstValue("divoid.organization_ids")`. Null → `null` (admin / auth-off). Empty string → `Array.Empty<long>()`. Non-empty → `value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray()`.

### `INodeService` signature gain

Every method that today accepts `(long callerId, bool isAdmin)` gains a trailing `long[] accessibleOrgs` parameter (`null` semantics = no filter / admin override). This is one mechanical edit per method, the same shape as the #1370 thread-`(callerId, isAdmin)` gain.

`CreateNode(NodeDetails node, long callerId, long[] accessibleOrgs)` — `isAdmin` is implicit because `accessibleOrgs == null` already covers it, but for symmetry with the other methods we add `bool isAdmin` here too (consistency over micro-optimization).

### `OrganizationAuthorization` contract

- `BuildOrgVisibilityPredicate(long[] accessibleOrgs, bool isAdmin) → PredicateExpression<Node>?` — returns `null` when `isAdmin || accessibleOrgs == null` (no filter). Returns `n => false` (an always-false predicate) when `accessibleOrgs.Length == 0`. Returns `n => n.OrganizationId.In(accessibleOrgs)` otherwise.
- `IsOrgAccessible(long? orgId, long[] accessibleOrgs, bool isAdmin) → bool` — in-process check used by `CreateNode` to validate a body-supplied `organizationId`. Returns `true` if `isAdmin || accessibleOrgs == null`; `false` if `orgId == null`; `accessibleOrgs.Contains(orgId.Value)` otherwise.

### `NodeService.GenerateFilter` composition order

ANDing order is **outer-first** for clarity and for the `WHERE 1=0` short-circuit:

```
predicate = orgPredicate           (outer — drops invisible-org rows first)
            AND nodeVisibility     (inner — per-node access, #1370)
            AND callerFilters      (id, type, name, status, bounds, ...)
```

`PredicateExpression<Node>` is associative under `&=`, so the order is logical, not performance-critical. The conventional ANDing order in `GenerateFilter` today (visibility first, caller filters second) is preserved; the org gate slots in **before** visibility.

### Org-CRUD endpoint contract

| Method + path | Body | Returns | Authorization |
|-|-|-|-|
| `GET /api/organizations` | (query: `OrganizationFilter`) | `Page<OrganizationDetails>` | any authenticated; results filtered to caller's `accessibleOrgs` (admin sees all). |
| `GET /api/organizations/{id}` | — | `OrganizationDetails` (incl. `members: long[]`) | caller must be member OR admin. 404 otherwise (mirrors the per-node 404-not-403). |
| `POST /api/organizations` | `OrganizationDetails` (name required) | created `OrganizationDetails` | admin only (policy: `admin`). |
| `PATCH /api/organizations/{id}` | `PatchOperation[]` | patched `OrganizationDetails` | admin OR member-of-the-org for `/name`; admin-only for `/ownerId`. (Same shape as #1370 per-property gates.) |
| `DELETE /api/organizations/{id}` | — | 200 | admin only; refuses if any node still has `OrganizationId == id` (returns 400 with a clear message — "cannot delete: N nodes reference this org"). |
| `POST /api/organizations/{id}/members` | `{ userId: long }` | 200 | admin only. Insert is idempotent — re-adding an existing member is a no-op (no 409). |
| `DELETE /api/organizations/{id}/members/{userId}` | — | 200 | admin only. Delete is idempotent — removing a non-member is a no-op. |

### Invariants

- Every `Node` has exactly one `OrganizationId`. Single-valued, non-null.
- A `User` may belong to zero or more `Organization`s. Zero is allowed but undesirable; bootstrap ensures every existing user has at least one (the "DiVoid" org).
- `Organization.Id == 1` is the bootstrap "DiVoid" org. This is conventional, not enforced — but the bootstrap is the only path that creates an org without explicit operator action, and the bootstrap runs first.
- The org gate is the outermost predicate; the per-node access gate runs inside it. A node invisible by org is also invisible by per-node access (the outer AND short-circuits).

## 9. Cross-Cutting Concerns

**Auth on/off posture.** `Auth:Enabled=false` → `accessibleOrgs = null` → no org predicate added. Mirrors #1370 `(callerId=0, isAdmin=true)`. All existing tests pass unchanged.

**Membership freshness vs cost.** The user explicitly traded freshness for cost: *"to not have to load linked organizations on every call"*. The design honours that by claim-carrying membership. JWT path: claim is computed on every request inside `TransformAsync` (one extra small SELECT, same DB connection as the user lookup it already does). API-key path: claim is snapshotted at mint time, served from the row on every authenticate. The asymmetry is documented in §3 working assumptions and surfaced in §10 trade-offs.

**Leak avoidance.** All visibility failures → 404 via `NotFoundException<Node>`. Org membership existence is not leaked. Per-node 403 paths from #1370 are unchanged — they were already converted to 404 in PR #130 (see node-access-layer.md §10).

**Indexing.** `Node.OrganizationId` gets a single-column index. The dominant non-admin list query becomes `WHERE OrganizationId IN (a,b,c) AND (existing per-node + caller predicate)`. With one or two orgs in `accessibleOrgs`, the planner uses `OrganizationId` as the leading filter; with many orgs, the planner switches to other indexes — Postgres handles `IN (...)` efficiently. No composite index today; add one if EXPLAIN ever shows a bad plan (one boot, not a migration).

**Concurrency.** No multi-row coordination. `Node.OrganizationId` is a single-column write via PATCH (admin only). `UserOrganization` rows are insert / delete primitives — last-writer-wins on duplicate insert is fine (the row is a set membership; idempotency is built in).

**Idempotency.** `POST /api/organizations/{id}/members` is naturally idempotent on the row primitive: insert wrapped in a "where not exists" → 0 affected rows → 200 anyway (the membership is the state, not the verb). Same for delete.

**Observability.** No new logging required. The existing PATCH controller logs `"Patching node '{nodeId}'"` etc. If `/organizationId` patches deserve a higher-signal audit log later, that is a small additive change — not in scope. Org CRUD endpoints log writes (POST / PATCH / DELETE) per Code Contracts §11.

**Error handling.** No new exception types. The "caller asked for org X but is not a member" case throws `ArgumentException` (existing handler → 400). The "caller has no memberships at all" case throws `InvalidOperationException` (existing handler → 500). All visibility failures throw `NotFoundException<Node>` (existing).

**Consistency model.** Within a transaction (single PATCH array touching multiple paths), atomicity is unchanged from the per-node access layer. Cross-org node transfer is a single-property PATCH; it succeeds or fails atomically.

**Caching.** None added. The API-key denormalisation of `OrganizationIds` is a form of caching (snapshot-at-mint), discussed in §10 trade-offs.

## 10. Quality Attributes & Trade-offs

**Scalability.** O(1) per single-node check (index seek + IN membership). O(log n) per list query given the index. No new joins on the hot path.

**Performance.** One `long` column per node (~8 bytes); one index. One JSON column on `ApiKey` (~64 bytes typical). One new claim of ~10-50 bytes per JWT. At the 10K-node scale next year, all noise.

**Maintainability.** New entity follows the existing entity-DTO-mapper-service-controller pattern verbatim. New helper sits next to `NodeAuthorization`. Composition order in `GenerateFilter` is one extra `&=` line at the top.

**Evolvability.** Per-org roles slot onto `UserOrganization` (add a `Role` column) without disturbing the column shape. Self-service org creation (non-admin) slots onto `OrganizationController` (relax the policy). Cross-org sharing (a node visible to multiple orgs) would require a different shape — `Node.OrganizationId` becomes a join table — and is documented as out-of-scope so the future change is explicit, not silent.

### Trade-offs explicitly accepted

| Trade-off | Why we accept it |
|-|-|
| **API-key claim is a snapshot at mint time, not live** | User explicitly asked for this performance posture (*"to not have to load linked organizations on every call"*). Membership changes don't invalidate outstanding keys; operators rotate keys when they need new membership to take effect. Same posture as the existing `Permissions` snapshot on `ApiKey`. Trade-off: a removed user retains org access until their key is revoked or naturally expires. For our scale (single-digit users) this is fine; at scale we add key-revocation or a short-TTL refresh mechanism — out of scope today. |
| **JWT path loads membership on every request** | Per-request DB hit for membership is fine because `KeycloakClaimsTransformation` already loads the user row on every authenticated request. Adding a second small SELECT (or extending the user lookup with a join) does not introduce a *new* round-trip semantics — the request was already paying one DB cost. The user's "to not have to load on every call" concern was about the API-key path (the hot path for agent traffic); JWT traffic is the human-frontend path where per-request DB hits are acceptable. |
| **No FK constraints on `Node.OrganizationId`** | Mirrors `Node.OwnerId` in #1370 — sentinel-friendly, no migration churn, admin-only delete already guards orphan creation. FK adds enforcement at the cost of cross-table coordination on every insert; for our scale and our discipline, the convention is enough. |
| **One organization per node** (not many-to-many) | The user said *"id field"* (singular). A node belonging to multiple orgs is a different shape entirely (join table, predicate-rewrite from `IN` to `EXISTS`). Sharing is a future use case; today YAGNI. |
| **Two organization concepts coexist** — the `organization` node-type (#30) AND the new `Organization` entity. | These are different layers. The node-type is a graph-side concept used for documentation, encyclopedia entries, and graph navigation. The entity is an enforcement primitive for visibility. They could eventually be unified (an "organization node" *is* the row in the entity table), but unification today would either (a) re-shape the existing graph posture or (b) couple visibility enforcement to graph-link conventions — both larger than the user's ask. Per Design Contracts §4 (less is better), the simpler shape is two coexisting concepts with clear boundaries. The eventual unification is a separate task with its own design. |
| **Cross-org "transition" returns bare ids, not stub nodes** | The user's words name `links` list visibility, not "show me a placeholder node". Returning a `{ id: 99, name: "[hidden]" }` stub would be a third visibility tier ("partially visible") with its own contract surface; YAGNI and inconsistent with the binary-visibility model. Bare ids in the links array let the frontend draw the arrow and decide how to render an unreachable endpoint. |
| **Bootstrap org id is a `const`, not a configurable** | Per Design Contracts §3 — no operator will tune this; no environment difference by design; no telemetry-then-tune story. The `const` shape avoids YAGNI configurability while still being centralized (one symbol referenced from `Node.OrganizationId` default, from `OrganizationService.GetPrimaryOrgId`, and from the bootstrap block). |
| **Empty `accessibleOrgs` claim → visible nothing** | A misconfigured user (no memberships) seeing nothing is correct behaviour. Loud failure is better than silent fall-through to admin-equivalent. The dev-mode fallback (`null → admin-equivalent`) only triggers under `Auth:Enabled=false`, never for an authenticated principal. |
| **Org-membership endpoint uses `userId` in the body for POST and in the path for DELETE** | Insert + remove asymmetry follows REST collection semantics — `POST /members` with body identifies the new resource; `DELETE /members/{id}` identifies the existing one. The shape mirrors the prevailing convention in this codebase (mapper-list endpoints accept filters as query strings; single-resource endpoints carry the id in the path). |

### Alternatives considered and rejected

| Alternative | Why rejected |
|-|-|
| **Org as a graph link** (`node ─belongs-to→ organization-node`) | Adds two join hops to every list-mode query; the column form composes into `GenerateFilter` with a single AND. The user explicitly said *"id field"*, not "graph link". |
| **Live membership lookup on every request** | The user's ask explicitly forbids this for the API-key path. Implementing both paths uniformly (live lookup or snapshot) keeps the contract simpler, but the asymmetry is justified — the API-key hot path is the bandwidth concern, the JWT human path is not. |
| **Many-to-many `Node ↔ Organization`** | One node, multiple orgs is a different model (sharing). User said singular "id field". Out of scope for this PR (named in §2). |
| **Per-org `Access` flags** (read/write at org membership level) | Duplicates the per-node `Access` axis at a different layer; Design Contracts §2 form-2 (parallel layer). Per-node `Access` already covers the per-row publicity question; org membership is a binary set. |
| **Cascade delete: deleting an org deletes all its nodes** | Surprising and destructive; a single PATCH could destroy hundreds of rows. Refuse-if-referenced is the conservative posture and matches the user's general "explicit > implicit" preference. |
| **A "DefaultOrganizationId" column on `User`** | One more column to maintain; the "smallest id" deterministic rule covers the actual case (single-membership users always get their one org; multi-membership users override per-create). YAGNI. |
| **A `Full = 3` named symbol for `Read | Write`** in `NodeAccess` (mentioned in #1370 §2) — for symmetry with a hypothetical `OrganizationAccess` enum | We're not adding an `OrganizationAccess` enum. Org membership is binary. No new enum, no named symbol, no Design Contracts §6 anti-pattern. |
| **Skip the architect for this task** | The skip-the-architect rule (#1184) is for parallel features under ~50 LoC mirroring a precedent. This task adds three entities, a new claim on two auth paths, a new composition layer in `GenerateFilter`, and a bootstrap block. Comfortably beyond the threshold. |

## 11. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|-|-|-|-|
| `[DefaultValue(BootstrapOrgIdConst)]` is omitted on `Node.OrganizationId` → `INSERT` fails on SQLite for existing rows when the column is added | Medium (repeat-observed trap per DiVoid #297) | Service refuses to start cleanly | `[DefaultValue]` is on the column. The first-increment tests include schema reconciliation + a smoke create. |
| Bootstrap block runs in the wrong order (before schema reconcile) | Low | Schema reconciliation fails, service won't start | Bootstrap block is at the end of `StartAsync`, after schema reconciliation, inside the same transaction. |
| Bootstrap org gets a non-`1` id (some external row was inserted before reconciliation) | Low (the only row source is the bootstrap itself, on a fresh DB) | The `const BootstrapOrgIdConst = 1` becomes a lie | The bootstrap is idempotent and gated on `COUNT(*) == 0`; in the rare scenario where another row precedes it (e.g. a manual test fixture), the bootstrap is skipped and the column default refers to a non-existent id. Mitigation: the bootstrap explicitly checks `COUNT(*) == 0` and creates the row before the column default is ever consulted by a node insert. On a fresh DB, the row is id `1` reliably (single `INSERT` into an `AutoIncrement` PK). |
| API-key snapshot stale (user removed from org but their key still grants access) | High over time (user adds + removes happen) | Medium — surfaces as "ex-member still sees nodes" | Documented in §3 working assumptions and §10 trade-offs. Operator rotates the key (delete + re-mint) when membership change must take effect. Future: short-TTL refresh or per-key revocation. |
| JWT path SELECT for membership on every request becomes a hot-path cost | Low at design-time scale | Low | Single small query with a covering index; the user row is already being loaded on the same connection. If it becomes a problem (post-deploy observation), cache the membership in a short-TTL memory cache scoped to the principal. |
| The `divoid.organization_ids` claim becomes too large for a JWT (many orgs) | Very low (single-digit orgs/user at scale) | Medium | If membership ever genuinely explodes, switch the claim from "the full list" to "a token that the service resolves once per request from a short-TTL cache". That's a different design; the current shape is right for the named scale. |
| An admin accidentally deletes the bootstrap "DiVoid" org | Low (admin-only, refuses if any node references it; nodes always reference it because of the column default) | High | The refuse-if-referenced guard makes it functionally impossible to delete a bootstrap org with any nodes in it. The admin would have to transfer every node away first; that is intentional. |
| Cross-org link "transition" leaks the existence of nodes in other orgs via `ListLinks` | Medium without the fix | Low (id alone is not sensitive; full nodes still hidden) | `ListLinks` filters its `ids` parameter through the visibility predicate before returning adjacency rows. The bare far-end id is the user's explicitly-accepted leak; the node payload is not. |
| `CreateNode` with no `organizationId` and a caller in multiple orgs writes to the wrong default org | Medium (caller intent ambiguous) | Low (visible to the caller; correctable via PATCH) | Deterministic rule (`MIN(accessibleOrgs)`) is documented in §7 and in the API doc. Frontends are expected to pass `organizationId` explicitly when the caller has multiple memberships. |
| A test using `NodeUserLookupHttpTests`-style auth-disabled fixture now also needs an org context | Low (auth-disabled mode is admin-equivalent for orgs too) | Low | `Auth:Enabled=false` → `accessibleOrgs = null` → admin-equivalent in the new layer. Existing tests continue to pass without modification. New auth-enabled tests use the existing JWT fixture pattern. |

## 12. Migration / Rollout Strategy

The graph has ~200 nodes and is dev-local. Migration is small and runs at first boot.

**Phase 1 — schema and entities (first PR, bundled with design).**

1. Create `Backend/Models/Organizations/Organization.cs` and `UserOrganization.cs`. Register both in `Init/DatabaseModelService.StartAsync` BEFORE the existing entities (or after — order is irrelevant to schema; for readability, register the two new ones immediately after `User` and before `Node` so the section reads "user, user-org membership, node").
2. Add `OrganizationId` to `Node.cs` with `[DefaultValue(BootstrapOrgIdConst)]`, `[Index("organization")]`, `[AllowPatch]`.
3. Add `OrganizationIds` (JSON string) to `ApiKey.cs` with `[JsonColumn]`.
4. Bootstrap block at the end of `StartAsync` per §7.

**Phase 2 — claim emission + caller resolution (first PR).**

1. `ApiKeyAuthenticationHandler.HandleAuthenticateAsync`: read `details.OrganizationIds` (denormalized), emit one `divoid.organization_ids` claim with the CSV value.
2. `KeycloakClaimsTransformation.TransformAsync`: after loading the `User` row, run a small SELECT against `UserOrganization` for the user's org ids, emit the same claim.
3. `NodeController.ResolveCaller`: extend to return `(callerId, isAdmin, accessibleOrgs)`; parse the new claim.

**Phase 3 — service threading + outer predicate (first PR, partial; remainder is a follow-up PR if surface is too large).**

The first PR ships the org gate on:
- `INodeService.ListPaged` / `GetNodeById` / `Delete` (these are the smoke-test surfaces).
- The composition in `GenerateFilter`.
- `CreateNode` (so the OrganizationId column is correctly written for new nodes immediately).
- `OrganizationAuthorization` helper file.

The first PR's follow-up (a second PR) carries:
- `Patch`, `UploadContent`, `GetNodeData`, `LinkNodes`, `UnlinkNodes` org-thread extensions.
- `ListPagedByPath` org-thread extension (path mode shares `GenerateFilter` via `ComposeHops`, so the composition is already in place — only the parameter thread needs to extend).
- `ListLinks` visibility filtering (the cross-org link transition rule on the bulk-links endpoint).
- The `OrganizationController` CRUD surface and member-add/remove endpoints.
- Auth-enabled HTTP test fixture coverage for cross-org list filtering, cross-org get → 404, cross-org link transition.

Both PRs reference this design; the second PR's body says "completes the visibility threading and org-CRUD surface for the design in #1725; first increment was PR #X."

The follow-up PR is **named in this design** (per template §5: "subsequent increments are separate PRs that reference the merged design") and is filed as a sibling task at design-doc-merge time.

**Phase 4 — operator step (post-merge).** Toni reviews the bootstrap behaviour on dev, confirms the "DiVoid" org appears with every existing user as a member, confirms list / get / delete are scoped by org on auth-enabled fixtures. No data migration needed; column defaults + the bootstrap block do all the work at first boot.

## 13. Open Questions

None blocking. Two items the user may want to revisit *after* deploy, neither blocking:

1. **Org-level admin role** — a future feature, named as out-of-scope in §2. Surfaces when a non-admin org member needs to add or remove fellow members without involving Toni.
2. **API-key snapshot refresh mechanism** — short-TTL re-snapshot or explicit "rotate this key" tooling. Surfaces if users start changing org membership often.

The bootstrap default (single "DiVoid" org with every user as a member) was a design call the orchestrator asked the architect to make; the design makes it per §7. No open question.

## 14. Implementation Guidance for the Next Agent

The first PR (this branch) ships:

### M1 — Entities and schema

1. Create `Backend/Models/Organizations/Organization.cs` — entity per §7, with a `public const long BootstrapOrgIdConst = 1` defined alongside.
2. Create `Backend/Models/Organizations/UserOrganization.cs` — composite-key join entity per §7.
3. Edit `Backend/Models/Nodes/Node.cs` — add `OrganizationId` (`long`, `[Index("organization")]`, `[DefaultValue(1L)]`, `[AllowPatch]`).
4. Edit `Backend/Models/Auth/ApiKey.cs` — add `OrganizationIds` (`string`, `[JsonColumn]`, `[AllowPatch]`).
5. Edit `Backend/Init/DatabaseModelService.cs`:
   - Register `Organization` and `UserOrganization` for schema reconciliation (after `User`, before `Node` for readability).
   - Append the bootstrap block per §7 (`COUNT(*) == 0` → seed "DiVoid" org id 1 + bulk-insert membership for every existing user). Inside the existing transaction; before commit.

### M2 — DTO + mapper + filter for Organization

1. Create `Backend/Models/Organizations/OrganizationDetails.cs` — DTO with `Id`, `Name`, `OwnerId`, `Created`, `LastUpdate`. `Members: long[]` is **NOT** on the list-mode DTO (cardinality discipline); add it only to the single-get response shape.
2. Create `Backend/Models/Organizations/OrganizationMapper.cs` — `FieldMapper<OrganizationDetails, Organization>`. Standard pattern. `DefaultListFields = ["id", "name", "ownerId", "created", "lastupdate"]`.
3. Create `Backend/Models/Organizations/OrganizationFilter.cs` — `ListFilter` with `Id`, `Name` (wildcard-aware via `ContainsWildcards`).

### M3 — Service for Organization

1. Create `Backend/Services/Organizations/IOrganizationService.cs` + `OrganizationService.cs`. Methods:
   - `CreateOrganization(OrganizationDetails, long callerId)`.
   - `GetOrganizationById(long id, long callerId, long[] accessibleOrgs, bool isAdmin)`.
   - `ListPaged(OrganizationFilter, long callerId, long[] accessibleOrgs, bool isAdmin, CancellationToken)`.
   - `Patch(long id, PatchOperation[], long callerId, long[] accessibleOrgs, bool isAdmin, CancellationToken)`.
   - `Delete(long id, long callerId, bool isAdmin)` — refuses if any node references the org.
   - `AddMember(long orgId, long userId)` — admin-only; idempotent.
   - `RemoveMember(long orgId, long userId)` — admin-only; idempotent.
   - `ListMembers(long orgId, long[] accessibleOrgs, bool isAdmin)` — member or admin only.
   - `GetUserOrganizationIds(long userId)` — the helper read by `KeycloakClaimsTransformation` and used at API-key mint time. Returns `long[]`.
2. Register in `Startup.ConfigureServices` as `services.AddTransient<IOrganizationService, OrganizationService>()`.

### M4 — Org authorization helper

1. Create `Backend/Services/Nodes/OrganizationAuthorization.cs`. Two methods per §8 (`BuildOrgVisibilityPredicate`, `IsOrgAccessible`). Internal/static — sibling shape to `NodeAuthorization`.

### M5 — Claim emission + caller resolution

1. Edit `Backend/Auth/ApiKeyAuthenticationHandler.cs`: after building the existing identity, read `details.OrganizationIds` (JSON-decode → CSV-encode), emit `divoid.organization_ids` claim. Empty list → empty string `""`.
2. Edit `Backend/Auth/KeycloakClaimsTransformation.cs`: after the user-row check, call `IOrganizationService.GetUserOrganizationIds(user.Id)` (inject the service), CSV-encode, emit the claim.
3. Edit `Backend/Auth/ClaimsExtensions.cs`: add `OrganizationIdsClaimType = "divoid.organization_ids"` and a `GetAccessibleOrgs(this ClaimsPrincipal) → long[]?` helper (`null` = absent claim; empty array = empty string claim).
4. Edit `Backend/Controllers/V1/NodeController.cs`: extend `ResolveCaller()` return type to `(long callerId, bool isAdmin, long[]? accessibleOrgs)`; thread through every service call.
5. Edit `Backend/Services/Auth/ApiKeyService.CreateApiKey`: at mint time, call `IOrganizationService.GetUserOrganizationIds(apiKey.UserId.Value)`, JSON-encode, set `ApiKey.OrganizationIds`.

### M6 — Thread org through Node service entries

The first PR threads the parameter through every method (mechanical) but only applies the gate in `ListPaged`, `GetNodeById`, `Delete`, `GenerateFilter`, and `CreateNode`. Other methods (`Patch`, `UploadContent`, `GetNodeData`, `LinkNodes`, `UnlinkNodes`, `ListPagedByPath`, `ListLinks`) accept the parameter but defer the gate composition to the follow-up PR — the parameter passes through unused for now. This keeps signatures stable across both PRs.

Rationale for the partial split: the gate composition is mechanical (one `&=` line per WHERE), but each composition is also a place where the test surface needs coverage. The first PR proves the architecture end-to-end on one read path + one write path + the list path + create — that is enough to validate the design. The follow-up extends to every remaining surface and writes the full HTTP-test matrix.

### M7 — Organization controller

1. Create `Backend/Controllers/V1/OrganizationController.cs` per the contract in §8. `[Route("api/organizations")]`, `[ApiController]`. Standard `[Authorize(Policy=...)]` per method.

### M8 — Smoke tests for the first increment

1. Verify auth-disabled posture is unchanged: existing tests pass (no test rewrites needed; `accessibleOrgs = null` short-circuits the predicate).
2. Add one smoke HTTP test (`OrganizationBootstrapHttpTests.cs` or extend `NodeAccessHttpTests` with an `[Auth-enabled] cross-org cannot see` group):
   - Two users in two different orgs.
   - User-A creates a node; the node gets Org-A.
   - User-B (different org, non-admin) lists nodes — does NOT see User-A's node.
   - User-B `GET /api/nodes/{user-A-node-id}` → 404.
   - Admin sees both nodes.
3. One round-trip test for the bootstrap: fresh in-memory SQLite, no orgs at DB start → after `DatabaseModelService.StartAsync`, exactly one row in `organization` named "DiVoid" with id 1.

The full HTTP-test matrix for the follow-up PR (cross-org PATCH, content GET/POST, link transition, link create across orgs, member add/remove idempotency, etc.) is enumerated above implicitly but does not need verbatim listing here — the implementer follows the §6 contract.

### M9 — Docs

1. Append a brief "Organization layer" section to `docs/architecture/auth-and-bootstrap.md` (2-4 lines, pointing to this design doc).
2. No external API doc surface change required — claim addition is internal; new endpoints follow the existing OpenAPI shape.

### Follow-up PR (named in PR body, filed as DiVoid task at PR-open time)

Sibling task scope:
- M6 remaining surfaces (`Patch`, `UploadContent`, `GetNodeData`, `LinkNodes`, `UnlinkNodes`, `ListPagedByPath`, `ListLinks`).
- `ListLinks` visibility filtering per §6 cross-org link transition.
- `OrganizationController` member-add / remove endpoints (the org CRUD itself ships in the first PR per M7; member endpoints are the natural pair that ship with `AddMember` / `RemoveMember` exposure).
- Full auth-enabled HTTP-test matrix (the §14 M8 smoke is enough for the first PR; the matrix is exhaustive coverage).

### Pre-PR self-check (Code Contracts §16 + §6.10 implementer audit per template #1220)

- [ ] One public type per file — every new file declares exactly one top-level type. The base-name carve-out (§1) applies only to generic siblings; nothing here is generic.
- [ ] `[DefaultValue]` on every new non-null value-type column (`Node.OrganizationId`, the bootstrap org row's timestamps).
- [ ] `[AllowPatch]` on the class AND on each patchable property (per Code Contracts §5.2).
- [ ] No body `//` comments in new C# files (per Code Contracts §4 and template §6.10 grep). The grep `git diff origin/main...HEAD -- '*.cs' | grep -cE "^\+\s*//[^/]"` must be 0.
- [ ] XML `<summary>` on every new public type / method / property — 1–2 content lines per Code Contracts §3 / template §6.10 ceiling check.
- [ ] No `var` (explicit types throughout).
- [ ] No `private` modifier on fields.
- [ ] Domain-object return types on new controllers (no `IActionResult` wrapping).
- [ ] No `try`/`catch` in controllers.
- [ ] Existing `NotFoundException<T>` / `ArgumentException` used; no new exception types invented.
- [ ] Tests `[TestFixture, Parallelizable]` + `[Test, Parallelizable]`, `Assert.That(value, Is.EqualTo(...))`.

---

*End of design document.*
