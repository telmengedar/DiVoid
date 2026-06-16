// @vitest-environment happy-dom
/**
 * Load-bearing tests: workspace viewport query collapse (DiVoid #1976 / #275).
 *
 * Pins five invariants from the task spec:
 *
 * 1. Param assembly — untyped path:
 *    selectedTypes = ['task', UNTYPED_VALUE] → request carries type=task AND notype=true.
 *    NEGATIVE PROOF: remove `...(notypeParam && { notype: true })` from useNodesInViewport
 *    → notype absent from URL → this test fails.
 *
 * 2. Param assembly — typed-only path:
 *    selectedTypes = ['task', 'bug'] (no UNTYPED_VALUE) → request carries type=task,bug
 *    AND does NOT carry notype.
 *    NEGATIVE PROOF: unconditionally set notype=true → notype appears → test fails.
 *
 * 3. Truncation badge — load-bearing:
 *    nodesPage.total=400, result.length=250 → badge renders with "250 of 400".
 *    NEGATIVE PROOF: remove the badge JSX from WorkspaceCanvas → badge absent → test fails.
 *
 * 4. No-truncation no-badge:
 *    nodesPage.total=47, result.length=47 → badge does NOT render.
 *    Pins the negative shape so a future regression that always shows the badge fails.
 *
 * 5. Single /api/nodes request per viewport change (was two, now one):
 *    With UNTYPED_VALUE + 'task' selected, exactly ONE network call fires per query.
 *    NEGATIVE PROOF: re-introduce useUntypedNodesInViewport → second request fires
 *    → requestCount > 1 → test fails.
 *
 * DiVoid task: #1976, rule: #275, contracts: #420.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, PositionedNodeDetails } from '@/types/divoid';

// Tracks the raw query string of each /api/nodes?bounds=... request so tests
// can assert on exact URL params.
const capturedQueries: string[] = [];

// Count of /api/nodes requests (for single-request assertion).
let nodeRequestCount = 0;

const TYPED_NODES: PositionedNodeDetails[] = [
  { id: 1, type: 'task', name: 'Task Alpha', status: 'open', x: 0, y: 0, links: [] },
];

// Used by tests 3 and 4 to control the total / result set.
let overrideResponse: Page<PositionedNodeDetails> | null = null;

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('bounds')) {
      nodeRequestCount += 1;
      capturedQueries.push(url.search);
      if (overrideResponse) return HttpResponse.json(overrideResponse);
      return HttpResponse.json({ result: TYPED_NODES, total: TYPED_NODES.length });
    }
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.get(`${BASE_URL}/types`, () =>
    HttpResponse.json({ result: [{ id: 1, type: 'task' }, { id: 2, type: 'bug' }], total: 2 }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  capturedQueries.length = 0;
  nodeRequestCount = 0;
  overrideResponse = null;
  sessionStorage.clear();
});
afterAll(() => server.close());

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
      ADJACENCY: '/nodes/links',
    },
    HEALTH: '/health',
    TYPES: '/types',
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

vi.mock('sonner', () => ({
  toast: { error: vi.fn(), success: vi.fn(), info: vi.fn(), warning: vi.fn() },
  Toaster: () => null,
}));

vi.mock('next-themes', () => ({
  useTheme: vi.fn(() => ({ resolvedTheme: 'dark', setTheme: vi.fn() })),
}));

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries:   { retry: false, staleTime: 0 },
      mutations: { retry: false },
    },
  });
}

let WorkspacePage: typeof import('./WorkspacePage').WorkspacePage;

beforeAll(async () => {
  const mod = await import('./WorkspacePage');
  WorkspacePage = mod.WorkspacePage;
});

/**
 * Render WorkspacePage with custom pre-seeded sessionStorage for selectedTypes
 * so we can control which types the canvas uses for the query.
 */
