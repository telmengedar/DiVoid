# Architectural Document: Keycloak (OIDC) User Authentication for DiVoid Backend

## 1. Problem Statement

DiVoid Backend currently authenticates every request with a single custom `ApiKey` bearer scheme (`Backend/Auth/ApiKeyAuthenticationHandler.cs`). API keys are intended for service callers, agents, and CLI usage. A web frontend for human users is planned next; humans must authenticate against the existing organisation IdP — **Keycloak** at `https://auth.mamgo.io`, realm `master`, client `DiVoid` — and the backend must accept and validate the resulting OIDC access tokens.

Each Keycloak principal who is permitted to use DiVoid carries a custom user attribute **`UserId`** whose value is the primary key of the matching row in DiVoid's `divoid_user` table. That attribute is the FK link between Keycloak identity and DiVoid identity; the backend must extract it from the inbound JWT, look the row up, verify the user is enabled, and produce a `ClaimsPrincipal` shaped identically to the one the existing `ApiKeyAuthenticationHandler` emits, so that the existing `PermissionAuthorizationHandler` and the existing `admin/write/read` policies continue to work without modification.

**Success criteria**

- A browser-issued Keycloak access token presented as `Authorization: Bearer <jwt>` is accepted by the backend, resolved to a DiVoid `User` row, and passes the same policy gates as an API key with the same permissions.
- The existing `Auth:Enabled` master switch still bypasses all authentication for local development.
- All current API-key-authenticated callers continue to work without any change to their requests.
- Existing tests continue to pass; new tests cover the JWT path with a locally-signed token (no live Keycloak required in CI).

## 2. Scope & Non-Scope

**In scope**

- Adding the `JwtBearer` authentication scheme alongside the existing `ApiKey` scheme.
- A claims-transformation step that lifts the Keycloak `UserId` attribute into the same `ClaimTypes.NameIdentifier` shape used today, validates the DiVoid user row, and emits `permission` claims.
- Configuration plumbing (`Keycloak:Authority`, `Keycloak:Audience`, `Keycloak:RequireHttpsMetadata`).
- A permission-source decision for JWT users and the (small) schema change that decision implies.
- The fallback authorization policy enumerating both schemes so either kind of authenticated caller passes by default.
- Hardened JWT validation parameters (issuer/audience/lifetime/signing-key/clock-skew).
- A test approach that mints JWTs locally with a test RSA key and feeds them through the real authentication pipeline.

**Out of scope (filed as follow-up tasks — see section 14)**

- Frontend (web UI, login screen, token storage, refresh flow).
- A login/redirect endpoint on the backend (the SPA owns the OIDC dance; the backend only validates bearer tokens it receives).
- User provisioning UI, admin tooling for the `User` table, or self-service profile management.
- Configuring the Keycloak realm/client itself — that is a Keycloak admin task.
- Replacing or rotating the existing `DIVOID_KEY_PEPPER` model for API keys.
- Swagger UI redesign or multi-scheme `AddSecurityDefinition` polish (we keep the existing swagger surface; a follow-up can add a JWT security definition once the frontend exists and demands it).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Validation needed |
|---|---|---|---|
| A1 | Keycloak issuer URL is `https://auth.mamgo.io/realms/master`; the `iss` claim in tokens matches that exact string. | High | Confirm with one real token. |
| A2 | The Keycloak `DiVoid` client is configured to emit the `UserId` user attribute into **access tokens** (not only ID tokens), via a "User Attribute" protocol mapper. | **Medium — must be validated** | Toni / Keycloak admin must verify the mapper exists and targets `access.token=true`. |
| A3 | The claim name in the resulting JWT is `"userId"` (camelCase). Keycloak's user-attribute mapper lowercases the first letter of the attribute name by default when serializing to JSON. The "Token Claim Name" field on the mapper can override this; the deployed mapper does not, so the emitted claim name is `"userId"`. Verified against a real access token. | Confirmed | Verified — real token contains `userId: 1`. |
| A4 | The `aud` claim value in tokens issued by the DiVoid client is the literal string `"DiVoid"` — **not** necessarily the `client_id`. Keycloak can emit a different `aud` value depending on realm and client configuration. The concrete value has been confirmed: `Keycloak:Audience` must be set to `"DiVoid"` in production config. | Confirmed | Toni has confirmed the `aud` claim value; verified by decoding a real token. |
| A5 | Tokens are RS256-signed; JWKS is published at the standard `.well-known` endpoint under the realm. | High | Standard Keycloak. |
| A6 | DiVoid runs HTTP-only in dev (port 5007 / port 80 in prod via the Program.cs Kestrel override). `RequireHttpsMetadata = true` is fine in prod (Keycloak itself is HTTPS) but must remain configurable so dev keeps working. | High | n/a |
| A7 | The DiVoid `divoid_user` row for each Keycloak user is pre-provisioned out-of-band (created via the existing API-key admin flow or a small admin script). Auto-provisioning is rejected — see section 5. | Recommendation | Toni's call; doc records the rationale. |
| A8 | `<Nullable>disable</Nullable>` remains in Backend.csproj; new types are written in the same style as the rest of the project. | Hard constraint | n/a |
| A9 | The `Pooshit.AspNetCore.Services` error pipeline (`ErrorHandlerMiddleware`) is the canonical place to translate exceptions to HTTP responses. New code does not bypass it. | Hard constraint | n/a |

