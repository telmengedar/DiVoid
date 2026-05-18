# Architectural Document: Agent User-Id Lookup for Non-Admin Callers (task #478)

## 1. Problem Statement

The Hivemind messaging system (DiVoid #435) requires `recipientId` on `POST /api/messages` to be an **auth user-id** (column `divoid_user.id`). Agents walking the graph to identify a recipient end with an **agent node-id** (`Node.Id` where `Type="agent"`). There is currently no non-admin way to bridge agent-node-id → user-id: `GET /api/users` and `GET /api/users/{id}` are both `[Authorize(Policy = "admin")]` (`UserController.cs:64, 74`), and walking the graph (`GET /api/nodes`, `?linkedto=`) cannot cross into the `divoid_user` table — `divoid_user` is not a participant in `NodeLink`. The routing step "identify the main agent's user-id" therefore stalls and the agent falls back to asking the human out-of-band — the exact friction the messaging system exists to remove.

Success criterion: a non-admin agent holding a `read` permission can resolve an agent node-id to a user-id in a single GET call, with no broadening of `/api/users`' admin envelope.

## 2. Scope & Non-Scope

**In scope.** A single read-only resolver endpoint that returns, for a given agent node-id, the user-id of the auth principal whose `HomeNodeId` equals that node-id. Non-admin readable (policy: `read`). Error semantics aligned with the codebase's existing `NotFoundException<T>` pattern.

**Out of scope.** Listing all agents; reverse lookup (user-id → agent-node-id); name resolution; widening `/api/users` or `/api/users/{id}`; any change to the messaging endpoint; any schema migration; bulk lookup; caching layer; any frontend wiring.

## 3. Assumptions & Constraints

- **The bridge already exists in data.** `User.HomeNodeId` (nullable `long`, `User.cs:60`) was introduced as a "frontend hint" (per its own XML doc) but is in fact the *only* persistent link from a user record to a graph node. The live data confirms this: user id 2 ("Selene (Claude)") carries `homeNodeId: 11`, matching agent node #11 "Selene (Claude Code)". The convention "agent.user.HomeNodeId == agent.node.Id" is already established for the only two agent identities currently authenticated (Selene, and by extension the Tiger Moans/Timon mapping that motivated #478). Treating `HomeNodeId` as the canonical bridge is therefore consistent with reality, not a new invention.
- **No schema work.** All three task-doc shapes are achievable purely through query composition over existing tables.
- **No agent-type enforcement at the API layer.** The endpoint returns the user-id for *any* node-id with a matching `HomeNodeId`, not just `Type="agent"`. The "agents only" filter the task description suggested would require a `NodeType` join and adds nothing useful: a user whose `HomeNodeId` points at a non-agent node is rare-or-nonexistent, and filtering it out would silently produce a misleading `404` (the data exists, the filter hid it). Cleaner to return whatever exists.
- **`HomeNodeId` is patchable by every user on their own record** (`UserController.cs:85-91`, `PatchMe`), so an agent can self-establish or update this binding without admin help. That makes the resolver a stable contract: when a new agent identity is provisioned and patches its own `HomeNodeId`, the lookup starts working with no operator intervention.
- **Permission policy `read` is the right gate.** Every consumer of this lookup is also a consumer of `POST /api/messages` (which requires `write`). `write` implies `read` (`PermissionAuthorizationHandler.cs:20-22`), so gating on `read` is the most permissive valid choice and matches the rest of the listing/read surface (`NodeController.cs:49, 68, 88, 102` are all `read`).
- **JsonStreamOutputFormatter is not relevant here.** It only kicks in for `IResponseWriter` returns (`Startup.cs:58`, formatter at index 0). This is a single-object GET; standard formatter pipeline applies.

## 4. Architectural Overview — Recommendation

**Recommend option 2 (resolver endpoint on the node), in the form `GET /api/nodes/{nodeId:long}/user`.**

Pick on existing-pattern grounds, not aesthetics:

| Candidate | Fit verdict |
|---|---|
| **1. `GET /api/agents` (list)** | Reject. Forces an in-process or join-side filter to "only users whose `HomeNodeId` is non-null AND points at an `agent`-typed node," which is two concepts entangled in one endpoint. The codebase has no `agent` concept anywhere (`Models/`, `Services/`, `Controllers/`) — every existing listing endpoint is over either entity tables (users, keys, nodes, messages) or link adjacency (`/api/nodes/links`). Introducing `/api/agents` invents a new conceptual surface to solve a single-key lookup; that is the over-production smell in **CODE-CONTRACTS §3** (cited verbatim in agent memory: "list-materializing streams, in-process filtering"). Also leaks more than needed — the caller asked for *a user-id*, not for *all agents*. |
| **2. `GET /api/nodes/{nodeId}/user` (single-key resolver)** | **Accept.** Mirrors the existing nested-resource shape of `NodeController` exactly: `/api/nodes/{nodeId}/content` (`NodeController.cs:101`), `/api/nodes/{sourceNodeId}/links` (`NodeController.cs:159`). Returns one row by single-column lookup on the indexed-by-pk `divoid_user` table. Lives under the controller whose policy gate (`read`) is already correct, alongside other `read`-scoped node reads. The lookup composes into a single SQL statement via the existing Ocelot `database.Load<User>().Where(u => u.HomeNodeId == nodeId)` pattern — same shape used in `UserService.GetUserById` (`UserService.cs:43-49`). |
| **3. Embed `userId` on `GET /api/nodes/{id}` payload** | Reject. `NodeDetails` is the wire shape for *every* node read — list endpoints, `GetById`, path queries. Adding `userId` to it widens the projection on every list call (mappers materialise their full mapping set in the query when the field is registered; see `NodeMapper.cs:39-80`). To avoid that, you would need a conditional FieldMapping (added only when caller passes `?fields=userId`), which doubles the surface area and gives the field a different mapping-shape than every other property. It also creates an awkward semantic: `userId` is non-null only for a tiny fraction of nodes, so a default-projection response would carry a near-uniformly-null field. The codebase already segregates this pattern: `Similarity` (`NodeDetails.cs:39`) is opt-in only because it requires `?query=` to register the mapping, and that conditional-mapping path exists precisely because the field is exceptional. Adding a second exceptional field doubles the precedent without earning it. |

Option 2 also gives the cleanest 404 semantics for "no link exists yet," which is the realistic high-frequency case for newly-provisioned agent nodes.

## 5. Components & Responsibilities

The change touches three files and adds exactly one method per layer. Every other concern (auth, error mapping, response shape, JSON serialisation) is already wired and reused.

| Component | Lives in | Adds | Does NOT own |
|---|---|---|---|
| `NodeController` | `Backend/Controllers/V1/NodeController.cs` | One `[HttpGet("{nodeId:long}/user")]` action, `[Authorize(Policy = "read")]`, returning `UserIdResponse`. | Permission semantics (delegated to policy handler); existence of the user/node (delegated to service); JSON shape (delegated to formatter pipeline). |
| `INodeService` / `NodeService` | `Backend/Services/Nodes/INodeService.cs`, `NodeService.cs` | One `Task<long> GetUserIdForNode(long nodeId)` method that loads exactly one column from `divoid_user` filtered by `HomeNodeId == nodeId`, throws `NotFoundException<User>(nodeId)` when no row matches. | Anything user-record-related — does not project a full `UserDetails`, does not load `Permissions`, does not load `Email`. Owns *only* the id resolution. |
| `UserIdResponse` DTO | `Backend/Models/Users/UserIdResponse.cs` (new) | A trivial record-shaped DTO carrying a single `long UserId` field. | Anything else. No name, no permissions, no email, no createdAt. |

The service method belongs on `INodeService` rather than `IUserService` because: (a) the route lives under `/api/nodes/{nodeId}/user` so the controller dependency is already `INodeService`; (b) the input is a node-id and the operation is conceptually "given this node, what auth identity claims it"; (c) `IUserService` would have to acquire a node-id concept it currently has zero knowledge of, contaminating its bounded responsibility.

## 6. Interactions & Data Flow

Synchronous, single HTTP request, single SQL statement.

```
client                 NodeController               NodeService                  IEntityManager (Ocelot)            divoid_user
  |  GET /api/nodes/395/user
  |  Authorization: Bearer ...
  +--->|
       | DiVoidBearer scheme dispatches → ApiKey or JwtBearer
       | KeycloakClaimsTransformation populates permissions
       | PermissionRequirement("read") satisfied (read|write|admin)
       +--->|
            | GetUserIdForNode(395)
            +--->|
                 | Load<User>(u => u.Id).Where(u => u.HomeNodeId == 395).ExecuteScalarAsync<long?>()
                 +--->|
                      | SELECT id FROM divoid_user WHERE homenodeid = 395 LIMIT 1
                      +--->|
                      |<---|  (id) or null
                 |<---|
            | if null → throw NotFoundException<User>(nodeId 395) → ErrorHandlerMiddleware → 404 {code,text}
            | else    → return UserIdResponse { UserId = id }
       |<---|
  |<---|  200 {"userId": 2}
```

No async fan-out, no transaction, no list materialisation, no second round-trip for total count. The query reads one indexed scalar.

## 7. Data Model (Conceptual)

No data-model change. The conceptual link the endpoint surfaces is already present:

```
   divoid_user                       node (graph)
   -----------                       ------------
   id ──────── (auth identity)       id ──────── (graph identity)
   name                              type        ("agent", "person", ...)
   ...                               name
   home_node_id  ────────────────────►  (id of agent-or-person node)
```

`HomeNodeId` is nullable — most user records will not point at a node. The endpoint surfaces only the populated rows; unpopulated rows produce `404`.

**Cardinality assumption.** The relation is treated as 1:1 for the purposes of this endpoint (one user record points at one agent node; one agent node is pointed at by at most one user record). The schema does not enforce this — `HomeNodeId` has no unique index, so in principle two user records could share the same `HomeNodeId`. If that ever happens, the resolver picks one arbitrarily (`LIMIT 1` semantics from `ExecuteScalarAsync`). Surfacing that as `409 Conflict` would be overkill given that the data is admin-controlled and the contention case has never been observed. See open question #1.

## 8. Contracts & Interfaces (Abstract)

### Wire contract

| Aspect | Specification |
|---|---|
| Route | `GET /api/nodes/{nodeId:long}/user` |
| Auth | `[Authorize(Policy = "read")]`. Standard DiVoidBearer scheme (JWT or API key). |
| Path parameter | `nodeId` — any positive `long`; not restricted to agent-typed nodes (see §3). |
| Query parameters | None. |
| Request body | None. |
| Response (200) | `application/json; { "userId": <long> }` — single field, server-assigned, never null. |
| Response (401) | Canonical `{ code: "authentication_*", text: "..." }` body emitted by `ErrorHandlerMiddleware` from `AuthenticationFailedException`. |
| Response (403) | Canonical `{ code: "authorization_missingscope", text: "Caller lacks required permission 'read'" }`. |
| Response (404) | Canonical `NotFoundException<User>(nodeId)` body. Returned when (a) no `divoid_user` row has `HomeNodeId == nodeId`, OR (b) `nodeId` itself does not exist as a `Node`. The endpoint does not differentiate — existence of the node is not relevant; existence of the *binding* is. This collapses two cases into one 404 and prevents the endpoint from being used to probe node existence (a non-admin information-leak surface). |

### Service contract

`Task<long> INodeService.GetUserIdForNode(long nodeId)`:

- **Input:** `nodeId` — any non-negative `long`. No validation beyond "the database accepts it"; the SQL will simply return no row for an out-of-range or non-existent id.
- **Output:** the `divoid_user.id` of the row whose `HomeNodeId` equals `nodeId`.
- **Throws:** `NotFoundException<User>` when no row matches. Maps to HTTP 404 via the existing error-handler chain (`Startup.cs:81`, plus the `Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException<T>` handler bundled in `AddErrorHandlers()`).
- **Invariants:** read-only; idempotent; no side effects; no transaction; no logging beyond the controller-level `LogInformation` (per `MessageController.cs:35` precedent — one structured event line per request, no PII).

### DTO contract — `UserIdResponse`

A new file in `Backend/Models/Users/UserIdResponse.cs`:

- One public field: `long UserId`.
- No `[AllowPatch]` (read-only response). No write path.
- Lives in `Backend.Models.Users` namespace (it is a user-shaped fact, even though the resolver lives on the node controller).
- Does NOT carry the node-id, the user's name, email, permissions, createdAt, or any other field. Single-purpose response by deliberate choice — if a caller needs more they call `/api/users/me` (their own) or admin endpoints (someone else's).

