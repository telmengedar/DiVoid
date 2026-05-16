# Architectural Document: In-Database Messaging System

> DiVoid task **#425**. Source for downstream implementation by `john-backend-dev`. The doc-commit on branch `docs/messaging-architecture` is intended to be folded into the implementation PR, not opened as a standalone PR.

## 1. Problem Statement

DiVoid needs a lightweight, in-database way for users — humans and agents — to leave short notes for each other. The driving use case is a poll-based **agent inbox**: a later update to the Hivemind Protocol (DiVoid node 190) will instruct every agent to query its inbox at task start (and/or end), act on any messages found, and delete them once handled. Messages are not a chat system, not a notification system, and not an audit trail — they are short, addressed, one-way drops that exist only until the recipient processes them.

Success criteria:

- The recipient's inbox query (`list messages where recipient = me`) is cheap and indexed — agents will hit this frequently.
- The wire shape and authorization rules are unambiguous so that agents and the future frontend talk to the same contract.
- The endpoint surface is small (four endpoints), matches the existing controller idioms (`NodeController`, `UserController`), and earns no review pushback from `jenny-qa-reviewer` against DiVoid node 114 (backend code contracts).

## 2. Scope & Non-Scope

**In scope**

- A new `Message` entity persisted via Pooshit.Ocelot, registered in `DatabaseModelService`.
- `MessageDetails` DTO + `MessageMapper` translation layer following the existing two-class pattern.
- `MessageFilter : ListFilter` with paging, sort, and recipient/author filters.
- `IMessageService` + `MessageService` implementing four operations: create, get-by-id, list (with permission scoping), delete.
- `MessageController` at `api/messages` with `POST`, `GET`, `GET/{id}`, `DELETE/{id}`.
- Permission scoping: messages are visible only to the sender, the recipient, or an `admin` caller. Deletion is allowed only to the recipient or to an `admin` caller (sender cannot recall).
- Test coverage at the HTTP integration level using the existing `JwtAuthFixture` pattern.

**Out of scope (and stays out)**

- Threading, replies, conversation views.
- Editing a message after send. Messages are **immutable** — no `PATCH`, no `[AllowPatch]` properties on the entity.
- Read receipts, unread counters, read/unread state.
- Broadcast / multi-recipient / group sends.
- Push, websockets, server-sent events, real-time delivery. Delivery is **poll-only**.
- Attachments. Body is Markdown text only; if attachments are ever needed they will be a separate file-storage feature.
- Archive / soft-delete / restore. Delete is hard delete; once gone, gone.
- A frontend UI. The backend exposes everything a future UI would need; the UI itself is a separate task.
- Rate limiting, abuse prevention, content moderation.

## 3. Assumptions & Constraints

- The Pooshit.Ocelot persistence stack continues to be the canonical data layer. No EF Core. Schema is built by `Backend/Init/DatabaseModelService.cs` at startup via `SchemaService.CreateOrUpdateSchema<T>`; there is no migrations folder. (Already established in `CLAUDE.md`.)
- Both database backends remain supported: SQLite for dev / tests, PostgreSQL for the online instance. Nothing in this design relies on Postgres-specific features (no embeddings, no `pgvector`, no `embedding()` function — messages have no semantic-search angle).
- Authentication is the existing dual-scheme arrangement (`DiVoidBearer` policy scheme dispatching to `JwtBearer` or `ApiKey`). The authenticated principal's DiVoid user id is available via `ClaimsPrincipal.GetDivoidUserId()` (see `Backend/Auth/ClaimsExtensions.cs`).
- Permissions follow the existing `admin → write → read` implication chain handled by `PermissionAuthorizationHandler`. There is no new permission to introduce.
- `<Nullable>disable</Nullable>` applies in `Backend.csproj`. Reference types in entity and DTO classes carry no `?`. Value-type optionals like `DateTime?` are still expressed with `?`.
- `Backend/Models/Auth/ApiKey.cs` and `Backend/Models/Users/User.cs` use plain `DateTime` (UTC, set via `DateTime.UtcNow`); this design follows suit. `DateTimeOffset` would be a one-off in this codebase and is rejected on consistency grounds — see section 11.
- The architecture document is committed alongside the implementation in a single PR; this doc-commit is the design baseline John implements against.

## 4. Architectural Overview

```
                  ┌─────────────────────┐
                  │ MessageController   │   api/messages
                  │ (Authorize "write") │   ── POST  create
                  │ (Authorize "read")  │   ── GET   list (scoped)
                  │ (Authorize "read")  │   ── GET   /{id}
                  │ (Authorize "write") │   ── DELETE/{id}
                  └──────────┬──────────┘
                             │
                             ▼
                  ┌─────────────────────┐
                  │ IMessageService     │
                  │  ── Create          │
                  │  ── GetById         │
                  │  ── ListPaged       │
                  │  ── Delete          │
                  │                     │   uses authorId,
                  │  takes callerId +   │   recipientId, and isAdmin
                  │  isAdmin from       │   to compose predicates
                  │  controller         │
                  └──────────┬──────────┘
                             │
                             ▼
                  ┌─────────────────────┐
                  │ IEntityManager      │
                  │  (Pooshit.Ocelot)   │
                  └──────────┬──────────┘
                             │
                             ▼
                  ┌─────────────────────┐
                  │ Message (table)     │
                  │  PK Id              │
                  │  AuthorId           │   idx "author"
                  │  RecipientId        │   idx "recipient" (hot path)
                  │  Subject (≤256)     │
                  │  Body (text)        │
                  │  CreatedAt          │   idx "recipient" (composite tail)
                  └─────────────────────┘
```

