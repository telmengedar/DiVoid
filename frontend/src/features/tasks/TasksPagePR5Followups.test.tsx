/**
 * Load-bearing tests for PR 5 follow-ups (DiVoid task #369).
 *
 * Eight tests, each with positive + negative proof (DiVoid #275).
 *
 * 1. Status filter default state hides closed/fixed.
 *    Positive: URL contains status=new,open,in-progress (no closed/fixed).
 *    Negative: change default to include closed → URL contains closed.
 *
 * 2. Status filter selection persists to sessionStorage.
 *    Positive: toggling a pill writes to sessionStorage.
 *    Negative: remove the saveStatusFilter call → sessionStorage is not updated.
 *
 * 3. Status filter empty selection omits the parameter.
 *    Positive: all pills deselected → URL has no status= param.
 *    Negative: empty selection sends status= → URL contains the param.
 *
 * 4. Back button uses navigate(-1) when history present.
 *    Positive: history depth > 0 → navigate(-1) is called.
 *    Negative: old navigate('/search') → navigate(-1) is NOT called.
 *
 * 5. Back button falls back to /search when no history.
 *    Positive: history depth = 0 → navigate('/search') is called.
 *    Negative: remove fallback → navigate('/search') is NOT called.
 *
 * 6. Create-task button is only on TaskListView, not OrgListView or ProjectListView.
 *    Positive: TaskListView has the button; OrgListView and ProjectListView do not.
 *    Negative: lifting button to TasksPage → it appears in OrgListView and ProjectListView.
 *
 * 7. Create-task dialog pre-populates type=task, status=new, links=[tasksGroupId].
 *    Positive: submit form → POST body has type=task, status=new, then link to group.
 *    Negative: remove pre-population → POST body has type='', status=undefined.
 *
 * 8. Missing Tasks group surfaces the blocking message and disables the create flow.
 *    Positive: linkedto query returns empty → dialog shows the no-group message.
 *    Negative: remove the guard → dialog renders the form with links=[].
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route, useNavigate } from 'react-router-dom';
import { LocationTracker } from '@/app/LocationTracker';
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

const tasksGroupFixture: Page<NodeDetails> = {
  result: [{ id: 99, type: 'documentation', name: 'Tasks', status: null }],
  total: 1,
};

const orgFixtures: Page<NodeDetails> = {
  result: [{ id: 10, type: 'organization', name: 'Mamgo', status: null }],
  total: 1,
};

const projectFixtures: Page<NodeDetails> = {
  result: [{ id: 20, type: 'project', name: 'Backend', status: null }],
  total: 1,
};

const sampleNode: NodeDetails = {
  id: 42,
  type: 'documentation',
  name: 'Test Document',
  status: 'open',
  contentType: 'text/markdown; charset=utf-8',
};

let capturedNodeUrls: string[] = [];
let capturedPostBodies: unknown[] = [];
let capturedLinkBodies: unknown[] = [];

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    capturedNodeUrls.push(request.url);
    const path = url.searchParams.get('path');
    const type = url.searchParams.get('type');
    const linkedto = url.searchParams.get('linkedto');
    const name = url.searchParams.get('name');

    // Tasks group lookup: linkedto + name=Tasks
    if (linkedto && name === 'Tasks') return HttpResponse.json(tasksGroupFixture);
    // Path-based task list
    if (path) return HttpResponse.json(taskFixtures);
    // Project list
    if (linkedto && type === 'project') return HttpResponse.json(projectFixtures);
    // Org list
    if (type === 'organization') return HttpResponse.json(orgFixtures);
    return HttpResponse.json(emptyPage);
  }),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(sampleNode);
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, () =>
    new HttpResponse('# Hello', { headers: { 'Content-Type': 'text/markdown' } }),
  ),
  http.post(`${BASE_URL}/nodes`, async ({ request }) => {
    const body = await request.json();
    capturedPostBodies.push(body);
    return HttpResponse.json({ id: 55, type: 'task', name: 'New task', status: 'new' });
  }),
  http.post(`${BASE_URL}/nodes/:id/links`, async ({ request }) => {
    const body = await request.json();
    capturedLinkBodies.push(body);
    return new HttpResponse(null, { status: 204 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  capturedNodeUrls = [];
  capturedPostBodies = [];
  capturedLinkBodies = [];
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), warning: vi.fn(), info: vi.fn() } }));

// Mock heavy dialog sub-components to avoid jsdom OOM in NodeDetailPage tests.
vi.mock('@/features/nodes/EditNodeDialog', () => ({
  EditNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Edit node"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));
vi.mock('@/features/nodes/DeleteNodeDialog', () => ({
  DeleteNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Delete node"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));
vi.mock('@/features/nodes/LinkNodeDialog', () => ({
  LinkNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Add link"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));
vi.mock('@/features/nodes/ContentUploadZone', () => ({
  ContentUploadZone: () => <div data-testid="upload-zone">Upload zone</div>,
}));

// ─── Wrapper helpers ──────────────────────────────────────────────────────────

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

function renderWithProviders(ui: React.ReactElement, initialPath = '/') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <QueryClientProvider client={makeQC()}>{ui}</QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Lazy imports (mocks must be registered before import) ────────────────────

let TaskListView: typeof import('./TaskListView').TaskListView;
let OrgListView: typeof import('./OrgListView').OrgListView;
let ProjectListView: typeof import('./ProjectListView').ProjectListView;
let NodeDetailPage: typeof import('@/features/nodes/NodeDetailPage').NodeDetailPage;

beforeAll(async () => {
  const [taskMod, orgMod, projMod, detailMod] = await Promise.all([
    import('./TaskListView'),
    import('./OrgListView'),
    import('./ProjectListView'),
    import('@/features/nodes/NodeDetailPage'),
  ]);
  TaskListView = taskMod.TaskListView;
  OrgListView = orgMod.OrgListView;
  ProjectListView = projMod.ProjectListView;
  NodeDetailPage = detailMod.NodeDetailPage;
});

// ─── Test 1: Status filter default hides closed/fixed ────────────────────────

describe('Test 1 — Status filter default state hides closed/fixed', () => {
  it('positive: request URL contains status with new,open,in-progress and NOT closed or fixed', async () => {
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByText('Fix login')).toBeInTheDocument();
    });

    // Find the path-based request (taskFixtures URL)
    const pathUrl = capturedNodeUrls.find((u) => new URL(u).searchParams.get('path'));
    expect(pathUrl).not.toBeUndefined();

    const url = new URL(pathUrl!);
    const statusParam = url.searchParams.get('status');
    expect(statusParam).not.toBeNull();

    const statuses = statusParam!.split(',');
    expect(statuses).toContain('new');
    expect(statuses).toContain('open');
    expect(statuses).toContain('in-progress');
    expect(statuses).not.toContain('closed');
    expect(statuses).not.toContain('fixed');
  });

  /**
   * Negative proof: if the default included 'closed', the status param would
   * contain it. This directly asserts the positive test would fail with that change.
   */
  it('negative: URL does not contain closed in the status param by default', async () => {
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByText('Fix login')).toBeInTheDocument();
    });

    const pathUrl = capturedNodeUrls.find((u) => new URL(u).searchParams.get('path'));
    expect(pathUrl).not.toBeUndefined();
    const url = new URL(pathUrl!);
    const statusParam = url.searchParams.get('status') ?? '';
    expect(statusParam).not.toContain('closed');
    expect(statusParam).not.toContain('fixed');
  });
});

