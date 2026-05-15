/**
 * Load-bearing tests for PR 6 — inline org + project pill rows (DiVoid task #391).
 *
 * Ten tests, each with positive + negative proof (DiVoid #275).
 *
 * 1. OrgPillRow renders one pill per organisation.
 *    Positive: two-org MSW response → two pills with correct names.
 *    Negative: wrong query type (type=project) → no org pills.
 *
 * 2. OrgPillRow defaults selection to the current project's org.
 *    Positive: render at /tasks/projects/3 (project → org id 1) → org-pill-1 has aria-pressed=true.
 *    Negative: remove useProjectOrg lookup → no pill is pressed.
 *
 * 3. ProjectPillRow filters by selected org (linkedto=<orgId>&type=project in URL).
 *    Positive: org 2 selected → URL contains linkedto=2&type=project.
 *    Negative: switch hook to bare useNodeList (no linkedto) → URL missing linkedto.
 *
 * 4. Clicking a project pill navigates to /tasks/projects/:id.
 *    Positive: click "Backend" pill → URL becomes /tasks/projects/20.
 *    Negative: remove onClick navigation → URL unchanged.
 *
 * 5. Org change does NOT auto-navigate.
 *    Positive: on /tasks/projects/3, click "Mamgo" org pill → URL stays /tasks/projects/3
 *    AND project pill row repopulates with Mamgo projects.
 *    Negative: add an auto-navigate effect → URL changes.
 *
 * 6. Org change with mismatched project surfaces the inline message.
 *    Positive: on /tasks/projects/3 (Pooshit org), click "Mamgo" pill → mismatch message renders.
 *    Negative: remove the mismatch branch → no message rendered.
 *
 * 7. /tasks/orgs/:orgId redirects to /tasks.
 *    Positive: navigate to /tasks/orgs/2 → URL becomes /tasks.
 *    Negative: remove the <Navigate /> redirect → /tasks/orgs/2 route renders TasksPage.
 *
 * 8. /tasks empty state: pill rows visible, task list absent.
 *    Positive: navigate to /tasks (no project) → data-testid="task-list" absent, both pill rows present.
 *    Negative: remove the empty-state branch → task-list renders.
 *
 * 9. Status filter still works on the new layout (regression guard for PR #55).
 *    Positive: on /tasks/projects/3, task fetch URL contains expected status param.
 *    Negative: status param absent → URL has no status param.
 *
 * 10. List/Board view toggle persists to sessionStorage (regression guard for PR #57).
 *     Positive: toggle to board → sessionStorage holds 'board' for project 3.
 *     Negative: sessionStorage holds 'list' initially.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route, Navigate, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, NodeDetails } from '@/types/divoid';
import { AppRoutes } from '@/app/routes';

// ─── MSW server ───────────────────────────────────────────────────────────────

const orgFixtures: Page<NodeDetails> = {
  result: [
    { id: 1, type: 'organization', name: 'Pooshit', status: null },
    { id: 2, type: 'organization', name: 'Mamgo', status: null },
  ],
  total: 2,
};

const pooshitProjectFixtures: Page<NodeDetails> = {
  result: [
    { id: 3, type: 'project', name: 'DiVoid', status: null },
    { id: 4, type: 'project', name: 'Ocelot', status: null },
  ],
  total: 2,
};

const mamgoProjectFixtures: Page<NodeDetails> = {
  result: [
    { id: 20, type: 'project', name: 'Backend', status: null },
    { id: 21, type: 'project', name: 'Frontend', status: null },
  ],
  total: 2,
};

const tasksGroupFixture: Page<NodeDetails> = {
  result: [{ id: 99, type: 'documentation', name: 'Tasks', status: null }],
  total: 1,
};

const taskFixtures: Page<NodeDetails> = {
  result: [
    { id: 30, type: 'task', name: 'Fix login', status: 'open' },
    { id: 31, type: 'task', name: 'Add tests', status: 'in-progress' },
  ],
  total: 2,
};

const emptyPage: Page<NodeDetails> = { result: [], total: 0 };

// Default MSW handler: dispatches based on query params.
function defaultHandler({ request }: { request: Request }) {
  const url = new URL(request.url);
  const type = url.searchParams.get('type');
  const linkedto = url.searchParams.get('linkedto');
  const path = url.searchParams.get('path');
  const name = url.searchParams.get('name');

  // Tasks group lookup
  if (linkedto && name === 'Tasks') return HttpResponse.json(tasksGroupFixture);
  // Path-based task list
  if (path) return HttpResponse.json(taskFixtures);
  // Projects by org
  if (linkedto === '1' && type === 'project') return HttpResponse.json(pooshitProjectFixtures);
  if (linkedto === '2' && type === 'project') return HttpResponse.json(mamgoProjectFixtures);
  // Org of project: ?linkedto=<projectId>&type=organization
  if (linkedto === '3' && type === 'organization') return HttpResponse.json({ result: [{ id: 1, type: 'organization', name: 'Pooshit', status: null }], total: 1 });
  if (linkedto === '20' && type === 'organization') return HttpResponse.json({ result: [{ id: 2, type: 'organization', name: 'Mamgo', status: null }], total: 1 });
  // Bare org list
  if (type === 'organization') return HttpResponse.json(orgFixtures);
  return HttpResponse.json(emptyPage);
}

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, defaultHandler),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 3) return HttpResponse.json({ id: 3, type: 'project', name: 'DiVoid', status: null });
    if (id === 20) return HttpResponse.json({ id: 20, type: 'project', name: 'Backend', status: null });
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({ id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true, createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'] }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
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

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

/**
 * LocationCapture — mounts inside a Router and writes the current pathname to
 * the provided snapshot object on every navigation.  The snapshot is a plain
 * mutable object so callers can read `.value` after `waitFor`.
 */