The shape is the same as `User`/`UserDetails`/`UserMapper`/`UserService`/`UserController` — see those files as the closest precedent (single-entity CRUD with no graph joins). `NodeController` is the richer precedent with filter, paging, and patch; messages reuse its filter/paging idioms but skip the patch path entirely.

Permission scoping is enforced **in the service layer** by composing predicates from the caller identity, not at the controller. The controller's job is limited to: extract `(callerId, isAdmin)`, pass them to the service, return the result.

## 5. Components & Responsibilities

### 5.1 `Message` (DB entity) — `Backend/Models/Messages/Message.cs`

Owns: the persisted shape of one message and the indices used by the inbox query.

Does **not** own: any patch surface (no `[AllowPatch]`), any link to nodes, any FK constraint (the Pooshit.Ocelot style in this repo expresses references by `long` id without DB-level FKs — see `NodeLink.SourceId/TargetId`, `ApiKey.UserId`, `User.HomeNodeId`).

### 5.2 `MessageDetails` (API DTO) — `Backend/Models/Messages/MessageDetails.cs`

Owns: the wire shape returned by the API. No additions beyond the entity fields. No write-only fields (unlike `NodeDetails.Links`).

Does **not** own: persistence concerns or mapping logic.

### 5.3 `MessageMapper : FieldMapper<MessageDetails, Message>` — `Backend/Models/Messages/MessageMapper.cs`

Owns: the field-name → DB-column translation, the default field projection used when the caller doesn't supply `?fields=`, and the registered sort keys.

Does **not** own: any join logic. The mapper deliberately does **not** join `User` for author/recipient names. See section 9 for the reasoning.

### 5.4 `MessageFilter : ListFilter` — `Backend/Models/Messages/MessageFilter.cs`

Owns: the query-string surface that callers can apply on the list endpoint — paging, sort, and the two id-based filters (`recipientId`, `authorId`).

Does **not** own: permission scoping. Even if the caller sets `recipientId=42` the service still ANDs the principal-scoping predicate on top.

### 5.5 `IMessageService` / `MessageService` — `Backend/Services/Messages/`

Owns: the four operations (create, get, list, delete), the permission-scoping predicate, the optimistic delete pattern, the `NotFoundException<Message>` mapping.

Does **not** own: extraction of caller identity from the HTTP principal — the controller passes those in as parameters. This mirrors `AuthService.GetWhoami(ClaimsPrincipal)` in shape, except the service takes the plain `(long callerId, bool isAdmin)` pair so it stays unit-testable without a fake `ClaimsPrincipal`.

### 5.6 `MessageController` — `Backend/Controllers/V1/MessageController.cs`

Owns: HTTP plumbing — routes, `[Authorize]` attributes, model-binding, write-side logging. Extracts `(callerId, isAdmin)` from `User` (`ControllerBase.User`) and forwards to the service.

Does **not** own: predicate composition, error handling, or business rules.

## 6. Interactions & Data Flow

### 6.1 Send a message — `POST /api/messages`

1. Controller is gated by `[Authorize(Policy = "write")]`. Auth pipeline resolves the principal.
2. Controller extracts `callerId = User.GetDivoidUserId()`.
3. Body bound to `MessageDetails` (caller supplies `recipientId`, `subject`, `body`).
4. Controller calls `messageService.Create(callerId, body)`. The service ignores any `authorId` on the inbound DTO and forces `AuthorId = callerId` server-side — clients cannot impersonate.
5. Service validates: recipient must exist (load `User` by id, throw `NotFoundException<User>` if absent), subject not empty, subject length ≤256, body not empty.
6. Service inserts with `CreatedAt = DateTime.UtcNow` and `ReturnID()`.
7. Service re-reads via `GetById(callerId, isAdmin: true, id)` to return the canonical persisted shape (matches `NodeService.CreateNode` and `UserService.CreateUser` round-trip pattern).
8. Controller returns `MessageDetails` directly (no `IActionResult` wrapping — see code-contracts §7.2).

### 6.2 List the recipient's inbox — `GET /api/messages?recipientId=me`

The hot path. Sequence:

1. `[Authorize(Policy = "read")]` gates entry.
2. Controller extracts `(callerId, isAdmin)` from `User`.
3. Controller binds `MessageFilter` from query string. The `ArrayParameterBinderProvider` at `ModelBinderProviders[0]` (`Startup.cs:99`) handles `?recipientId=1,2,3` shapes — same as `NodeFilter.Id`/`Type`/`Name`/`LinkedTo`.
4. Service composes the predicate:
   - **If `isAdmin`**: no principal-scoping clause; apply caller-supplied `recipientId` / `authorId` filters as-is.
   - **Otherwise**: AND a scoping clause: `(AuthorId == callerId OR RecipientId == callerId)`. Caller-supplied `recipientId` / `authorId` filters are AND-ed on top of that scope — they can only narrow within the caller's visible set, never widen it.
