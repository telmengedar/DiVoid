// @vitest-environment happy-dom
/**
 * Load-bearing tests: workspace single-call fold (DiVoid #310 / #275).
 *
 * Two layers of proof:
 *
 * A. Pure-function layer (buildEdgesFromInlineLinks) — tests the edge
 *    reconstruction logic directly, mirroring the nodesSyncMerge.test.ts
 *    pattern. These are fast, deterministic, and exercise every scenario.
 *
 * B. Network integration layer — mounts WorkspacePage, asserts ZERO calls
 *    to /api/nodes/links, and at least ONE call to /api/nodes with
 *    `fields` containing "links". This proves the fold at the HTTP boundary.
 *
 * ## Load-bearing contract
 *
 * Part A FAILS when buildEdgesFromInlineLinks is reverted to a shape that
 * treats the adjacency-page result instead of inline links (e.g. falls back
 * to an empty edge set) — the positive assertions fail.
 *
 * Part B FAILS when WorkspaceCanvas reverts to useNodeAdjacency because:
 *   1. A request to /api/nodes/links is intercepted → adjacencyIntercepted.length > 0.
 *   2. The `fields` param no longer contains "links" (since the adjacency call
 *      is separate) — the fields assertion can detect this.
 *
 * DiVoid task #310 / #1213, rule #275.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, PositionedNodeDetails } from '@/types/divoid';

// ─── Part A: Pure-function unit tests ─────────────────────────────────────────
//
// This is the same pattern as nodesSyncMerge.test.ts: extract and test the pure
// transformation so the invariants are verified without mounting React.

describe('buildEdgesFromInlineLinks — pure edge reconstruction', () => {
  // Import lazily so the module initialises after vi.mock() wires are in place.
  // (The function itself has no side effects — the import is safe here.)

  it('emits an edge for each pair where both endpoints are visible', async () => {
    const { buildEdgesFromInlineLinks } = await import('./WorkspaceCanvas');

    const nodes: PositionedNodeDetails[] = [
      { id: 10, type: 'task', name: 'A', status: null, x: 0, y: 0, links: [20] },
      { id: 20, type: 'task', name: 'B', status: null, x: 0, y: 0, links: [10, 30] },
      { id: 30, type: 'task', name: 'C', status: null, x: 0, y: 0, links: [20] },
    ];
    const visibleIds = new Set(['10', '20', '30']);

    const edges = buildEdgesFromInlineLinks(nodes, visibleIds);

    expect(edges).toHaveLength(2);
    const ids = edges.map((e) => e.id).sort();
    expect(ids).toEqual(['10-20', '20-30']);
  });

  /**
   * NEGATIVE PROOF: reverting the `if (!visibleIds.has(tgtStr)) continue` guard
   * would include the out-of-viewport edge (10-99), making edges.length === 3 and
   * causing this test to fail.
   */
  it('suppresses edges where one endpoint is outside the visible set', async () => {
    const { buildEdgesFromInlineLinks } = await import('./WorkspaceCanvas');

    const nodes: PositionedNodeDetails[] = [
      // id=10 links to 20 (visible) and 99 (not in viewport).
      { id: 10, type: 'task', name: 'A', status: null, x: 0, y: 0, links: [20, 99] },
      { id: 20, type: 'task', name: 'B', status: null, x: 0, y: 0, links: [10] },
    ];
    const visibleIds = new Set(['10', '20']); // 99 NOT in the set

    const edges = buildEdgesFromInlineLinks(nodes, visibleIds);

    // Only the 10-20 edge survives; 10-99 is suppressed.
    expect(edges).toHaveLength(1);
    expect(edges[0].id).toBe('10-20');
    expect(edges.some((e) => e.id.includes('99'))).toBe(false);
  });

  /**
   * NEGATIVE PROOF: reverting the dedup logic (removing the `seen` set) would
   * emit two edges for the 10-20 pair (once from row 10, once from row 20),
   * making edges.length === 2 and causing the `toHaveLength(1)` assertion to fail.
   */
  it('deduplicates edges that appear in both endpoint rows', async () => {
    const { buildEdgesFromInlineLinks } = await import('./WorkspaceCanvas');

    const nodes: PositionedNodeDetails[] = [
      { id: 10, type: 'task', name: 'A', status: null, x: 0, y: 0, links: [20] },
      { id: 20, type: 'task', name: 'B', status: null, x: 0, y: 0, links: [10] },
    ];
    const visibleIds = new Set(['10', '20']);

    const edges = buildEdgesFromInlineLinks(nodes, visibleIds);

    // The edge 10-20 is seen twice (once per row) but emitted once.
    expect(edges).toHaveLength(1);
    expect(edges[0].id).toBe('10-20');
  });

  it('emits no edges for a node with empty links[]', async () => {
    const { buildEdgesFromInlineLinks } = await import('./WorkspaceCanvas');

    const nodes: PositionedNodeDetails[] = [
      { id: 40, type: 'task', name: 'D', status: null, x: 0, y: 0, links: [] },
    ];
    const visibleIds = new Set(['40']);

    const edges = buildEdgesFromInlineLinks(nodes, visibleIds);
    expect(edges).toHaveLength(0);
  });

  it('handles rows with no links field (absent — not opted in)', async () => {
    const { buildEdgesFromInlineLinks } = await import('./WorkspaceCanvas');

    // Simulate rows from a query that did NOT include fields=links.
    const nodes: PositionedNodeDetails[] = [
      { id: 1, type: 'task', name: 'X', status: null, x: 0, y: 0 },
      { id: 2, type: 'task', name: 'Y', status: null, x: 0, y: 0 },
    ];
    const visibleIds = new Set(['1', '2']);

    const edges = buildEdgesFromInlineLinks(nodes, visibleIds);
    // No links field → skip → no edges.
    expect(edges).toHaveLength(0);
  });
});

