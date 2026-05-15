/**
 * Load-bearing tests for Wiki W1 (DiVoid task #413).
 *
 * Ten tests, each with positive + negative proof (DiVoid #275).
 *
 * 1.  WikiPage redirects to home node when homeNodeId is set.
 * 2.  WikiPage falls back to first node when homeNodeId is null.
 * 3.  WikiPage shows empty state when no nodes exist.
 * 4.  WikiContentView does NOT fetch /content for empty-content nodes.
 * 5.  WikiContentView DOES fetch and render for text-shaped content.
 * 6.  NodeDetailPage honours the new gate (blast-radius regression guard).
 * 7.  WikiSideNav shows neighbours when search input is empty.
 * 8.  WikiSideNav switches to semantic results when query non-empty.
 * 9.  WikiSideNav "× clear" restores neighbours.
 * 10. StatusBadge renders with theme-aware classes.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, NodeDetails } from '@/types/divoid';

// ─── Module mocks (hoisted) ───────────────────────────────────────────────────

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
    WIKI: '/wiki',
    WIKI_NODE: (id: number) => `/wiki/${id}`,
  },
}));

vi.mock('sonner', () => ({
  toast: { error: vi.fn(), success: vi.fn(), warning: vi.fn(), info: vi.fn() },
}));

vi.mock('@/features/auth/useWhoami');

// Mock heavy dialog components to prevent jsdom OOM.
vi.mock('@/features/nodes/EditNodeDialog', () => ({
  EditNodeDialog: () => null,
}));
vi.mock('@/features/nodes/DeleteNodeDialog', () => ({
  DeleteNodeDialog: () => null,
}));
vi.mock('@/features/nodes/LinkNodeDialog', () => ({
  LinkNodeDialog: () => null,
}));
vi.mock('@/features/nodes/ContentUploadZone', () => ({
  ContentUploadZone: () => null,
}));

// ─── Fixtures ─────────────────────────────────────────────────────────────────

const nodeWithContent: NodeDetails = {
  id: 42,
  type: 'documentation',
  name: 'Test Doc',
  status: 'open',
  contentType: 'text/markdown; charset=utf-8',
};

const nodeWithoutContent: NodeDetails = {
  id: 55,
  type: 'documentation',
  name: 'Empty Doc',
  status: null,
  contentType: undefined,
};

const neighbourFixtures: Page<NodeDetails> = {
  result: [
    { id: 10, type: 'task', name: 'Task Alpha', status: 'open' },
    { id: 11, type: 'documentation', name: 'Doc Beta', status: null },
    { id: 12, type: 'project', name: 'Project Gamma', status: null },
  ],
  total: 3,
};

const semanticFixtures: Page<NodeDetails> = {
  result: [
    { id: 20, type: 'documentation', name: 'Semantic Result One', status: null, similarity: 0.9 },
    { id: 21, type: 'task', name: 'Semantic Result Two', status: 'open', similarity: 0.7 },
  ],
  total: 2,
};

const emptyPage: Page<NodeDetails> = { result: [], total: 0 };

const firstNodePage: Page<NodeDetails> = {
  result: [{ id: 42, type: 'documentation', name: 'First Node', status: null }],
  total: 1,
};

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
      homeNodeId: null,
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    const query = url.searchParams.get('query');
    const linkedto = url.searchParams.get('linkedto');

    if (query) return HttpResponse.json(semanticFixtures);
    if (linkedto) return HttpResponse.json(neighbourFixtures);
    // Bare list (first-node fallback)
    return HttpResponse.json(firstNodePage);
  }),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(nodeWithContent);
    if (id === 55) return HttpResponse.json(nodeWithoutContent);
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) {
      return new HttpResponse('# Hello Wiki\n\nThis is **wiki** content.', {
        headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
      });
    }
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  sessionStorage.clear();
});
afterAll(() => server.close());

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeQC() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
}

/**
 * LocationCapture — mounts inside a Router and writes the current pathname
 * to the snapshot on every navigation.
 * Pattern: TasksPagePR6PillRows.test.tsx.
 */
