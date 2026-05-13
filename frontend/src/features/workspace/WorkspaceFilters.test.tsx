// @vitest-environment happy-dom
/**
 * Load-bearing tests for workspace type + status filters (DiVoid #318, #275).
 *
 * ## What is tested
 *
 * 1. Filter-wiring positive proof (type filter):
 *    - Mount WorkspacePage with seeded data (viewportPageWithFilterFixtures).
 *    - Verify "First task" (type=task) renders by default.
 *    - Deselect "task" in the type filter → assert no task node renders.
 *    - Re-select "task" → assert "First task" reappears.
 *
 * 2. Default status exclusion positive proof:
 *    - Mount WorkspacePage with seeded data.
 *    - Without any user interaction, "Closed task" (status=closed) must NOT render.
 *    - This asserts the default status filter correctly excludes closed/fixed.
 *
 * 3. Negative proof (filter wiring revert):
 *    - When the filter is NOT wired into the hook (all nodes returned regardless
 *      of type/status), the closed task DOES render.
 *    - This test uses a different MSW handler that ignores type/status params.
 *    - It must FAIL if the closed node appears when it shouldn't.
 *
 * ## Filter wiring
 *
 * WorkspaceCanvas passes selectedTypes/selectedStatuses into useNodesInViewport
 * and useUntypedNodesInViewport. The MSW handler simulates backend filtering
 * by checking ?status and ?type params. The negative proof overrides the
 * handler to return all nodes unconditionally.
 *
 * DiVoid task #318, design doc #283.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import {
  BASE_URL,
  adjacencyPage,
  viewportPageWithFilterFixtures,
} from '@/test/msw/handlers';
import type { Page, PositionedNodeDetails } from '@/types/divoid';

// ─── MSW server — filter-aware handler ───────────────────────────────────────
//
// The handler simulates backend type/status filtering:
//  - If ?type is present, only nodes with matching types are returned.
//  - If ?status is present, only nodes with matching statuses are returned.
//  - If ?nostatus=true is present, null-status nodes are included too.
//  - Without filters, all nodes are returned (unfiltered viewport fetch).
//
// This mirrors the real backend behaviour so the filter wiring tests are
// testing actual API parameter passing, not just UI state.

function filterViewportPage(url: URL): Page<PositionedNodeDetails> {
  const typeParam   = url.searchParams.get('type');
  const statusParam = url.searchParams.get('status');
  const nostatus    = url.searchParams.get('nostatus') === 'true';

  let results = [...viewportPageWithFilterFixtures.result];

  if (typeParam) {
    const types = typeParam.split(',');
    results = results.filter((n) => n.type && types.includes(n.type));
  }

  if (statusParam || nostatus) {
    const statuses = statusParam ? statusParam.split(',') : [];
    results = results.filter((n) => {
      if (n.status === null) return nostatus;
      return statuses.includes(n.status);
    });
  }

  return { result: results, total: results.length };
}

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
      return HttpResponse.json(filterViewportPage(url));
    }
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.get(`${BASE_URL}/nodes/links`, () => HttpResponse.json(adjacencyPage)),
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
  http.post(`${BASE_URL}/nodes`, () =>
    HttpResponse.json({ id: 99, type: 'task', name: 'New node', status: 'open' }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  // Clear sessionStorage between tests so filter state doesn't bleed across tests.
  sessionStorage.clear();
});
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────

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

vi.mock('next-themes', () => ({
  useTheme: vi.fn(() => ({ resolvedTheme: 'dark', setTheme: vi.fn() })),
}));

// ─── Helpers ──────────────────────────────────────────────────────────────────

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

function renderPage() {
  const qc = makeQC();
  return render(
    <MemoryRouter initialEntries={['/workspace']}>
      <QueryClientProvider client={qc}>
        <WorkspacePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('WorkspaceFilters — type filter wiring (load-bearing positive proof)', () => {
  /**
   * POSITIVE PROOF:
   *
   * Mount with all types selected (default). "First task" appears.
   * Open type filter, deselect "task" → backend receives ?type without task →
   * MSW returns only non-task nodes → "First task" disappears.
   * Re-select "task" → "First task" reappears.
   *
   * This test FAILS if filter selections are not passed to useNodesInViewport.
   */
  it('deselecting task type hides task nodes; re-selecting shows them again', async () => {
    renderPage();

    // Wait for canvas and first batch of nodes.
    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Default state: task nodes visible.
    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Find and click the "Type" filter trigger button.
    const typeBtn = screen.getByRole('button', { name: /type filter/i });
    fireEvent.click(typeBtn);

    // Popover should open — find the "task" checkbox.
    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: /type filter options/i })).toBeInTheDocument();
    }, { timeout: 3000 });

    const taskCheckbox = screen.getByRole('checkbox', { name: /^task$/i });
    expect(taskCheckbox).toBeChecked();

    // Deselect task.
    fireEvent.click(taskCheckbox);
    expect(taskCheckbox).not.toBeChecked();

    // MSW now returns only non-task nodes → "First task" should not render.
    await waitFor(() => {
      expect(screen.queryByText('First task')).not.toBeInTheDocument();
    }, { timeout: 5000 });

    // Re-select task.
    fireEvent.click(taskCheckbox);
    expect(taskCheckbox).toBeChecked();

    // "First task" should reappear.
    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    }, { timeout: 5000 });
  });
});

