# Architectural Document: DiVoid Web Frontend — Bootstrap, Keycloak Login, Graph Mode, Task Mode

**Author:** Sarah (software architect)
**Date:** 2026-05-12
**Status:** Proposed — supersedes nothing (greenfield)
**Umbrella task:** [DiVoid node #223](https://divoid.mamgo.io/api/nodes/223)
**Related design docs:** [#192 Keycloak backend auth](https://divoid.mamgo.io/api/nodes/192), [#116 graph-query feature](https://divoid.mamgo.io/api/nodes/116), [#71 free-graph philosophy](https://divoid.mamgo.io/api/nodes/71)
**Code-contracts reference (backend, applied here in spirit):** [#114](https://divoid.mamgo.io/api/nodes/114)

---

## 1. Problem Statement

DiVoid is a shared memory graph for humans and agents. Backend authentication via Keycloak landed in PRs #27–#31; the next step is a **human-facing web frontend** that lets Toni (and eventually other Keycloak-authenticated humans) log in and operate the graph from a browser at `divoid.mamgo.io`.

Two end-state UX modes are agreed:

1. **Free workspace mode** — a graph view of nodes connected to each other; create, edit, link, and drop files as nodes.
2. **Task management mode** — a per-project todo list with the classic open / in-progress / closed lifecycle.

This document is the **architecture for the bootstrap of that frontend** and the **PR-decomposition roadmap** that will get from "no repo" to those two end states in coherent, independently mergeable chunks.

**Success criteria for the bootstrap (PR 1):**

- A Vite / React 19 / TS / Tailwind 4 SPA lives in the `frontend/` subdirectory of the existing DiVoid repo at `C:\dev\claude\DiVoid\frontend\`, alongside the `Backend/` directory. **The frontend is not a separate repo** — Toni's call (2026-05-12): DiVoid is one product, the repo holds both halves.
- Visiting `localhost:3000` (dev) or `https://divoid.mamgo.io` (prod) redirects to Keycloak (master realm, `DiVoid` client), completes the Authorization Code + PKCE dance, and lands the user on an authenticated home page that displays *who they are* and *what permissions they have*.
- The frontend calls the DiVoid backend at `<API_BASE_URL>` with `Authorization: Bearer <access-token>` and round-trips at least one `GET` call against the authenticated principal (`GET /api/users/me`).
- Everything downstream of that bootstrap (graph view, task view, file drops) is **out of scope for PR 1** and is sequenced across PRs 2–5.

## 2. Scope & Non-Scope

### In scope (this document)

- Stack pick (concrete versions).
- Repo location and structure.
- Auth integration design (SPA-side OIDC PKCE against Keycloak).
- App shell, routing, layout, navigation.
- Data access patterns: TanStack Query hooks per DiVoid endpoint, with explicit hooks for each of the three retrieval modes (path-query, `linkedto`, semantic).
- Free-workspace mode (architecture only, library candidates with trade-offs).
- Task-management mode (architecture only).
- **PR decomposition roadmap** (PRs 1–5, each independently mergeable).
- Cross-cutting concerns: error UX, token handling, theming, accessibility, observability.
- Backend additions the design depends on (filed as sibling tasks).
- Deployment notes (dev origin, prod host, nginx, env vars).
- Testing strategy.

### Out of scope (this document)

- Final visual / UX design (mockups, exact component placement, copy).
- The graph-viz library *implementation choice* (architecture lists candidates with trade-offs; final pick happens in PR 4 once Pierre evaluates against a real seeded graph).
- Per-component prop tables.
- Implementation code of any kind.
- The Keycloak realm/client configuration itself (operated by the Keycloak admin — design *consumes* it; design *does not change* it).
- Auth0-style features that don't apply (organizations, MFA UI flows, etc.).
- Tenant / multi-organisation models — DiVoid is single-tenant today.

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Validation needed |
|---|---|---|---|
| A1 | Keycloak issuer is `https://auth.mamgo.io/realms/master`; OIDC discovery at `<issuer>/.well-known/openid-configuration` works. | High | n/a — confirmed by backend PR #27 working in prod. |
| A2 | The Keycloak `DiVoid` client has Authorization Code + PKCE enabled and accepts public-client redirects to `https://divoid.mamgo.io/*` and `http://localhost:3000/*`. | **Medium — must be validated** | Toni / Keycloak admin to confirm: Valid Redirect URIs, Valid Post Logout Redirect URIs, Web Origins. |
| A3 | The `DiVoid` client emits `userId` (camelCase) in the access token, and `aud="DiVoid"` is on the access token. | Confirmed via backend tests in PR #29. | n/a |
| A4 | Refresh-token rotation is on (Keycloak default for confidential clients; for public clients the operator can opt in). The frontend will *use* refresh tokens; this is the canonical SPA pattern post-2023. | **Medium — must be validated** | Toni / Keycloak admin to confirm: client access type `public`, "Standard Flow Enabled" on, refresh tokens enabled. |
| A5 | Backend API is reachable at `https://divoid.mamgo.io/api/*` and `http://localhost:5007/api/*` (or wherever the operator runs it). CORS is configured on the backend to allow `localhost:3000` and the prod origin. | Confirmed | Backend PR #34 added CORS for `localhost:3000` and `divoid.mamgo.io`. |
| A6 | The backend `GET /api/users/me` endpoint is implemented. | Confirmed | Merged in PR #35 (refactored from `GET /api/auth/whoami`). |
| A7 | DiVoid backend never issues tokens; it only validates them. The SPA is the OIDC relying party. | Hard constraint | n/a |
| A8 | DiVoid graph data is **not** secret-tier; standard browser-app posture (HTTPS, SameSite cookies if any, no localStorage for the access token) is sufficient. PII inside the graph (people nodes) is the limit; nothing approaches PCI/HIPAA. | Hard constraint | n/a |
| A9 | The visual / styling stack is the mamgo-customer-portal stack (Vite + React 19 + TS + Tailwind 4 + Radix UI + TanStack Query + react-router-dom 7 + framer-motion + lucide-react + sonner + Vitest). Only the auth client diverges (Keycloak, not Auth0). | Hard constraint | n/a |
| A10 | The frontend lives in `frontend/` inside the existing DiVoid repo at `C:\dev\claude\DiVoid\frontend\`, alongside the `Backend/` directory. Pierre's PR 1 branches off DiVoid `main` and brings in *both* this design doc (placed at `docs/architecture/frontend-bootstrap.md`) AND the Vite scaffolding in `frontend/`, in a single PR. | Confirmed | Toni, 2026-05-12. |

## 4. Architectural Overview

```
 Browser (SPA — DiVoid frontend/)
 ┌────────────────────────────────────────────────────────────────────┐
 │                                                                    │
 │  ┌──────────────┐    ┌──────────────────────┐    ┌──────────────┐ │
 │  │ React Router │◄───┤ App Shell + Layout   │───►│ Theme (CSS)  │ │
 │  │  (routes.tsx)│    │ Top nav / mode toggle│    │ next-themes  │ │
 │  └──────┬───────┘    └──────────┬───────────┘    └──────────────┘ │
 │         │                       │                                  │
 │         ▼                       ▼                                  │
 │  ┌──────────────────────────────────────────────────────────────┐ │
 │  │           Auth context (react-oidc-context)                  │ │
 │  │  - User + access token in MEMORY (not localStorage)          │ │
 │  │  - Silent refresh via refresh-token grant                    │ │
 │  │  - Login / logout via window.location to Keycloak            │ │
 │  └──────────────────────────────┬───────────────────────────────┘ │
 │                                 │ provides accessToken()           │
 │                                 ▼                                  │
 │  ┌──────────────────────────────────────────────────────────────┐ │
 │  │  Data layer (lib/api.ts + TanStack Query hooks)              │ │
 │  │                                                              │ │
 │  │   useNodeList(filter)        ── GET /api/nodes               │ │
 │  │   useNodeListLinkedTo(id, f) ── GET /api/nodes?linkedto=     │ │
 │  │   useNodePath(expr)          ── GET /api/nodes/path          │ │
 │  │   useNodeSemantic(query, f)  ── GET /api/nodes?query=        │ │
 │  │   useNode(id) / useNodeContent(id)                           │ │
 │  │   useWhoami()                ── GET /api/users/me            │ │
 │  │   useCreateNode / usePatchNode / useDeleteNode                │ │
 │  │   useLink / useUnlink / useUploadContent                     │ │
 │  └──────────────────────────────┬───────────────────────────────┘ │
 │                                 │ fetch(...) with Bearer header   │
 │                                 ▼                                  │
 │  ┌──────────────────────────────────────────────────────────────┐ │
 │  │  Toast / error surface (sonner)                              │ │
 │  │  Forms (react-hook-form + zod)                               │ │
 │  │  Routes:                                                     │ │
 │  │    /                  authenticated landing (whoami)         │ │
 │  │    /workspace         graph view (PR 4)                      │ │
 │  │    /tasks             task list (PR 5)                       │ │
 │  │    /tasks/:projectId  per-project tasks (PR 5)               │ │
 │  │    /search            cross-mode search surface (PR 2)       │ │
 │  │    /callback          OIDC redirect handler (PR 1)           │ │
 │  │    /logout            post-logout handler (PR 1)             │ │
 │  └──────────────────────────────────────────────────────────────┘ │
 └────────────────────────────────────────────────────────────────────┘
                │                                       │
                │ OIDC PKCE + refresh                   │ Bearer JWT
                ▼                                       ▼
   ┌────────────────────────────┐         ┌─────────────────────────────┐
   │ Keycloak                   │         │ DiVoid Backend              │
   │ auth.mamgo.io/realms/master│         │ divoid.mamgo.io/api/*       │
   │ client: DiVoid             │         │ JwtBearer validates token   │
   └────────────────────────────┘         └─────────────────────────────┘
```

Two distinct external dependencies, talked to independently:

- **Keycloak** for identity (logging in, refresh, logout). The SPA is the relying party; the backend never sees Keycloak directly.
- **DiVoid backend** for data. Every request carries the access token; the backend validates against Keycloak's JWKS and resolves it to a DiVoid user via the `KeycloakClaimsTransformation` from PR #27.

The frontend never proxies the backend through Keycloak or vice versa. Two flows, one origin (the browser).

## 5. Components & Responsibilities

### 5.1 App shell

- **Owns:** the top-level layout (header / sidebar / main / footer chrome), the global theme provider, the `QueryClientProvider`, the OIDC `AuthProvider`, the toast container, the router outlet, the mode-switch indicator (free workspace vs tasks).
- **Does NOT own:** any business logic, any data fetching, any feature-specific UI. Pure composition.

### 5.2 Auth context (`features/auth/`)

- **Owns:** the OIDC client instance (one per app), session lifecycle (login redirect, callback handling, silent refresh, logout), the in-memory access token, the `User` object derived from ID-token claims.
- **Exposes:** `useAuth()` hook returning `{ user, isAuthenticated, accessToken, login(), logout() }`.
- **Does NOT own:** API calls; it provides the token, nothing more. Permissions are *not* read from the ID token — they come from `/api/users/me`, see 5.3.

### 5.3 Whoami source-of-truth (`features/auth/useWhoami.ts`)

- **Owns:** the canonical "who is logged in to *DiVoid* as opposed to *Keycloak*" answer.
- A `useQuery` against `GET /api/users/me` returning `{ id, name, email, enabled, createdAt, permissions[] }`.
- **Rationale:** the access token's `userId` claim is enough for the *backend* to look up the row, but the *frontend* needs the DiVoid user shape too (display name, permission set for UI gating). The `/api/users/me` response is the canonical authority on what the user can do.
- **Does NOT** decode the JWT client-side for permissions. Permissions live on the DiVoid user row; the SPA reads them from `/api/users/me`, not from the token.

### 5.4 Data layer (`lib/api.ts` + `features/*/queries.ts`)

- **Owns:** a typed wrapper around `fetch` that:
  - injects `Authorization: Bearer <accessToken()>` on every call,
  - serialises query strings (including the array forms `?id=1,2,3` the backend's `ArrayParameterBinderProvider` accepts),
  - throws a typed `DivoidApiError { code, text, status }` on non-2xx,
  - surfaces 401 to the auth context (which triggers refresh or re-login),
  - surfaces other errors to the caller for toast / inline display.
- **Owns:** the typed DTO mirrors of backend models (`NodeDetails`, `Page<T>`, `PatchOperation`, error shape).
- **Owns:** one TanStack Query hook per logical data operation. The hooks live near the features that use them (`features/nodes/queries.ts`, `features/tasks/queries.ts`), but the underlying `fetch` shim is centralised in `lib/api.ts`.
- **Does NOT own:** caching policy beyond TanStack defaults — staleTime / refetchOnWindowFocus tuning is per-feature.

### 5.5 Three retrieval-mode hooks (canonical pattern)

Per DiVoid node #9, the backend exposes three retrieval modes; the frontend wraps each in its own typed hook:

| Hook | Backend call | Use when |
|---|---|---|
| `useNodePath(pathExpr, listFilter, options)` | `GET /api/nodes/path?path=...` | Topology is known — e.g. "open tasks for project X". |
| `useNodeListLinkedTo(id, filter, options)` | `GET /api/nodes?linkedto=<id>&...` | One-hop walk from a known id. |
| `useNodeSemantic(query, filter, options)` | `GET /api/nodes?query=<plain>&...` | Plain-language search, optionally narrowed by filter. Each result carries `similarity`. |

Plus the unscoped:

| Hook | Backend call |
|---|---|
| `useNodeList(filter)` | `GET /api/nodes` — bare listing (last-resort fallback per node #9 guidance). |
| `useNode(id)` | `GET /api/nodes/{id}` |
| `useNodeContent(id, contentType)` | `GET /api/nodes/{id}/content` — returns `Blob` or `string` based on type. |

### 5.6 Mutation hooks

| Hook | Backend call |
|---|---|
| `useCreateNode()` | `POST /api/nodes` |
| `usePatchNode(id)` | `PATCH /api/nodes/{id}` with `PatchOperation[]` body |
| `useDeleteNode(id)` | `DELETE /api/nodes/{id}` |
| `useLink()` | `POST /api/nodes/{id}/links` body: target id |
| `useUnlink()` | `DELETE /api/nodes/{sourceId}/links/{targetId}` |
| `useUploadContent(id, contentType)` | `POST /api/nodes/{id}/content` body: raw bytes |

All mutation hooks invalidate the corresponding list / get queries on success and surface errors via sonner toast.

### 5.7 Free-workspace mode (PR 4 — placeholder component now)

- **Owns:** the canvas view of a subgraph (starts at the user's "home node" or a selected pivot node, expands by walks), node creation via canvas double-click, link creation by dragging between nodes, file-as-node creation by HTML5 drop onto the canvas, viewport persistence per-user.
- **Does NOT own:** the underlying data fetching — uses the hooks from 5.5. Visualisation library renders the cached state.

### 5.8 Task-management mode (PR 5 — placeholder route now)

- **Owns:** per-project task list (filtered by `linkedto=<projectId>&type=task`), status-bucket layout, inline status PATCH, task creation form, task content (scope description) viewer/editor.
- **Does NOT own:** the underlying data fetching — uses the same hooks from 5.5 with `type=task` and `linkedto=<projectId>` filters.

## 6. Interactions & Data Flow

### 6.1 First-page-load happy path

```
1. Browser GET https://divoid.mamgo.io/.
2. nginx serves index.html (SPA fallback).
3. SPA boots. AuthProvider checks for an in-memory session — none.
4. AuthProvider redirects to Keycloak:
     https://auth.mamgo.io/realms/master/protocol/openid-connect/auth
       ?client_id=DiVoid
       &redirect_uri=https://divoid.mamgo.io/callback
       &response_type=code
       &scope=openid profile email
       &code_challenge=<S256(PKCE verifier)>
       &code_challenge_method=S256
       &state=<csrf>
5. User authenticates against Keycloak. Keycloak redirects back to
   /callback?code=<auth-code>&state=<csrf>.
6. /callback route consumes the auth-code (POST to token endpoint with the
   PKCE verifier, public client, no secret). Receives:
     { access_token, refresh_token, id_token, expires_in }.
7. AuthProvider stores access_token + refresh_token in memory only.
   Schedules silent refresh 30s before expiry.
8. AuthProvider redirects to "/" (or to the route the user originally
   requested, preserved in the state parameter).
9. App shell renders. useWhoami() fires GET /api/users/me with the
   access token; backend resolves the userId claim → divoid_user row →
   returns { id, name, email, enabled, createdAt, permissions[] }.
10. Landing page displays "Hello, <name>. Permissions: read, write."
```

### 6.2 Silent refresh

```
1. ~30s before access_token expiry, AuthProvider's refresh timer fires.
2. POST to Keycloak's token endpoint with grant_type=refresh_token and the
   current refresh_token. Public client → no secret.
3. Receive a new access_token (and rotated refresh_token if rotation is on).
4. Update in-memory tokens. TanStack Query's authorization header is now
   live for the next batch of calls.
```

### 6.3 401 on an API call

```
1. fetch() to /api/nodes returns 401.
2. lib/api.ts sees 401, throws DivoidApiError.
3. lib/api.ts also notifies AuthProvider (event bus or callback hook).
4. AuthProvider attempts a silent refresh.
   - On success: TanStack Query retries the failed query once with the new
     token. Transparent to the user.
   - On failure (refresh token also expired/revoked): full redirect to
     Keycloak login.
5. No infinite retry loop: max 1 refresh attempt per failed request.
```

### 6.4 Logout

```
1. User clicks logout in the header.
2. AuthProvider clears in-memory tokens.
3. Redirect to Keycloak end-session endpoint:
     <issuer>/protocol/openid-connect/logout?id_token_hint=<id_token>
       &post_logout_redirect_uri=https://divoid.mamgo.io/logout
4. Keycloak invalidates the session, redirects to /logout.
5. /logout renders a "you have been signed out" page with a "sign in
   again" button that triggers 6.1 step 4.
```

### 6.5 Three-mode retrieval in action (workspace mode)

```
1. User opens /workspace. The view pivots on a "home" node (the user's
   agent / person node, or the last viewed node).
2. useNodeListLinkedTo(homeId) populates the first ring.
3. User types in a search box: "auth token validation".
4. useNodeSemantic("auth token validation", { type: ["documentation"],
   linkedTo: [3] }) fires.
5. Results are merged into the canvas as a highlighted overlay.
6. User clicks a result. useNode(id) + useNodeListLinkedTo(id) expand it.
7. User wants "all open tasks under project DiVoid". They type a path
   expression in the advanced panel: [type:project,name:DiVoid]/[type:task,status:open].
8. useNodePath(expr) fires. Results render as a task-shaped ring on canvas.
```

### 6.6 Task-mode CRUD

```
1. User opens /tasks/3 (project DiVoid).
2. useNodeListLinkedTo(3, { type: ["task"] }) returns all tasks.
3. UI buckets them by status (new / open / in-progress / closed).
4. User clicks "create task". Modal opens with form (react-hook-form + zod
   for name validation). On submit:
     useCreateNode({ type: "task", name, status: "new" }) → returns id.
     useLink(id, 3) links it to the project.
     useUploadContent(id, "text/markdown") uploads the scope description.
5. Mutation invalidates the task-list query; the new task appears.
6. User drags a task from "open" to "in-progress". usePatchNode(id)
   sends [{ op: "replace", path: "/status", value: "in-progress" }].
   Optimistic update applied locally; rollback on error.
```

## 7. Data Model (Conceptual)

The frontend mirrors the backend's `NodeDetails` DTO. No frontend-only entities are introduced.

| Concept | Shape | Owned by |
|---|---|---|
| `NodeDetails` | `{ id: number, type: string, name: string, status: string \| null, similarity?: number, contentType?: string }` | Backend; frontend mirrors. |
| `Page<NodeDetails>` | `{ result: NodeDetails[], total: number, continue?: number }` | Backend; frontend mirrors. |
| `PatchOperation` | `{ op: "replace"\|"add"\|"remove"\|"flag"\|"unflag"\|"embed", path: string, value?: unknown }` | Backend; frontend mirrors. |
| `DivoidApiError` | `{ code: string, text: string, status: number }` | Frontend-side wrapper around backend's `{ code, text }` JSON. |
| `UserDetails` | `{ id: number, name: string, email: string \| null, enabled: boolean, createdAt: string, permissions: string[] }` | Backend endpoint `GET /api/users/me`; frontend mirrors. |
| `OidcSession` | `{ accessToken: string, refreshToken: string, idToken: string, expiresAt: number, user: OidcProfile }` | Frontend-only, in-memory, transient. |
| `OidcProfile` | Subset of ID-token claims (sub, preferred_username, name, email). | Frontend-only. |

**Important:** the frontend does **not** persist any of these in localStorage / sessionStorage / IndexedDB beyond what TanStack Query's in-memory cache holds for the duration of a session. Reload = re-fetch. (Token storage discussion in §9.2.)

## 8. Contracts & Interfaces (Abstract)

### 8.1 Frontend ↔ Keycloak (OIDC)

| Operation | Endpoint | Inputs | Outputs |
|---|---|---|---|
| Discovery | `GET <issuer>/.well-known/openid-configuration` | none | OIDC config (auth_endpoint, token_endpoint, end_session_endpoint, jwks_uri). |
| Authorize | redirect to `auth_endpoint` | `client_id=DiVoid`, `redirect_uri`, `response_type=code`, `scope=openid profile email`, `code_challenge`, `code_challenge_method=S256`, `state`. | Redirect back with `code` and `state`. |
| Token exchange | `POST token_endpoint` | `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id=DiVoid`, `code_verifier`. | `{ access_token, refresh_token, id_token, expires_in, token_type, scope }`. |
| Token refresh | `POST token_endpoint` | `grant_type=refresh_token`, `refresh_token`, `client_id=DiVoid`. | Same shape, possibly with rotated `refresh_token`. |
| End session | redirect to `end_session_endpoint` | `id_token_hint`, `post_logout_redirect_uri`. | Redirect back. |

Public client (no secret). PKCE required (S256). All HTTPS (`https://auth.mamgo.io/...`).

### 8.2 Frontend ↔ DiVoid Backend

The full HTTP contract is at [node #8](https://divoid.mamgo.io/api/nodes/8). The frontend consumes it as-is. Highlights:

| Endpoint | Method | Auth | Notes |
|---|---|---|---|
| `/api/nodes` | GET | Bearer | List with filters (`id`, `type`, `name`, `status`, `linkedto`, `nostatus`, `query`, paging). |
| `/api/nodes/path` | GET | Bearer | Path-query grammar from node #116. |
| `/api/nodes/{id}` | GET | Bearer | Single node. |
| `/api/nodes` | POST | Bearer (write) | Create. |
| `/api/nodes/{id}` | PATCH | Bearer (write) | JSON-Patch. |
| `/api/nodes/{id}` | DELETE | Bearer (write) | |
| `/api/nodes/{id}/links` | POST | Bearer (write) | Body: target node id. |
| `/api/nodes/{sourceId}/links/{targetId}` | DELETE | Bearer (write) | |
| `/api/nodes/{id}/content` | GET | Bearer | Binary or text content. |
| `/api/nodes/{id}/content` | POST | Bearer (write) | Raw bytes; `Content-Type` header sets type. |
| `/api/users/me` | GET | Bearer | Returns the DiVoid user (PR #35). Shape: `{ id, name, email, enabled, createdAt, permissions[] }`. |
| `/api/health` | GET | none | Liveness only. |

Error shape (all 4xx / 5xx with a body): `{ code: string, text: string }`. Status codes follow conventional REST. Frontend's `DivoidApiError` wraps both.

### 8.3 Configuration contract (frontend build / runtime)

| Key | Form | Required | Notes |
|---|---|---|---|
| `VITE_KEYCLOAK_AUTHORITY` | URL | yes | Default `https://auth.mamgo.io/realms/master`. |
| `VITE_KEYCLOAK_CLIENT_ID` | string | yes | Default `DiVoid`. |
| `VITE_API_BASE_URL` | URL | yes | `http://localhost:5007/api` (dev) or `https://divoid.mamgo.io/api` (prod). |
| `VITE_OIDC_REDIRECT_URI` | URL | yes | `<origin>/callback`. |
| `VITE_OIDC_POST_LOGOUT_REDIRECT_URI` | URL | yes | `<origin>/logout`. |

All `VITE_*` are baked into the bundle at build time. No runtime config injection in PR 1; if needed later, switch to a generated `config.json` served from nginx and fetched on app boot. (Out of scope for PR 1.)

## 9. Cross-Cutting Concerns

### 9.1 Auth client library — concrete pick

**Choice: `react-oidc-context` + `oidc-client-ts`.**

- Maintained, OIDC-spec-compliant, framework-agnostic core (`oidc-client-ts`) with a React-binding layer.
- Handles Authorization Code + PKCE, silent refresh via refresh token grant, end-session, and the iframe-based session check if ever needed.
- Works with any compliant IdP — not Keycloak-specific. If we ever migrate the IdP, this is one fewer thing to rewrite.
- Lightweight (~30KB gzipped), no realm-specific assumptions.

**Alternative considered: `@react-keycloak/web`.**

- Keycloak-specific bindings, smaller surface.
- Coupling: assumes Keycloak idioms (which is fine — Toni isn't planning to migrate IdPs).
- Slightly less active maintenance than `oidc-client-ts`.

**Verdict:** `react-oidc-context` is preferred because the abstraction-cost is negligible and the IdP-portability is a freebie. If, during PR 1, Pierre discovers a Keycloak-specific feature that `react-oidc-context` doesn't expose cleanly (e.g. realm-roles inspection), we can revisit — but the design contract here doesn't depend on it.

### 9.2 Token storage

| Token | Where | Why |
|---|---|---|
| Access token | **In memory only** (React state inside `AuthProvider`). | XSS-extractable from localStorage; in-memory is the standard SPA posture for short-lived tokens. |
| Refresh token | **In memory only.** | Same reason. Loss on tab close is acceptable — the user logs back in on next visit. |
| ID token | In memory only; used for `id_token_hint` on logout. | Same reason. |
| OIDC state / nonce / PKCE verifier | `sessionStorage` (cleared on tab close). | Required by the OIDC dance to survive the redirect from `/auth` back to `/callback`. Short-lived (<60s), not a token. |

**Explicit non-choice:** no localStorage for any token. The cost is "user must log in again on browser restart"; the benefit is "an XSS bug doesn't leak a long-lived credential." Worth it.

**Silent refresh strategy:** refresh-token grant against Keycloak's token endpoint, scheduled ~30 seconds before access-token expiry. No iframe-based silent SSO (deprecated post-2023 due to third-party-cookie restrictions; refresh-token grant is the modern equivalent).

### 9.3 Error UX

Three classes of error, three handling patterns:

| Class | Pattern | Example |
|---|---|---|
| Auth failure (401 from API, refresh failed) | Redirect to Keycloak login. No toast. | Token expired beyond refresh, user revoked. |
| Authz failure (403 from API) | Toast: "You don't have permission to do that." Inline disable UI controls preemptively when `whoami` lacks the permission. | Read-only user trying to PATCH. |
| Validation / data error (400, 404, 409 from API) | Toast with `error.text` from the backend's `{ code, text }` body. Inline form errors when applicable. | Bad PATCH path, missing node. |
| Network / 5xx | Toast: "Something went wrong. Please try again." Retry button. | Backend down. |

Toast library: `sonner` (already in the customer-portal stack).

**Permission-gated UI:** the `/api/users/me` response drives client-side gating. Buttons that would trigger a write are hidden / disabled when `permissions` lacks `write`. This is **defence-in-depth UX**, not a security boundary — the backend remains the source of truth and returns 403 if the user bypasses the UI.

### 9.4 Observability

- **Browser console** for development — `lib/api.ts` debug-logs requests when `import.meta.env.DEV`.
- **No production error-reporting service in PR 1** (Sentry et al). Filed as a follow-up sibling task.
- **Backend's logs** carry the authoritative audit trail; the frontend doesn't try to duplicate it.

### 9.5 Routing

Top-level routes (post-bootstrap target shape):

| Path | Purpose | Auth-gated | PR |
|---|---|---|---|
| `/` | Authenticated landing (whoami + recent activity). | yes | 1 |
| `/callback` | OIDC code exchange handler. | no | 1 |
| `/logout` | Post-logout landing. | no | 1 |
| `/search` | Cross-mode search surface (semantic + linkedto + path tabs). | yes | 2 |
| `/nodes/:id` | Node detail view (metadata + content + neighbours). | yes | 2 |
| `/workspace` | Free-workspace graph view. | yes | 4 |
| `/tasks` | Cross-project task overview. | yes | 5 |
| `/tasks/:projectId` | Per-project task list. | yes | 5 |

**Navigation:** top tabs for mode-switching (Workspace / Tasks), avatar dropdown for logout / profile. Tabs over sidebar because (a) two top-level modes only — sidebar wastes space, (b) consistent with customer-portal idioms.

### 9.6 Theming and accessibility

- Light + dark themes via `next-themes` + Tailwind class strategy (same as customer-portal).
- Radix UI primitives are accessible by default (ARIA, keyboard).
- Colour contrast verified per WCAG AA at the component level (Radix defaults pass; custom skins must be checked).
- All form inputs labelled; all interactive icons have `aria-label`.

### 9.7 Internationalisation

- **Not in PR 1.** English only. Customer-portal has an `i18n` layer; we'll mirror it when needed.
- File the i18n scaffolding as a follow-up if Toni wants German support before any other localisable user joins.

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed | Trade-off |
|---|---|---|
| **Time-to-first-screen** | Vite + esbuild dev, Vite production build with code splitting per route. Bootstrap PR ships with one route loaded eagerly; graph/task routes lazy-load. | We pay a small cold-start on first navigation to workspace/tasks. Acceptable for a tool used by one user. |
| **Auth security** | PKCE, public client, no localStorage tokens, refresh-token rotation (operator opt-in), `aud`-pinned backend. | We rely on the user's browser keeping tabs alive for the session. Mitigation: silent refresh + clean re-login on cold start. |
| **Backend backpressure** | TanStack Query default 5min stale time on lists, no polling for v1. Mutations invalidate only the relevant queries. | If multiple agents are mutating the graph concurrently, the SPA may show stale state until next navigation. Acceptable for v1. |
| **Bundle size** | Customer-portal stack is well-tree-shaken. Graph-viz library is the largest single dep — kept lazy. | The lazy-load on `/workspace` adds ~300ms to first-view time. Acceptable. |
| **Maintainability** | One stack, one auth client, one data layer, mirrored DTOs. Clean separation by feature folder. No global state library beyond TanStack Query (no Redux, no Zustand) — server state lives in the cache, UI state lives in components. | Adopting one of those would help if/when we have client-side game state. Not now. |
| **Testability** | Vitest + React Testing Library for hooks and components. MSW (Mock Service Worker) at the network boundary for integration-style tests. | Integration tests against a real DiVoid dev instance possible but optional; MSW with recorded fixtures is enough for PR 1–3. |
| **Operability** | Static SPA served via nginx; no SSR. Build artefact is the same in dev and prod. | We lose SSR-only features (SEO, fast first paint with data). Neither matters here — internal tool, authenticated only. |

### Alternatives rejected

| Choice | Why not |
|---|---|
| **Next.js (SSR)** | Adds a Node runtime in prod, more deploy complexity, no SEO value for an authenticated internal tool. Vite SPA is the right scope. |
| **Remix** | Same SSR concern. |
| **`@react-keycloak/web`** | Tight coupling to Keycloak; `react-oidc-context` is the same surface area with IdP portability. |
| **Zustand / Redux for global state** | Over-engineering. TanStack Query covers server state; React context covers auth; component state covers everything else. |
| **GraphQL client** | Backend is REST; no contract to bind to. Would add a translation layer that buys nothing. |
| **localStorage for tokens** | XSS risk for negligible UX benefit. |
| **Auth0** | Already-rejected by constraint. (Customer-portal uses Auth0 — DiVoid uses Keycloak.) |

## 11. Risks & Mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | The Keycloak `DiVoid` client is not configured for `http://localhost:3000/*` as Valid Redirect URI / Web Origin. | PR 1 dev login fails silently with a Keycloak error page. | A2 is flagged for explicit verification. **Pierre must confirm with a real `localhost:3000` test before opening PR 1.** If misconfigured, file a task on the Keycloak admin (Toni) and pause. |
| R2 | Refresh tokens are not enabled for the public `DiVoid` client. | Users get logged out every ~5 minutes (access-token lifetime). | A4 is flagged. If not enabled, file a Keycloak-side task and fall back to silent re-authorization via redirect until enabled. |
| R3 | Backend CORS does not allow the frontend origins. | Every API call from the SPA returns CORS error. | Resolved: backend PR #34 added CORS for `localhost:3000` and `https://divoid.mamgo.io`. |
| R4 | `/api/users/me` not implemented before PR 1. | Landing page has nothing to render. | Resolved: implemented in backend PR #35. |
| R5 | Graph viz library doesn't scale to >1000 nodes in canvas. | Workspace mode unusable as graph grows. | PR 4 evaluates against a seeded test graph that approximates expected scale (~5k nodes within 12 months). If candidate library fails, fall back. |
| R6 | A `permission` change (admin grants user `write`) doesn't reflect in the SPA until logout. | UX confusion. | `whoami` query refetches on window focus + on every mutation that touches `divoid_user`. Worst case: refresh fixes it. Documented limitation. |
| R7 | Long-running path queries time out via `CancellationToken` (per design #116) and present as 400 or hanging. | UX confusion. | `lib/api.ts` enforces a 30s client-side timeout that calls `AbortController.abort()`; surfaces as a friendly toast. |
| R8 | XSS via rendered node content (markdown with embedded HTML). | Token exfiltration. | Markdown rendering uses a sanitiser (`rehype-sanitize` or equivalent); arbitrary HTML is stripped. PR 2's responsibility when content rendering lands. |
| R9 | Drag-and-drop file upload sends huge files synchronously. | UI blocked, request times out. | PR 3 / PR 4: progress indicator + 10MB initial soft cap. File-as-node upload uses streaming `POST /api/nodes/{id}/content`. |
| R10 | The visualisation library leaks the graph data through dev-tools-readable globals. | Trivial, low-impact. | Standard React idioms keep state in component-local context. Document if any chosen library breaks this. |

## 12. Migration / Rollout Strategy

Greenfield — there is nothing to migrate. The rollout is the PR roadmap in §13.

**Sequencing constraint:** every PR after PR 1 is independently mergeable from a *frontend* perspective. All backend dependencies (CORS PR #34, users/me PR #35) are merged.

```
                      PR 1 (frontend bootstrap + login)   ← YOU ARE HERE
                        │
        ┌───────────────┼────────────────┬─────────────┐
        ▼               ▼                ▼             ▼
      PR 2          (parallel-able)    PR 4         PR 5
   (read paths)                       (graph)     (tasks)
        │
        ▼
      PR 3
   (write paths)
```

PRs 4 and 5 depend on PR 2 (the data layer for reads) and PR 3 (the data layer for writes). PR 4 and PR 5 are independent of each other and can be parallelised.

## 13. Open Questions

1. **(Q1)** What is the current Keycloak `DiVoid` client config? Need confirmation of:
   - Access type: must be `public`.
   - Valid Redirect URIs include `https://divoid.mamgo.io/callback` and `http://localhost:3000/callback`.
   - Valid Post-Logout Redirect URIs include the same with `/logout`.
   - Web Origins include `https://divoid.mamgo.io` and `http://localhost:3000` (or `+` for all valid redirect origins).
   - Standard Flow Enabled = on; Direct Access Grants Enabled = off (browser flows only); Service Accounts Enabled = off.
   - PKCE Code Challenge Method = `S256` (under "Advanced").
   - Refresh-token rotation: on or off? Determines whether we update the in-memory refresh token after each refresh.
2. **(Q2 — Resolved)** Backend CORS for `http://localhost:3000` and `https://divoid.mamgo.io`: added in PR #34.
3. **(Q3 — Resolved)** `/api/users/me` shape: `{ id, name, email, enabled, createdAt, permissions[] }`. Implemented in PR #35.
4. **(Q4)** Should the frontend host an `/api/health`-like local liveness page (e.g. `/healthz` served by nginx)? **Recommendation:** yes, see `nginx.conf` in the frontend directory. Included in PR 1.
5. **(Q5)** Does Toni want the OIDC `scope` to include `offline_access` for longer-lived refresh tokens, or stick with the default? **Recommendation:** default (no offline_access). Keep the session ephemeral; long-lived tokens are not a goal.
6. **(Q6)** For the graph view (PR 4), is force-directed layout the default, or do we want hierarchical / radial? **Recommendation:** force-directed for the default "explore" view; hierarchical mode for "show me the tree under this project node." Decided in PR 4.

## 14. Implementation Guidance for the Next Agent

### 14.1 PR decomposition

**PR 1 — Scaffolding + Keycloak login + authenticated landing (Pierre)** ← this PR

- **Scope:**
  - `npm create vite@latest` template, React 19 + TS + SWC.
  - Install the customer-portal stack (Tailwind 4, Radix UI, TanStack Query, react-router-dom 7, framer-motion, lucide-react, sonner, react-hook-form, zod) at the same major versions where compatible.
  - Install `react-oidc-context` + `oidc-client-ts`.
  - Folder layout: `src/{app,components,features,lib,styles,test,types}` (mirror customer-portal).
  - `App.tsx`, `main.tsx`, `routes.tsx`.
  - Theme provider + tailwind base + sonner toaster.
  - Routes: `/`, `/callback`, `/logout`.
  - `AuthProvider` configured against `VITE_KEYCLOAK_AUTHORITY` and `VITE_KEYCLOAK_CLIENT_ID`, public client, PKCE S256.
  - `useWhoami()` hook + landing-page render showing `Hello, <name>` and the permission set.
  - `lib/api.ts` with the Bearer-injecting fetch wrapper and `DivoidApiError`.
  - One Vitest unit test for the api layer (token injected into fetch), one for `useWhoami` (happy path via MSW).
  - CI: GitHub Actions workflow that runs `npm ci && npm run lint && npm test && npm run build` on every PR.
  - `Dockerfile` + `nginx.conf` modeled on customer-portal's (static SPA + `/healthz`).
  - `.env.example` with all `VITE_*` keys documented.
- **Endpoint used:** `GET /api/users/me` (not `/api/auth/whoami` — that was the original spec; PR #35 renamed it).
- **Dependencies:** all resolved — backend PR #34 (CORS) and PR #35 (users/me) are merged.

**PR 2 — Read paths: node list / search / detail (Pierre)**

- Implement `useNodeList`, `useNodeListLinkedTo`, `useNodePath`, `useNodeSemantic`, `useNode`, `useNodeContent`.
- Add `/search` route with three tabs: Semantic, Linked, Path.
- Add `/nodes/:id` route showing metadata + content + neighbours.
- Tests: hook-level tests with MSW for each of the three retrieval modes.
- **Dependencies:** PR 1.

**PR 3 — Write paths: create / patch / delete / link / unlink / upload (Pierre)**

- Implement mutation hooks with optimistic updates.
- Form wiring: react-hook-form + zod.
- Permission gating from `useWhoami`.
- **Dependencies:** PR 2.

**PR 4 — Free-workspace graph view (Pierre)**

- Add `/workspace` route with graph canvas.
- Library pick: `@xyflow/react` as starting point.
- **Dependencies:** PR 3.

**PR 5 — Task management view (Pierre)**

- Add `/tasks` and `/tasks/:projectId` routes.
- Status-bucket layout.
- **Dependencies:** PR 3.

### 14.2 Graph-viz library candidates (PR 4 decision)

| Library | Render | Strengths | Weaknesses |
|---|---|---|---|
| **`reactflow` / `@xyflow/react`** | SVG / Canvas hybrid | React-native API, well-maintained, declarative, good docs, light footprint, supports custom node renderers (rich React components per node). | Force layout is opt-in (not built-in) — needs a layout helper. Not optimised for >5k nodes without virtualisation. |
| **`cytoscape.js`** | Canvas | Mature, supports very large graphs (>50k nodes), rich layout algorithms built in, well-tested. | Not React-native — needs a wrapper. Custom node visuals are less ergonomic. |
| **`vis-network`** | Canvas | Easy, classic look, force-directed default. | Less actively maintained than the alternatives; less ergonomic React integration. |
| **`sigma.js`** | WebGL | Excellent perf on very large graphs. | Lower-level; less out-of-the-box visual polish. |

**Recommended starting point:** `@xyflow/react` (formerly `react-flow`).

### 14.3 Per-PR file map

```
src/
  app/
    App.tsx           # AuthProvider + QueryClientProvider + ThemeProvider + Router
    main.tsx
    routes.tsx        # route tree, lazy-loaded for /workspace and /tasks
  components/
    layout/           # AppShell, TopNav, UserMenu
    common/           # Button, etc. (Radix-based)
  features/
    auth/
      AuthProvider.tsx
      useWhoami.ts
      Callback.tsx      # /callback handler
      LogoutLanding.tsx
    nodes/
      queries.ts        # the read-side hooks (PR 2)
      mutations.ts      # the write-side hooks (PR 3)
      NodeDetail.tsx    # PR 2
      SearchPage.tsx    # PR 2
    workspace/
      WorkspacePage.tsx # PR 4
    tasks/
      TasksPage.tsx     # PR 5
      ProjectTasksPage.tsx
  lib/
    api.ts              # fetch wrapper + Bearer injection + error mapping
    cn.ts               # tailwind class merge
    constants.ts        # env-derived constants
  styles/
    globals.css
  test/
    setup.ts
    msw/
      handlers.ts
      server.ts
  types/
    divoid.ts           # NodeDetails, Page<T>, PatchOperation, UserDetails, DivoidApiError
```

---

*This document is the architectural contract for the DiVoid frontend bootstrap. It is mirrored as a DiVoid `documentation` node linked to umbrella task #223 and project #3. Updates to either copy should be reflected in the other.*