The DTO is intentionally not a record. The codebase consistently uses `public class` with `{ get; set; }` for wire DTOs (see `MessageDetails`, `UserDetails`, `NodeDetails`, `ApiKeyDetails`) and the JSON pipeline is configured around that idiom. Records would be a deviation for the sake of one new file; reject.

## 9. Cross-Cutting Concerns

- **Authentication.** Unchanged — caller authenticates via the existing `DiVoidBearer` policy scheme; both API-key and JWT paths flow through the same `[Authorize(Policy = "read")]` gate. `KeycloakClaimsTransformation` continues to populate the `permission` claims used by `PermissionAuthorizationHandler`.
- **Authorization.** `read` policy. Per `PermissionAuthorizationHandler.cs:20-22`, this is satisfied by `read`, `write`, or `admin` — the broadest valid policy, matching every other read-side endpoint in `NodeController`.
- **Information disclosure.** Returning a user-id when the binding exists is intentional and not a leak: the user-id is also visible on every `Message` row the caller can already read, on the caller's own `/api/users/me`, and on any `ApiKey` row admin endpoints surface. The collapsed 404 (no-node OR no-binding → same response) prevents the endpoint from being used to enumerate node existence beyond what `GET /api/nodes/{id}` already exposes (which is `read`-gated and returns 404 the same way).
- **Observability.** One structured log line at the controller entry, shape `event=node.user.lookup nodeId={NodeId} callerId={CallerId}`. No log line on the 404 path — `ErrorHandlerMiddleware` already emits the canonical error log. No telemetry on the user-id returned (it is not PII beyond what calls already disclose).
- **Error handling.** Single failure mode: no matching row. Mapped via `NotFoundException<User>` → 404. No retries, no fallbacks; the resolver is read-only and idempotent so caller retry on transport errors is safe and not the endpoint's concern.
- **Caching.** None at this layer. The data changes on `PATCH /api/users/me {homeNodeId}`; a cache layer would introduce TTL/invalidation overhead with no measured benefit. The `divoid_user` table is small and `HomeNodeId` should be indexed if it is not already (see §12 risk).
- **Concurrency / idempotency.** Read-only, no transaction. Multiple concurrent calls always return the same answer for the same input (subject to the rare PATCH-during-lookup race, which a caller cannot distinguish from "the value just changed" anyway).

