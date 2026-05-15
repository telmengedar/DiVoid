/**
 * Load-bearing tests for PR 7 — home-node-aware pill rows + auto-redirect (DiVoid task #400).
 *
 * Eight tests, each with positive + negative proof (DiVoid #275).
 *
 * 1. OrgPillRow with homeNodeId set queries ?linkedto=<homeId>&type=organization.
 *    Positive: homeNodeId=10, MSW asserts URL contains linkedto=10.
 *    Negative: remove linkedto branch → URL missing linkedto → test fails.
 *
 * 2. OrgPillRow with homeNodeId=null falls back to unfiltered query.
 *    Positive: homeNodeId=null, URL does NOT contain linkedto.
 *    Negative: always use linkedto → URL has linkedto param → test fails.
 *
 * 3. ProjectPillRow intersects against homeProjectIds.
 *    Positive: homeProjectIds=Set([3,4]), MSW returns [3,4,5] for the org, only 3+4 render.
 *    Negative: remove filter → pill 5 renders too → test fails.
 *
 * 4. ProjectPillRow with homeProjectIds=null renders all org projects.
 *    Positive: homeProjectIds=null, MSW returns [3,4,5], all three render.
 *    Negative: use empty set instead of null → zero pills → test fails.
 *
 * 5. TasksPage auto-redirects to first home project when /tasks + homeNodeId set.
 *    Positive: /tasks + homeNodeId=10 + home-projects=[3,4] → URL becomes /tasks/projects/3.
 *    Negative: remove redirect effect → URL stays /tasks → test fails.
 *
 * 6. No auto-redirect when homeNodeId=null.
 *    Positive: /tasks + homeNodeId=null → URL stays /tasks, empty-state renders.
 *    Negative: homeNodeId=null → no linkedto org request (proves guard active).
 *
 * 7. No auto-redirect when home-projects is empty.
 *    Positive: /tasks + homeNodeId=10 + home-projects=[] → URL stays /tasks.
 *    Negative: homeNodeId=10 + non-empty home-projects → redirect fires.
 *
 * 8. No auto-redirect when projectId already in URL.
 *    Positive: /tasks/projects/5 + homeNodeId=10 + home-projects=[3,4] → URL stays /tasks/projects/5.
 *    Negative: /tasks + homeNodeId=10 + home-projects=[3,4] → redirect fires.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, NodeDetails } from '@/types/divoid';

// ─── Module mocks (hoisted before all imports by vitest) ──────────────────────

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

/**
 * useWhoami mock: default returns homeNodeId=null (existing-test-safe baseline).
 * TasksPage tests that need homeNodeId=10 override via mockReturnValue before render.
 *
 * Tests 1–4 render OrgPillRow/ProjectPillRow directly with explicit props —
 * they do not call useWhoami at all.
 */
vi.mock('@/features/auth/useWhoami');

// ─── MSW server ───────────────────────────────────────────────────────────────

const pooshitOrgFixtures: Page<NodeDetails> = {
  result: [
    { id: 1, type: 'organization', name: 'Pooshit', status: null },
    { id: 2, type: 'organization', name: 'Mamgo', status: null },
  ],
  total: 2,
};

const orgProjectsAllFixtures: Page<NodeDetails> = {
  result: [
    { id: 3, type: 'project', name: 'DiVoid', status: null },
    { id: 4, type: 'project', name: 'Ocelot', status: null },
    { id: 5, type: 'project', name: 'Extra', status: null },
  ],
  total: 3,
};

const homeOrgFixtures: Page<NodeDetails> = {
  result: [
    { id: 1, type: 'organization', name: 'Pooshit', status: null },
    { id: 2, type: 'organization', name: 'Mamgo', status: null },
  ],
  total: 2,
};

const homeProjectFixtures: Page<NodeDetails> = {
  result: [
    { id: 3, type: 'project', name: 'DiVoid', status: null },
    { id: 4, type: 'project', name: 'Ocelot', status: null },
  ],
  total: 2,
};

const emptyPage: Page<NodeDetails> = { result: [], total: 0 };

const tasksGroupFixture: Page<NodeDetails> = {
  result: [{ id: 99, type: 'documentation', name: 'Tasks', status: null }],
  total: 1,
};

const taskFixtures: Page<NodeDetails> = {
  result: [{ id: 30, type: 'task', name: 'Fix login', status: 'open' }],
  total: 1,
};