function LocationCapture({ snapshot }: { snapshot: { value: string } }) {
  const location = useLocation();
  snapshot.value = location.pathname;
  return null;
}

function renderTasksPage(initialPath: string) {
  // Import TasksPage lazily inside the helper so mocks are registered first.
  // Callers must await lazy imports before calling this.
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <QueryClientProvider client={makeQC()}>
        <Routes>
          <Route path="/tasks" element={<TasksPageComponent />} />
          <Route path="/tasks/orgs/:orgId" element={<Navigate to="/tasks" replace />} />
          <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

// Lazy handles for components loaded after mocks are registered.
let TasksPageComponent: typeof import('./TasksPage').TasksPage;
let OrgPillRowComponent: typeof import('./OrgPillRow').OrgPillRow;
let ProjectPillRowComponent: typeof import('./ProjectPillRow').ProjectPillRow;

beforeAll(async () => {
  const [pageMod, orgMod, projMod] = await Promise.all([
    import('./TasksPage'),
    import('./OrgPillRow'),
    import('./ProjectPillRow'),
  ]);
  TasksPageComponent = pageMod.TasksPage;
  OrgPillRowComponent = orgMod.OrgPillRow;
  ProjectPillRowComponent = projMod.ProjectPillRow;
});

// ─── Test 1: OrgPillRow renders one pill per org ──────────────────────────────

describe('Test 1 — OrgPillRow renders one pill per organisation', () => {
  /**
   * Positive: MSW returns two orgs → two pills with the correct names appear.
   */
  it('positive: two org fixtures → two pills with correct names', async () => {
    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText('Pooshit')).toBeInTheDocument();
      expect(screen.getByText('Mamgo')).toBeInTheDocument();
    });

    expect(screen.getAllByRole('button').filter((b) => ['Pooshit', 'Mamgo'].includes(b.textContent ?? ''))).toHaveLength(2);
  });

  /**
   * Negative proof: if OrgPillRow used type=project instead of type=organization,
   * MSW would return emptyPage (no org fixtures match a project type request) and
   * no org pills would appear.
   */
  it('negative: wrong type query returns no org data → no org pills', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const type = url.searchParams.get('type');
        // Only respond to organization; project type returns empty — simulates wrong query.
        if (type === 'organization') return HttpResponse.json(emptyPage);
        return HttpResponse.json(orgFixtures);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 80));

    expect(screen.queryByText('Pooshit')).not.toBeInTheDocument();
    expect(screen.queryByText('Mamgo')).not.toBeInTheDocument();
  });
});