## 4. Architectural Overview

```
                Client (browser SPA or service)
                          │
                          │ Authorization: Bearer <token>
                          ▼
        ┌─────────────────────────────────────────────────┐
        │  ASP.NET Core authentication middleware         │
        │                                                 │
        │   ┌──────────────────┐   ┌──────────────────┐  │
        │   │ JwtBearer scheme │   │  ApiKey scheme   │  │
        │   │  (default)       │   │  (additional)    │  │
        │   └────────┬─────────┘   └────────┬─────────┘  │
        │            │                      │             │
        │   detects JWT shape       detects "<keyId>.<…>" │
        │   (3 dot-separated         shape — falls        │
        │    base64url segments)     through if not JWT    │
        └────────────┼──────────────────────┼─────────────┘
                     │                      │
                     ▼                      │
        ┌──────────────────────────┐        │
        │ KeycloakClaimsTransform  │        │
        │ (IClaimsTransformation)  │        │
        │  - reads UserId claim    │        │
        │  - loads divoid_user row │        │
        │  - emits NameIdentifier  │        │
        │  - emits permission[]    │        │
        └────────────┬─────────────┘        │
                     │                      │
                     ▼                      ▼
        ┌─────────────────────────────────────────────────┐
        │  Authorization middleware                       │
        │   Fallback policy: schemes=[JwtBearer, ApiKey]  │
        │   Permission policies: admin / write / read     │
        │   (unchanged — read "permission" claim)         │
        └─────────────────────────────────────────────────┘
                          │
                          ▼
                    Controller action
```

The new components are exactly two: **(a)** a `JwtBearer` registration with tightened validation parameters, and **(b)** a `KeycloakClaimsTransformation : IClaimsTransformation` that normalises Keycloak tokens into the same claim shape API-key principals already have. Everything downstream — `PermissionAuthorizationHandler`, the three policies, controllers — is untouched.

A small schema change adds a single non-PII column to `divoid_user` to hold the user's permissions (section 5 picks option (a); section 11 records the rejected alternatives).

## 5. Components & Responsibilities

### 5.1 `JwtBearer` authentication registration (in `Startup.ConfigureServices`)

- **Owns:** validating inbound JWTs against Keycloak's JWKS, building the initial `ClaimsPrincipal` from the token, raising 401 on validation failure.
- **Does NOT own:** mapping Keycloak claim names to DiVoid identity; loading the DiVoid user row; permission decisions.

### 5.2 `KeycloakClaimsTransformation` (new, `Backend/Auth/`)

- **Owns:** the second-leg transformation of any authenticated principal whose authentication type is `JwtBearer`. For every such principal:
  1. Read the Keycloak `UserId` claim (configurable claim name; default `UserId`).
  2. If absent, return the original principal **without** the DiVoid claims; the request will fail authorization at the fallback policy or at any policy needing `permission`. (See section 9 for the precise 401 vs 403 distinction.)
  3. Load the `divoid_user` row by id; if missing or `Enabled=false`, return the original principal unchanged — same effect: the user lacks any DiVoid permission claims and cannot pass any policy.
  4. Add `ClaimTypes.NameIdentifier = User.Id.ToString()` so existing `User.GetUserId()`-style helpers and any downstream `NameIdentifier` reads work uniformly across both schemes.
  5. Add one `permission` claim per entry in `User.Permissions` (see 5.3).
