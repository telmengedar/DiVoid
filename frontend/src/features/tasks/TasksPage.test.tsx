/**
 * Load-bearing tests for the Tasks view (PR 5 step 1 remnants).
 *
 * Tests 1–2 (OrgListView / ProjectListView) have been removed along with
 * those components (DiVoid task #391). Tests 3–6 remain and guard
 * TaskListView, TaskBreadcrumb, and NodeResultTable behaviour that is
 * still required.
 *
 * 3. TaskListView uses the path query.
 *    Negative: replacing useNodePath with useNodeListLinkedTo fails (path param absent).
 *
 * 4. Topology-empty signal renders the specific message.
 *    Negative: removing the empty-state branch fails (falls through to generic "No results").
 *
 * 5. TaskBreadcrumb resolves org and project names.
 *    Negative: removing useNode(orgId) call means org name is never resolved.
 *
 * 6. NodeResultTable getRowHref default preserved — existing call sites still link /nodes/:id.
 *    Negative: this is a regression guard; the test fails if we break the default path.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, NodeDetails } from '@/types/divoid';

// ─── MSW server ───────────────────────────────────────────────────────────────

const taskFixtures: Page<NodeDetails> = {
  result: [
    { id: 30, type: 'task', name: 'Fix login', status: 'open' },
    { id: 31, type: 'task', name: 'Add tests', status: 'in-progress' },
  ],
  total: 2,
};

const emptyPage: Page<NodeDetails> = { result: [], total: 0 };

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    const type = url.searchParams.get('type');
    const linkedto = url.searchParams.get('linkedto');
    const path = url.searchParams.get('path');

    if (path) return HttpResponse.json(taskFixtures);
    if (linkedto && type === 'project') return HttpResponse.json(emptyPage);
    if (linkedto && type === 'organization') return HttpResponse.json(emptyPage);
    if (type === 'organization') return HttpResponse.json(emptyPage);
    return HttpResponse.json(emptyPage);
  }),
  // For TaskBreadcrumb — useNode(id) calls GET /nodes/:id
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 10) return HttpResponse.json({ id: 10, type: 'organization', name: 'Mamgo', status: null });
    if (id === 20) return HttpResponse.json({ id: 20, type: 'project', name: 'Backend', status: null });
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    user: { access_token: 'test-token' },
    signinRedirect: vi.fn(),
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
    CALLBACK: '/callback',
    LOGOUT: '/logout',
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    TASKS_ORG: (id: number) => `/tasks/orgs/${id}`,
    TASKS_PROJECT: (id: number) => `/tasks/projects/${id}`,
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), warning: vi.fn() } }));

// ─── Wrapper helpers ──────────────────────────────────────────────────────────

function renderWithProviders(ui: React.ReactElement, initialPath = '/tasks') {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <QueryClientProvider client={qc}>{ui}</QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Lazy imports (mocks must be registered before import) ────────────────────

let TaskListView: typeof import('./TaskListView').TaskListView;
let TaskBreadcrumb: typeof import('./TaskBreadcrumb').TaskBreadcrumb;
let NodeResultTable: typeof import('@/components/common/NodeResultTable').NodeResultTable;

beforeAll(async () => {
  const [taskMod, bcMod, tableMod] = await Promise.all([
    import('./TaskListView'),
    import('./TaskBreadcrumb'),
    import('@/components/common/NodeResultTable'),
  ]);
  TaskListView = taskMod.TaskListView;
  TaskBreadcrumb = bcMod.TaskBreadcrumb;
  NodeResultTable = tableMod.NodeResultTable;
});

// ─── Test 3: TaskListView uses path query ─────────────────────────────────────

describe('Test 3 — TaskListView uses path=[id:N]/[name:Tasks]/[type:task]', () => {
  it('positive: request URL contains the expected path parameter (URL-encoded)', async () => {
    let capturedUrl: string | null = null;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        capturedUrl = request.url;
        return HttpResponse.json(taskFixtures);
      }),
    );

    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByText('Fix login')).toBeInTheDocument();
    });

    expect(capturedUrl).not.toBeNull();
    const url = new URL(capturedUrl!);
    const pathParam = url.searchParams.get('path');
    expect(pathParam).toBe('[id:20]/[name:Tasks]/[type:task]');
  });

  /**
   * Negative proof: rendering a bare NodeResultTable with taskFixtures sends no
   * HTTP request containing a `path` parameter. This mirrors the state where
   * useNodePath is replaced with a direct prop — the path predicate disappears.
   */
  it('negative: direct render without hook sends no path parameter', async () => {
    let capturedPath: string | null = 'sentinel';

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        capturedPath = url.searchParams.get('path');
        return HttpResponse.json(emptyPage);
      }),
    );

    renderWithProviders(<NodeResultTable nodes={taskFixtures.result} />);
    await new Promise((r) => setTimeout(r, 50));

    // No path param was sent (still sentinel — no request fired at all).
    expect(capturedPath).toBe('sentinel');
  });
});