describe('WorkspaceFilters — default status exclusion (load-bearing positive proof)', () => {
  /**
   * POSITIVE PROOF:
   *
   * Without any user interaction, "Closed task" (status=closed) must NOT appear
   * on the canvas. The default status filter excludes closed/fixed.
   *
   * The MSW handler respects ?status params and will not return closed nodes
   * when status=closed is absent from the query.
   *
   * This test FAILS if the default status filter is not wired correctly.
   */
  it('closed task is hidden without user interaction (default status filter)', async () => {
    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Wait long enough for data to load.
    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    }, { timeout: 5000 });

    // "Closed task" must NOT be visible (excluded by default status filter).
    expect(screen.queryByText('Closed task')).not.toBeInTheDocument();
  });
});

describe('WorkspaceFilters — negative proof (filter not wired)', () => {
  /**
   * NEGATIVE PROOF:
   *
   * Override MSW to return ALL nodes unconditionally (ignores type/status params).
   * This simulates what would happen if filter params were NOT passed to the hook.
   *
   * Expected: "Closed task" IS in the DOM — proving the default status filter
   * is the only thing preventing it from appearing in the positive test.
   *
   * Observable: the closed node appears when the backend ignores the filter.
   * If this test FAILS (closed task not in DOM), it means something else is
   * filtering it — investigate.
   */
  it('closed task renders when backend ignores status filter (negative proof)', async () => {
    // Override handler to return all nodes unconditionally.
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('bounds')) {
          return HttpResponse.json(viewportPageWithFilterFixtures);
        }
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // With the unfiltered handler, the closed task IS returned by the backend.
    // It will appear in the DOM because the frontend does not post-filter.
    await waitFor(() => {
      expect(screen.getByText('Closed task')).toBeInTheDocument();
    }, { timeout: 5000 });
  });
});

describe('WorkspaceFilters — sessionStorage persistence', () => {
  /**
   * Verify that filter selections survive a simulated remount (sessionStorage round-trip).
   *
   * This tests the loadSet/saveSet logic in useWorkspaceFilters without
   * exercising the full canvas — simpler and faster.
   */
  it('persists type filter selection to sessionStorage and reloads it', async () => {
    // Pre-seed sessionStorage with a partial selection (task deselected).
    const partial = ['bug', 'documentation', 'session-log', 'project', 'organization',
                     'agent', 'person', 'chat', 'feature', 'status', '__untyped__'];
    sessionStorage.setItem('divoid.workspace.typeFilter', JSON.stringify(partial));

    // Dynamically import the hook to get the sessionStorage-seeded value.
    const { useWorkspaceFilters: hook } = await import('./useWorkspaceFilters');

    // We test the hook in isolation via a tiny React component
    let capturedTypes: string[] | null = null;

    const { renderHook } = await import('@testing-library/react');
    const { result } = renderHook(() => hook());

    await act(async () => {
      capturedTypes = result.current.selectedTypes;
    });

    expect(capturedTypes).not.toBeNull();
    expect(capturedTypes).not.toContain('task');
    expect(capturedTypes).toContain('documentation');
    // typeFilterActive: selection differs from default (all selected)
    expect(result.current.typeFilterActive).toBe(true);
  });
});