5. Service builds a `LoadOperation<Message>` with the mapper's standard join (in our case, no join — see section 9), applies the filter via `ApplyFilter(filter, mapper)` (clamps `count ≤ 500`, applies limit/offset, applies sort via mapper-resolved field).
6. Returns an `AsyncPageResponseWriter<MessageDetails>` that the `JsonStreamOutputFormatter` (registered at `OutputFormatters[0]` in `Startup.cs:57`) streams directly to the response body. The controller does **not** materialize the page.

### 6.3 Get a single message — `GET /api/messages/{id}`

1. `[Authorize(Policy = "read")]` gates entry.
2. Service loads the row by id. If absent → `NotFoundException<Message>`.
3. Service applies authorization: caller must be admin, the sender, or the recipient. If none → throw `AuthorizationFailedException` (mapped to 403 by `AuthorizationFailedExceptionHandler`).
4. Return `MessageDetails`.

A small architectural choice here: the "load then authorize" sequence reveals existence to a caller who has neither permission nor relation to the message (they get 403 rather than 404). That is acceptable for this feature — message ids are not enumerable per the schema (auto-increment) and the surface is internal-only at the org level. If existence-hiding ever matters, the service can switch to "load with `WHERE Id = @id AND (AuthorId = callerId OR RecipientId = callerId)`" and translate empty result to `NotFoundException<Message>`. Called out so future-John doesn't have to rediscover the choice.

### 6.4 Delete a message — `DELETE /api/messages/{id}`

1. `[Authorize(Policy = "write")]` gates entry.
2. Service composes the optimistic-delete predicate: `WHERE Id = @id AND (RecipientId = callerId OR isAdmin)`. Admin path is expressed by **omitting** the recipient clause server-side, not by `OR 1=1`.
3. `ExecuteAsync()` returns affected rows; if 0 → `NotFoundException<Message>(id)`. This collapses the "doesn't exist" and "exists but caller is not allowed to delete it" cases into a single 404, which is the established optimistic pattern used everywhere else in this codebase (`NodeService.Delete`, `UserService.DeleteUser`).
4. Controller returns void (HTTP 200 with empty body, same as other DiVoid delete endpoints).

**Sender-cannot-recall is deliberate.** The service does not allow `AuthorId = callerId` to delete — only `RecipientId = callerId` or `isAdmin`. If a sender mis-addresses a message the recipient can delete it; admin recovery covers genuine accidents. This keeps the semantic crisp and avoids "delivered/recalled/seen" state.

## 7. Data Model (Conceptual)

### 7.1 Entity fields and types

| Property | DB type | API type | Nullable | Default | Notes |
|---|---|---|---|---|---|
| `Id` | bigint, PK, autoinc | `long` | no | — | `[PrimaryKey, AutoIncrement]` |
| `AuthorId` | bigint | `long` | no | — | id of the sending `User`; no DB-level FK |
| `RecipientId` | bigint | `long` | no | — | id of the addressed `User`; no DB-level FK |
| `Subject` | varchar(256) | `string` | no | — | `[Size(256)]`; trimmed of leading/trailing whitespace by the service; non-empty enforced server-side |
| `Body` | text | `string` | no | — | Markdown content; size guard imposed at the request-pipeline level (existing body-size limit, not a per-property cap). |
| `CreatedAt` | timestamp | `DateTime` | no | `DateTime.UtcNow` (service-set) | UTC always |

`<Nullable>disable</Nullable>` is in effect on `Backend.csproj`, so the `string` columns do not carry `?`. Defaults are set in the service at insert time, not via `[DefaultValue]` (matches `User.CreatedAt` precedent).

### 7.2 Indices

Two indices on the `Message` table:

- `[Index("recipient")]` on **`RecipientId`** plus `[Index("recipient")]` on **`CreatedAt`**. Pooshit.Ocelot composes multiple `[Index]` attributes with the same scope name into a single composite index — same idiom as `NodeMapper`'s composite `("node")` index on `(TypeId, Name)` (see `Backend/Models/Nodes/Node.cs:22-31`). The composite `(RecipientId, CreatedAt)` index covers the hot inbox path: `WHERE RecipientId = @me ORDER BY CreatedAt DESC LIMIT ...`.
- `[Index("author")]` on **`AuthorId`** only. Used for the secondary "what have I sent" query and for the principal-scoping predicate's OR-branch. Single-column is enough — author-side queries are not expected to be sorted by date in tight loops.

No FK constraints. The codebase consistently uses `long` references without `FOREIGN KEY` DDL (see `NodeLink`, `ApiKey.UserId`, `User.HomeNodeId`). Schema correctness is service-level.

### 7.3 Conceptual relationships

```
User ──┐
       │ (AuthorId)
       │
       ▼
    Message
       ▲
       │ (RecipientId)
       │
User ──┘
```

Two `long` references into `User`. No graph link into `Node` — messages are a parallel concept to the node graph, not embedded in it.

### 7.4 Lifecycle

A `Message` has exactly two states: **exists** and **deleted**. There is no soft-delete, no archive, no read-state column. The recipient (or admin) is the only one who can transition `exists → deleted`. This is the entire state machine.

## 8. Contracts & Interfaces (Abstract)

### 8.1 `IMessageService`