- **Does NOT own:** validating the JWT signature (that is `JwtBearer`'s job); deciding policy outcomes (that is `PermissionAuthorizationHandler`).
- **Idempotency:** `IClaimsTransformation.TransformAsync` is called on every request; the transform must short-circuit cheaply if the principal is already augmented or is not a JWT principal. Detection: `principal.Identity.AuthenticationType == JwtBearerDefaults.AuthenticationScheme` (or equivalent). API-key principals skip the transform entirely.

### 5.3 `User` entity — minor schema extension

- **Adds:** one new column `Permissions : string` storing a JSON array of permission strings (same encoding the existing `ApiKey.Permissions` column already uses). Allowed values mirror the API-key vocabulary: `admin`, `write`, `read`.
- **Migration:** registered in `Init/DatabaseModelService.cs` alongside the existing `CreateOrUpdateSchema<User>` call. `SchemaService` is additive — adding a nullable column on next startup is the established mechanism.
- **Rationale:** the chosen permission-source option (see section 11). The column is nullable / empty for users who exist only as API-key owners; only Keycloak-backed humans need it populated.

### 5.4 `UserService.GetUserById` — reused

No new method needed for the claims transform. The existing `GetUserById(long)` already returns `null`-via-`NotFoundException<User>`, but the transform should not let that exception propagate — it catches and treats it as "missing user → drop permissions, leave the request to fail authorization." A new internal `TryGetUserForAuth(long) -> User` method on `UserService` is a clean alternative; either is acceptable. (The implementer picks the lower-friction option; see implementation guidance, section 14.)

### 5.5 Configuration

New `appsettings.json` section:

```
"Keycloak": {
  "Authority": "https://auth.mamgo.io/realms/master",
  "Audience": "",                          // empty in committed file; Toni populates per env
  "RequireHttpsMetadata": false,           // false in dev; true in prod via appsettings.Production.json
  "UserIdClaimName": "userId"              // camelCase — matches Keycloak's default first-letter-lowercase mapper emission
}
```

`Auth:Enabled` remains the master switch. When `Auth:Enabled=false`, **neither** scheme registers and the open-policy fallback applies, exactly as today.

When `Auth:Enabled=true` but `Keycloak:Audience` is empty, the service must **fail startup with a clear message**, in the same shape `ApiKeyService` already uses for a missing pepper (`MissingPepperException`). This prevents accidentally booting a "permissive" auth config that validates issuer but not audience.

## 6. Interactions & Data Flow

### 6.1 Happy path — Keycloak-authenticated request

```
1. Browser sends:  GET /api/nodes  with  Authorization: Bearer eyJhbGciOi...
2. ASP.NET tries JwtBearer first (default authenticate scheme).
3. JwtBearer downloads JWKS from Keycloak (cached), validates signature,
   issuer (== Keycloak:Authority), audience (== Keycloak:Audience),
   lifetime (with default 5min clock skew).
   - On failure: result = NoResult, ApiKey scheme is NOT tried for JWT
     shape because token doesn't match "<keyId>.<secret>" form (see 6.3
     for the routing rule). Authentication ultimately fails → 401 with
     WWW-Authenticate: Bearer.
4. On success, principal has standard Keycloak claims including "UserId".
5. KeycloakClaimsTransformation runs:
     - reads UserId = "42"
     - loads divoid_user row id=42, Enabled=true, Permissions=["read","write"]
     - emits NameIdentifier=42, permission=read, permission=write
6. Authorization runs the controller's policy ("read" / "write" / "admin"
   or the fallback). PermissionAuthorizationHandler reads the permission
   claims and decides.
7. Controller action runs.
```

### 6.2 Happy path — API-key request (unchanged)

```
1. Service sends:  Authorization: Bearer <keyId>.<secret>
2. ASP.NET tries JwtBearer first. JwtBearer attempts to parse the
   token as a JWS, fails the structural check (no 3-segment base64url
   form), returns NoResult.
3. ASP.NET falls through to ApiKey scheme (registered as an additional
   scheme). ApiKeyAuthenticationHandler validates as today.
4. KeycloakClaimsTransformation sees AuthenticationType != JwtBearer
   and short-circuits.
5. Same authorization pipeline.
```

### 6.3 Scheme-routing detail

There is no manual "is this a JWT" sniffer in DiVoid code. The behaviour above is the standard ASP.NET multi-scheme pipeline:

- `DefaultAuthenticateScheme = JwtBearer`. JwtBearer is tried first.
- A non-JWT bearer token causes JwtBearer's handler to return `NoResult` (not `Fail`) — the .NET JwtBearer handler treats malformed-as-JWS bearer values as "not for me," not as an error. (The implementer must verify and, if needed, set `JwtBearerEvents.OnMessageReceived` to short-circuit cleanly; this is a small implementation detail, not an architectural decision.)
- The fallback authorization policy lists both schemes, so authorization will internally call `AuthenticateAsync("ApiKey")` after JwtBearer abstains, and the API-key handler runs.

If the JwtBearer handler ever returns `Fail` (e.g. token *looks* like a JWT but is invalid), authorization should **not** silently fall through to ApiKey. That distinction is what makes the error surface in section 9 well-defined.

### 6.4 Cross-cutting flow — claim emission timing

`IClaimsTransformation.TransformAsync` runs once per request after the authenticate step succeeds and before the authorization step. The DB lookup it performs is one indexed primary-key fetch — acceptable per request for now. A caching layer (e.g., memory-cache keyed on user id with a short TTL) is an obvious follow-up if request rates climb, but is **explicitly out of scope** for this PR.

## 7. Data Model (Conceptual)

Single change: `divoid_user.Permissions` (string, JSON-encoded array, nullable).

```
divoid_user
─────────────
Id            long  PK, auto-inc
Name          string
Email         string  indexed
Enabled       bool
CreatedAt     datetime
Permissions   string  NEW — JSON array of {"admin"|"write"|"read"}, nullable
```

Conceptual relationship: a `divoid_user` row is the target of two distinct identity bindings:

- Zero-to-many `ApiKey` rows (`ApiKey.UserId` → `User.Id`), each with their own `Permissions` set on the key.
- Zero-or-one Keycloak principal (linked via the Keycloak side's `UserId` attribute, not stored on the DiVoid side — DiVoid does not store any Keycloak `sub` or other IdP identifier; the link is one-way, IdP → DiVoid).

Permissions for a request are sourced from whichever scheme authenticated it:

- ApiKey scheme: `ApiKey.Permissions` (per-key, as today).
- JwtBearer scheme: `User.Permissions` (per-user).

This is deliberate: API keys are scoped capabilities (you might issue an admin user a `read`-only key for a CI runner); JWT principals are the user themselves, so their permission set is on the user row.

## 8. Contracts & Interfaces (Abstract)

### 8.1 Claim-shape contract (the seam between auth and authorization)

After authentication + claims-transformation, the `ClaimsPrincipal` reaching authorization satisfies:

| Claim | Cardinality | Meaning |
|---|---|---|
| `ClaimTypes.NameIdentifier` | exactly 1 | DiVoid `User.Id` as decimal string (`"0"` if no row, but in practice a no-row JWT will lack `permission` claims and fail policies). |
| `permission` | 0..N | Each value is one of `admin`, `write`, `read`. |
| All Keycloak-emitted claims (`sub`, `iss`, `aud`, `iat`, `exp`, custom mappers) | as-issued | Preserved on JWT principals; absent on ApiKey principals. Downstream code must not rely on Keycloak-specific claims for permission decisions. |

This is the invariant the design relies on. Any code outside `Backend/Auth/` that needs the DiVoid user id reads `NameIdentifier`; any code that needs a permission reads via the existing policy gates (`[Authorize(Policy = "write")]`) — never directly off the principal.

### 8.2 Configuration contract

| Key | Type | Required when | Default | Effect |
|---|---|---|---|---|
| `Auth:Enabled` | bool | always | `true` | Master switch. `false` ⇒ no auth, all policies open (unchanged today). |
| `Keycloak:Authority` | string | `Auth:Enabled=true` | `"https://auth.mamgo.io/realms/master"` | OIDC discovery base. |
| `Keycloak:Audience` | string | `Auth:Enabled=true` | `""` (intentional — startup fails if empty) | Expected `aud` claim. For this realm/client the value is the literal string `"DiVoid"` (confirmed from a real token — **not** the Keycloak `client_id`). Always verify by decoding a real access token before setting. |
| `Keycloak:RequireHttpsMetadata` | bool | optional | `false` (dev), set `true` in `appsettings.Production.json` | Whether OIDC metadata fetch requires HTTPS. |
| `Keycloak:UserIdClaimName` | string | optional | `"userId"` | Name of the JWT claim that carries the DiVoid `User.Id`. Defaults to `"userId"` (camelCase) to match Keycloak's default first-letter-lowercase emission from the user-attribute mapper. Override only if the mapper's "Token Claim Name" is explicitly set to something different. |
| `DIVOID_KEY_PEPPER` | string ≥ 32 bytes | `Auth:Enabled=true` | none | Existing API-key pepper. Unchanged. |

### 8.3 Token validation contract (the `JwtBearer` side)

| Parameter | Value | Rationale |
|---|---|---|
| `ValidateIssuer` | `true` | Pin to Keycloak realm. |
| `ValidIssuer` | `Keycloak:Authority` | Single accepted issuer. |
| `ValidateAudience` | `true` | Prevent token reuse across clients. |
| `ValidAudience` | `Keycloak:Audience` | Pin to the `DiVoid` client. |
| `ValidateLifetime` | `true` | Refuse expired/not-yet-valid tokens. |
| `ClockSkew` | `TimeSpan.FromMinutes(2)` | Keycloak's default is 5; we tighten slightly. |
| `ValidateIssuerSigningKey` | `true` | Verify against JWKS. |
| `RequireSignedTokens` | `true` | Refuse `alg=none`. |
| `RequireExpirationTime` | `true` | Refuse tokens without `exp`. |
| `RoleClaimType` | not set | We do not use ASP.NET role-based gates; permissions live on a custom `permission` claim only. |
| `NameClaimType` | `"preferred_username"` (or default) | Cosmetic; not used for authorization. |

Signing keys are pulled from JWKS via the OIDC discovery document at `<Authority>/.well-known/openid-configuration`. The default JwtBearer middleware caches and rotates them automatically.

## 9. Cross-Cutting Concerns

### 9.1 Error surface (the 401 / 403 distinction)

- **No `Authorization` header** → fallback policy fails authenticate → 401, `WWW-Authenticate: Bearer`.
- **Invalid JWT** (bad signature, expired, wrong audience, wrong issuer) → JwtBearer issues 401 with `WWW-Authenticate: Bearer error="invalid_token"`. The .NET default behaviour does this; the implementer does not customise it unless it conflicts with `Pooshit.AspNetCore.Services` error middleware.
- **Valid JWT, no `UserId` claim** → principal passes authentication but has no `NameIdentifier` or `permission` claims from DiVoid. Any policy needing `permission` → 403. The fallback policy (which only requires `RequireAuthenticatedUser`) → 200 if the endpoint is not gated by `read/write/admin`. **Decision:** that is the right behaviour — a Keycloak user without a DiVoid row is "authenticated but unauthorized for any DiVoid action," which is exactly what 403 means.
- **Valid JWT, `UserId` claim, but DiVoid user disabled or absent** → same as above: no `permission` claims emitted → 403.
- **Non-JWT bearer that is also not a known API key** → JwtBearer abstains, ApiKey fails → 401.
- **Wrong scheme arrangement risk** (mentioned in section 6.3): if both schemes call `Fail` instead of `NoResult`, the response becomes confusing. The fix is to verify, during implementation, that `JwtBearerEvents.OnMessageReceived` (or `OnAuthenticationFailed`) is tuned so non-JWT bearers do not produce a `Fail`. This is verified by the cross-scheme test case in section 10.

### 9.2 Logging & observability

- New log events follow the existing `event=auth.*` convention used in `ApiKeyAuthenticationHandler`:
  - `event=auth.jwt.failed reason={invalid_token|expired|wrong_audience|...}`
  - `event=auth.jwt.no_user_id` (valid token but missing claim)
  - `event=auth.jwt.unknown_user userId={…}`
  - `event=auth.jwt.disabled_user userId={…}`
  - `event=auth.jwt.success userId={…}`
- Token contents (the raw JWT string) are **never logged**. Only `iss`, `aud`, and the resolved DiVoid `userId` should appear in logs. The implementer must add a redaction note next to the JwtBearer config.

### 9.3 Security posture

- HTTPS is enforced by the deployment, not by the app (Program.cs does not enable HTTPS redirection). `RequireHttpsMetadata` controls only the JWKS fetch, which goes to Keycloak's HTTPS endpoint regardless. The dev `false` value is acceptable because the metadata URL itself is `https://`.
- The DiVoid backend does not see, store, or proxy Keycloak refresh tokens or client secrets. It is a pure token verifier.
- The DiVoid backend never calls back to Keycloak's token endpoint, userinfo endpoint, or admin API as part of this design. (Discovery + JWKS only.)
- The `UserId` user-attribute mapping is the only piece of business-meaningful data in the token. If it ever needs to be PII-sensitive, the mapper can be renamed (the claim name is configurable).

### 9.4 Idempotency, retries, caching

- `IClaimsTransformation` runs once per request and performs one indexed PK read. No write side-effects. Cheap to re-run on retry.
- No caching is added in this PR. (Follow-up.)

### 9.5 Concurrency

- `IClaimsTransformation` instances are transient per ASP.NET conventions; concurrency is the framework's problem, not ours.
- The DB read uses the existing `IEntityManager`, which is the singleton used everywhere else in the project.

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed | Trade-off |
|---|---|---|
| **Backwards compatibility** | API-key path is untouched. Scheme selection happens by token shape at the framework level. | We rely on JwtBearer's `NoResult` behaviour for non-JWT bearers; if a future .NET version changes that, both schemes' interaction needs revisiting. The cross-scheme test pins it. |
| **Performance** | One extra DB read per JWT request (PK lookup on `divoid_user`). JWKS is cached by middleware. | No caching yet; if request rates ever hit thousands/sec we'll need an in-process LRU. Not now. |
| **Security** | Standard `JwtBearer` validation, audience-pinned, signing-key-pinned. Tightened clock skew. Startup fails closed if `Keycloak:Audience` is empty. | Configuration-driven `RequireHttpsMetadata` is `false` in dev — acceptable because the URL is HTTPS anyway, but the doc explicitly calls out that prod settings must set it true. |
| **Maintainability** | Two new files (transformation + small startup change), no new abstractions, same claim shape as today. | We're adding a column to `divoid_user` instead of a side table or a Keycloak-roles-driven design (section 11). Cost: one schema column. Benefit: zero net new patterns; the column is the obvious place to evolve. |
| **Testability** | Locally-signed JWTs; test host stands up an in-process JWKS endpoint and points `Keycloak:Authority` at it. No live Keycloak in CI. | Slightly more setup in the test harness than mocking the auth handler outright — but the value is high (we exercise the real validation code path). |
| **Operability** | Startup-time validation of config (audience required). Clear `event=auth.*` log lines. | We deliberately do *not* surface a `/auth/whoami` endpoint in this PR; the frontend will need one later, filed as follow-up. |

## 11. Risks & Mitigations

### 11.1 Permission-source decision

Three live options, picked deliberately:

| Option | Description | Verdict |
|---|---|---|
| **(a) `User.Permissions` column** | New JSON-array column on `divoid_user`. Permissions are per-user, set by an admin via PATCH. | **Chosen.** Smallest footprint, mirrors the existing `ApiKey.Permissions` shape exactly, no Keycloak-side configuration burden, evolves cleanly (you can extend the JSON shape later). The cost is one nullable string column. |
| **(b) Keycloak roles** | `realm_access.roles` or client-role claims emitted by Keycloak; backend maps role names to DiVoid permissions. | Rejected for now. Couples DiVoid permission grants to Keycloak realm configuration, which is operated by a different process. Useful later if/when DiVoid permissions multiply, but premature today. |
| **(c) Hybrid** | Roles from token, identity link from `UserId` attribute. | Rejected for now. Hybrid means two sources of truth, which is the most expensive option to operate. Option (a) is forward-compatible with (b) — we can layer (b) on later by extending the claims transformation to *also* read role claims and union the sets. |

**Risk:** Option (a) means an admin must explicitly grant DiVoid permissions to a Keycloak-backed user before they can do anything. **Mitigation:** that is exactly the desired posture — a Keycloak user who just got created should not implicitly have `write` to DiVoid. The 403 they get on first use is the correct signal.

### 11.2 Multi-scheme fall-through ambiguity

**Risk:** A malformed bearer could land in the wrong handler and produce a confusing error.

**Mitigation:** Section 9.1 spells out the expected behaviour; section 10 lists a cross-scheme test case ("ApiKey works alongside JwtBearer") that locks the contract.

### 11.3 Missing `UserId` user-attribute mapper in Keycloak

**Risk:** The Keycloak admin forgot to add the protocol mapper, or added it to the ID token only. Production tokens contain no `UserId` claim — every browser user gets a 403.

**Mitigation:** (a) The configurable claim name (`Keycloak:UserIdClaimName`) lets us point at whatever mapper exists. (b) The `event=auth.jwt.no_user_id` log line will fire on every failed request, surfacing the misconfiguration immediately. (c) Listed as the first follow-up task in section 14.

### 11.4 Auto-provisioning vs pre-provisioning

**Risk:** First-time login fails because the DiVoid user row doesn't exist yet.

**Decision:** **Pre-provisioning, not auto-provisioning.** Reasons:

- Auto-provisioning needs a name and email, which means trusting Keycloak claims for PII fields — a much bigger trust decision than just trusting `UserId`.
- DiVoid `User.Id` is an auto-incremented `long`. The Keycloak side is configured with the *expected* DiVoid id as a user attribute; that workflow already requires an admin to know the id, which means the admin has already created the row.
- We can revisit later if the operational cost becomes painful. Filed as follow-up.

**Mitigation if revisited:** The transformation already has the "no DiVoid row" branch; turning it into "create on first sight" is a localised change.

### 11.5 JWKS rotation / cold-cache failure

**Risk:** Keycloak rotates signing keys; the middleware's cache misses; the JWKS fetch fails under network partition.

**Mitigation:** Default `JwtBearer` behaviour handles rotation. If JWKS is unreachable, validation fails closed (401). No specific code change needed; just documented behaviour.

### 11.6 `Auth:Enabled=false` accidentally shipped to prod

**Risk:** Lower-environment artefact deployed to prod.

**Mitigation:** Pre-existing concern, not introduced by this change. The existing `StartupWarningService` is the right place to surface a loud log warning when `Auth:Enabled=false`; the implementer should verify it already does so for this case, and extend if not. (Out of scope to change in this PR otherwise.)

## 12. Migration / Rollout Strategy

There is no live JWT-authenticated client today; the rollout is one-shot:

1. **Pre-merge (Toni / Keycloak admin):** ensure the `DiVoid` client has a user-attribute mapper named `UserId` that emits to access tokens. Note the `client_id` value.
2. **Merge the PR** with `Keycloak:Audience` empty in the committed `appsettings.json`. Service is still healthy because `Auth:Enabled=false` is the dev default in the existing settings file (verify), or because Toni populates `Keycloak:Audience` in the deployment's environment-overlay config before deploying.
3. **Configure the production overlay** with `Keycloak:RequireHttpsMetadata=true` and set `Keycloak:Audience` to **whatever value appears in the `aud` claim of a real access token issued by the DiVoid client**. For this realm/client that value is the literal string `"DiVoid"` — it is **not** the Keycloak `client_id` by default. To verify before committing the config, decode a real token (base64-decode the middle segment, parse JSON, look at `aud`) and use exactly that value.
4. **Provision DiVoid users** for each human who needs access: create the `divoid_user` row, set `Permissions`, and configure their Keycloak user with the `UserId` attribute matching the row id.
5. **Verify with a real token** before the frontend lands: mint a token via Keycloak's account console or `curl`, hit `/api/nodes`, confirm 200.

Existing API-key callers see zero behaviour change at every step.

## 13. Open Questions

1. **Confirm the protocol-mapper "Token Claim Name"** on the Keycloak `DiVoid` client side. Default = attribute name = `UserId`, but it is configurable. The `Keycloak:UserIdClaimName` config key handles divergence, but we should set it once we know.
2. **Should the protocol mapper be configured to set `Add to access token = true` only, or also `Add to ID token`?** Backend only reads access tokens; ID tokens are not seen here. Recommendation: access token only, to minimise PII exposure.
3. **Do we want a `/api/auth/whoami` endpoint** in this PR? Recommendation: **no** — the frontend hasn't been built and there is no concrete consumer. Filed as follow-up.
4. **Logging redaction policy for `sub`/`preferred_username`?** Today we don't log `sub`. Recommendation: keep it that way; `UserId` (= DiVoid `User.Id`) is the only identifier that appears in logs.
5. **Should the `User.Permissions` field be `[AllowPatch]`?** Recommendation: **yes** — admins manage permissions via the existing PATCH endpoint on `/api/users/{id}` with the same JSON-Patch grammar that already handles `Email` and `Enabled`. No new admin surface needed.

## 14. Implementation Guidance for the Next Agent

Ordered milestones. Each milestone is small enough to verify on its own.

**M1 — Configuration & schema (no auth changes yet)**

- Add the `Keycloak` config section to `Backend/appsettings.json` (with `Audience` empty, as documented).
- Add the `Permissions` column to `Backend/Models/Users/User.cs`, including `[AllowPatch]`.
- The existing `DatabaseModelService` already calls `CreateOrUpdateSchema<User>`; verify it picks up the new column on next startup (additive schema change, this is the established pattern).
- Tests at this point: zero functional change, existing tests must still pass.

**M2 — JwtBearer registration**

- In `Startup.ConfigureServices`, when `AuthEnabled` is true, register `JwtBearer` as the default authenticate/challenge scheme **and** keep the existing `ApiKey` scheme as an additional scheme.
- Configure `TokenValidationParameters` per the table in section 8.3.
- Update the fallback authorization policy to enumerate both schemes.
- Add the startup-time validation: if `Auth:Enabled=true` and `Keycloak:Audience` is empty, throw the same shape of exception as `MissingPepperException`.
- Tests at this point: an existing API-key test should still pass; a token-shaped-but-bogus JWT should produce a clean 401.

**M3 — `KeycloakClaimsTransformation`**

- New file `Backend/Auth/KeycloakClaimsTransformation.cs`, implementing `IClaimsTransformation`.
- Behaviour exactly as in section 5.2.
- Register as `services.AddTransient<IClaimsTransformation, KeycloakClaimsTransformation>()` in `Startup.ConfigureServices`.
- Tests at this point: a locally-signed JWT with a `UserId` claim and a matching `divoid_user` row → 200 on a `[Authorize(Policy="read")]` endpoint, with the row's `Permissions` driving the policy.

**M4 — Negative-path coverage**

Add tests for each of:

- JWT with valid signature, missing `UserId` claim → 403 on any permissioned endpoint.
- JWT with valid signature, `UserId` claim, but no `divoid_user` row → 403.
- JWT with valid signature, `UserId` claim, `divoid_user.Enabled=false` → 403.
- JWT signed by an unknown key → 401.
- JWT with `exp` in the past → 401.
- JWT with wrong audience → 401.
- ApiKey scheme still works in the same test host (cross-scheme regression).

**M5 — Test fixture for local JWT signing**

The implementer must set up:

- A test-only `RSA` key generated in the test fixture (process-lifetime).
- A small in-process `HttpMessageHandler` that intercepts requests to `<Authority>/.well-known/openid-configuration` and `<Authority>/protocol/openid-connect/certs` (or whichever JWKS path Keycloak uses, verified once) and returns the test key's JWK.
- Wire that handler into `JwtBearerOptions.BackchannelHttpHandler`.
- A helper that mints test tokens with arbitrary claims, signed with the same key.

This approach exercises the real `JwtBearer` middleware path — meaningfully better than mocking `IAuthenticationService` outright. (Mock alternative is acceptable if the in-process JWKS plumbing turns out to be heavier than expected; flag it in the PR and we can revisit.)

**M6 — Documentation**

- Update `Backend/CLAUDE.md` (project instructions) only if a section's claim becomes outdated by this change. Likely additions:
  - A short note in the "Routing" or "Architecture" section that JWT bearer tokens are accepted in addition to API keys.
  - A note that `Keycloak:Audience` is a startup-required config value when auth is on.
- The `docs/architecture/auth-and-bootstrap.md` document already exists; update it to cross-reference this design, do not duplicate.

**M7 — File the follow-up tasks before opening the PR**

Create the following DiVoid `task` nodes, linked to project 3:

1. **"Configure Keycloak user-attribute mapper for `UserId` on the `DiVoid` client (master realm)"** — Keycloak admin step. Confirms A2, A3. Required before any human can authenticate in prod.
2. **"Set `Keycloak:Audience` in DiVoid production config to the `DiVoid` Keycloak client_id"** — populates A4.
3. **"Add `/api/auth/whoami` endpoint for the upcoming frontend"** — returns the resolved DiVoid user id + permissions for the current principal. Trivial once this PR lands.
4. **"Cache claims-transformation DB lookup"** — in-process LRU keyed on DiVoid user id, ~30s TTL, invalidate on PATCH /api/users/{id}. Only if profiling shows the per-request DB hit is material.
5. **"Decide whether to layer Keycloak realm/client roles onto DiVoid permissions"** — option (b) from section 11.1, for if/when DiVoid permission granularity grows beyond `admin/write/read`.
6. **"Auto-provision DiVoid user on first JWT sight"** — only if the manual provisioning step becomes operationally painful. Includes a decision on how much PII to trust from Keycloak claims.

**PR-scope reminder.** This is one PR, one feature: backend-side OIDC auth. Anything that needs the frontend, anything that needs a Keycloak realm-config change, anything that adds new admin surfaces — out of scope, on the follow-up list.