## 10. Quality Attributes & Trade-offs

- **Scalability.** Indexed point lookup on a small table; capacity is dictated by the underlying DB connection pool, not this endpoint.
- **Performance.** Single statement, single round-trip. Latency is dominated by the auth pipeline, not the query.
- **Maintainability.** Smallest possible surface: one controller action, one service method, one DTO file. Mirrors the existing nested-resource pattern (`/{nodeId}/content`, `/{nodeId}/links`) so future readers find it where they expect.
- **Trade-offs deliberately accepted.**
  - *No `Type="agent"` enforcement* — keeps the endpoint generic and avoids a `NodeType` join. Cost: a caller might resolve a non-agent node and get a user-id back. Mitigation: in practice, nothing except an agent's user record carries `HomeNodeId`, and even if a human user did, the lookup is still semantically correct (the user record that claims this node).
  - *Collapsed 404* — node-exists-but-no-binding and node-does-not-exist return the same response. Cost: caller cannot distinguish "I have the wrong node-id" from "the agent has not bound itself yet." Mitigation: same caller can `GET /api/nodes/{id}` to disambiguate if they need to.
  - *No bulk variant* — `?nodeId=1,2,3` is plausibly useful but speculative. Cost: a frontend wanting many lookups must fan out. Mitigation: revisit if and when a real caller fans out >5 lookups per page.