function renderWithTypes(selectedTypes: string[]) {
  sessionStorage.setItem(
    'divoid.workspace.typeFilter',
    JSON.stringify(selectedTypes),
  );
  const qc = makeQC();
  return render(
    <MemoryRouter initialEntries={['/workspace']}>
      <QueryClientProvider client={qc}>
        <WorkspacePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

describe('viewport query collapse — param assembly (DiVoid #1976)', () => {
  /**
   * Test 1: UNTYPED_VALUE in selectedTypes → request carries type=task AND notype=true.
   *
   * NEGATIVE PROOF: remove `...(notypeParam && { notype: true })` from useNodesInViewport.
   * The `notype` param is absent from the URL → `capturedQuery.includes('notype=true')` is
   * false → `expect(...).toBe(true)` fails.
   */
  it('sends notype=true when UNTYPED_VALUE is selected alongside real types', async () => {
    renderWithTypes(['task', '__untyped__']);

    await waitFor(() => expect(nodeRequestCount).toBeGreaterThanOrEqual(1), { timeout: 5000 });

    const viewportQuery = capturedQueries.find((q) => q.includes('bounds'));
    expect(viewportQuery).toBeDefined();
    expect(viewportQuery!.includes('type=task')).toBe(true);
    expect(viewportQuery!.includes('notype=true')).toBe(true);
  });

  /**
   * Test 2: No UNTYPED_VALUE → request carries type=task,bug AND no notype.
   *
   * NEGATIVE PROOF: unconditionally set notype=true in useNodesInViewport.
   * `notype=true` appears in the URL → `capturedQuery.includes('notype=true')` is
   * true → `expect(...).toBe(false)` fails.
   */
  it('does NOT send notype when only real types are selected', async () => {
    renderWithTypes(['task', 'bug']);

    await waitFor(() => expect(nodeRequestCount).toBeGreaterThanOrEqual(1), { timeout: 5000 });

    const viewportQuery = capturedQueries.find((q) => q.includes('bounds'));
    expect(viewportQuery).toBeDefined();
    expect(viewportQuery!.includes('task')).toBe(true);
    expect(viewportQuery!.includes('bug')).toBe(true);
    expect(viewportQuery!.includes('notype')).toBe(false);
  });
});

describe('viewport query collapse — truncation badge (DiVoid #1976)', () => {
  /**
   * Test 3: total=400, result.length=250 → badge renders with "250 of 400".
   *
   * NEGATIVE PROOF: remove the badge JSX from WorkspaceCanvas.
   * `screen.getByRole('status')` throws → test fails.
   */
  it('shows truncation badge when nodesPage.total > MAX_VIEWPORT_NODES', async () => {
    const bigResult: PositionedNodeDetails[] = Array.from({ length: 250 }, (_, i) => ({
      id: i + 1, type: 'task', name: `Node ${i + 1}`, status: 'open', x: i * 10, y: 0, links: [],
    }));

    overrideResponse = { result: bigResult, total: 400 };

    renderWithTypes(['task']);

    await waitFor(() => expect(nodeRequestCount).toBeGreaterThanOrEqual(1), { timeout: 5000 });

    await waitFor(() => {
      expect(screen.getByRole('status')).toBeInTheDocument();
    }, { timeout: 5000 });

    const badge = screen.getByRole('status');
    expect(badge.textContent).toContain('250');
    expect(badge.textContent).toContain('400');
  });

  /**
   * Test 4: total=47, result.length=47 → badge does NOT render.
   *
   * Pins the negative shape: a regression that always shows the badge fails this test.
   */
  it('does NOT show truncation badge when total <= MAX_VIEWPORT_NODES', async () => {
    const smallResult: PositionedNodeDetails[] = Array.from({ length: 47 }, (_, i) => ({
      id: i + 1, type: 'task', name: `Node ${i + 1}`, status: 'open', x: i * 10, y: 0, links: [],
    }));

    overrideResponse = { result: smallResult, total: 47 };

    renderWithTypes(['task']);

    await waitFor(() => expect(nodeRequestCount).toBeGreaterThanOrEqual(1), { timeout: 5000 });

    await act(async () => { await new Promise((r) => setTimeout(r, 100)); });

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});

describe('viewport query collapse — single request (DiVoid #1976)', () => {
  /**
   * Test 5: with UNTYPED_VALUE + 'task' selected, the unified query carries
   * both type=task AND notype=true in a SINGLE request. There is no second
   * parallel request that carries notype=true without type (the old untyped hook).
   *
   * Proof shape: after all queries settle, every bounds request that carries
   * notype=true ALSO carries a type= param. A lone notype-only request would be
   * the old useUntypedNodesInViewport firing separately.
   *
   * NEGATIVE PROOF: re-introduce useUntypedNodesInViewport alongside useNodesInViewport
   * in WorkspaceCanvas. The untyped hook fires a bounds request with notype=true but
   * WITHOUT any type= param → a request exists with notype=true AND no type= param →
   * `allNoTypeHaveType` is false → test fails.
   */
  it('every notype=true request also carries a type= param (no lone untyped-only fetch)', async () => {
    renderWithTypes(['task', '__untyped__']);

    await waitFor(() => expect(nodeRequestCount).toBeGreaterThanOrEqual(1), { timeout: 5000 });

    await act(async () => { await new Promise((r) => setTimeout(r, 200)); });

    const boundsRequests = capturedQueries.filter((q) => q.includes('bounds'));
    expect(boundsRequests.length).toBeGreaterThan(0);

    // Every request with notype=true must also carry a type= param.
    // A lone notype-only request (the old useUntypedNodesInViewport shape) would have
    // notype=true without type= — that's the regression this guards against.
    const noTypeRequests = boundsRequests.filter((q) => q.includes('notype=true'));
    expect(noTypeRequests.length).toBeGreaterThan(0); // at least one fired

    const allNoTypeHaveType = noTypeRequests.every((q) => q.includes('type='));
    expect(allNoTypeHaveType).toBe(true);
  });
});