// ─── Part B: Network integration — fold proven at HTTP boundary ────────────────

const nodeListCalls: string[] = [];

const foldFixture: Page<PositionedNodeDetails> = {
  result: [
    { id: 10, type: 'task',          name: 'Node A', status: 'open', x: 100, y: 100, links: [20, 99] },
    { id: 20, type: 'documentation', name: 'Node B', status: null,   x: 200, y: 100, links: [10, 30] },
    { id: 30, type: 'project',       name: 'Node C', status: null,   x: 300, y: 100, links: [20] },
    { id: 40, type: 'task',          name: 'Node D', status: 'open', x: 400, y: 100, links: [] },
  ],
  total: 4,
};

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    nodeListCalls.push(url.search);
    if (url.searchParams.get('bounds')) return HttpResponse.json(foldFixture);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.get(`${BASE_URL}/types`, () =>
    HttpResponse.json({ result: [{ id: 6, type: 'task', count: 1 }], total: 1 }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  nodeListCalls.length = 0;
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), warning: vi.fn() } }));
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

describe('workspace fold: network proof (DiVoid #310 load-bearing)', () => {
  /**
   * POSITIVE PROOF — single-call fold at the network boundary:
   *
   * The canvas issues at least ONE call to /api/nodes with fields including "links",
   * and ZERO calls to /api/nodes/links.
   *
   * Load-bearing: revert WorkspaceCanvas to call useNodeAdjacency →
   *   1. A request fires to /api/nodes/links → adjacencyIntercepted.length > 0 → fails.
   *   2. The viewport call no longer includes "links" in fields → fails.
   */
  it('issues ONE /api/nodes call with fields=links; ZERO calls to /api/nodes/links', async () => {
    const adjacencyIntercepted: string[] = [];
    server.use(
      http.get(`${BASE_URL}/nodes/links`, ({ request }) => {
        adjacencyIntercepted.push(request.url);
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    const { WorkspacePage } = await import('./WorkspacePage');
    const qc = makeQC();

    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <WorkspacePage />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => screen.getByTestId('rf__wrapper'), { timeout: 5000 });
    await waitFor(() => screen.getByText('Node A'), { timeout: 5000 });

    await act(async () => {
      await new Promise<void>((resolve) => setTimeout(resolve, 300));
    });

    // ZERO calls to the old adjacency endpoint.
    expect(adjacencyIntercepted).toHaveLength(0);

    // At least one viewport call, and it must include "links" in fields.
    const viewportCalls = nodeListCalls.filter((qs) => qs.includes('bounds'));
    expect(viewportCalls.length).toBeGreaterThan(0);
    expect(viewportCalls.some((qs) => qs.includes('links'))).toBe(true);
  });
});