- **Rejected alternatives** (also see §4 table). Embedding `userId` on `NodeDetails` (option 3) loses on three counts simultaneously: conditional-mapping mechanics, polluted default projection, and a near-uniformly-null field on every list response. A bulk listing endpoint (option 1) loses on conceptual-surface invention and over-production smell.

## 11. Risks & Mitigations

| Risk | Likelihood | Severity | Mitigation |
|---|---|---|---|
| `HomeNodeId` has no DB index → table scan as `divoid_user` grows. | Medium (`User.cs:60` declares no `[Index]`). | Low (table is tiny in absolute terms). | Add `[Index("homenodeid")]` to `User.HomeNodeId` in the same PR. `DatabaseModelService` will apply on next startup via `SchemaService.CreateOrUpdateSchema<User>` (`DatabaseModelService.cs:34`). |
| Two user records share the same `HomeNodeId`. | Very low (no current writes do this; `PATCH /me` is per-caller). | Low (resolver returns one arbitrarily). | Document the 1:1 assumption in the service method's XML doc; surface as open question #1 for John to decide whether to add a unique index. Recommend: don't, until observed. |
| Caller misuses the endpoint to probe node existence. | Low. | Negligible (the `GET /api/nodes/{id}` endpoint already discloses node existence to the same `read` scope). | Collapsed 404 (§8) eliminates this anyway. |
| Future schema work introduces a richer node↔user link (e.g. dedicated link type) that obsoletes `HomeNodeId` as the bridge. | Medium over 6+ months. | Low — the service method is the only place that touches the bridge; the wire contract is unchanged regardless of the underlying join. | Keep the bridge mechanic confined to `NodeService.GetUserIdForNode`. When a richer link arrives, update that one method. |
| Anyone with `read` can resolve every agent's user-id and address messages to them. | High by design — this is the goal. | None — `recipientId` was already meant to be discoverable; the gap was the discovery mechanism. | Documented in §9 (information disclosure). |