function defaultNodesHandler({ request }: { request: Request }) {
  const url = new URL(request.url);
  const type = url.searchParams.get('type');
  const linkedto = url.searchParams.get('linkedto');
  const path = url.searchParams.get('path');
  const name = url.searchParams.get('name');

  if (linkedto && name === 'Tasks') return HttpResponse.json(tasksGroupFixture);
  if (path) return HttpResponse.json(taskFixtures);
  if (linkedto === '10' && type === 'organization') return HttpResponse.json(homeOrgFixtures);
  if (linkedto === '10' && type === 'project') return HttpResponse.json(homeProjectFixtures);
  if (linkedto === '1' && type === 'project') return HttpResponse.json({ result: [{ id: 3, type: 'project', name: 'DiVoid', status: null }, { id: 4, type: 'project', name: 'Ocelot', status: null }], total: 2 });
  if (linkedto === '3' && type === 'organization') return HttpResponse.json({ result: [{ id: 1, type: 'organization', name: 'Pooshit', status: null }], total: 1 });
  if (linkedto === '5' && type === 'organization') return HttpResponse.json({ result: [{ id: 1, type: 'organization', name: 'Pooshit', status: null }], total: 1 });
  if (type === 'organization') return HttpResponse.json(pooshitOrgFixtures);
  return HttpResponse.json(emptyPage);
}

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, defaultNodesHandler),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 3) return HttpResponse.json({ id: 3, type: 'project', name: 'DiVoid', status: null });
    if (id === 5) return HttpResponse.json({ id: 5, type: 'project', name: 'Extra', status: null });
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  sessionStorage.clear();
  // clearAllMocks resets call counts and queued returns but does NOT restore the
  // original function implementation — restoreAllMocks would undo the vi.mock hoisting
  // and cause the mock reference to return undefined.
  vi.clearAllMocks();
  // Re-apply the homeNodeId=null default so each test starts from a clean baseline.
  if (useWhoamiMock) {
    useWhoamiMock.mockReturnValue(makeWhoamiReturn(null) as never);
  }
});
afterAll(() => server.close());

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

function LocationCapture({ snapshot }: { snapshot: { value: string } }) {
  const location = useLocation();
  snapshot.value = location.pathname;
  return null;
}

/**
 * Builds a useWhoami mock return value.
 * @param homeNodeId - null for no home node, number for a set home node.
 */
function makeWhoamiReturn(homeNodeId: number | null) {
  return {
    data: {
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
      homeNodeId,
    },
    isLoading: false, isError: false, isSuccess: true,
    status: 'success' as const,
    error: null, fetchStatus: 'idle' as const,
    isFetching: false, isPending: false, isRefetching: false,
    refetch: vi.fn(), dataUpdatedAt: 0, errorUpdatedAt: 0,
    failureCount: 0, failureReason: null, isLoadingError: false,
    isPaused: false, isPlaceholderData: false, isRefetchError: false,
    isStale: false,
  };
}

// ─── Lazy imports (mocks must be registered before import) ────────────────────

let TasksPageComponent: typeof import('./TasksPage').TasksPage;
let OrgPillRowComponent: typeof import('./OrgPillRow').OrgPillRow;
let ProjectPillRowComponent: typeof import('./ProjectPillRow').ProjectPillRow;
let useWhoamiMock: ReturnType<typeof vi.fn>;

beforeAll(async () => {
  const [pageMod, orgMod, projMod, whoamiMod] = await Promise.all([
    import('./TasksPage'),
    import('./OrgPillRow'),
    import('./ProjectPillRow'),
    import('@/features/auth/useWhoami'),
  ]);
  TasksPageComponent = pageMod.TasksPage;
  OrgPillRowComponent = orgMod.OrgPillRow;
  ProjectPillRowComponent = projMod.ProjectPillRow;
  // Grab the mock function reference so tests can control return values.
  useWhoamiMock = vi.mocked(whoamiMod.useWhoami);
  // Set default: homeNodeId=null.
  useWhoamiMock.mockReturnValue(makeWhoamiReturn(null) as ReturnType<typeof whoamiMod.useWhoami>);
});

// ─── Test 1: OrgPillRow with homeNodeId set queries linkedto=<homeId> ─────────

describe('Test 1 — OrgPillRow with homeNodeId set queries ?linkedto=<homeId>&type=organization', () => {
  it('positive: homeNodeId=10 → request URL contains linkedto=10 and type=organization', async () => {
    let capturedUrl: string | null = null;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '10') capturedUrl = request.url;
        return HttpResponse.json(homeOrgFixtures);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} homeNodeId={10} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(capturedUrl).not.toBeNull());

    const url = new URL(capturedUrl!);
    expect(url.searchParams.get('linkedto')).toBe('10');
    expect(url.searchParams.get('type')).toBe('organization');
  });

  it('negative: homeNodeId=null → no request with linkedto=10 is made', async () => {
    let linkedtoRequestMade = false;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '10') linkedtoRequestMade = true;
        if (url.searchParams.get('type') === 'organization') return HttpResponse.json(pooshitOrgFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} homeNodeId={null} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText('Pooshit')).toBeInTheDocument());
    expect(linkedtoRequestMade).toBe(false);
  });
});