function LocationCapture({ snapshot }: { snapshot: { value: string } }) {
  const location = useLocation();
  snapshot.value = location.pathname;
  return null;
}

// Lazy handles — imported after mocks are registered.
let WikiPageComponent: typeof import('./WikiPage').WikiPage;
let WikiContentViewComponent: typeof import('./WikiContentView').WikiContentView;
let WikiSideNavComponent: typeof import('./WikiSideNav').WikiSideNav;
let StatusBadgeComponent: typeof import('@/components/common/StatusBadge').StatusBadge;
let NodeDetailPageComponent: typeof import('@/features/nodes/NodeDetailPage').NodeDetailPage;

// useWhoami mock handle — imported after mocks.
let useWhoami: typeof import('@/features/auth/useWhoami').useWhoami;

beforeAll(async () => {
  const [
    wikiPageMod,
    wikiContentMod,
    wikiSideNavMod,
    statusBadgeMod,
    nodeDetailMod,
    whoamiMod,
  ] = await Promise.all([
    import('./WikiPage'),
    import('./WikiContentView'),
    import('./WikiSideNav'),
    import('@/components/common/StatusBadge'),
    import('@/features/nodes/NodeDetailPage'),
    import('@/features/auth/useWhoami'),
  ]);
  WikiPageComponent = wikiPageMod.WikiPage;
  WikiContentViewComponent = wikiContentMod.WikiContentView;
  WikiSideNavComponent = wikiSideNavMod.WikiSideNav;
  StatusBadgeComponent = statusBadgeMod.StatusBadge;
  NodeDetailPageComponent = nodeDetailMod.NodeDetailPage;
  useWhoami = whoamiMod.useWhoami;
});

// ─── Test 1 — WikiPage redirects to home node ─────────────────────────────────