## 12. Migration / Rollout Strategy

Not applicable in the traditional sense — this is a new endpoint with no callers to migrate. Two notes for John:

1. Apply the change behind the existing `Auth:Enabled` matrix. Both branches in `Startup.cs:116-229` already define `read` correctly; no startup change required.
2. After deploy, on the dev instance, verify: `curl -H "Authorization: Bearer <token>" $DIVOID_URL/nodes/11/user` returns `{"userId":2}`. If it returns 404, the agent's user record has no `HomeNodeId` set — fix the **data**, not the **code**.

## 13. Open Questions

1. **Unique index on `User.HomeNodeId`?** Currently absent. Adding it makes the 1:1 assumption a hard invariant; not adding it leaves the resolver's "arbitrary pick on collision" behaviour latent. **Recommendation: don't add until observed.** Either way is acceptable; the wire contract is identical.
2. **`[Index("homenodeid")]` on `User.HomeNodeId`?** Recommended for the same PR. Cheap, future-proofs the query as `divoid_user` grows.
3. **Future reverse-lookup (`GET /api/users/{userId}/node`)?** Out of scope for #478. Worth a follow-up task if a frontend ever needs it.

## 14. Implementation Guidance for the Next Agent

Ordered phases for John:

1. **Wire the contract.**
   - Add `Backend/Models/Users/UserIdResponse.cs` with a single `long UserId` property and XML docs covering the "no binding → 404" contract.
   - Add `Task<long> GetUserIdForNode(long nodeId)` to `INodeService` and `NodeService`. Implementation: one `database.Load<User>(u => u.Id).Where(u => u.HomeNodeId == nodeId).ExecuteScalarAsync<long?>()` followed by `if (result == null) throw new NotFoundException<User>(nodeId); return result.Value;`. **Do not** wrap in a transaction. **Do not** load extra columns. **Do not** materialise an `IAsyncEnumerable`.
   - Add `[HttpGet("{nodeId:long}/user")] [Authorize(Policy = "read")] public Task<UserIdResponse> GetUser(long nodeId)` action to `NodeController`. One `LogInformation` line at entry, then delegate; return `new UserIdResponse { UserId = await ... }`. **Do not** introduce a controller-level try/catch — the existing error-handler middleware maps `NotFoundException<User>` to 404 already.
