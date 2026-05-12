# DiVoid Frontend

The web frontend for [DiVoid](https://divoid.mamgo.io) — a shared memory graph for humans and agents.

Architecture design: [`docs/architecture/frontend-bootstrap.md`](../docs/architecture/frontend-bootstrap.md)
DiVoid umbrella task: [node #223](https://divoid.mamgo.io/api/nodes/223)

## Stack

| Concern | Library |
|---|---|
| Build | Vite 8 + SWC |
| UI | React 19 + TypeScript |
| Styling | Tailwind CSS 4 |
| Components | Radix UI primitives |
| Auth | react-oidc-context + oidc-client-ts (Keycloak PKCE) |
| Data fetching | TanStack Query 5 |
| Routing | react-router-dom 7 |
| Forms | react-hook-form + zod |
| Toasts | sonner |
| Icons | lucide-react |
| Tests | Vitest + React Testing Library + MSW |

## Prerequisites

- Node 20+
- A running DiVoid backend (see `Backend/`) or access to the production instance at `https://divoid.mamgo.io/api`

## Running locally

```bash
cd frontend
npm install
cp .env.example .env.local   # fill in your values — see comments in .env.example
npm run dev                   # http://localhost:3000
```

The Keycloak `DiVoid` client must have `http://localhost:3000/callback` in **Valid Redirect URIs** and `http://localhost:3000` in **Web Origins** (confirm with the Keycloak admin).

## Environment variables

See `.env.example` for the full list of `VITE_*` variables and their descriptions.

| Variable | Required | Description |
|---|---|---|
| `VITE_KEYCLOAK_AUTHORITY` | yes | Keycloak issuer URL |
| `VITE_KEYCLOAK_CLIENT_ID` | yes | Keycloak client ID (`DiVoid`) |
| `VITE_API_BASE_URL` | yes | DiVoid backend API base URL |
| `VITE_OIDC_REDIRECT_URI` | yes | OIDC callback URI after login |
| `VITE_OIDC_POST_LOGOUT_REDIRECT_URI` | yes | OIDC redirect URI after logout |

## Scripts

| Command | Description |
|---|---|
| `npm run dev` | Start dev server at `localhost:3000` |
| `npm run build` | Type-check + production build |
| `npm run preview` | Preview the production build locally |
| `npm test` | Run Vitest test suite (single run) |
| `npm run test:watch` | Vitest in watch mode |
| `npm run lint` | TypeScript type-check (no emit) |

## Production deployment

The `Dockerfile` + `nginx.conf` build a static SPA served by nginx with an SPA fallback (`try_files $uri /index.html`) and a `/healthz` endpoint.

```bash
docker build \
  --build-arg VITE_KEYCLOAK_AUTHORITY=https://auth.mamgo.io/realms/master \
  --build-arg VITE_KEYCLOAK_CLIENT_ID=DiVoid \
  --build-arg VITE_API_BASE_URL=https://divoid.mamgo.io/api \
  --build-arg VITE_OIDC_REDIRECT_URI=https://divoid.mamgo.io/callback \
  --build-arg VITE_OIDC_POST_LOGOUT_REDIRECT_URI=https://divoid.mamgo.io/logout \
  -t divoid-frontend .
docker run -p 80:80 divoid-frontend
```
