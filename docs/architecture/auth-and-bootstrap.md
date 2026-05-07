# Architectural Document: API-Key Authentication and Bootstrap for DiVoid

> **Revision 2 — 2026-05-07.** Bootstrap path replaced (CLI-only, no env-var seed) per Toni's decisions filed at DiVoid node 77. Pepper promoted from optional to required. Other adjustments inline. Original revision preserved in git history; the diff is summarized at the end of this document.

## Goals

- Move DiVoid from "open service on localhost" to "every request authenticated by an API key" so it can be exposed online.
- Make minting API keys a privileged operation gated by an explicit `admin` permission, so anyone holding a non-admin key cannot escalate.
- Solve the chicken-and-egg problem of creating the very first admin key in a system where every endpoint requires a key.
- Stay coherent with what already exists: `Pooshit.Ocelot` persistence, the `ApiKey`/`ApiKeyService`/`KeyGenerator` triple, the `Pooshit.AspNetCore.Services` error pipeline, and the `Startup.cs` shape.
- Leave a credible runway for finer-grained authorization, hashed storage, and per-user key management without painting ourselves into a corner now.

## Non-goals

- Browser-based login, OAuth, OIDC, password authentication, sessions, cookies. Out of scope. API keys only.
- Mutual TLS, IP allowlists, rate limiting, abuse detection. Worth doing later; not part of this design.
- Per-resource ACLs ("key X may only edit nodes under project Y"). Called out as future work, not built now.
- A frontend or operator UI for key management. The API is the interface; humans use `curl` or the implementation agent does.
- Data migration of existing keys. There is one local DB on Toni's machine and it is fine to wipe it on the cutover.

## Current state (one paragraph)

The mechanical pieces of API-key auth exist (`ApiKey` entity, `ApiKeyService`, `KeyGenerator`) but are not registered in DI and not wired into the request pipeline. `Startup.Configure` is `UseRouting → ErrorHandlerMiddleware → UseEndpoints` with no authentication or authorization step, and `NodeController` is open to anonymous callers. The `User` entity is a stub (`Id`, `Name` only). The full enumeration of the wiring gap lives in DiVoid as task node 12 — readers should treat that as the prerequisite reading for this document. This design takes for granted that those mechanical steps will happen and instead settles the harder questions task-12 punted on.

---

## A. Bootstrap — how the first admin key comes into existence

**Decision: a CLI mode of the same `Backend` executable. The same binary runs as the web service when invoked with no arguments and as a CLI tool when invoked with arguments. The first admin key is minted by the operator running `dotnet Backend.dll create-admin --name <name> [--email <email>]` (or the equivalent against the published exe) inside the deployment environment — typically by `kubectl exec`-ing into the pod or running a one-shot job. There is no environment-variable bootstrap path.**

### Operator workflow

1. Deploy DiVoid in service mode. The container starts the web host. With zero admin keys present, the service starts normally but logs a single startup warning: *"no admin api key exists — no one can administer this instance until one is created via the create-admin CLI"*.
2. The operator opens a shell in the running container (or runs a short-lived job pod with the same image). Inside that shell, they invoke the CLI: `dotnet Backend.dll create-admin --name Toni --email toni@mamgo.io` (email is optional — see section F).
3. The CLI process opens its own connection to the configured database, generates a key via the existing `KeyGenerator`, persists the user + key + admin permission, prints the freshly-minted **full key string** to stdout, and exits with code 0.
4. The operator captures the printed key and stores it wherever they intend to keep admin secrets (password manager, deployment secret, etc.). The key is not recoverable later.
5. From now on, the operator authenticates against the HTTP API with that key and can mint additional users/keys via the `[Authorize(Policy = "admin")]`-gated endpoints.

The same CLI mode handles routine admin operations after bootstrap — `create-admin` is the canonical recovery path if every admin key is lost (deleted, expired, hashes corrupt). The operator can re-run it any time the deployment has shell access; the mechanism never degrades.

### Single-binary, dual-mode dispatch

Implementation shape: `Program.cs` inspects `args` before constructing the web host.