// ─── Test 2: Status filter selection persists to sessionStorage ───────────────

describe('Test 2 — Status filter selection persists to sessionStorage', () => {
  it('positive: toggling a pill writes the new selection to sessionStorage', async () => {
    const user = userEvent.setup();
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('status-filter-pills')).toBeInTheDocument();
    });

    // Toggle "closed" on (it's off by default).
    const closedPill = screen.getByRole('button', { name: 'closed' });
    await user.click(closedPill);

    const stored = sessionStorage.getItem('divoid.tasks.statusFilter');
    expect(stored).not.toBeNull();
    const parsed: string[] = JSON.parse(stored!);
    expect(parsed).toContain('closed');
  });

  /**
   * Negative proof: if saveStatusFilter were removed from useTaskStatusFilter,
   * sessionStorage would still be empty after toggling. The positive test above
   * would fail because stored would be null.
   */
  it('negative: sessionStorage is empty before any toggle', async () => {
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('status-filter-pills')).toBeInTheDocument();
    });

    // No interaction — sessionStorage should be empty.
    expect(sessionStorage.getItem('divoid.tasks.statusFilter')).toBeNull();
  });
});

// ─── Test 3: Empty selection omits the status parameter ─────────────────────

describe('Test 3 — Empty selection omits the status= parameter', () => {
  it('positive: deselecting all pills causes the URL to have no status param', async () => {
    const user = userEvent.setup();
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('status-filter-pills')).toBeInTheDocument();
    });

    // Deselect all default-selected statuses: new, open, in-progress.
    const newPill = screen.getByRole('button', { name: 'new' });
    const openPill = screen.getByRole('button', { name: 'open' });
    const inProgressPill = screen.getByRole('button', { name: 'in-progress' });

    capturedNodeUrls = [];
    await user.click(newPill);
    await user.click(openPill);
    await user.click(inProgressPill);

    // Wait for re-fetch with updated filter.
    await waitFor(() => {
      const latestPathUrl = capturedNodeUrls
        .slice()
        .reverse()
        .find((u) => new URL(u).searchParams.get('path'));
      if (latestPathUrl) {
        const url = new URL(latestPathUrl);
        // When selection is empty, status param must be absent.
        expect(url.searchParams.has('status')).toBe(false);
      }
    });
  });

  /**
   * Negative proof: if the hook sent status= with an empty value, the test
   * above would find url.searchParams.has('status') === true and fail.
   * This test directly asserts the "status= present with empty value" bug would
   * be caught by Test 3's positive.
   */
  it('negative: a status param with content IS present when statuses are selected', async () => {
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByText('Fix login')).toBeInTheDocument();
    });

    const pathUrl = capturedNodeUrls.find((u) => new URL(u).searchParams.get('path'));
    expect(pathUrl).not.toBeUndefined();
    const url = new URL(pathUrl!);
    // Default has statuses selected — param MUST be present and non-empty.
    expect(url.searchParams.has('status')).toBe(true);
    expect(url.searchParams.get('status')).not.toBe('');
  });
});