// ─── Test 2: OrgPillRow with homeNodeId=null falls back to unfiltered ──────────

describe('Test 2 — OrgPillRow with homeNodeId=null falls back to unfiltered query', () => {
  it('positive: homeNodeId=null → request URL has no linkedto param', async () => {
    let capturedUrl: string | null = null;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('type') === 'organization') capturedUrl = request.url;
        return HttpResponse.json(pooshitOrgFixtures);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} homeNodeId={null} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(capturedUrl).not.toBeNull());

    const url = new URL(capturedUrl!);
    expect(url.searchParams.has('linkedto')).toBe(false);
    expect(url.searchParams.get('type')).toBe('organization');
  });

  it('negative: homeNodeId=10 → request URL has linkedto=10', async () => {
    let capturedUrl: string | null = null;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '10' && url.searchParams.get('type') === 'organization') {
          capturedUrl = request.url;
        }
        return HttpResponse.json(homeOrgFixtures);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} homeNodeId={10} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(capturedUrl).not.toBeNull());
    expect(new URL(capturedUrl!).searchParams.get('linkedto')).toBe('10');
  });
});

// ─── Test 3: ProjectPillRow intersects against homeProjectIds ─────────────────

describe('Test 3 — ProjectPillRow intersects rendered pills against homeProjectIds', () => {
  it('positive: homeProjectIds=Set([3,4]), org returns [3,4,5] → only pills 3 and 4 render', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '1' && url.searchParams.get('type') === 'project') {
          return HttpResponse.json(orgProjectsAllFixtures);
        }
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <ProjectPillRowComponent orgId={1} selectedProjectId={undefined} homeProjectIds={new Set([3, 4])} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('project-pill-3')).toBeInTheDocument());
    expect(screen.getByTestId('project-pill-4')).toBeInTheDocument();
    expect(screen.queryByTestId('project-pill-5')).not.toBeInTheDocument();
    expect(screen.queryByText('Extra')).not.toBeInTheDocument();
  });

  it('negative: without homeProjectIds filter, all three projects render (including id=5)', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '1' && url.searchParams.get('type') === 'project') {
          return HttpResponse.json(orgProjectsAllFixtures);
        }
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <ProjectPillRowComponent orgId={1} selectedProjectId={undefined} homeProjectIds={undefined} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('project-pill-3')).toBeInTheDocument());
    expect(screen.getByTestId('project-pill-4')).toBeInTheDocument();
    expect(screen.getByTestId('project-pill-5')).toBeInTheDocument();
    expect(screen.getByText('Extra')).toBeInTheDocument();
  });
});

// ─── Test 4: ProjectPillRow with homeProjectIds=null renders all org projects ──

describe('Test 4 — ProjectPillRow with homeProjectIds=null renders all org projects', () => {
  it('positive: homeProjectIds=null → all three org projects render', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '1' && url.searchParams.get('type') === 'project') {
          return HttpResponse.json(orgProjectsAllFixtures);
        }
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <ProjectPillRowComponent orgId={1} selectedProjectId={undefined} homeProjectIds={null} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('project-pill-3')).toBeInTheDocument());
    expect(screen.getByTestId('project-pill-4')).toBeInTheDocument();
    expect(screen.getByTestId('project-pill-5')).toBeInTheDocument();
  });

  it('negative: homeProjectIds=empty Set → zero project pills render', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '1' && url.searchParams.get('type') === 'project') {
          return HttpResponse.json(orgProjectsAllFixtures);
        }
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <ProjectPillRowComponent orgId={1} selectedProjectId={undefined} homeProjectIds={new Set()} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 80));
    expect(screen.queryByTestId('project-pill-3')).not.toBeInTheDocument();
    expect(screen.queryByTestId('project-pill-4')).not.toBeInTheDocument();
    expect(screen.queryByTestId('project-pill-5')).not.toBeInTheDocument();
  });
});

// ─── Test 5: TasksPage auto-redirects to first home project ──────────────────