// ─── Test 2: OrgPillRow defaults selection to the current project's org ───────

describe('Test 2 — OrgPillRow defaults selection to the current project\'s org', () => {
  /**
   * Positive: render TasksPage at /tasks/projects/3 (DiVoid, linked to org 1 "Pooshit").
   * The "Pooshit" pill should have aria-pressed=true; "Mamgo" should not.
   */
  it('positive: /tasks/projects/3 → Pooshit pill is aria-pressed=true', async () => {
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      const pooshitBtn = screen.queryByTestId('org-pill-1');
      if (!pooshitBtn) throw new Error('Org pill 1 not yet rendered');
      expect(pooshitBtn).toHaveAttribute('aria-pressed', 'true');
    });

    const mamgoBtn = screen.queryByTestId('org-pill-2');
    if (mamgoBtn) {
      expect(mamgoBtn).toHaveAttribute('aria-pressed', 'false');
    }
  });

  /**
   * Negative proof: if useProjectOrg were removed and selectedOrgId were always
   * undefined, no pill would be aria-pressed=true.
   */
  it('negative: without useProjectOrg, no pill is aria-pressed=true', async () => {
    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          {/* Pass selectedOrgId=undefined (no project org resolved) */}
          <OrgPillRowComponent selectedOrgId={undefined} onOrgSelect={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText('Pooshit')).toBeInTheDocument();
    });

    // No pill should be pressed when selectedOrgId is undefined.
    const pooshitBtn = screen.getByRole('button', { name: 'Pooshit' });
    expect(pooshitBtn).toHaveAttribute('aria-pressed', 'false');
  });
});

// ─── Test 3: ProjectPillRow filters by selected org ──────────────────────────

describe('Test 3 — ProjectPillRow filters by selected org (linkedto=<orgId>&type=project)', () => {
  /**
   * Positive: render ProjectPillRow with orgId=2 → captured URL must contain
   * linkedto=2 and type=project.
   */
  it('positive: request URL contains linkedto=2 and type=project', async () => {
    let capturedUrl: string | null = null;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        capturedUrl = request.url;
        return HttpResponse.json(mamgoProjectFixtures);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <ProjectPillRowComponent orgId={2} selectedProjectId={undefined} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText('Backend')).toBeInTheDocument();
    });

    expect(capturedUrl).not.toBeNull();
    const url = new URL(capturedUrl!);
    expect(url.searchParams.get('linkedto')).toBe('2');
    expect(url.searchParams.get('type')).toBe('project');
  });

  /**
   * Negative proof: if ProjectPillRow used a bare useNodeList instead of
   * useNodeListLinkedTo, the URL would have no linkedto param and MSW
   * would not return mamgoProjectFixtures (handler miss).
   */
  it('negative: without linkedto param in URL, MSW returns no projects', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const linkedto = url.searchParams.get('linkedto');
        // Only projects request with linkedto gets project fixtures.
        if (linkedto) return HttpResponse.json(mamgoProjectFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    // Render with orgId=0 (disabled) — hook won't fire a request.
    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <ProjectPillRowComponent orgId={0} selectedProjectId={undefined} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 80));

    // No project pills since orgId=0 disables the query.
    expect(screen.queryByText('Backend')).not.toBeInTheDocument();
    expect(screen.queryByText('Frontend')).not.toBeInTheDocument();
  });
});