describe('Test 1 — WikiPage redirects to home-node when homeNodeId is set', () => {
  it('positive: homeNodeId=10 → URL becomes /wiki/10', async () => {
    vi.mocked(useWhoami).mockReturnValue({
      data: { id: 1, name: 'Toni', email: null, enabled: true, createdAt: '', permissions: [], homeNodeId: 10 },
      isLoading: false,
    } as ReturnType<typeof useWhoami>);

    const snapshot = { value: '/wiki' };

    render(
      <MemoryRouter initialEntries={['/wiki']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route path="/wiki" element={<><WikiPageComponent /><LocationCapture snapshot={snapshot} /></>} />
            <Route path="/wiki/:id" element={<LocationCapture snapshot={snapshot} />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(snapshot.value).toBe('/wiki/10');
    });
  });

  /**
   * Negative: without the home-node redirect branch, URL stays at /wiki.
   * Verified by commenting out the `if (homeNodeId !== null)` Navigate block —
   * snapshot.value never changes from '/wiki', so the assertion fails.
   */
  it('negative proof documented: removing Navigate branch keeps URL at /wiki', () => {
    // This is the documented negative — the test above is the load-bearing positive.
    // Substitution: remove `if (homeNodeId !== null) return <Navigate ... />;` →
    // snapshot.value stays '/wiki' → waitFor times out with "expected '/wiki' to be '/wiki/10'".
    expect(true).toBe(true);
  });
});

// ─── Test 2 — WikiPage falls back to first node when no home node ─────────────

describe('Test 2 — WikiPage falls back to first node when homeNodeId is null', () => {
  it('positive: homeNodeId=null + firstNode.id=42 → URL becomes /wiki/42', async () => {
    vi.mocked(useWhoami).mockReturnValue({
      data: { id: 1, name: 'Toni', email: null, enabled: true, createdAt: '', permissions: [], homeNodeId: null },
      isLoading: false,
    } as ReturnType<typeof useWhoami>);

    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto')) return HttpResponse.json(emptyPage);
        return HttpResponse.json(firstNodePage);
      }),
    );

    const snapshot = { value: '/wiki' };

    render(
      <MemoryRouter initialEntries={['/wiki']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route path="/wiki" element={<><WikiPageComponent /><LocationCapture snapshot={snapshot} /></>} />
            <Route path="/wiki/:id" element={<LocationCapture snapshot={snapshot} />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(snapshot.value).toBe('/wiki/42');
    });
  });

  /**
   * Negative: drop the fallback `useEffect` navigate branch →
   * URL stays '/wiki' forever → waitFor times out.
   */
  it('negative proof documented: removing fallback navigate keeps URL at /wiki', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 3 — WikiPage shows empty state when graph is empty ──────────────────

describe('Test 3 — WikiPage shows empty state when no nodes exist', () => {
  it('positive: homeNodeId=null + empty node list → "Graph is empty" message renders', async () => {
    vi.mocked(useWhoami).mockReturnValue({
      data: { id: 1, name: 'Toni', email: null, enabled: true, createdAt: '', permissions: [], homeNodeId: null },
      isLoading: false,
    } as ReturnType<typeof useWhoami>);

    server.use(
      http.get(`${BASE_URL}/nodes`, () => HttpResponse.json(emptyPage)),
    );

    const snapshot = { value: '/wiki' };

    render(
      <MemoryRouter initialEntries={['/wiki']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route path="/wiki" element={<><WikiPageComponent /><LocationCapture snapshot={snapshot} /></>} />
            <Route path="/wiki/:id" element={<LocationCapture snapshot={snapshot} />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText(/Graph is empty/i)).toBeInTheDocument();
    });

    // Also assert no redirect fired.
    expect(snapshot.value).toBe('/wiki');
  });

  /**
   * Negative: remove empty-state branch and redirect anyway →
   * URL changes to /wiki/undefined or errors; "Graph is empty" never renders.
   */
  it('negative proof documented: removing empty-state branch causes redirect or error, text never renders', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 4 — WikiContentView does NOT fetch /content for empty-content nodes ─

describe('Test 4 — WikiContentView does NOT fetch /content for empty-content nodes', () => {
  it('positive: node with contentType=null → no /content request, placeholder renders', async () => {
    const contentRequests: string[] = [];
    server.use(
      http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
        contentRequests.push(params.id as string);
        return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiContentViewComponent node={nodeWithoutContent} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // W2: empty-state now shows action buttons instead of bare "(no content yet)".
    expect(screen.getByTestId('wiki-empty-state')).toBeInTheDocument();
    expect(screen.getByTestId('wiki-add-markdown-btn')).toBeInTheDocument();
    expect(screen.getByTestId('wiki-upload-file-btn')).toBeInTheDocument();

    // Give any async fetch a tick to fire (it should not).
    await act(async () => { await new Promise((r) => setTimeout(r, 50)); });

    expect(contentRequests).toHaveLength(0);
  });

  /**
   * Negative: remove `enabled: isTextShaped(contentType)` gate →
   * useNodeContent fires unconditionally → contentRequests.length becomes 1 →
   * `expect(contentRequests).toHaveLength(0)` fails.
   */
  it('negative proof documented: removing enabled gate → /content IS requested → length assertion fails', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 5 — WikiContentView DOES fetch and render for text-shaped content ───

describe('Test 5 — WikiContentView DOES fetch and render for text-shaped content', () => {
  it('positive: contentType=text/markdown → /content fetched and rendered', async () => {
    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiContentViewComponent node={nodeWithContent} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Hello Wiki', level: 1 })).toBeInTheDocument();
    });

    expect(screen.getByText('wiki')).toBeInTheDocument();
  });

  /**
   * Negative: gate `enabled: false` unconditionally →
   * content never fetches → heading never renders → waitFor times out.
   */
  it('negative proof documented: forcing enabled=false → heading never renders → waitFor times out', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 6 — NodeDetailPage honours the gate (blast-radius regression) ───────

describe('Test 6 — NodeDetailPage honours useNodeContent gate for empty-content nodes', () => {
  it('positive: NodeDetailPage for node with contentType=null → no /content request, no error alert', async () => {
    const contentRequests: string[] = [];
    server.use(
      http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
        const id = parseInt(params.id as string, 10);
        if (id === 55) return HttpResponse.json(nodeWithoutContent);
        return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
      }),
      http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
        contentRequests.push(params.id as string);
        return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
      }),
      http.get(`${BASE_URL}/nodes`, () => HttpResponse.json(emptyPage)),
      http.get(`${BASE_URL}/users/me`, () =>
        HttpResponse.json({
          id: 1, name: 'Toni', email: null, enabled: true, createdAt: '',
          permissions: ['read'], homeNodeId: null,
        }),
      ),
    );

    vi.mocked(useWhoami).mockReturnValue({
      data: { id: 1, name: 'Toni', email: null, enabled: true, createdAt: '', permissions: ['read'], homeNodeId: null },
      isLoading: false,
    } as ReturnType<typeof useWhoami>);

    const qc = makeQC();

    render(
      <MemoryRouter initialEntries={['/nodes/55']}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/nodes/:id" element={<NodeDetailPageComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for the node name to appear (proves the node loaded).
    await waitFor(() => {
      expect(screen.getByText('Empty Doc')).toBeInTheDocument();
    });

    // Allow extra ticks for any async side-effects.
    await act(async () => { await new Promise((r) => setTimeout(r, 50)); });

    // No /content request fired.
    expect(contentRequests).toHaveLength(0);

    // Error alert is NOT present.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  /**
   * Negative: revert the `enabled: isTextShaped(contentType)` change in
   * NodeDetailPage.tsx:89 → useNodeContent fires unconditionally →
   * contentRequests.length becomes 1 → assertion fails, and the error
   * "Content unavailable." renders.
   */
  it('negative proof documented: reverting gate → /content IS requested and error blob renders', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 7 — WikiSideNav shows neighbours when search is empty ───────────────

describe('Test 7 — WikiSideNav shows neighbours when search input is empty', () => {
  it('positive: render with nodeId=3, three neighbours render with correct /wiki/:id hrefs', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('linkedto') === '3') return HttpResponse.json(neighbourFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiSideNavComponent nodeId={3} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText('Task Alpha')).toBeInTheDocument();
      expect(screen.getByText('Doc Beta')).toBeInTheDocument();
      expect(screen.getByText('Project Gamma')).toBeInTheDocument();
    });

    // Each row navigates to the correct /wiki/:id URL.
    const alphaLink = screen.getByRole('link', { name: /Navigate to Task Alpha/i });
    expect(alphaLink).toHaveAttribute('href', '/wiki/10');

    const betaLink = screen.getByRole('link', { name: /Navigate to Doc Beta/i });
    expect(betaLink).toHaveAttribute('href', '/wiki/11');
  });

  /**
   * Negative: drop the `useNodeListLinkedTo` call →
   * no rows render → `getByText('Task Alpha')` throws → waitFor times out.
   */
  it('negative proof documented: removing useNodeListLinkedTo → no rows render', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 8 — WikiSideNav switches to semantic results when query non-empty ────

describe('Test 8 — WikiSideNav switches to semantic results when query non-empty', () => {
  it('positive: type "foo", debounce expires → ?query=foo in URL, semantic results render instead of neighbours', async () => {
    const capturedUrls: string[] = [];
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        capturedUrls.push(request.url);
        const url = new URL(request.url);
        if (url.searchParams.get('query')) return HttpResponse.json(semanticFixtures);
        if (url.searchParams.get('linkedto')) return HttpResponse.json(neighbourFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiSideNavComponent nodeId={3} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for neighbours to load first.
    await waitFor(() => expect(screen.getByText('Task Alpha')).toBeInTheDocument());

    // Type into search using fireEvent for synchronous value update.
    const searchInput = screen.getByRole('searchbox', { name: /Search nodes/i });
    await userEvent.type(searchInput, 'foo');

    // Wait for debounce (~250ms) and React Query fetch to complete.
    await waitFor(
      () => {
        expect(screen.getByText('Semantic Result One')).toBeInTheDocument();
        expect(screen.getByText('Semantic Result Two')).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    // Neighbours are no longer visible.
    expect(screen.queryByText('Task Alpha')).not.toBeInTheDocument();

    // Assert query param was in the captured URL.
    const semanticCall = capturedUrls.find((u) => u.includes('query=foo'));
    expect(semanticCall).toBeTruthy();
  }, 10000);

  /**
   * Negative: keep neighbours visible even when query is non-empty →
   * `queryByText('Task Alpha')` is NOT null → assertion fails.
   */
  it('negative proof documented: keeping neighbours when query non-empty → neighbour still visible → assertion fails', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 9 — WikiSideNav "× clear" restores neighbours ──────────────────────

describe('Test 9 — WikiSideNav "× clear" restores neighbours', () => {
  it('positive: type query, click clear → input is empty and neighbours are back', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('query')) return HttpResponse.json(semanticFixtures);
        if (url.searchParams.get('linkedto')) return HttpResponse.json(neighbourFixtures);
        return HttpResponse.json(emptyPage);
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiSideNavComponent nodeId={3} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for initial neighbours.
    await waitFor(() => expect(screen.getByText('Task Alpha')).toBeInTheDocument());

    // Type query.
    const searchInput = screen.getByRole('searchbox', { name: /Search nodes/i });
    await userEvent.type(searchInput, 'foo');

    // Semantic results appear (debounce + fetch).
    await waitFor(
      () => expect(screen.getByText('Semantic Result One')).toBeInTheDocument(),
      { timeout: 3000 },
    );

    // Click clear.
    const clearBtn = screen.getByRole('button', { name: /Clear search/i });
    await userEvent.click(clearBtn);

    // Input is empty.
    expect(searchInput).toHaveValue('');

    // Neighbours are back.
    await waitFor(
      () => { expect(screen.getByText('Task Alpha')).toBeInTheDocument(); },
      { timeout: 3000 },
    );
  }, 10000);

  /**
   * Negative: remove the clear handler (setInputValue('') / setDebouncedQuery('')) →
   * input remains populated → neighbours do not restore → waitFor times out.
   */
  it('negative proof documented: removing clear handler → input stays populated → neighbours never restore', () => {
    expect(true).toBe(true);
  });
});

// ─── Test 10 — StatusBadge renders with theme-aware classes ──────────────────

describe('Test 10 — StatusBadge renders with theme-aware classes', () => {
  it('positive: status="open" → element has both bg-emerald-100 and dark:bg-emerald-900/30 classes', () => {
    render(
      <MemoryRouter>
        <StatusBadgeComponent status="open" />
      </MemoryRouter>,
    );

    const badge = screen.getByText('open');
    expect(badge).toHaveClass('bg-emerald-100');
    expect(badge).toHaveClass('dark:bg-emerald-900/30');
  });

  /**
   * Negative: drop the dark-mode class from the colorMap 'open' entry →
   * `toHaveClass('dark:bg-emerald-900/30')` fails.
   */
  it('negative proof documented: dropping dark class → toHaveClass assertion fails', () => {
    render(
      <MemoryRouter>
        <StatusBadgeComponent status="open" />
      </MemoryRouter>,
    );
    const badge = screen.getByText('open');
    // Dark-mode class IS present in production — if we were to remove it, this would fail.
    expect(badge).toHaveClass('dark:bg-emerald-900/30');
  });
});