| Operation | Inputs | Output | Semantics & failure modes |
|---|---|---|---|
| `Create` | `callerId`, `MessageDetails` (the inbound DTO; `AuthorId` is **ignored** and overwritten with `callerId`) | `MessageDetails` of the persisted row, including server-assigned `Id` and `CreatedAt` | `NotFoundException<User>` when `RecipientId` does not resolve to an enabled user. `ArgumentException` (→ 400 via `ArgumentExceptionHandler`) when `Subject` is empty/whitespace, exceeds 256 chars after trim, or `Body` is empty. |
| `GetById` | `callerId`, `isAdmin`, `id` | `MessageDetails` | `NotFoundException<Message>` when row absent. `AuthorizationFailedException` (→ 403) when row exists but caller is neither sender, recipient, nor admin. |
| `ListPaged` | `callerId`, `isAdmin`, `MessageFilter` | `AsyncPageResponseWriter<MessageDetails>` (page + count + continuation) | Pure filter call; never throws on permission grounds — non-admins simply get the scoped subset. |
| `Delete` | `callerId`, `isAdmin`, `id` | void | Optimistic. `NotFoundException<Message>(id)` when 0 rows affected (collapses absent and not-allowed-to-delete). |

Implementation notes for John (not contract):

- The service receives `(long callerId, bool isAdmin)`, not a `ClaimsPrincipal`. This keeps the service unit-testable without minting principals. The controller computes `isAdmin = User.HasClaim("permission", "admin")` — there is no helper for this yet; add a small `ClaimsExtensions.HasAdminPermission()` or inline the check, John's call.
- All four methods take an explicit caller; there is no `currentUserAccessor` ambient dependency.

### 8.2 HTTP surface

| Verb | Path | Auth policy | Body | Response | Status codes |
|---|---|---|---|---|---|
| `POST` | `/api/messages` | `write` | `MessageDetails` (server uses only `RecipientId`, `Subject`, `Body`) | `MessageDetails` of persisted row | 200, 400 (validation), 401, 403, 404 (unknown recipient) |
| `GET` | `/api/messages` | `read` | — | streamed page envelope of `MessageDetails` | 200, 400 (bad filter), 401, 403 |
| `GET` | `/api/messages/{id:long}` | `read` | — | `MessageDetails` | 200, 401, 403, 404 |
| `DELETE` | `/api/messages/{id:long}` | `write` | — | empty | 200, 401, 403, 404 |

No `PATCH`. No `[AllowPatch]` on any `Message` property — the patch extension at `Backend/Extensions/DatabasePatchExtensions.cs` throws `NotSupportedException` on properties without `[AllowPatch]`, so even if someone sends a `PATCH` to a route that doesn't exist they get a 404 from routing, not a half-success from the patch pipeline. **Explicitly do not add a `[HttpPatch]` action on this controller** — that is the lever readers will reach for and it must stay un-pulled.

### 8.3 Filter shape (`MessageFilter`)