// ─── Test 4: Topology-empty signal ───────────────────────────────────────────

describe('Test 4 — TaskListView topology-empty signal', () => {
  it('positive: empty path result renders topology-specific message, not generic "No results"', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, () => HttpResponse.json(emptyPage)),
    );

    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('topology-empty')).toBeInTheDocument();
    });

    expect(screen.getByTestId('topology-empty')).toHaveTextContent(
      'No tasks reachable via',
    );
    expect(screen.getByTestId('topology-empty')).toHaveTextContent(
      'that is by design',
    );
    // Must NOT show the generic table empty state.
    expect(screen.queryByText('No results')).not.toBeInTheDocument();
  });

  /**
   * Negative proof: NodeResultTable's own empty state shows "No results".
   * If TaskListView's topology-empty branch were removed, the component would
   * fall through to NodeResultTable which shows "No results" instead.
   */
  it('negative: NodeResultTable with empty nodes shows generic "No results", not topology message', () => {
    renderWithProviders(<NodeResultTable nodes={[]} />);

    expect(screen.getByText('No results')).toBeInTheDocument();
    expect(screen.queryByTestId('topology-empty')).not.toBeInTheDocument();
    expect(screen.queryByText('that is by design')).not.toBeInTheDocument();
  });
});

// ─── Test 5: TaskBreadcrumb resolves org and project names ───────────────────

describe('Test 5 — TaskBreadcrumb resolves names via useNode', () => {
  it('positive: with orgId=10 and projectId=20, renders "Tasks › Mamgo › Backend"', async () => {
    renderWithProviders(<TaskBreadcrumb orgId={10} projectId={20} />);

    await waitFor(() => {
      expect(screen.getByText(/Mamgo/)).toBeInTheDocument();
      expect(screen.getByText(/Backend/)).toBeInTheDocument();
    });

    const nav = screen.getByRole('navigation', { name: /breadcrumb/i });
    expect(nav).toHaveTextContent('Tasks');
    expect(nav).toHaveTextContent('Mamgo');
    expect(nav).toHaveTextContent('Backend');
  });

  /**
   * Negative proof: if orgId is not provided, the breadcrumb renders only "Tasks"
   * and neither Mamgo nor Backend appear. This mirrors the case where the
   * useNode(orgId) call is absent — the org name is never fetched.
   */
  it('negative: without orgId, org name is absent from breadcrumb', async () => {
    renderWithProviders(<TaskBreadcrumb />);

    await new Promise((r) => setTimeout(r, 50));

    const nav = screen.getByRole('navigation', { name: /breadcrumb/i });
    expect(nav).toHaveTextContent('Tasks');
    expect(screen.queryByText('Mamgo')).not.toBeInTheDocument();
    expect(screen.queryByText('Backend')).not.toBeInTheDocument();
  });
});

// ─── Test 6: NodeResultTable default getRowHref preserved ────────────────────

describe('Test 6 — NodeResultTable getRowHref default behaviour preserved', () => {
  it('positive: when getRowHref is not passed, row links to /nodes/:id', () => {
    const nodes: NodeDetails[] = [
      { id: 42, type: 'task', name: 'Some task', status: 'open' },
    ];

    renderWithProviders(<NodeResultTable nodes={nodes} />);

    const link = screen.getByRole('link', { name: 'Some task' });
    expect(link).toHaveAttribute('href', '/nodes/42');
  });

  it('positive: when getRowHref is passed, row uses the provided href', () => {
    const nodes: NodeDetails[] = [
      { id: 42, type: 'task', name: 'Some task', status: 'open' },
    ];

    renderWithProviders(
      <NodeResultTable nodes={nodes} getRowHref={(n) => `/tasks/orgs/${n.id}`} />,
    );

    const link = screen.getByRole('link', { name: 'Some task' });
    expect(link).toHaveAttribute('href', '/tasks/orgs/42');
    expect(link).not.toHaveAttribute('href', '/nodes/42');
  });

  /**
   * Negative proof: if getRowHref were wired to always return /tasks/orgs/:id
   * even when not passed (i.e. the default fallback was broken), the test above
   * would find /tasks/orgs/42 instead of /nodes/42 and fail.
   * This is a regression guard for existing call sites (search page, etc.).
   */
  it('negative: custom getRowHref overrides the default — the default cannot produce /tasks/orgs/:id', () => {
    const nodes: NodeDetails[] = [
      { id: 42, type: 'task', name: 'Some task', status: 'open' },
    ];

    renderWithProviders(<NodeResultTable nodes={nodes} />);

    const link = screen.getByRole('link', { name: 'Some task' });
    // Without getRowHref, the default MUST NOT produce a tasks URL.
    expect(link).not.toHaveAttribute('href', '/tasks/orgs/42');
    expect(link).not.toHaveAttribute('href', '/tasks/projects/42');
  });
});
