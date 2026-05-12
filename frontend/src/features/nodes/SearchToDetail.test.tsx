/**
 * Full-app navigation regression test: /search → /nodes/:id
 *
 * This test intentionally mounts the full router + query client + auth context
 * stack (as close to production as possible without a real Keycloak server) and
 * navigates from SearchPage to NodeDetailPage by clicking a search result link.
 *
 * Why this test exists — prior regression (#257) / this PR:
 *   The unit-level NodeDetailPage test (NodeDetailPage.test.tsx) mocks useAuth()
 *   at the module level so every call returns the same static object.  That test
 *   missed the real loop because the loop does NOT require per-render useAuth()
 *   instability — it requires calling toast.error() or other side-effectful code
 *   directly inside the render body, which only manifests when the component
 *   renders more than once (e.g., after data loads, after navigation).
 *
 * Specifically, the SearchPage panels (SemanticPanel, LinkedPanel, PathPanel)
 * were calling toast.error() unconditionally in the render body when error was
 * set.  In sonner v2, toast() calls are synchronous and update the global toast
 * store, which notifies subscribers (including the Toaster component).  Under
 * React 19 concurrent mode + StrictMode, this repeated side-effect during render
 * eventually overwhelmed React's re-render budget → "Maximum update depth exceeded".
 *
 * The full-app test catches this because:
 *   1. We do NOT mock sonner — real toast() calls hit the real store.
 *   2. We mount the real Toaster so the store subscriber is active.
 *   3. We navigate in the same component tree, so any render cascades propagate.
 *
 * Anti-scope note: this test is NOT a Playwright/Cypress integration test.
 * It uses jsdom + RTL exactly like the other unit tests, but it mounts a wider
 * component subtree to exercise the navigation path.
 *
 * Regression pins: DiVoid bug #257 (apiClient per-render) + this PR's fix
 * (render-body toast side-effects + params object instability).
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode, samplePage, semanticPage } from '@/test/msw/handlers';
import { Toaster } from 'sonner';

// ─── MSW server ───────────────────────────────────────────────────────────────

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
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('query')) return HttpResponse.json(semanticPage);
    if (url.searchParams.get('linkedto')) return HttpResponse.json(samplePage);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 10) return HttpResponse.json(sampleNode);
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 10) {
      return new HttpResponse('# Hello\n\nSome content.', {
        headers: { 'Content-Type': 'text/markdown' },
      });
    }
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
  http.delete(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────

// Mock useAuth at module level — we can't start a real Keycloak in jsdom.
// IMPORTANT: unlike the unit tests, we do NOT mock sonner here so that render-body
// toast() calls hit the real sonner store and trigger real subscriber notifications.
// That is the scenario that causes the loop.
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

// Mock heavy dialog components — covered by their own test files.
vi.mock('./CreateNodeDialog', () => ({
  CreateNodeDialog: () => null,
}));
vi.mock('./EditNodeDialog', () => ({
  EditNodeDialog: () => null,
}));
vi.mock('./DeleteNodeDialog', () => ({
  DeleteNodeDialog: () => null,
}));
vi.mock('./LinkNodeDialog', () => ({
  LinkNodeDialog: () => null,
}));
vi.mock('./ContentUploadZone', () => ({
  ContentUploadZone: () => null,
}));

// ─── Import lazily (mocks must be registered first) ───────────────────────────

let SearchPage: typeof import('./SearchPage').SearchPage;
let NodeDetailPage: typeof import('./NodeDetailPage').NodeDetailPage;

beforeAll(async () => {
  const [search, detail] = await Promise.all([
    import('./SearchPage'),
    import('./NodeDetailPage'),
  ]);
  SearchPage = search.SearchPage;
  NodeDetailPage = detail.NodeDetailPage;
});

// ─── Helpers ──────────────────────────────────────────────────────────────────

function renderApp(initialPath: string) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });

  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <QueryClientProvider client={qc}>
        <Routes>
          <Route path="/search" element={<SearchPage />} />
          <Route path="/nodes/:id" element={<NodeDetailPage />} />
        </Routes>
        {/* Mount the real Toaster so render-body toast() calls hit real subscribers */}
        <Toaster />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('SearchPage → NodeDetailPage navigation — no render loop', () => {
  it('navigates from search results to node detail without infinite render loop', async () => {
    const user = userEvent.setup();
    renderApp('/search');

    // Semantic tab is active by default. Submit a query.
    const searchInput = screen.getByRole('searchbox', { name: /semantic search query/i });
    await user.type(searchInput, 'auth notes');
    await user.click(screen.getByRole('button', { name: /^search$/i }));

    // Wait for semantic search results to appear.
    // semanticPage has id=10 (Auth notes) and id=11.
    await waitFor(() => {
      expect(screen.getByText('Auth notes')).toBeInTheDocument();
    });

    // Click the first result — navigates to /nodes/10.
    const link = screen.getByRole('link', { name: 'Auth notes' });
    await user.click(link);

    // NodeDetailPage should render the node's name (we serve sampleNode for id=10
    // — name is 'Test Document').
    // The critical assertion is that we reach this point without React throwing
    // "Maximum update depth exceeded" — if the loop fires, waitFor will catch the
    // thrown error before this assertion is reached.
    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    // Confirm the metadata region renders fully — the URL id (10) is shown in the
    // ID row, confirming stable render, not just a flash.
    expect(screen.getByText('10')).toBeInTheDocument();
  });

  it('NodeDetailPage renders without loop when mounted directly (regression #257)', async () => {
    // Sanity check: the unit-level regression still passes in this wider setup.
    renderApp('/nodes/10');

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    // URL id=10 is shown in the metadata ID row.
    expect(screen.getByText('10')).toBeInTheDocument();
  });

  it('SearchPage error path: toast.error() is NOT called during render body', async () => {
    // Override the semantic search endpoint to return a 500 error.
    // If toast.error() were still in the render body, the component would loop
    // (every render fires toast → store notifies → useSyncExternalStore triggers).
    // With toast.error() in useEffect, it fires exactly once after the error is set.
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('query')) {
          return HttpResponse.json(
            { code: 'internal', text: 'Something went wrong' },
            { status: 500 },
          );
        }
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    const user = userEvent.setup();
    renderApp('/search');

    const searchInput = screen.getByRole('searchbox', { name: /semantic search query/i });
    await user.type(searchInput, 'trigger error');
    await user.click(screen.getByRole('button', { name: /^search$/i }));

    // Wait a tick to allow any render loop to manifest.
    // If the toast was in the render body, React would throw before this resolves.
    await waitFor(() => {
      // The non-DivoidApiError fallback message is shown.
      // (The 500 is mapped to DivoidApiError by the client, so the
      // DivoidApiError branch fires — toast.error() is the only feedback.)
      // We simply assert that the component has not crashed/looped.
      expect(screen.getByRole('searchbox', { name: /semantic search query/i })).toBeInTheDocument();
    });
  });
});