- `args.Length == 0` (or only contains values that ASP.NET Core's host builder treats as host configuration): build the web host as today, run the service.
- Otherwise: dispatch to a CLI subcommand handler. The first arg is the verb (`create-admin`, future verbs as needed). Remaining args are options parsed into a small typed record. The CLI handler builds a minimal host (configuration + DI for `IEntityManager`, `IApiKeyService`, `IKeyGenerator`, `IUserService`), invokes the verb, prints the result, and `Environment.Exit(0)`s.

This keeps the deployment artefact a single image. Operators do not have to deploy a second tool, mount a sidecar, or bake CLI bits into a separate container.

### Reasoning

- **Single mechanism for both bootstrap and recovery.** An env-var seed only works on first start (the "skip if any admin exists" idempotence is what makes it safe), so once the system has admins, the env var is dead weight in the deployment config. Operators never remove it because nothing breaks if they don't, and a forgotten secret in production config is exactly the kind of latent risk we should not introduce. The CLI path works the same way every time, so there is nothing to clean up after first use.
- **No HTTP attack surface, ever.** Same property the env-var design had: there is never an unauthenticated request the network can reach. The CLI runs in-process against the database; it never opens a port.
- **Explicit operator action.** Bootstrap requires someone to log in to the deployment and run a command. That visibility is good — the act is auditable in the deployment platform's exec logs, and it cannot happen as a side effect of a redeploy.
- **Recovery story is the same as the bootstrap story.** No "if you lock yourself out, restore from backup" branch in the design — the operator has the CLI in their pocket regardless.
- **Re-trigger is harmless.** Running `create-admin` again just creates another admin user and key. Nothing is destroyed or implicitly invalidated.
- **Reverse-proxy / loopback positioning is irrelevant.** Unlike a "first-run open window" or "localhost-only" approach, the CLI does not interact with the HTTP layer at all, so proxy quirks cannot break it.

### What gets logged

- CLI mode logs: timestamp, verb, the user id and key id created (never the key value), exit code. These go to stdout/stderr by default and are picked up by the deployment platform's log aggregator the same way service-mode logs are. If the CLI fails (DB unreachable, schema not initialised, validation error), it emits a clear error and exits non-zero.
- Service mode logs the startup warning if no admin key is present, once per process lifetime, at `Warning` level.

### Alternatives considered and rejected

- **Env-var-seeded admin key on startup (the original recommendation in revision 1 of this document).** Rejected per Toni's decision: the env var lingers in production config after first use because nothing forces operators to remove it, and a secret that nobody curates is a security concern. The CLI path collapses bootstrap and recovery into one mechanism with no residual config to clean up.
- **First-run open window (`POST /api/users/bootstrap` allowed unauthenticated when DB is empty).** Rejected. The window is briefly an unauthenticated write endpoint exposed on the public internet. Reverse-proxy or load-balancer races at startup, a crash that wipes the DB, or any operational event that produces "DB is empty" turns it into a remote admin takeover. The probability is low but the blast radius is total.
- **Filesystem token file written by the service on first run.** Workable but operationally awkward in containerised deployments — the file ends up on an ephemeral writable path the operator has to fish out of a running container. Adds complexity over the CLI without compensating benefit.
- **localhost-only escape hatch.** Rejected for production. Behind a reverse proxy, "the connection is from 127.0.0.1" is exactly what every external request looks like (proxy-to-app), so the check either trusts `X-Forwarded-For` (spoofable upstream) or trusts the socket peer (always 127.0.0.1 behind a proxy, defeating the gate). Fragile under sidecar / service-mesh / private-network deployments.
- **Pre-baked compile-time admin key.** Rejected — secret in the binary, leaks via decompilation, awful rotation story.
- **Separate CLI project / second binary.** Rejected per Toni's preference. Two artefacts to build, ship, and version-pin together solves no problem we have. Single binary, branched on args, reuses every service registration.

---

## B. The "auth activated" toggle

**Decision: a single config flag `Auth:Enabled` (read from `appsettings.json` and overridable by the standard ASP.NET Core configuration sources), defaulting to `true`. Production must run with `true`. Local dev may set it to `false` for as long as DiVoid is single-developer; once anyone other than Toni is running it, dev runs with `true` too.**

Concretely:

- `Startup.ConfigureServices` reads `Auth:Enabled`. When `true`, the authentication scheme is registered and `[Authorize]` is enforced. When `false`, the auth handler is not registered and `[Authorize]` is short-circuited (or, equivalently, we use a fallback `AllowAnonymousAttribute` policy when disabled — see section D for the mechanism).
- The flag is a deployment-time decision, not a runtime toggle. Flipping it requires a process restart. We do not support changing the auth posture of a live process.
- The default is `true`. A new operator who forgets to configure anything gets the secure posture.
- For the **transition** (today's open server to tomorrow's gated server), the rollout plan in the "Migration / rollout plan" section below applies.

### Reasoning

- A config flag is the simplest mechanism that fits the existing pattern (`Database:Type` etc.). No compile-time variants, no runtime toggling complexity.
- Defaulting to `true` makes the secure posture the default. Disabling auth has to be an explicit, visible act in `appsettings.Development.json` — it cannot happen by accident.
- A single mode for production removes the "is this server gated or not" question. The answer is always "gated".

### Alternatives considered and rejected

- **Compile-time switch (`#if AUTH_ENABLED`).** Rejected. Two binaries to ship, two test paths, no way to toggle for a one-off dev-on-prod-replica investigation.
- **Always-on with no toggle at all.** Tempting and clean. Rejected because Toni and any new contributor cloning the repo today would hit "service starts, every endpoint 401s, no key exists, can't even bootstrap" the moment they `dotnet run`. The `Auth:Enabled=false` dev escape hatch is worth the small surface area.
- **Always-on with a "dev" bootstrap permission baked in.** Rejected as a more complicated version of the same idea — better to make the disable explicit in config.

---

## C. Permission model

**Decision: free-form `string[]` (the column shape is unchanged), with a closed vocabulary documented and validated at the service layer. Initial vocabulary: `admin`, `read`, `write`. Default for non-admin keys is `["read", "write"]`. `admin` is a strict superset — admin implies read and write. Enforcement via ASP.NET Core's standard `[Authorize(Policy = "...")]` attribute over an `IAuthorizationHandler` that reads `permission` claims off `HttpContext.User`.**

### Vocabulary

| permission | meaning today | examples |
|-|-|-|
| `admin` | privileged operations: create/list/revoke users and API keys | `POST /api/apikeys`, `POST /api/users`, `DELETE /api/apikeys/{id}` |
| `write` | mutate graph state (create/update/delete nodes, links, content) | `POST /api/nodes`, `PATCH /api/nodes/{id}`, `POST /api/nodes/{id}/links` |
| `read` | read graph state | `GET /api/nodes`, `GET /api/nodes/{id}/content` |

Rules:

- `admin` keys behave as if they also have `read` and `write`. The authorization handler resolves this — controllers do not need to declare three policies on every admin endpoint.
- An `ApiKey` with no permissions is invalid — keys are minted with a non-empty array. The service validates this on create.
- New permissions can be added to the vocabulary later. The validator is a single list to update.
- Validation at create time rejects unknown strings. We do not silently accept `"adimn"` and watch nothing match.

### Enforcement mechanism

1. The custom authentication handler (see section D) populates `HttpContext.User` on a successful key lookup with:
   - A `NameIdentifier` claim equal to the user id.
   - An authentication-scheme-name `ApiKey` so downstream code can recognise the auth source.
   - One `permission` claim per entry in the key's permissions array.
2. `Startup` registers named authorization policies — `admin`, `write`, `read` — each requiring the corresponding `permission` claim. The `write` policy also accepts `admin`; the `read` policy accepts `admin` or `write`. Encoded in a small `IAuthorizationHandler` that knows the implication chain, so policies stay one-line declarations.
3. Controllers declare requirements with the standard attribute: `[Authorize(Policy = "admin")]` on the api-key controller, `[Authorize(Policy = "write")]` on `POST/PATCH/DELETE /api/nodes`, `[Authorize(Policy = "read")]` on `GET /api/nodes`.
4. `[AllowAnonymous]` on whatever endpoints we want to exempt (health, see section D).

### Granularity — start coarse

Per-resource authorization (e.g., "this key can only edit nodes under project X") is **not** built now. Toni's stated requirement is that admin exists and most users can do most things; finer scopes would be over-engineering today. When we need it, the natural shape is to add a `Scope` field to `ApiKey` (a structured filter expression) and a resource-aware authorization handler — a future-work hook, not a current build.

### Alternatives considered and rejected

- **Single `IsAdmin` boolean on `ApiKey`.** Rejected. It collapses to "admin or not", which is the minimum requirement, but the moment we want a third state (read-only key for an analytics agent, for example) we are migrating the schema. The string[] column already exists; using it is free.
- **Hardcoded enum.** Rejected. Adding a permission means a code change *and* a migration. The free-string-with-validated-vocabulary pattern is the same one we use successfully for node `Type` in the graph — it leaves room to grow without schema churn.
- **Custom `[RequiresPermission("admin")]` attribute instead of `[Authorize(Policy = "admin")]`.** Rejected. The standard pipeline composes with everything else (action filters, integration tests using `WebApplicationFactory`, future schemes). Inventing a parallel mechanism gets nothing and costs interoperability.

---

## D. Authentication handler design

**Decision: ASP.NET Core `AuthenticationHandler<AuthenticationSchemeOptions>` registered as a custom scheme named `ApiKey`. The scheme is set as the default authentication and challenge scheme in `Startup`. A fallback authorization policy requires an authenticated user, so any endpoint without an explicit `[AllowAnonymous]` is protected by default.**

Header format:

- **`Authorization: Bearer <key>`** is the canonical form. We accept it.
- We do **not** accept the alternative `X-Api-Key: <key>` header. One canonical form is simpler to document, log, and grep for.
- The handler reads `Authorization`, splits on the first space, requires the scheme to be `Bearer` (case-insensitive), and treats the remainder as the key value.

Request flow:

1. Request arrives. The authentication middleware invokes the `ApiKey` handler.
2. Handler reads the `Authorization` header. If missing or not a Bearer scheme, returns `AuthenticateResult.NoResult()` — leaves it to authorization to 401 if the endpoint is `[Authorize]`.
3. If present, handler calls `IApiKeyService.GetApiKey(string)` (or a hashed-lookup variant — see section E) to resolve the key. If the lookup throws `NotFoundException<ApiKey>` or any failure, handler returns `AuthenticateResult.Fail("invalid api key")`.
4. On success, handler builds a `ClaimsPrincipal` with `NameIdentifier` = user id, an authentication scheme of `ApiKey`, and one claim per permission. Returns `AuthenticateResult.Success(...)`.
5. Authorization runs against `HttpContext.User` and the policy declared by the endpoint.

Failure responses:

- Missing/invalid key on a protected endpoint: HTTP `401 Unauthorized`. Response body is RFC 7807 problem details (matching the existing `ErrorHandlerMiddleware` format) with `title` "Authentication required" and a generic `detail` — never echo the key back.
- Authenticated but lacking the required permission: HTTP `403 Forbidden`. Same problem-details shape, `title` "Permission denied".
- The `WWW-Authenticate: Bearer` header is set on 401 responses so clients can identify the auth scheme.

Exemptions:

- **`OPTIONS` requests** are exempt (CORS preflight must succeed without auth). Handle either via `[AllowAnonymous]` + the convention that `OPTIONS` is short-circuited by the CORS middleware before authentication, or via an explicit pipeline guard — the former is the standard ASP.NET Core pattern and is what we should use.
- **Health endpoint** (`GET /api/health`) is exempt, marked `[AllowAnonymous]`. Operators and load balancers need an unauthenticated liveness probe. The endpoint discloses nothing sensitive — it returns "ok" or fails. **Deployment note:** Google Cloud ingress and most other managed load balancers probe `/` by default; they will fail liveness checks if the probe is not retargeted. **The deployment configuration must explicitly point the platform's health probe at `/api/health`.** This is a deployment-time configuration item, not a runtime concern of the service.
- Nothing else is exempt. The `[Authorize]` fallback policy ensures any new controller added later inherits the gated default.

### Reasoning

- The framework already provides authentication scheme registration, claim transformation, `[Authorize]` integration, fallback policies, `WWW-Authenticate` header handling, and the integration-test plumbing. Hand-rolled middleware would re-implement those one by one and inevitably skip a couple.
- Claim-based principals are what every other ASP.NET Core building block (action filters, `User.IsInRole`, `IAuthorizationHandler`) speaks. Stay in that vocabulary.
- The fallback "require auth unless `[AllowAnonymous]`" posture is the safe default for a service whose explicit goal is "no one without a key can access anything".

### Alternatives considered and rejected

- **Hand-rolled middleware that reads the header, looks up the key, and 401s inline.** Rejected. Smaller initial footprint, but it cannot participate in `[Authorize(Policy = ...)]` without re-implementing claim-based authorization from scratch. We would end up re-inventing exactly the abstractions the framework already gives us.
- **JWT with the API key as the signing material.** Overkill and confusing. Keys are opaque random bytes; introducing JWT adds a token format that does not solve a problem we have.
- **Header named `X-Api-Key` instead of `Authorization: Bearer`.** Rejected for canonicalization. Many tools and proxies special-case `Authorization` (redacting it from logs, masking in dashboards). We want that redaction.

---

## E. Key storage and lifecycle

**Decision: hash stored keys with HMAC-SHA-256 using a server-side pepper supplied via the `DIVOID_KEY_PEPPER` environment variable. A short plaintext key-id prefix makes lookup feasible. Key value is shown exactly once on creation and never returned again. Add `Enabled`, `CreatedAt`, `LastUsedAt`, and optional `ExpiresAt` fields. Soft-deletion is **not** added — `DELETE` removes the row.**

### Pepper requirement

- The pepper is a single secret value, ≥32 bytes of randomness (`openssl rand -hex 32` is fine), set in the deployment environment as `DIVOID_KEY_PEPPER`. Stored alongside any other deployment secrets (cloud secret manager, sealed config).
- **In production (`Auth:Enabled = true`), the service must refuse to start if `DIVOID_KEY_PEPPER` is unset or shorter than the minimum length.** This is a fail-closed posture: a misconfigured deployment that silently fell back to no-pepper would be a security regression no operator would notice. Better to fail loudly during deploy than to ship a degraded variant.
- In development (`Auth:Enabled = false`), the pepper is optional — log a startup info message if it is unset and use a fixed dev placeholder, so that switching `Auth:Enabled` to `true` locally without setting the pepper produces the same fail-closed startup error and the dev catches the misconfiguration before deploying.
- The pepper is never logged, never returned by any endpoint, and never written to the database. If it has to be rotated, the operation is "mint new keys with the new pepper, retire keys hashed with the old pepper" — there is no in-place re-hashing because the original key values are no longer recoverable.

### Storage shape

The raw key the user holds becomes a string of the form `<keyId>.<secret>` where:

- `keyId` is a short (8–12 character) random identifier generated at the same time as the secret. Stored plaintext on the row, indexed.
- `secret` is the long random portion (the existing `KeyGenerator` output, ideally widened to 32+ characters — the current 16-character output yields 80 bits of entropy, acceptable but on the low side; recommend bumping to 24 characters / 120 bits).

What the database stores:

- `KeyId` column: the prefix, plaintext, indexed.
- `KeyHash` column: `HMAC-SHA-256(pepper, full_key_string)`. Fixed 32 bytes.
- The plaintext `Key` column **goes away**.

A per-row salt is **not** added — the pepper subsumes its role at the threat model we have, and the keys themselves are 120+ bits of entropy so collision/rainbow-table concerns are not real.

### Lookup flow

On every authenticated request:

1. Split the incoming key on the first `.`. The left side is the `keyId`, the right side is the `secret`.
2. `SELECT ... WHERE KeyId = @keyId` — at most one row, indexed lookup.
3. If found, compute `HMAC-SHA-256(pepper, incoming_full_key)` and compare in constant time against the stored hash. Mismatch → fail.
4. If matched, check `Enabled = true` and `ExpiresAt` is null or in the future. If either fails → fail.
5. On success, asynchronously update `LastUsedAt` (fire-and-forget; don't make the auth latency depend on the write).

### Hash choice (HMAC-SHA-256 vs alternatives)

**HMAC-SHA-256 with the pepper is sufficient.** Argon2 / bcrypt / scrypt protect against offline brute force of *low-entropy* secrets (passwords). The secret here is 120+ bits of cryptographic randomness — brute-forcing it offline is computationally infeasible regardless of hash speed. Using a slow hash here would add hundreds of milliseconds to every authenticated request for no security benefit. Confirmed by industry practice: AWS, GitHub, Stripe all hash API keys with fast hashes (or HMAC) for the same reason.

The pepper raises the bar one more level: a database-only leak (DB exfiltration without app-server compromise) does not yield enough information to brute-force the keys offline — the attacker would also need to obtain `DIVOID_KEY_PEPPER` from the deployment environment. That separation matters because DB backups, replicas, and snapshots are routinely handled by less-trusted personnel and tooling than the live application server.

### Display

When `POST /api/apikeys` succeeds, the response body returns the **full** key string (`<keyId>.<secret>`) one time. The response is also annotated with a clear field-level comment in the API doc: this value is unrecoverable, store it now or revoke and reissue.

`GET /api/apikeys/{id}` and `GET /api/apikeys` return the `KeyId` and metadata only. The secret portion is never returned.

### Revocation

`Enabled = false` is the "revoke" mechanism. A simple PATCH to `/api/apikeys/{id}` flipping `/enabled` from `true` to `false` revokes the key. The auth handler rejects disabled keys at lookup. `DELETE /api/apikeys/{id}` is hard-delete.

### Expiry

`ExpiresAt` is nullable. Null means "no expiry" — the default. Setting a value enforces an expiration on auth lookup. We do not auto-rotate or auto-expire on schedule today; that is operator-managed.

### Audit metadata

- `CreatedAt`: timestamp at insert.
- `LastUsedAt`: nullable timestamp, updated asynchronously on each successful auth. Useful for spotting stale keys for cleanup.
- We do **not** track per-request audit logs in the `ApiKey` table — that belongs in structured request logs (see section G), not row metadata.

### Alternatives considered and rejected

- **Keep plaintext storage.** Rejected for online deployment. Anyone with read access to the database (DBA, backup operator, attacker who exfiltrates a backup) gets every active key in cleartext. Hashing turns a database leak from "instant total compromise" into "the keys still need to be rotated, but they cannot be replayed from the leaked data". Cheap to do.
- **Slow hash (Argon2 / bcrypt).** Rejected — adds latency without adding security against the relevant threat model (high-entropy secrets).
- **Plain SHA-256 without a pepper.** Rejected per Toni's decision: the pepper is a cheap layer that meaningfully changes the DB-leak threat model, and storing it as a deployment env-secret is operationally identical to other secrets the deployment already handles.
- **Per-row salt instead of (or in addition to) the pepper.** Rejected. The pepper covers the same threats more simply for this entropy regime; a per-row salt would add column space without changing any practical attacker calculation.
- **Encrypted-at-rest storage (encrypt the plaintext, decrypt on lookup).** Rejected. Reversible storage is strictly worse than hashed — a server compromise yields plaintext keys.
- **Soft-delete (`DeletedAt` column instead of hard delete).** Rejected. Apikeys are not user-generated content with audit-log obligations; revocation via `Enabled = false` already preserves the row for "this key existed, here is who used it last" and `DELETE` means delete. Two mechanisms is one too many.

---

## F. User model gaps

**Decision: add `Email` (string, nullable, indexed), `Enabled` (bool, default true), `CreatedAt` (timestamp). Keep `User : ApiKey` as 1:N. Disabling a user does **not** propagate to keys at the data layer — it is enforced at auth time by joining on the user row and rejecting if `User.Enabled = false`. Email is not required for any user, including admin users, but the service emits a startup warning if no admin user has an email set.**

### Reasoning per field

- `Email` — primary out-of-band contact for "your key was used in a way that looks weird" or "we are rotating, here is your new key". **Always nullable, never required by the schema or by validation.** The CLI's `create-admin --email` parameter is optional. If none of the admin users have an email set, the service logs a single startup warning at `Warning` level: *"no admin user has an email — there is no contact path if this instance needs to be reached out about"*. The warning does not block startup. Per Toni: it's the operator's call to accept that nobody is reachable; the system should not refuse to run over a missing contact email. Indexed because we will look up users by email when an admin creates them.
- `Enabled` — the offboarding switch. Disabling a user is one PATCH; the next request from any of their keys fails because the auth lookup also checks the parent user.
- `CreatedAt` — basic audit metadata, parity with `ApiKey`.

### Propagation strategy for disable

We do **not** flip `Enabled = false` on every key when a user is disabled. Instead, the auth handler's lookup joins through to the user row and rejects if either the key or the user is disabled. Reasoning:

- One source of truth — the user row — for "this human is offboarded, nothing they own works".
- Re-enabling a user re-activates all of their keys instantly with no per-key fixup.
- One join in the hot path is cheap; the trade is deliberate.

### What is explicitly not added

- No password hash, no email-verification flag, no MFA scaffolding. Out of scope. The hooks (`Email`, `Enabled`) are sufficient for the future to plug into without re-shaping the entity.
- No role-on-user — all role/permission information lives on `ApiKey`. A user is not "an admin" intrinsically; a user holds keys that have the `admin` permission. This keeps the authorization model coherent (a user could plausibly hold both an admin key for ops work and a read-only key for an integration).

### Alternatives considered and rejected

- **Add a `Role` column to `User`.** Rejected — see above. Two places to look for "what can this principal do" is one too many, and conflates user identity with key capability.
- **Cascade-disable keys when the user is disabled.** Rejected. The join-on-lookup approach is simpler, atomic, reversible.
- **Require email for admin users specifically.** Considered and rejected per Toni's decision: the warning is sufficient, the operator owns the consequences of leaving it unset.

---

## G. Operational concerns

### Admin key rotation without lockout

Standard procedure:

1. Operator authenticates with their existing admin key.
2. `POST /api/apikeys` to mint a new admin key for the same user.
3. Verify the new key works against `GET /api/apikeys` (or any admin endpoint).
4. `DELETE /api/apikeys/{old-key-id}` (or `PATCH .../enabled = false` for a soft revoke if there is any uncertainty).
5. Update wherever the old key was stored (deployment secret, agent config) with the new value.

If steps 2–3 fail because the operator misconfigured something, the old key still works — they can retry. The key-generation operation is idempotent in the sense that minting an extra key never breaks existing keys.

If every admin is somehow locked out simultaneously (every admin key revoked or expired with no one minted in time), the recovery is the same `create-admin` CLI invocation from section A — pod-exec, mint a fresh admin key, log the recovery as a graph node so it is not invisible.

### Deployment health probe

The deployment platform's health probe must be configured to call `GET /api/health`. By default Google Cloud ingress, GKE liveness probes, and most managed load balancers probe `GET /` — DiVoid does not respond to `/`, so the probe will fail and the platform will mark the service unhealthy and refuse traffic. **Set the probe path to `/api/health` explicitly in whatever deployment manifest is used (Helm chart, Kubernetes Deployment, Cloud Run service config, etc.).**

The endpoint itself is `[AllowAnonymous]` (see section D), returns a small JSON body indicating the process is alive, and does not check downstream dependencies — that keeps the probe semantics simple ("is the process up?") and avoids cascading failures where a transient DB blip marks the entire service unhealthy. A richer authenticated diagnostics endpoint can be added later if needed; it is a separate path, not a parameter on `/api/health`.

### Logging and metrics

The handler emits structured log events (via the existing `JsonLoggerProvider`) at:

- `Information` — key creation (`event=apikey.created`, `keyId`, `userId`, `permissions`, `actorUserId`). Never log the secret.
- `Information` — key revocation/deletion (`event=apikey.revoked` or `apikey.deleted`).
- `Warning` — failed auth (`event=auth.failed`, `reason=missing_header|invalid_key|disabled_key|disabled_user|expired`, request path, client IP). Rate-limit log emission per source IP if needed; that is a tuning question, not a design question.
- `Information` — successful auth at debug verbosity only (every request would be too noisy at info).
- `Warning` (once at startup) — *"no admin api key exists"* if the service starts with zero admin keys.
- `Warning` (once at startup) — *"no admin user has an email"* if no admin user has an email set.
- `Information` — CLI invocations (`event=cli.create-admin`, exit code, user id and key id when applicable; never the key value).

Metrics to expose (counter form is enough today):

- `auth_success_total{permission}` — successful authentications by primary permission.
- `auth_failure_total{reason}` — failures by reason class.
- `apikey_created_total`, `apikey_revoked_total` — administrative operations.

Concrete metric backend choice (Prometheus, OpenTelemetry, etc.) is out of scope for this design; counters as log fields are an acceptable starting point.

### Behind a reverse proxy

The reverse proxy must:

- Forward the `Authorization` header unchanged. Any proxy that strips it (some load balancers do, "for security") will break authentication.
- Use TLS to the public internet. The keys are bearer credentials; intercepting them on the wire is total compromise. TLS termination at the proxy with cleartext on the loopback to the app is acceptable.
- Have its health-probe target set to `/api/health` (see above) — the default `/` will fail.

The CLI bootstrap path is reverse-proxy-irrelevant: the `create-admin` invocation runs in-process against the database from inside the pod and never traverses the proxy.

---

## Schema deltas

### `ApiKey`

| Field | Status | Type | Notes |
|-|-|-|-|
| `Id` | unchanged | `long` PK, autoincrement | |
| `UserId` | unchanged | `long` | non-nullable from now on; every key owned by a user |
| `Key` | **removed** | — | replaced by `KeyId` + `KeyHash` |
| `KeyId` | **new** | `string`, indexed, ~12 chars | plaintext prefix used for lookup |
| `KeyHash` | **new** | `byte[]` (32 bytes) | HMAC-SHA-256 of `<keyId>.<secret>` with the deployment pepper |
| `Permissions` | unchanged | `string` (JSON `string[]`), `[AllowPatch]` | vocabulary validated at service layer |
| `Enabled` | **new** | `bool`, default `true`, `[AllowPatch]` | revocation toggle |
| `CreatedAt` | **new** | `DateTime` | set at insert |
| `LastUsedAt` | **new** | `DateTime?`, `[AllowPatch]` | updated async on each auth |
| `ExpiresAt` | **new** | `DateTime?`, `[AllowPatch]` | optional expiry |

### `User`

| Field | Status | Type | Notes |
|-|-|-|-|
| `Id` | unchanged | `long` PK, autoincrement | |
| `Name` | unchanged | `string` | |
| `Email` | **new** | `string?`, indexed | nullable; never required by schema or service |
| `Enabled` | **new** | `bool`, default `true`, `[AllowPatch]` | offboarding switch; checked by auth handler via join |
| `CreatedAt` | **new** | `DateTime` | set at insert |

### Configuration / environment

Not schema, but part of the deliverable:

| Name | Required | Notes |
|-|-|-|
| `Auth:Enabled` (config) | required in production (must be `true`) | controls whether the auth handler is registered and `[Authorize]` is enforced |
| `DIVOID_KEY_PEPPER` (env) | **required in production** | service refuses to start when `Auth:Enabled = true` and pepper is unset/short |

`DatabaseModelService.StartAsync` already calls `CreateOrUpdateSchema<ApiKey>` and `<User>` — Pooshit's schema service handles additive column changes, so no migrations folder is needed. The plaintext `Key` column removal is the only destructive change; given the local-only state of the DB, the cutover wipes it.

---

## Migration / rollout plan

1. **Implement and ship with `Auth:Enabled = false` as the dev default.** Existing local dev keeps working unchanged. The auth handler is registered but not gating anything yet; the controllers carry `[Authorize(Policy = ...)]` attributes that are no-ops while the fallback policy is `AllowAnonymous` (this is the toggle described in section B).
2. **Run the schema migration on Toni's local DB.** The `ApiKey.Key` column is dropped; existing dev keys (there are none in active use today) are gone. Any local API keys minted historically need to be re-created — acceptable given alpha.
3. **Add the `ApiKeyController` and `UserController`** (CRUD surfaces gated by `[Authorize(Policy = "admin")]`), the auth handler, and the `Program.cs` CLI dispatch.
4. **Verify in dev.**
   - Set `Auth:Enabled = true` and `DIVOID_KEY_PEPPER` to a test value in `appsettings.Development.json` / dev env.
   - Restart in service mode. Confirm a request without a key returns 401.
   - Run `dotnet Backend.dll create-admin --name dev-admin` (or `dotnet run --project Backend -- create-admin --name dev-admin`). Confirm it prints a key, exits 0, and the printed key authenticates against the HTTP API.
   - Confirm the service refuses to start in `Auth:Enabled = true` mode if `DIVOID_KEY_PEPPER` is unset.
5. **Production deployment.**
   - Set `Auth:Enabled = true` in `appsettings.Production.json` (or environment).
   - Set `DIVOID_KEY_PEPPER` in the deployment environment.
   - Configure the deployment platform's health probe to `/api/health`.
   - Deploy. The service starts and logs a "no admin key exists" warning.
   - Pod-exec into the running container (or run a one-shot job pod against the same image): `kubectl exec ... -- dotnet Backend.dll create-admin --name Toni --email toni@mamgo.io`.
   - Capture the printed key from the CLI output. Store it in the operator's secret manager.
   - Use the key against the live API to verify: `curl -H "Authorization: Bearer <key>" https://<host>/api/nodes` should succeed.
6. **Document the deployment** as a `documentation` node linked to the DiVoid project (the runbook for "how to bring up DiVoid in a new environment"), distinct from the reference doc at node 8. This document — the architecture doc — is the design; the runbook is the operational checklist.

The transition for "existing local devs hitting an open server" is: their first run after pulling the change still works because `Auth:Enabled = false` is the dev default. They are not surprised. When they want to flip to gated locally, they set `Auth:Enabled = true` and `DIVOID_KEY_PEPPER`, restart in service mode, then run the CLI to mint themselves an admin key.

---

## Open questions

All resolved 2026-05-07. Toni's decisions are filed at DiVoid node 77 (`Decisions: auth design Q&A with Toni (2026-05-07)`) and have been folded into the sections above:

- **Pepper** — yes, mandatory in production via `DIVOID_KEY_PEPPER`. Section E.
- **CLI shape** — yes, ship the CLI as the **only** bootstrap path. Single binary, dual mode, dispatch on args in `Program.cs`. The env-var-seed path proposed in revision 1 is dropped. Section A.
- **Admin email contact** — not required. Warning logged at startup if no admin user has an email. Section F.
- **Health endpoint** — exempt from auth. Deployment must explicitly target `/api/health` because most platforms default to `/`. Sections D and G.
- **Vocabulary validation strictness** — implementer's call; design says reject unknown strings hard with a clear error. Section C.

---

## Implementation guidance for the next agent

This is the priority order for the implementation task that will follow this design. Each step is an architectural unit; the implementation agent will break it down further.

1. **Schema additions** (`ApiKey`, `User`) — in `Models/Auth/ApiKey.cs` and `Services/Users/User.cs`, plus DTOs and mappers. No new entity registration needed; `DatabaseModelService` already covers them.
2. **Storage hashing** — adapt `ApiKeyService.CreateApiKey` to split into `KeyId` + `secret`, store `HMAC-SHA-256(pepper, full_key)`, return the full `<keyId>.<secret>` string once. Adapt `GetApiKey(string)` to do prefix lookup + constant-time HMAC compare. Drop the `Key` column. Read the pepper from `DIVOID_KEY_PEPPER` via `IConfiguration`. Fail-closed at startup if `Auth:Enabled = true` and pepper is unset/short.
3. **DI registration** — add `IApiKeyService`, `IKeyGenerator`, and the (new) `IUserService` to `Startup.ConfigureServices`.
4. **Authentication handler** — register a custom `ApiKey` scheme, build the `ClaimsPrincipal` with permission claims, register `read`/`write`/`admin` policies, set the `[Authorize]` fallback policy.
5. **CLI dispatch in `Program.cs`** — branch on `args` to either build the web host (no args) or run a CLI command (any args). First verb: `create-admin --name <name> [--email <email>]`. Reuses `IUserService` and `IApiKeyService`. Prints the key on success and exits 0; prints a structured error and exits non-zero on failure. Future verbs slot into the same dispatch.
6. **Startup warnings** — emit the "no admin api key exists" warning and the "no admin user has an email" warning when the relevant conditions hold at service-mode startup.
7. **`ApiKeyController`** — full CRUD surface, all gated by `[Authorize(Policy = "admin")]`.
8. **`UserController`** — minimal CRUD (create, list, patch enabled/email, soft-disable), gated by `[Authorize(Policy = "admin")]`.
9. **Apply policies** to `NodeController` — `read` on GETs, `write` on POST/PATCH/DELETE.
10. **Health endpoint** with `[AllowAnonymous]`, returning a trivial liveness payload. Confirm it works without a key.
11. **Logging hooks** at the points enumerated in section G.
12. **Smoke test plan** — service-mode startup with and without pepper; CLI mode `create-admin` happy path; CLI mode error paths (DB unreachable, validation failure); bootstrap flow end-to-end (deploy → CLI → HTTP); key rotation flow; disabled-user-cascade; expired-key behavior; fallback-policy behavior on a controller without explicit `[Authorize]`. The implementation agent owns turning this into NUnit tests in `Backend.tests`.

The QA review (`jenny-qa-reviewer`) should explicitly verify: (a) no plaintext key value is ever logged, returned by GET endpoints, or persisted; (b) the CLI `create-admin` path produces a working key end-to-end; (c) `[Authorize]` fallback closes the gap if a future controller forgets the attribute; (d) disabling a user halts every key they own without per-key edits; (e) the service refuses to start in production posture without a pepper.

---

## Diff vs. revision 1

For readers who saw the first version of this document:

- **Section A** rewritten. Bootstrap is now CLI-only (`dotnet Backend.dll create-admin ...`). The `DIVOID_BOOTSTRAP_ADMIN_KEY` env var is dropped. Reasoning: env-secret hygiene — single mechanism for bootstrap and recovery, no residual config to clean up.
- **Section E** strengthened. Pepper is mandatory in production, not optional. `KeyHash` is HMAC-SHA-256 with the pepper, not plain SHA-256. Per-row salt removed.
- **Section F** softened. `User.Email` is fully optional, no admin-must-have-email enforcement. A startup warning fires if no admin has an email; nothing else changes.
- **Section G** gains the deployment-health-probe note: platforms must target `/api/health` explicitly, not `/`.
- **Schema deltas** updated: `KeyHash` description changed, `KeySalt` removed, configuration table added with `Auth:Enabled` and `DIVOID_KEY_PEPPER`.
- **Migration plan** rewritten around the CLI-only bootstrap.
- **Open questions** all resolved; section now points at decisions doc node 77.
- **Implementation guidance** updated: bootstrap-service step folded into the CLI-dispatch step; pepper handling and startup warnings listed explicitly.