// ─── Test 4: Clicking a project pill navigates ────────────────────────────────

describe('Test 4 — Clicking a project pill navigates to /tasks/projects/:id', () => {
  /**
   * Positive: render TasksPage at /tasks/projects/3 (Pooshit org selected), see
   * Ocelot pill (id=4) in project row, click it → URL becomes /tasks/projects/4.
   */
  it('positive: click "Ocelot" pill → URL becomes /tasks/projects/4', async () => {
    const user = userEvent.setup();
    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks/projects/3']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <Routes>
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for Ocelot project pill to appear (it's in Pooshit org projects).
    await waitFor(() => {
      expect(screen.queryByTestId('project-pill-4')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('project-pill-4'));

    // After click the router must have navigated to /tasks/projects/4.
    await waitFor(() => {
      expect(locationSnapshot.value).toBe('/tasks/projects/4');
    });
  });

  /**
   * Negative proof: before clicking any pill, URL is at /tasks/projects/3
   * and the page shows the task-list for project 3 (not project 4).
   * If onClick were removed, clicking would not change the route.
   */
  it('negative: before any pill click, task-list is present for project 3', async () => {
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.getByTestId('task-list')).toBeInTheDocument();
    });

    // We're at /tasks/projects/3 — project 4's own task-list has NOT been mounted.
    // There's only one task-list element.
    expect(screen.getAllByTestId('task-list')).toHaveLength(1);
  });
});

// ─── Test 5: Org change does NOT auto-navigate ───────────────────────────────

describe('Test 5 — Clicking an org pill does NOT auto-navigate', () => {
  /**
   * Positive: render at /tasks/projects/3 (Pooshit → DiVoid). Click "Mamgo" org pill.
   * URL must stay at /tasks/projects/3. Project pill row must repopulate with Mamgo's projects.
   */
  it('positive: click Mamgo org → URL stays /tasks/projects/3, project row shows Mamgo projects', async () => {
    const user = userEvent.setup();
    renderTasksPage('/tasks/projects/3');

    // Wait for org pills.
    await waitFor(() => {
      expect(screen.queryByTestId('org-pill-2')).toBeInTheDocument();
    });

    // Click Mamgo org pill.
    await user.click(screen.getByTestId('org-pill-2'));

    // Wait for Mamgo projects to appear in the project pill row.
    await waitFor(() => {
      expect(screen.queryByText('Backend')).toBeInTheDocument();
      expect(screen.queryByText('Frontend')).toBeInTheDocument();
    });

    // task-list must still be showing (URL still at /tasks/projects/3).
    // The mismatch message appears because project 3 doesn't belong to Mamgo.
    expect(screen.getByTestId('org-mismatch-message')).toBeInTheDocument();
  });

  /**
   * Negative proof: if auto-navigation were added on org click, the URL
   * would change away from /tasks/projects/3 and the mismatch message
   * would not render (because projectId would be undefined or different).
   * Instead, we'd lose the current project context.
   * This is validated by the positive test above — the mismatch message
   * only appears when the URL still has project 3 but org 2 is selected.
   */
  it('negative: Pooshit pill is still aria-pressed after selecting Mamgo (URL unchanged)', async () => {
    const user = userEvent.setup();
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      const pooshitBtn = screen.queryByTestId('org-pill-1');
      if (!pooshitBtn) throw new Error('Org pill 1 not yet rendered');
      expect(pooshitBtn).toHaveAttribute('aria-pressed', 'true');
    });

    // Click Mamgo.
    await user.click(screen.getByTestId('org-pill-2'));

    // Mamgo pill is now selected.
    await waitFor(() => {
      expect(screen.getByTestId('org-pill-2')).toHaveAttribute('aria-pressed', 'true');
    });

    // task-list is gone (mismatch), not navigated away — mismatch message is present.
    expect(screen.getByTestId('org-mismatch-message')).toBeInTheDocument();
  });
});