describe('Test 5 — TasksPage auto-redirects to first home project when /tasks + homeNodeId set', () => {
  /**
   * Positive: /tasks + homeNodeId=10 (via useWhoami mock) + home-projects=[3,4].
   * URL must end up at /tasks/projects/3 (first project in name-sorted set).
   */
  it('positive: /tasks + homeNodeId=10 + home-projects=[3,4] → URL becomes /tasks/projects/3', async () => {
    useWhoamiMock.mockReturnValue(makeWhoamiReturn(10) as never);

    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(locationSnapshot.value).toBe('/tasks/projects/3');
    }, { timeout: 3000 });
  });

  /**
   * Negative proof: homeNodeId=null → no redirect, URL stays at /tasks.
   */
  it('negative: homeNodeId=null → no redirect, URL stays /tasks, empty-state renders', async () => {
    // useWhoami returns homeNodeId=null (already the default from afterEach reset).
    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('tasks-empty-state')).toBeInTheDocument());
    expect(locationSnapshot.value).toBe('/tasks');
  });
});

// ─── Test 6: No auto-redirect when homeNodeId=null ────────────────────────────

describe('Test 6 — No auto-redirect when homeNodeId=null', () => {
  it('positive: homeNodeId=null → URL stays /tasks and empty-state is shown', async () => {
    // Default useWhoami returns homeNodeId=null.
    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('tasks-empty-state')).toBeInTheDocument());
    expect(locationSnapshot.value).toBe('/tasks');
  });

  /**
   * Negative proof: homeNodeId=null → no linkedto=10 org request.
   * Proves the null guard is what prevents both the filtered org query and the redirect.
   */
  it('negative: homeNodeId=null → org request has no linkedto param (guard is active)', async () => {
    let linkedtoOrgRequestMade = false;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '10' && url.searchParams.get('type') === 'organization') {
          linkedtoOrgRequestMade = true;
        }
        if (url.searchParams.get('type') === 'organization') return HttpResponse.json(pooshitOrgFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('tasks-empty-state')).toBeInTheDocument());
    expect(linkedtoOrgRequestMade).toBe(false);
  });
});

// ─── Test 7: No auto-redirect when home-projects is empty ────────────────────

describe('Test 7 — No auto-redirect when home-projects is empty', () => {
  it('positive: homeNodeId=10 + empty home-projects → URL stays /tasks', async () => {
    useWhoamiMock.mockReturnValue(makeWhoamiReturn(10) as never);

    // Override: home-node projects query returns empty.
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const linkedto = url.searchParams.get('linkedto');
        const type = url.searchParams.get('type');

        if (linkedto === '10' && type === 'project') return HttpResponse.json(emptyPage);
        if (linkedto === '10' && type === 'organization') return HttpResponse.json(homeOrgFixtures);
        if (type === 'organization') return HttpResponse.json(pooshitOrgFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Give the query time to resolve and the effect to run.
    await new Promise((r) => setTimeout(r, 200));

    // URL must stay at /tasks — redirect guard fires because set is empty.
    expect(locationSnapshot.value).toBe('/tasks');
    expect(screen.getByTestId('tasks-empty-state')).toBeInTheDocument();
  });

  it('negative: homeNodeId=10 + non-empty home-projects → redirect fires', async () => {
    useWhoamiMock.mockReturnValue(makeWhoamiReturn(10) as never);

    const locationSnapshot = { value: '/tasks' };

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(locationSnapshot.value).toBe('/tasks/projects/3');
    }, { timeout: 3000 });
  });
});

// ─── Test 8: No auto-redirect when projectId already in URL ─────────────────

describe('Test 8 — No auto-redirect when projectId already in URL', () => {
  /**
   * Positive: /tasks/projects/5 + homeNodeId=10 + home-projects=[3,4].
   * 5 is NOT in the home-projects set, and parsedProjectId is set → no redirect.
   */
  it('positive: /tasks/projects/5 + homeNodeId=10 + home-projects=[3,4] → URL stays /tasks/projects/5', async () => {
    useWhoamiMock.mockReturnValue(makeWhoamiReturn(10) as never);

    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks/projects/5']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Give home-projects query time to resolve.
    await new Promise((r) => setTimeout(r, 200));

    // URL must stay at /tasks/projects/5 (parsedProjectId guard prevents redirect).
    expect(locationSnapshot.value).toBe('/tasks/projects/5');
  });

  /**
   * Negative proof: /tasks (no projectId) + same homeNodeId=10 + home-projects=[3,4].
   * The redirect DOES fire, proving the guard is specifically parsedProjectId.
   */
  it('negative: /tasks + homeNodeId=10 + home-projects=[3,4] → redirect fires', async () => {
    useWhoamiMock.mockReturnValue(makeWhoamiReturn(10) as never);

    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(locationSnapshot.value).toBe('/tasks/projects/3');
    }, { timeout: 3000 });
  });
});
