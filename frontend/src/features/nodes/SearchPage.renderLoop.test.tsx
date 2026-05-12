// @vitest-environment happy-dom
/**
 * Regression test: SearchPage mount must not trigger an infinite render loop.
 *
 * Bug (fixed in commit 2bd292c): CreateNodeDialog called mutation.reset() inside
 * a useEffect with [open, reset, mutation] deps. TanStack Query recreates the
 * mutation object every render, so the effect fired → mutation.reset() changed
 * state → new render → new mutation object → effect fired again → loop.
 *
 * The bug fires the moment /search mounts — before any user interaction —
 * because SearchPage always mounts CreateNodeDialog with open={false}, and the
 * buggy useEffect ran on every render from the first mount.
 *
 * This test mounts the REAL CreateNodeDialog (no mock) so the harness in
 * src/test/setup.ts can catch "Maximum update depth exceeded" from console.error
 * and fail the test if the loop is reintroduced.
 *
 * Load-bearing proof recorded on PR #42:
 *   Negative: reverting the fix → the render loop spins so fast it exhausts the
 *     V8 heap before React can log "Maximum update depth exceeded". The worker
 *     process crashes with: "FATAL ERROR: Ineffective mark-compacts near heap
 *     limit — Allocation failed — JavaScript heap out of memory". Test result:
 *     hard failure (worker exits unexpectedly). Even with happy-dom the OOM fires
 *     in ~50 s; the loop is genuine and unambiguous.
 *   Positive: fix present → test passes in ~90 ms, no console.error calls, no
 *     harness trigger.
 *
 * DiVoid node #273.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────
// Minimal surface: whoami (to gate the "New node" button + ensure write perms
// so canWrite=true, maximising dialog mount surface) + empty node list.

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1,
      name: 'Toni',
      email: 'toni@mamgo.io',
      enabled: true,
      createdAt: '2026-01-01T00:00:00Z',
      permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, () =>
    HttpResponse.json({ result: [], total: 0 }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────
// NOTE: CreateNodeDialog is intentionally NOT mocked here.
// The whole point of this test is that the real dialog mounts and
// does NOT trigger an infinite render loop.

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    user: { access_token: 'test-token' },
    signinRedirect: vi.fn(),
    signinSilent: vi.fn().mockResolvedValue(undefined),
  })),
}));

vi.mock('@/lib/constants', () => ({
  API_BASE_URL: BASE_URL,
  API: {
    USERS: { ME: '/users/me' },
    NODES: {
      LIST: '/nodes',
      DETAIL: (id: number) => `/nodes/${id}`,
      CONTENT: (id: number) => `/nodes/${id}/content`,
      LINKS: (id: number) => `/nodes/${id}/links`,
      UNLINK: (s: number, t: number) => `/nodes/${s}/links/${t}`,
    },
    HEALTH: '/health',
  },
  ROUTES: {
    HOME: '/',
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

// ─── Import after mocks ───────────────────────────────────────────────────────

let SearchPage: typeof import('./SearchPage').SearchPage;

beforeAll(async () => {
  const mod = await import('./SearchPage');
  SearchPage = mod.SearchPage;
});

// ─── Helper ───────────────────────────────────────────────────────────────────

function renderPage() {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={qc}>
        <SearchPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Test ─────────────────────────────────────────────────────────────────────

describe('SearchPage — render loop regression (real CreateNodeDialog)', () => {
  it('SearchPage_MountsWithoutInfiniteRenderLoop', async () => {
    // Mount the page. The render-stability harness in src/test/setup.ts
    // intercepts console.error in afterEach and throws if
    // "Maximum update depth exceeded" appears — no explicit assertion needed.
    //
    // The real CreateNodeDialog mounts unconditionally with open={false}.
    // If the buggy useEffect is present, React logs the loop error immediately
    // and the harness converts it to a hard test failure.
    renderPage();

    // Positive assertion: the page actually rendered something visible.
    // findByRole waits for async effects to settle (whoami query, etc.)
    // before the harness runs its afterEach check.
    const heading = await screen.findByRole('heading', { name: /search/i });
    expect(heading).toBeInTheDocument();
  });
});