Inherits `ListFilter` (which inherits `PageFilter`): `Count` (≤500, clamped by `ApplyFilter`), `Continue` (offset), `Fields` (projection), `Sort`, `Descending`, `Query` (unused here — Pooshit's base ListFilter carries it for semantic search but messages have no embedding, see section 11).

Adds:

- `RecipientId : long[]` — filter to one or more recipient ids. Combined with the array binder, supports `?recipientId=1,2,3`, `?recipientId=[1,2,3]`, `?recipientId=1&recipientId=2`.
- `AuthorId : long[]` — same shape, for the sender-side filter.

No string-array fields, so no wildcard handling (no `LIKE` branching, no `ContainsWildcards()` calls). State that fact in the type's XML doc so future maintainers don't accidentally add `Subject` as a filter field without also adding the wildcard fan-out logic from `NodeService.GenerateFilter`.

### 8.4 Sort keys

Registered in `MessageMapper`:

- `id`
- `recipientid`
- `authorid`
- `createdat`

`ApplyFilter`'s mapper-based overload (`Backend/Extensions/FilterExtensions.cs:69`) does a strict `mapper[filter.Sort]` lookup — anything else throws `KeyNotFoundException` (mapped to 400 by the existing pipeline). Default sort if `Sort` is empty is **unspecified by the framework** — `ApplyFilter` skips the order-by when `Sort` is empty. The service should explicitly add an `OrderByCriteria` of `createdat DESC` when `filter.Sort` is empty, so the inbox returns newest first by default. (Mirrors how `ApplySemanticSearch` overrides the sort in `NodeService.cs:421`.)

### 8.5 `DefaultListFields`

`["id", "authorId", "recipientId", "subject", "createdat"]`. Body is intentionally **excluded** from the default list projection — the inbox UI/agent typically wants the headers only and follows up with a `GET /{id}` to read the body. Callers who want the body in the list response pass `?fields=id,authorId,recipientId,subject,body,createdat`. This keeps the hot path's payload small without forcing a separate endpoint.

## 9. Cross-Cutting Concerns

### 9.1 Authentication

No new schemes. Uses the existing `DiVoidBearer` policy scheme from `Startup.cs`. Both JWT (Keycloak) and API-key principals reach the controller indistinguishably.

### 9.2 Authorization

Three layers, in order:

1. **Endpoint policy** (`[Authorize(Policy = "read"|"write")]`) — the framework rejects unauthenticated or insufficiently-permissioned callers before the action runs.
2. **Principal scoping in the service** — applied to `ListPaged` and `Delete`. Non-admin callers see only messages where `AuthorId == callerId OR RecipientId == callerId`; admins see all.
3. **Row-level check in `GetById`** — load, then authorize; throw `AuthorizationFailedException` if the loaded row does not match sender/recipient/admin.

The admin-implication chain (`admin ⇒ write ⇒ read`) is handled by `PermissionAuthorizationHandler` — admins automatically pass `[Authorize(Policy = "write")]` and `[Authorize(Policy = "read")]`. The service-side `isAdmin` check is the same claim test (`HasClaim("permission", "admin")`); be explicit that this is the **same** signal the framework already used to admit the request, not a second authorization decision — the service uses it only to decide whether to relax the scoping predicate.

### 9.3 Logging

Per code-contracts §7.1 and §11: log only write operations.

- `POST` → `logger.LogInformation("event=message.create author={CallerId} recipient={RecipientId}", ...)`.
- `DELETE` → `logger.LogInformation("event=message.delete by={CallerId} id={MessageId}", ...)`.
- `GET` and `GET /{id}` → no logging.

Never log `Subject` or `Body` content — messages can contain arbitrary user/agent input. The Auth pipeline rule "never log raw JWT contents" (see `Startup.cs:158`) is the precedent: log identifiers, not payloads.

### 9.4 Error handling

No try/catch in the controller (code-contracts §12, enforced by `jenny-qa-reviewer`). Errors flow through `ErrorHandleMiddleware`:

- `NotFoundException<Message>` / `NotFoundException<User>` → 404.
- `ArgumentException` (validation) → 400.
- `AuthorizationFailedException` → 403.
- `AuthenticationFailedException` → 401.

No new exception types are needed. The contract is "use existing exceptions" (code-contracts §12); inventing `MessageNotFoundException` is a smell.

### 9.5 Idempotency, concurrency, retries

- **Create**: not idempotent. Two `POST` calls with identical bodies produce two messages. That is intentional — agents may legitimately want to send the same reminder twice. If the future Hivemind Protocol update needs idempotency (e.g. "agent sends 'task complete' once per task even if it retries"), the convention should be the calling agent's responsibility (include a correlation id in the body, check inbox for an existing one), not a server-side dedup table. Documented here so the decision isn't accidentally drifted.
- **Delete**: idempotent via the optimistic pattern — the second delete returns 404 and the recipient's inbox is the same shape it was after the first. Acceptable.
- **Concurrent inbox poll + delete**: agents that delete-after-reading are not protected against double-processing if two agent instances poll the same inbox. Out of scope — agents that need this discipline implement it themselves (e.g. operate on one message at a time and delete immediately). Documented so the constraint is explicit.

### 9.6 Consistency

Single-row writes only (`Create`, `Delete`); both operations are single statements, so no `database.Transaction()` scope is needed. (Compare `NodeService.CreateNode` which **does** open a transaction because it inserts a node and one or more `NodeLink` rows together.) Adding a transaction wrapper around a one-statement write is the "single-statement transactions" anti-pattern flagged in user memory; do not do it.

### 9.7 Caching

None at the service level. The inbox is the canonical source of truth and is queried directly. If polling pressure ever justifies caching, that is a separate decision driven by metrics, not part of this design.

### 9.8 Configuration

No new config keys. No new env vars. No new feature flag. The feature is unconditionally available on every deployment that has authentication enabled (which is all of them — the only "auth disabled" path is in `Backend.tests/TestSetup.cs`).

## 10. Quality Attributes & Trade-offs

### 10.1 Scalability of the inbox query

The composite `(RecipientId, CreatedAt)` index plus the natural `LIMIT/OFFSET` clamp (≤500 from `ApplyFilter`) gives the inbox query a planner-friendly shape: index range scan on `RecipientId`, descending range on `CreatedAt`, no sort step. PostgreSQL and SQLite both honor this.

The author-side index is single-column on `AuthorId` only. Sender-side queries are expected to be infrequent and unsorted-by-date; a composite would burn write cost for a read shape that's rarely exercised. Trade-off accepted.

### 10.2 Storage growth

Messages are short and short-lived (delete-after-read is the protocol). Worst case if recipients stop deleting: linear growth in `RecipientId × CreatedAt` order. No retention policy is part of this design; if it becomes necessary it's a future feature ("auto-delete messages older than N days") that can be a hosted service or a CLI verb.

### 10.3 No join to `User` in the mapper

The mapper deliberately omits a `Join<User>` for author/recipient names — unlike `NodeMapper.CreateOperation` which joins `NodeType`. Reasoning:

- The inbox is the hot path. Each inbox poll would multiply by two `User` joins (author + recipient) if we resolved names server-side.
- `MessageDetails` carries `authorId`/`recipientId`. Callers that need names hit `GET /api/users/{id}` once per distinct id they care about (often just one — themselves) and cache. The frontend follow-up will need a user-by-id lookup anyway for the rest of the UI; this just defers the join to the client.
- Authentication gating on `GET /api/users/{id}` is currently `[Authorize(Policy = "admin")]` (see `UserController.cs:64`). That blocks a non-admin caller from resolving an arbitrary user's name via the user endpoint. **This is an open question** — see section 13. Two clean options: (a) widen `GET /api/users/{id}` to `read` for the purpose of name resolution; (b) embed a `userName` projection in `MessageMapper` after all. Pick one before the frontend lands; for the backend implementation in this PR neither is needed.

**Alternative rejected**: pre-joining author/recipient names server-side. Rejected because it bakes a frontend assumption (display names everywhere) into the backend contract and inflates every inbox row even when the consumer is an agent that doesn't care about names.

### 10.4 No `[AllowPatch]` anywhere

Decision: zero `[AllowPatch]` attributes on `Message` and no `PATCH` route on `MessageController`. The patch extension is keyed by attribute, so a stray `PATCH` request would fall through to a 404 from routing. This is the strongest expression of "messages are immutable" — the patch surface is not just unused, it is structurally absent. Trade-off: if a future feature ever needs a per-message edit (e.g. "mark as flagged"), it has to be added explicitly. That cost is acceptable for the protection it gives.

### 10.5 Sender cannot recall

Trade-off: simpler semantics vs. user comfort. Picked simpler semantics. A "recall" feature would force the system to track delivered-vs-recalled state, give recipients a window in which the message exists, and answer "is the version I'm reading the latest one?". For an agent inbox these questions waste cycles. If a human ever uses this feature and mis-sends, an admin can delete on their behalf.

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Caller-supplied `authorId` on `POST` enables impersonation | Service **ignores** any inbound `AuthorId` and overwrites with `callerId`. This is the most important single rule in the service. The HTTP test must assert that `POST` with `authorId` set to another user's id still records `callerId` as the author. |
| Non-admin caller widens scope via `?recipientId=42` (querying another user's inbox) | Service ANDs the scoping clause `(AuthorId = callerId OR RecipientId = callerId)` **before** the caller-supplied filter. The caller's `recipientId=42` can only narrow within the scope. Load-bearing test: a non-admin caller sends `?recipientId=<other>` and the response is empty / does not contain the other user's messages. |
| `GET /api/messages/{id}` reveals existence of someone else's message via 403-vs-404 | Acknowledged in section 6.3. Acceptable for v1. If existence-hiding becomes a requirement, switch the service to a single load-with-WHERE-clause and translate empty → 404. |
| Subject overflow / SQL truncation surprises the caller | `[Size(256)]` in PostgreSQL refuses overflow with a runtime error; service validates length before insert and throws `ArgumentException` (→ 400) so the caller gets a clear message instead of a 500. |
| `Body` arbitrarily large messages consume DB and bandwidth | The existing ASP.NET Core request-body-size limit caps the payload at the pipeline; no per-property cap on `Body`. If a deployment needs a stricter limit it's a config-level decision (Kestrel `MaxRequestBodySize`) not a schema one. Document this in the XML doc on `Message.Body`. |
| Recipient deletes a message they meant to keep | No recovery — accepted. The protocol is "delete when handled"; misclicks are user error. Admin can re-create from logs if it's that important, but logs do not contain body content (see 9.3) so this is only partial. |
| Two agents polling the same inbox both process and delete the same message | Section 9.5 — agents are expected to operate on one message at a time and delete immediately, or implement their own claim mechanism. Out of scope for the backend. |
| New `IMessageService` registration accidentally omitted from `Startup` | Pre-PR checklist item; HTTP integration tests fail with DI resolution error if it's missing, so the test suite catches it. |
| Schema not created on a fresh DB | `DatabaseModelService` must include `await schemaService.CreateOrUpdateSchema<Message>(transaction)`. Existing `DatabaseModelServiceTests` covers this for the other entities and should be extended to assert the `Message` table exists. |

## 12. Migration / Rollout Strategy

There is no migration. The feature ships as one PR. On first start with the new binary:

1. `DatabaseModelService` creates the `Message` table.
2. The four endpoints become available.
3. Nothing else changes — no existing endpoint is altered, no existing entity is modified.

If the PR is rolled back, the empty `Message` table is harmless (it stays in the DB; the rollback target binary simply doesn't reference it). If rolled back and then re-rolled-forward, `CreateOrUpdateSchema` is idempotent.

The downstream Hivemind Protocol update (DiVoid node 190) — telling agents to query their inbox at task start/end — is a **separate task** filed after this PR merges. It is not part of the rollout sequence here.

## 13. Open Questions

These are parked for John (during implementation) or for follow-up tickets. None of them block the design.

1. **Should `GET /api/users/{id}` be widened to `read`?** Currently `admin`. The future frontend will want to display author/recipient names alongside messages. Two clean options exist (see section 10.3) — picking one is a separate decision because it affects user-endpoint policy more broadly than messaging. **Default recommendation for John**: leave `UserController` untouched in this PR; the messaging endpoints return ids and the name-resolution story is the frontend's problem. Open a follow-up ticket against `john-backend-dev` titled "Decide name-resolution policy for non-admin callers" if it becomes a blocker.
2. **`isAdmin` extraction helper**: where does the `bool isAdmin = User.HasClaim("permission", "admin")` line live? Inline in the controller (one site of use today, fine), or as a `ClaimsExtensions.IsAdmin()` extension method that mirrors `GetDivoidUserId()`? Architecturally either is acceptable. John's call — but if the extension is added, add it as one tiny commit ahead of the messaging commit so the messaging service has it available cleanly.
3. **Subject length cap**: 256 is the recommendation here, chosen to match the typical email-subject ceiling and to fit comfortably in a single `varchar(256)`. If product wants 200 or 512 instead, it's a one-character change in the `[Size(...)]` attribute plus the service-side validation; no architectural impact. **Default**: 256.
4. **`DateTime` vs `DateTimeOffset`**: the codebase uses `DateTime` (UTC) everywhere — `User.CreatedAt`, `ApiKey.CreatedAt/LastUsedAt/ExpiresAt`. This design follows suit. `DateTimeOffset` would be the principled choice in a greenfield codebase, but introducing it here would be a one-off that requires every future timestamp to either match the convention or fight it. **Decision: `DateTime` (UTC), set via `DateTime.UtcNow` in the service at insert time.** If the project ever does a sweep to `DateTimeOffset` this entity moves with the others.
5. **Default sort direction**: section 8.4 specifies `createdat DESC` as the service-imposed default when `filter.Sort` is empty. If product wants oldest-first as default, change the `OrderByCriteria` in one place. The mapper still registers `createdat` as a sortable field either way.

## 14. Implementation Guidance for the Next Agent

Build in this order. Each step is a discrete architectural unit; commits can be one-per-step or bundled, John's preference.

1. **Entity & schema registration** — `Backend/Models/Messages/Message.cs` with the fields and indices in section 7. Add `await schemaService.CreateOrUpdateSchema<Message>(transaction)` to `Backend/Init/DatabaseModelService.cs` next to the existing five entities. Extend `Backend.tests/Tests/DatabaseModelServiceTests.cs` to assert the table is created.
2. **DTO + mapper + filter** — `MessageDetails.cs`, `MessageMapper.cs` (registers `id`, `authorid`, `recipientid`, `subject`, `body`, `createdat` mappings; sets `DefaultListFields` per section 8.5; no `Join<User>`), `MessageFilter.cs` (extends `ListFilter`, adds `RecipientId`/`AuthorId` long arrays only).
3. **Service interface + implementation** — `Backend/Services/Messages/IMessageService.cs`, `Backend/Services/Messages/MessageService.cs`. Implement the four operations per section 8.1 and the scoping rules in section 6. Use `PredicateExpression<Message>` composition (per code-contracts §6.3). Default sort handling per section 8.4. The service signature takes `(long callerId, bool isAdmin, ...)` — no `ClaimsPrincipal` dependency.
4. **DI registration** — add `services.AddTransient<IMessageService, MessageService>();` in `Backend/Startup.cs` alongside the other transient services. Order: interface → concrete (code-contracts §8).
5. **Controller** — `Backend/Controllers/V1/MessageController.cs` at route `api/messages`. Four actions per section 8.2. `[Authorize(Policy = ...)]` per the table. No `IActionResult` wrapping. `logger.LogInformation` only on `POST` and `DELETE`. Extract `(callerId, isAdmin)` once per action; forward to the service.
6. **Tests** — see section 15 below. Land alongside the implementation in the same PR.
7. **Pre-PR checklist** — run through the items in code-contracts §16. Particular attention to: explicit types (no `var`), `[ProducesResponseType]` on every action, no `private` on fields, XML docs on all public members.

## 15. Testing strategy

The load-bearing assertion discipline (DiVoid #275) applies: each test must say what regression it catches, not "test exists." Tests use the existing `JwtAuthFixture` pattern (`Backend.tests/Fixtures/JwtAuthFixture.cs`) for the auth-enabled path and the simpler `TestSetup.CreateTestFactory` for cases where permission scoping is not the point.

### 15.1 Required HTTP integration fixture

`Backend.tests/Tests/MessageHttpTests.cs` — parallels `WhoamiHttpTests` in shape. Uses `JwtAuthFixture` so JWT-based principals with explicit permission sets are available.

**Test cases (each with a load-bearing failure description):**

| # | Test | Failure mode it catches |
|---|---|---|
| T1 | `Create_WithValidPayload_PersistsAndReturnsRow` | A regression that breaks the create → DB write → re-read round trip (echoing the input back without persisting). Mirrors `NodeCreateHttpTests.CreateNode_WithStatus_StatusPersistedToDatabase`. |
| T2 | `Create_AuthorIdInBodyIsIgnored_ServerForcesCallerAsAuthor` | A regression that lets clients impersonate by setting `authorId` in the POST body. This is the most important authorization test in the suite — if it fails the impersonation risk in section 11 is open. |
| T3 | `Create_UnknownRecipient_Returns404` | Validation regression: messages can be addressed to non-existent users. |
| T4 | `Create_EmptySubject_Returns400` and `Create_OversizeSubject_Returns400` | Validation regression for the 256-cap. Without the test, a `[Size(256)]`-only protection produces a 500 on overflow instead of a 400. |
| T5 | `List_NonAdmin_OnlySeesOwnMessages` | The principal-scoping clause is missing or wrong. The fixture inserts messages between three distinct users; a non-admin caller's list response must not contain any message where neither author nor recipient is the caller. |
| T6 | `List_NonAdmin_WithRecipientIdOfAnother_ReturnsEmpty` | The scoping clause is ANDed but is being widened by a caller-supplied filter. Caller-supplied `?recipientId=<other>` is intersected with the scope, not replaced by it. |
| T7 | `List_Admin_SeesAllMessages` | The admin-bypass path is broken (admins are being scoped). Specifically tests that the service's `isAdmin == true` branch omits the scoping clause. |
| T8 | `List_SortByCreatedAtDescending_NewestFirst` | The default-sort handling described in section 8.4 is missing — without it, ordering is whatever the DB happens to return. |
| T9 | `Get_OtherUsersMessage_Returns403` | The row-level check in `GetById` is missing — a non-related caller can read any message by id. |
| T10 | `Delete_BySender_Returns404` | The "sender cannot recall" rule is broken — sender is allowed to delete their own sent messages. |
| T11 | `Delete_ByRecipient_Succeeds_And_SecondDeleteReturns404` | Optimistic delete + idempotency contract. Second call must be a clean 404, not 500. |
| T12 | `Delete_ByAdmin_Succeeds_OnAnyMessage` | Admin-bypass for delete is missing. |
| T13 | `Patch_AnyMessageProperty_Returns404OrMethodNotAllowed` | Someone added an `[HttpPatch]` route or an `[AllowPatch]` attribute. The test asserts the patch surface is structurally absent. |
| T14 | `Subject_Trimmed_LeadingTrailingWhitespace_ServerSide` (optional, recommend including) | Service does not normalize whitespace; trivial bug surface. If the service decides not to trim, drop this test — but say so explicitly so the next reader doesn't add it back. |

### 15.2 Schema test

Extend `Backend.tests/Tests/DatabaseModelServiceTests.cs` to assert the `Message` table is created on a fresh DB and that the indices on `RecipientId` and `AuthorId` exist (whichever assertion the existing pattern supports — match the precedent).

### 15.3 What is NOT tested

- Controller-level unit tests via direct instantiation — explicitly forbidden by code-contracts §13.4.
- Message-content moderation, profanity filtering, length-of-body limits — out of scope.
- Concurrent-poll-and-delete race conditions — out of scope per section 9.5.

## 16. Frontend follow-up scope

Out of scope for this PR. A future frontend feature will need:

- An inbox view (calls `GET /api/messages` with `?recipientId=<currentUserId>` or relies on the scoping default), supports the standard list paging envelope.
- A message-detail view (calls `GET /api/messages/{id}` to fetch the `body`).
- A send dialog (calls `POST /api/messages` with `{ recipientId, subject, body }`).
- A delete button on each message (`DELETE /api/messages/{id}`).
- Resolution of `authorId` / `recipientId` to display names — depends on the decision in open question #1 (widen `GET /api/users/{id}` to `read`, or embed `userName` in `MessageMapper`). The frontend PR cannot land without that decision being made.

No new backend endpoint is expected to be required by the frontend beyond what this PR ships, except possibly the user-by-id widening above.

---

## Appendix: gotchas to flag during implementation and review

These are repo-specific landmines the contracts and CLAUDE.md call out. Restating them here so John doesn't have to re-discover them and so reviewers cite them by document rather than personal preference.

- **`JsonStreamOutputFormatter` is `OutputFormatters[0]`** (`Backend/Startup.cs:57`). The list endpoint returns an `AsyncPageResponseWriter<MessageDetails>` and the controller does **not** `await` and materialize the page. Returning a `Task<Page<MessageDetails>>` directly would bypass the stream formatter and break the contract.
- **`ArrayParameterBinderProvider` is `ModelBinderProviders[0]`** (`Backend/Startup.cs:99`). `?recipientId=1,2,3`, `?recipientId=[1,2,3]`, `?recipientId={1,2,3}`, and repeated `?recipientId=1&recipientId=2` all work for any `long[]` query parameter — no special binder code needed.
- **`ApplyFilter` clamps `Count` to 500** and applies `Limit`/`Offset` itself. Do **not** call `operation.Limit(...)` or `operation.Offset(...)` from the service.
- **`ApplyFilter` strict sort lookup**: `mapper[filter.Sort]` throws `KeyNotFoundException` on an unknown key. The mapper must register every name a caller might `?sort=` by.
- **`PATCH` discipline**: the patch extension at `Backend/Extensions/DatabasePatchExtensions.cs` requires `[AllowPatch]` on each property. Messages are immutable — **do not** add `[AllowPatch]` and **do not** add a `[HttpPatch]` action.
- **No FK constraints in this codebase.** `AuthorId` and `RecipientId` are plain `long` columns referencing `User.Id` without DDL-level FK declarations. Same idiom as `NodeLink.SourceId/TargetId`, `ApiKey.UserId`, `User.HomeNodeId`.
- **`<Nullable>disable</Nullable>` in `Backend.csproj`** — entity `string` properties carry no `?`. The test project has `<Nullable>enable</Nullable>` (opposite); test code uses `?` and `null!` as appropriate.
- **Optimistic delete**: check affected-rows count from `ExecuteAsync()`; throw `NotFoundException<Message>(id)` on zero. No pre-check.
- **Log writes only**; never log `Subject` or `Body` content.
