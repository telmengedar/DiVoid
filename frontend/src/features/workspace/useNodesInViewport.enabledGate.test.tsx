// @vitest-environment happy-dom
/**
 * Load-bearing tests for the empty-dimension `enabled` backstop in
 * useNodesInViewport (DiVoid #1981 CF-1, QA review #10854 items 1-2).
 *
 * Background: see DiVoid #1981 CF-1 and QA review #10854 for why a
 * per-dimension AND-gate is required here instead of the OR-across-both
 * -dimensions formula #1981 approach 2 sketches.
 *
 * Test 1 is the literal substitution #1981/#10854 specify: both dimensions
 * empty, no request fires. Tests 2-3 pin the single-dimension case per
 * dimension. Test 4 is the positive control proving the gate does not
 * over-suppress ordinary, non-empty filter states.
 *
 * NEGATIVE PROOF: reverting `enabled` back to `bounds !== null` makes
 * tests 1-3 fail (`nodeRequestCount` reaches 1 instead of staying at 0);
 * test 4 still passes, since it is the positive control — MSW's handler
 * increments a closure-scoped counter on every `/api/nodes?bounds=...`
 * call (§13.8), so the count is deterministic.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import { useNodesInViewport, type ViewportBounds, type ViewportFilterParams } from './useNodesInViewport';

let nodeRequestCount = 0;

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('bounds')) {
      nodeRequestCount += 1;
    }
    return HttpResponse.json({ result: [], total: 0 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  nodeRequestCount = 0;
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
    NODES: {
      LIST: '/nodes',
      DETAIL: (id: number) => `/nodes/${id}`,
      CONTENT: (id: number) => `/nodes/${id}/content`,
      LINKS: (id: number) => `/nodes/${id}/links`,
      UNLINK: (s: number, t: number) => `/nodes/${s}/links/${t}`,
      ADJACENCY: '/nodes/links',
    },
  },
}));

const BOUNDS: ViewportBounds = [0, 0, 100, 100];

function renderViewport(filters: ViewportFilterParams) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0 } } });
  return renderHook(() => useNodesInViewport(BOUNDS, filters), {
    wrapper: ({ children }) => (
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    ),
  });
}

describe('useNodesInViewport — empty-dimension enabled backstop (DiVoid #1981 CF-1 / #10854)', () => {
  it('does not fire when both the type and status dimensions are empty', async () => {
    renderViewport({ selectedTypes: [], selectedStatuses: [] });

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(nodeRequestCount).toBe(0);
  });

  it('does not fire when only the type dimension is empty (status still has a selection) — the reachable #10854 CF-1 scenario', async () => {
    renderViewport({ selectedTypes: [], selectedStatuses: ['open', 'new'] });

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(nodeRequestCount).toBe(0);
  });

  it('does not fire when only the status dimension is empty (type still has a selection)', async () => {
    renderViewport({ selectedTypes: ['task'], selectedStatuses: [] });

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(nodeRequestCount).toBe(0);
  });

  it('fires normally when both dimensions have a selection (positive control)', async () => {
    renderViewport({ selectedTypes: ['task'], selectedStatuses: ['open'] });

    await waitFor(() => expect(nodeRequestCount).toBe(1));
  });
});