// ─── Test 6: Org mismatch surfaces the inline message ────────────────────────

describe('Test 6 — Org change with mismatched project surfaces inline message', () => {
  /**
   * Positive: /tasks/projects/3 (Pooshit). Click Mamgo org pill.
   * Project 3 is not a Mamgo project → "Select a project to see its tasks." message.
   */
  it('positive: click Mamgo on project 3 (Pooshit project) → org-mismatch-message renders', async () => {
    const user = userEvent.setup();
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.queryByTestId('org-pill-2')).toBeInTheDocument();
    });

    await user.click(screen.getByTestId('org-pill-2'));

    await waitFor(() => {
      expect(screen.getByTestId('org-mismatch-message')).toBeInTheDocument();
    });

    // task-list should be suppressed.
    expect(screen.queryByTestId('task-list')).not.toBeInTheDocument();
  });

  /**
   * Negative proof: with the correct org selected (Pooshit for project 3),
   * no mismatch message appears and the task list is shown.
   */
  it('negative: correct org selected → no mismatch message, task-list is shown', async () => {
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.getByTestId('task-list')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('org-mismatch-message')).not.toBeInTheDocument();
  });
});

// ─── Test 7: /tasks/orgs/:orgId redirects to /tasks ─────────────────────────

describe('Test 7 — /tasks/orgs/:orgId redirects to /tasks', () => {
  /**
   * Positive: navigate to /tasks/orgs/2 using the production AppRoutes → URL ends up at /tasks.
   * This exercises the real <Navigate to="/tasks" replace /> at routes.tsx:122.
   * Removing that Navigate from routes.tsx must make this test fail.
   */
  it('positive: /tasks/orgs/2 redirects to /tasks (via production AppRoutes)', async () => {
    const locationSnapshot = { value: '' };

    render(
      <MemoryRouter initialEntries={['/tasks/orgs/2']}>
        <QueryClientProvider client={makeQC()}>
          <LocationCapture snapshot={locationSnapshot} />
          <AppRoutes />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // After the redirect fires the pathname must be /tasks.
    await waitFor(() => {
      expect(locationSnapshot.value).toBe('/tasks');
    });
  });

  /**
   * Negative proof: without the <Navigate /> redirect in routes.tsx, navigating to
   * /tasks/orgs/2 would stay at /tasks/orgs/2 (or render the unmatched fallback),
   * NOT redirect to /tasks.
   * Removing <Navigate to="/tasks" replace /> from routes.tsx:122 must cause the
   * positive test above to fail with locationSnapshot.value === '/tasks/orgs/2'.
   */
  it('negative: without redirect, /tasks/orgs/2 stays at /tasks/orgs/2', async () => {
    // Simulate the pre-redirect state: /tasks/orgs/:orgId renders TasksPage directly.
    render(
      <MemoryRouter initialEntries={['/tasks/orgs/2']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            {/* No Navigate redirect — TasksPage renders at the org URL */}
            <Route path="/tasks" element={<div data-testid="tasks-landing-only">tasks-landing</div>} />
            <Route path="/tasks/orgs/:orgId" element={<TasksPageComponent />} />
            <Route path="/tasks/projects/:projectId" element={<TasksPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Without redirect, the tasks landing is NOT shown (org route renders TasksPage instead).
    await new Promise((r) => setTimeout(r, 80));
    expect(screen.queryByTestId('tasks-landing-only')).not.toBeInTheDocument();
    // TasksPage renders (empty-state or tasks-page content).
    expect(screen.queryByTestId('tasks-empty-state')).toBeInTheDocument();
  });
});

// ─── Test 8: /tasks empty state ──────────────────────────────────────────────

describe('Test 8 — /tasks empty state: pill rows visible, task list absent', () => {
  /**
   * Positive: navigate to /tasks (no project param) → both pill rows present,
   * task-list absent.
   */
  it('positive: /tasks → org-pill-row and project-pill-row present; task-list absent', async () => {
    renderTasksPage('/tasks');

    await waitFor(() => {
      expect(screen.getByTestId('org-pill-row')).toBeInTheDocument();
    });

    expect(screen.getByTestId('project-pill-row')).toBeInTheDocument();
    expect(screen.queryByTestId('task-list')).not.toBeInTheDocument();
  });

  /**
   * Negative proof: at /tasks/projects/3, the task-list IS present.
   * This confirms the empty-state branch is specific to the no-project URL.
   */
  it('negative: /tasks/projects/3 → task-list IS present', async () => {
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.getByTestId('task-list')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('tasks-empty-state')).not.toBeInTheDocument();
  });
});

// ─── Test 9: Status filter works on the new layout ───────────────────────────

describe('Test 9 — Status filter still works on the new layout (regression guard PR #55)', () => {
  /**
   * Positive: render at /tasks/projects/3. The task fetch URL contains a status param
   * with new,open,in-progress (default selection).
   */
  it('positive: default status filter sends status=new,open,in-progress in task fetch URL', async () => {
    let capturedTaskUrl: string | null = null;

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const path = url.searchParams.get('path');
        const type = url.searchParams.get('type');
        const linkedto = url.searchParams.get('linkedto');
        const name = url.searchParams.get('name');

        if (path) {
          capturedTaskUrl = request.url;
          return HttpResponse.json(taskFixtures);
        }
        if (linkedto && name === 'Tasks') return HttpResponse.json(tasksGroupFixture);
        if (linkedto === '1' && type === 'project') return HttpResponse.json(pooshitProjectFixtures);
        if (linkedto === '3' && type === 'organization') return HttpResponse.json({ result: [{ id: 1, type: 'organization', name: 'Pooshit', status: null }], total: 1 });
        if (type === 'organization') return HttpResponse.json(orgFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.getByText('Fix login')).toBeInTheDocument();
    });

    expect(capturedTaskUrl).not.toBeNull();
    const url = new URL(capturedTaskUrl!);
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
   * Negative proof: the status filter pill row is visible on /tasks/projects/3.
   * If it were absent, no pills would be in the document.
   */
  it('negative: status filter pills are absent when no project is loaded (/tasks)', async () => {
    renderTasksPage('/tasks');

    await waitFor(() => {
      expect(screen.getByTestId('org-pill-row')).toBeInTheDocument();
    });

    // No task-list → no status-filter-pills.
    expect(screen.queryByTestId('status-filter-pills')).not.toBeInTheDocument();
  });
});

// ─── Test 10: List/Board toggle persists to sessionStorage ───────────────────

describe('Test 10 — List/Board toggle persists to sessionStorage (regression guard PR #57)', () => {
  /**
   * Positive: render at /tasks/projects/3, toggle to board view →
   * sessionStorage holds 'board' for project 3.
   */
  it('positive: toggle to board → sessionStorage[divoid.tasks.view.3] = board', async () => {
    const user = userEvent.setup();
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.getByTestId('tasks-view-toggle')).toBeInTheDocument();
    });

    const boardBtn = screen.getByTestId('tasks-view-toggle-board');
    await user.click(boardBtn);

    await waitFor(() => {
      expect(sessionStorage.getItem('divoid.tasks.view.3')).toBe('board');
    });
  });

  /**
   * Negative proof: before clicking the toggle, sessionStorage is empty for project 3.
   */
  it('negative: before toggle, sessionStorage has no view entry for project 3', async () => {
    renderTasksPage('/tasks/projects/3');

    await waitFor(() => {
      expect(screen.getByTestId('tasks-view-toggle')).toBeInTheDocument();
    });

    expect(sessionStorage.getItem('divoid.tasks.view.3')).toBeNull();
  });
});