2. **Index `HomeNodeId`.** Add `[Index("homenodeid")]` to `User.HomeNodeId` (`User.cs:60`). Mention in the PR body that schema updates apply on next startup via `DatabaseModelService` (`SchemaService.CreateOrUpdateSchema<User>`, `DatabaseModelService.cs:34`).
3. **Write the load-bearing tests (§15 below).** Both T+ and T- must be captured in the PR description with the mental-substitution outcome — this is the DiVoid #275 doctrine and Jenny enforces it.
4. **PR body must include:** the route, the auth policy, the 404-collapse contract, the new index, and the negative-proof outcome of T1/T6. No code listing in the body — link to the diff.

Do NOT in this PR: change `UserController`; broaden `/api/users` or `/api/users/{id}`; add a list/bulk variant; touch `NodeDetails`; touch `NodeMapper`; touch `MessageController` or `MessageService`.

## 15. Test Cases John Must Write

Add to `Backend.tests/Tests/NodeUserLookupHttpTests.cs`, mirroring the fixture+helper layout of `MessageHttpTests.cs` (use `JwtAuthFixture`, `InsertUserAsync`, `ClientWithToken`).

### 15.1 Mandatory cases

| Id | Case | Setup | Assertion |
|---|---|---|---|
| **T1** (happy path) | Bound agent resolves to its user-id. | Insert user U with `HomeNodeId = N` where N is a created agent-typed node. Authenticate with U's API key. | `GET /api/nodes/{N}/user` → 200, body `{"userId": U.Id}`. |
| **T2** (no binding → 404) | Existing node with no user pointing at it returns 404. | Create a node M; assert no user has `HomeNodeId == M.Id`. | `GET /api/nodes/{M}/user` → 404 with canonical `NotFoundException<User>` body. |
| **T3** (collapsed 404, non-existent node) | A node-id that does not exist returns 404 with the same shape. | Pick an id known not to exist (e.g. `9999999`). | `GET /api/nodes/9999999/user` → 404 with the same body shape as T2. (Asserting equality of `code` between T2 and T3 is the strongest form of the contract.) |
| **T4** (auth required) | Unauthenticated request. | No `Authorization` header. | `GET /api/nodes/{N}/user` → 401 with canonical authentication-failure body. |
| **T5** (read sufficient) | Caller with **only** `read` permission succeeds. | Insert U with `Permissions=["read"]`, bind to node N. Authenticate as U. | `GET /api/nodes/{N}/user` → 200. (Proves `read` is the gate, not `write`.) |
| **T6 — load-bearing negative-proof (CRITICAL)** | The endpoint exists and is wired. | Use T1 setup. | `GET /api/nodes/{N}/user` → 200, body deserialises to `UserIdResponse` with `UserId == U.Id`. **Mental substitution:** remove the `[HttpGet("{nodeId:long}/user")]` action from `NodeController`. Re-run T6. **Expected:** 404 (route not found) or 405 — test fails with a concrete attributable error. Capture both outcomes in the PR description per DiVoid #275. |