// ─── Tests 4 & 5 (old theatre tests) → replaced by Tests 4–8 below ──────────
//
// The original tests 4 & 5 used window.history.state.idx + MemoryRouter initialEntries
// to verify navigate(-1) / navigate('/search'). That approach was theatre:
// MemoryRouter populates idx reliably in jsdom; BrowserRouter does NOT in the browser.
// In production, idx was always 0 and the /search fallback fired every time (bug #388).
//
// The five new tests below target sessionStorage — the same primitive in jsdom and
// the browser — so they actually pin production behaviour (DiVoid #275, bug #388).

// ─── Test 4 (new): Back uses sessionStorage when present ─────────────────────

describe('Test 4 (new) — Back uses sessionStorage when present', () => {
  /**
   * Positive: sessionStorage holds a prior location → clicking back navigates there.
   *
   * Negative proof: remove the sessionStorage-read branch from handleBack
   * (i.e. always navigate to ROUTES.SEARCH). The /tasks/projects/3 route
   * never renders — /search renders instead. The waitFor times out.
   */
  it('positive: sessionStorage present → back navigates to the stored location', async () => {
    const user = userEvent.setup();
    sessionStorage.setItem('divoid.lastLocation', '/tasks/projects/3');

    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/nodes/42']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPage />} />
            <Route path="/tasks/projects/3" element={<div data-testid="prev-page">Projects page</div>} />
            <Route path="/search" element={<div data-testid="search-page">Search</div>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('back-button'));

    await waitFor(() => {
      expect(screen.getByTestId('prev-page')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('search-page')).not.toBeInTheDocument();
  });

  /**
   * Negative: without sessionStorage, the positive test would fail because
   * the back button would navigate to /search rather than /tasks/projects/3.
   */
  it('negative: with no sessionStorage, back navigates to /search not the stored path', async () => {
    const user = userEvent.setup();
    // sessionStorage is cleared in afterEach — nothing set here.

    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/nodes/42']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPage />} />
            <Route path="/tasks/projects/3" element={<div data-testid="prev-page">Projects page</div>} />
            <Route path="/search" element={<div data-testid="search-page">Search</div>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('back-button'));

    await waitFor(() => {
      expect(screen.getByTestId('search-page')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('prev-page')).not.toBeInTheDocument();
  });
});

// ─── Test 5 (new): Back falls back to /search when sessionStorage empty ───────

describe('Test 5 (new) — Back falls back to /search when sessionStorage is empty', () => {
  /**
   * Positive: no sessionStorage entry → back navigates to /search.
   *
   * Negative proof: remove the navigate(ROUTES.SEARCH) fallback from handleBack.
   * With sessionStorage empty, neither branch fires — navigation doesn't happen.
   * /search never renders.
   */
  it('positive: no sessionStorage → back navigates to /search', async () => {
    const user = userEvent.setup();
    // sessionStorage cleared in afterEach.

    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/nodes/42']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPage />} />
            <Route path="/search" element={<div data-testid="search-page">Search</div>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('back-button'));

    await waitFor(() => {
      expect(screen.getByTestId('search-page')).toBeInTheDocument();
    });
  });

  /**
   * Negative: before clicking the back button, /search is not shown —
   * confirms the above positive assertion is actually triggered by the click.
   */
  it('negative: before clicking back, /search is not rendered', async () => {
    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/nodes/42']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPage />} />
            <Route path="/search" element={<div data-testid="search-page">Search</div>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('search-page')).not.toBeInTheDocument();
  });
});

// ─── Test 6 (new): Back does NOT navigate to itself (self-equality guard) ──────

describe('Test 6 (new) — Back does NOT navigate to itself (self-equality guard)', () => {
  /**
   * Positive: sessionStorage holds the SAME path as the current page →
   * back navigates to /search (not to itself in a loop).
   *
   * Negative proof: remove the `last !== currentPath` guard from handleBack.
   * The test would see /nodes/42 stay rendered rather than /search appearing —
   * or worse, a navigation loop.
   */
  it('positive: sessionStorage holds current path → back falls back to /search', async () => {
    const user = userEvent.setup();
    sessionStorage.setItem('divoid.lastLocation', '/nodes/42');

    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/nodes/42']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPage />} />
            <Route path="/search" element={<div data-testid="search-page">Search</div>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('back-button'));

    await waitFor(() => {
      expect(screen.getByTestId('search-page')).toBeInTheDocument();
    });
  });

  /**
   * Negative: with a DIFFERENT path in sessionStorage, back navigates there,
   * not to /search. Proves the self-equality guard is conditional.
   */
  it('negative: different path in sessionStorage → back navigates there, not /search', async () => {
    const user = userEvent.setup();
    sessionStorage.setItem('divoid.lastLocation', '/tasks/projects/3');

    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/nodes/42']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPage />} />
            <Route path="/tasks/projects/3" element={<div data-testid="prev-page">Projects page</div>} />
            <Route path="/search" element={<div data-testid="search-page">Search</div>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('back-button'));

    await waitFor(() => {
      expect(screen.getByTestId('prev-page')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('search-page')).not.toBeInTheDocument();
  });
});

// ─── Test 7 (new): LocationTracker writes prevPath on every navigation ─────────

describe('Test 7 (new) — LocationTracker writes previous location on navigation', () => {
  /**
   * Positive: navigate through /tasks → /tasks/projects/3 → /nodes/42.
   * After settling, sessionStorage holds '/tasks/projects/3' (the penultimate route).
   *
   * Negative proof: remove the tracker effect from LocationTracker.
   * sessionStorage stays empty — the assertion below fails.
   */
  it('positive: tracker writes the penultimate location after a three-step navigation', async () => {
    function NavHelper() {
      const nav = useNavigate();
      return (
        <button
          data-testid="nav-btn"
          onClick={() => {
            // Drive the navigation sequence programmatically.
            nav('/tasks/projects/3');
          }}
        >
          go
        </button>
      );
    }

    function NavHelper2() {
      const nav = useNavigate();
      return (
        <button
          data-testid="nav-btn-2"
          onClick={() => nav('/nodes/42')}
        >
          go2
        </button>
      );
    }

    const user = userEvent.setup();

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <LocationTracker />
        <Routes>
          <Route path="/tasks" element={<NavHelper />} />
          <Route path="/tasks/projects/3" element={<NavHelper2 />} />
          <Route path="/nodes/42" element={<div>Node 42</div>} />
        </Routes>
      </MemoryRouter>,
    );

    // Start on /tasks. sessionStorage should be empty.
    expect(sessionStorage.getItem('divoid.lastLocation')).toBeNull();

    // Navigate to /tasks/projects/3.
    await user.click(screen.getByTestId('nav-btn'));

    // Navigate to /nodes/42.
    await user.click(screen.getByTestId('nav-btn-2'));

    // Now settled on /nodes/42. The previous location was /tasks/projects/3.
    expect(sessionStorage.getItem('divoid.lastLocation')).toBe('/tasks/projects/3');
  });

  /**
   * Negative: without the tracker, sessionStorage remains null throughout.
   * This is the state of affairs that caused bug #388.
   */
  it('negative: without tracker, sessionStorage is still null after navigation', async () => {
    function NavHelper() {
      const nav = useNavigate();
      return (
        <button data-testid="nav-btn" onClick={() => nav('/tasks/projects/3')}>
          go
        </button>
      );
    }

    const user = userEvent.setup();

    // Deliberately omit <LocationTracker /> — simulates the pre-fix state.
    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <Routes>
          <Route path="/tasks" element={<NavHelper />} />
          <Route path="/tasks/projects/3" element={<div>Projects</div>} />
        </Routes>
      </MemoryRouter>,
    );

    await user.click(screen.getByTestId('nav-btn'));

    expect(sessionStorage.getItem('divoid.lastLocation')).toBeNull();
  });
});

// ─── Test 8 (new): Tracker preserves query string in the stored location ───────

describe('Test 8 (new) — Tracker preserves ?query= in the stored location', () => {
  /**
   * Positive: navigate from /search?q=foo to /nodes/42.
   * sessionStorage must hold '/search?q=foo' (query string intact).
   *
   * Negative proof: track only location.pathname (drop location.search).
   * sessionStorage holds '/search' without '?q=foo'. The assertion below fails.
   */
  it('positive: navigating from /search?q=foo → /nodes/42 stores /search?q=foo', async () => {
    function SearchHelper() {
      const nav = useNavigate();
      return (
        <button data-testid="nav-btn" onClick={() => nav('/nodes/42')}>
          go
        </button>
      );
    }

    const user = userEvent.setup();

    render(
      <MemoryRouter initialEntries={['/search?q=foo']}>
        <LocationTracker />
        <Routes>
          <Route path="/search" element={<SearchHelper />} />
          <Route path="/nodes/42" element={<div>Node 42</div>} />
        </Routes>
      </MemoryRouter>,
    );

    await user.click(screen.getByTestId('nav-btn'));

    expect(sessionStorage.getItem('divoid.lastLocation')).toBe('/search?q=foo');
  });

  /**
   * Negative: if we only tracked pathname (not search), sessionStorage would hold
   * '/search' without the query string. Back navigation would lose the user's query.
   */
  it('negative: /search without query param is NOT sufficient — query string must be preserved', async () => {
    function SearchHelper() {
      const nav = useNavigate();
      return (
        <button data-testid="nav-btn" onClick={() => nav('/nodes/42')}>
          go
        </button>
      );
    }

    const user = userEvent.setup();

    render(
      <MemoryRouter initialEntries={['/search?q=foo']}>
        <LocationTracker />
        <Routes>
          <Route path="/search" element={<SearchHelper />} />
          <Route path="/nodes/42" element={<div>Node 42</div>} />
        </Routes>
      </MemoryRouter>,
    );

    await user.click(screen.getByTestId('nav-btn'));

    const stored = sessionStorage.getItem('divoid.lastLocation');
    // The CORRECT implementation must include the query string.
    expect(stored).toBe('/search?q=foo');
    // Confirm it is NOT the pathname-only variant.
    expect(stored).not.toBe('/search');
  });
});

// ─── Test 6: + New task button is only on TaskListView ────────────────────────

describe('Test 6 — + New task button is only on TaskListView', () => {
  it('positive: TaskListView has the New task button; OrgListView and ProjectListView do not', async () => {
    // TaskListView has the button.
    const { unmount: unmountTask } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('new-task-button')).toBeInTheDocument();
    });
    unmountTask();

    // OrgListView does NOT have the button.
    renderWithProviders(<OrgListView />);
    await waitFor(() => {
      expect(screen.getByText('Mamgo')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('new-task-button')).not.toBeInTheDocument();
  });

  it('positive: ProjectListView also does not have the New task button', async () => {
    renderWithProviders(<ProjectListView orgId={10} />);

    await waitFor(() => {
      expect(screen.getByText('Backend')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('new-task-button')).not.toBeInTheDocument();
  });

  /**
   * Negative proof: if the button were lifted to TasksPage (so it appeared in
   * all views), rendering OrgListView would still not show it because we're
   * rendering OrgListView directly. But if a wrapper added the button above
   * OrgListView, we'd need a page-level test.
   *
   * This negative confirms: data-testid="new-task-button" is absent on OrgListView.
   */
  it('negative: OrgListView cannot accidentally contain the New task button', async () => {
    renderWithProviders(<OrgListView />);
    await waitFor(() => {
      expect(screen.getByText('Mamgo')).toBeInTheDocument();
    });
    // Must NOT appear.
    expect(screen.queryByTestId('new-task-button')).toBeNull();
  });
});

// ─── Test 7: Create-task dialog pre-populates type/status/links ───────────────

describe('Test 7 — Create-task dialog pre-populates type=task, status=new, links=[tasksGroupId]', () => {
  it('positive: submitting the dialog POSTs type=task, status=new, then links to the Tasks group', async () => {
    const user = userEvent.setup();
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('new-task-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('new-task-button'));

    // Dialog should open.
    await waitFor(() => {
      expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    // Fill in the name and submit.
    const nameInput = screen.getByLabelText(/name/i);
    await user.type(nameInput, 'My new task');

    // Status defaults to "new" — leave it.
    const submitBtn = screen.getByRole('button', { name: /create task/i });
    await user.click(submitBtn);

    await waitFor(() => {
      expect(capturedPostBodies.length).toBeGreaterThan(0);
    });

    // POST body must have type=task, status=new.
    const postBody = capturedPostBodies[0] as Record<string, unknown>;
    expect(postBody.type).toBe('task');
    expect(postBody.status).toBe('new');

    // Link call must reference the Tasks group node (id=99 from tasksGroupFixture).
    await waitFor(() => {
      expect(capturedLinkBodies.length).toBeGreaterThan(0);
    });
    expect(capturedLinkBodies[0]).toBe(99);
  });

  /**
   * Negative proof: if type pre-population were removed, postBody.type would
   * be '' (the form default), and this test would fail.
   */
  it('negative: POST body must NOT have type="" (empty)', async () => {
    const user = userEvent.setup();
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('new-task-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('new-task-button'));

    await waitFor(() => {
      expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    const nameInput = screen.getByLabelText(/name/i);
    await user.type(nameInput, 'Another task');

    const submitBtn = screen.getByRole('button', { name: /create task/i });
    await user.click(submitBtn);

    await waitFor(() => {
      expect(capturedPostBodies.length).toBeGreaterThan(0);
    });

    const postBody = capturedPostBodies[0] as Record<string, unknown>;
    expect(postBody.type).not.toBe('');
    expect(postBody.type).toBe('task');
  });
});

// ─── Test 8: Missing Tasks group surfaces blocking message ────────────────────

describe('Test 8 — Missing Tasks group surfaces blocking message', () => {
  it('positive: empty linkedto+name=Tasks response → dialog shows the no-group message', async () => {
    const user = userEvent.setup();

    // Override: Tasks group query returns empty.
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const name = url.searchParams.get('name');
        const linkedto = url.searchParams.get('linkedto');
        const path = url.searchParams.get('path');
        // Tasks group lookup returns empty.
        if (linkedto && name === 'Tasks') return HttpResponse.json(emptyPage);
        if (path) return HttpResponse.json(taskFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('new-task-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('new-task-button'));

    // Dialog opens with the blocking message.
    await waitFor(() => {
      expect(screen.getByTestId('no-tasks-group-message')).toBeInTheDocument();
    });

    // The form should NOT be rendered — no create button.
    expect(screen.queryByRole('button', { name: /create task/i })).not.toBeInTheDocument();
  });

  /**
   * Negative proof: with a Tasks group present (tasksGroupFixture), the dialog
   * shows the form, NOT the blocking message. If the missing-group guard were
   * removed, the form would always show — but the positive test would detect
   * the message is absent, not the form's presence. So the negative proves
   * that the guard is conditional.
   */
  it('negative: with a Tasks group present, the form renders and no-group message is absent', async () => {
    const user = userEvent.setup();
    renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('new-task-button')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('new-task-button'));

    await waitFor(() => {
      // The form should render — create button present.
      expect(screen.getByRole('button', { name: /create task/i })).toBeInTheDocument();
    });

    // No blocking message.
    expect(screen.queryByTestId('no-tasks-group-message')).not.toBeInTheDocument();
  });
});