### 15.2 Anti-pattern guard

Add one assertion in T1: deserialise the 200 body as a `JsonDocument` and assert it has exactly **one** top-level property (`userId`). This catches regression where someone later adds `name`, `email`, or other fields to `UserIdResponse` — a single-purpose-response contract is load-bearing for the privacy posture and must be defended by the test, not by reviewer goodwill.

## 16. Smells & Contracts to Avoid (CODE-CONTRACTS §3 — over-production)

Lifting the four smell-classes from agent memory (DiVoid #114 §3) and applying to this PR specifically:

| Smell | Concrete risk in this PR | Avoid by |
|---|---|---|
| **List-materializing streams.** | Resolver iterating `database.Load<User>().ExecuteEntitiesAsync()` and `await foreach`ing to find the match. | Use the single-row scalar form: `Load<User>(u => u.Id).Where(u => u.HomeNodeId == nodeId).ExecuteScalarAsync<long?>()`. One column, one row, one round-trip. |
| **In-process filtering.** | Loading all users and `Where`ing in C#. | Always filter at the DB layer. The predicate is trivial and indexed; in-process is a smell with no upside. |
| **Single-statement transactions.** | Wrapping the read in `using Transaction transaction = database.Transaction(); ... transaction.Commit();`. | Don't. Reads are not transactional in this codebase — see every read path in `UserService`, `NodeService.GetNodeById`, `MessageService.GetById`. Only writes that touch ≥2 tables use transactions (`NodeService.CreateNode`, `NodeService.Delete`, `MessageService` does not). |
| **Redundant async/Task wraps.** | `public async Task<long> GetUserIdForNode(long nodeId) { return await Foo(); }` when `Foo()` already returns `Task<long>`. | The method body has *one* await (the scalar load) and a null-check that throws. The `async` keyword is required to use `await`; that's fine. Don't introduce a second helper that wraps the first. Don't return `Task.FromResult` over already-awaited values. |

Additional contract reminders for the controller-side action:

- **No try/catch in the action.** Throw upward; the middleware maps it.
- **No `ProblemDetails` construction.** The canonical `{code,text}` shape comes from `Pooshit.AspNetCore.Services` error handlers (`Startup.cs:76`).
- **No async `Task<UserIdResponse> GetUser(...)` returning `Task.FromResult(new UserIdResponse { UserId = await ... })`** — that's the redundant-wrap pattern. Just `await` the service call and `return new UserIdResponse { UserId = id };`.
- **Single log line at entry**, structured (`event=node.user.lookup nodeId={NodeId} callerId={CallerId}`), at `LogInformation` level. Match `MessageController.cs:35`'s shape.

## 17. Scope Cuts — Explicitly NOT in This PR

- No change to `/api/users` or `/api/users/{id}` policies. They stay `admin`.
- No new fields on `User`, `UserDetails`, `Node`, or `NodeDetails`.
- No new `NodeType` value introduced; no agent-type filter at the endpoint.
- No bulk variant; no `?nodeId=1,2,3` array form.
- No name-resolution (`agentName -> userId` is a different feature; would need a `User.Name`-based path that the codebase deliberately avoids for collision reasons).
- No reverse lookup (`userId -> nodeId`).
- No caching, no rate limiting, no metrics counter (the existing log line is sufficient observability for the expected call volume).
- No frontend touch; no Swagger/OpenAPI doc rewrite beyond the XML comments that the existing pipeline picks up automatically.
- No migration script — `[Index("homenodeid")]` is applied at startup by `DatabaseModelService`.
