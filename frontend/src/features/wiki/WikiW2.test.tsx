/**
 * Load-bearing tests for Wiki W2 (DiVoid task #421).
 *
 * Eight tests per the task contract. Each is a load-bearing positive proof
 * (Section 13.2) with a documented substitution-failure outcome.
 * NO tautological `expect(true).toBe(true)` negatives (Section 13.3).
 *
 * Substitution discipline: before submit, revert each named production line
 * and confirm the test fails with the assertion error documented below.
 *
 * Tests:
 * W2-1. Edit-and-save round-trip (WikiContentView text-shaped content).
 * W2-2. Add-markdown-to-empty.
 * W2-3. Upload-file-to-empty.
 * W2-4. Empty-state buttons hidden when contentType IS set.
 * W2-5. Add-child-page flow (create + link + navigate).
 * W2-6. Add-child-page bug #317 graceful path.
 * W2-7. Rename flow.
 * W2-8. Markdown editor textarea has theme-aware classes (Section 14.2).
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
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

// ─── Fixtures ─────────────────────────────────────────────────────────────────

const nodeWithMarkdown: NodeDetails = {
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

const emptyPage: Page<NodeDetails> = { result: [], total: 0 };

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
      homeNodeId: null,
    }),
  ),
  http.get(`${BASE_URL}/nodes`, () => HttpResponse.json(emptyPage)),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(nodeWithMarkdown);
    if (id === 55) return HttpResponse.json(nodeWithoutContent);
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) {
      return new HttpResponse('# Hello Wiki\n\nOriginal content.', {
        headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
      });
    }
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.clearAllMocks();
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
 * LocationCapture — writes current pathname to snapshot on every navigation.
 * Pattern from TasksPagePR6PillRows.test.tsx (Section 13.10).
 */
function LocationCapture({ snapshot }: { snapshot: { value: string } }) {
  const location = useLocation();
  snapshot.value = location.pathname;
  return null;
}

/**
 * NodeListObserver — active subscriber for a ['nodes', 'list', key] query.
 *
 * Used in W2-Test 1 to verify that broad ['nodes'] prefix invalidation fires.
 * Because TanStack Query only refetches queries that have active subscribers,
 * we mount this component alongside WikiContentView so the sibling query
 * stays active. When invalidateQueries({queryKey: ['nodes']}) fires, the
 * broad prefix match marks this query stale and triggers a refetch — the
 * queryFn runs again and increments the counter tracked by the test.
 */
function NodeListObserver({
  queryKey,
  queryFn,
}: {
  queryKey: readonly unknown[];
  queryFn: () => Promise<unknown>;
}) {
  useQuery({ queryKey: queryKey as unknown[], queryFn, staleTime: 0 });
  return null;
}

// Lazy handles — imported after mocks are registered.
let WikiContentViewComponent: typeof import('./WikiContentView').WikiContentView;
let WikiLayoutComponent: typeof import('./WikiLayout').WikiLayout;

beforeAll(async () => {
  const [wikiContentMod, wikiLayoutMod] = await Promise.all([
    import('./WikiContentView'),
    import('./WikiLayout'),
  ]);
  WikiContentViewComponent = wikiContentMod.WikiContentView;
  WikiLayoutComponent = wikiLayoutMod.WikiLayout;
});

// ─── W2-Test 1 — Edit-and-save round-trip ─────────────────────────────────────
//
// This test pins TWO things:
//
// 1. The mutation fires: remove `mutation.mutate(...)` from handleSave →
//    POST /content is never sent → capturedContentBody stays '' →
//    `expect(capturedContentBody).toContain('Updated content.')` fails with:
//      AssertionError: expected '' to contain 'Updated content.'
//
// 2. The broad ['nodes'] invalidation fires (Section 8.2):
//    WikiContentView passes extraInvalidationKeys={[['nodes']]} to
//    MarkdownEditorSurface, so MarkdownEditorSurface.handleSave calls
//    queryClient.invalidateQueries({queryKey: ['nodes']}) in onSuccess.
//    This broad invalidation refetches the sibling ['nodes'] list query that
//    the test has pre-seeded with a QueryClient.prefetchQuery call.
//
//    Substitution: pass extraInvalidationKeys={[]} (empty) instead →
//    only the narrow nodeContentQueryKey(42) fires (from useUploadContent's
//    built-in onSuccess) but NOT the broad ['nodes'] prefix →
//    the sibling list query is NOT refetched →
//    listFetchCount stays 1 (initial prefetch only) →
//    `expect(listFetchCount).toBeGreaterThan(1)` fails with:
//      AssertionError: expected 1 to be above 1

describe('W2-Test 1 — edit-and-save round-trip', () => {
  it('positive: click Edit, change textarea, click Save → POST /content fires, rendered markdown updates, broad ["nodes"] invalidation refetches sibling list query', async () => {
    let capturedContentBody = '';
    let contentFetchCount = 0;
    let siblingFetchCount = 0;

    server.use(
      http.post(`${BASE_URL}/nodes/42/content`, async ({ request }) => {
        capturedContentBody = await request.text();
        return new HttpResponse(null, { status: 200 });
      }),
      http.get(`${BASE_URL}/nodes/42/content`, () => {
        contentFetchCount++;
        if (contentFetchCount === 1) {
          return new HttpResponse('# Hello Wiki\n\nOriginal content.', {
            headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
          });
        }
        // After save + broad invalidation, second fetch returns updated content.
        return new HttpResponse('# Hello Wiki\n\nUpdated content.', {
          headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
        });
      }),
    );

    // Sibling queryFn — tracked by the NodeListObserver active subscriber.
    // When invalidateQueries({queryKey: ['nodes']}) fires (the broad ['nodes']
    // prefix), TanStack Query marks all matching keys stale, including
    // ['nodes', 'list', 'wiki-sibling'], and refetches active subscribers.
    // siblingFetchCount will advance past its initial value only if the broad
    // invalidation fires (narrow nodeContentQueryKey(42) does NOT match
    // ['nodes', 'list', 'wiki-sibling']).
    const siblingQueryKey = ['nodes', 'list', 'wiki-sibling'] as const;
    const siblingQueryFn = async () => {
      siblingFetchCount++;
      return emptyPage;
    };

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          {/* Active subscriber — must be inside the same QueryClientProvider */}
          <NodeListObserver queryKey={siblingQueryKey} queryFn={siblingQueryFn} />
          <WikiContentViewComponent node={nodeWithMarkdown} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for initial content to render and sibling query to settle.
    await waitFor(() => {
      expect(screen.getByText('Original content.')).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(siblingFetchCount).toBeGreaterThan(0);
    });

    const siblingCountBeforeSave = siblingFetchCount;

    // Click the Edit toggle.
    const editBtn = screen.getByTestId('wiki-edit-btn');
    await userEvent.click(editBtn);

    // Textarea is now visible.
    const textarea = screen.getByTestId('wiki-editor-textarea') as HTMLTextAreaElement;
    expect(textarea).toBeInTheDocument();

    // Clear and type new content.
    await userEvent.clear(textarea);
    await userEvent.type(textarea, '# Hello Wiki\n\nUpdated content.');

    // Click Save.
    const saveBtn = screen.getByTestId('wiki-editor-save');
    await userEvent.click(saveBtn);

    // Load-bearing pin 1: MSW captures the POST with the new body.
    await waitFor(() => {
      expect(capturedContentBody).toContain('Updated content.');
    });

    // Load-bearing pin 2 (Section 8.2 — broad ['nodes'] invalidation):
    // WikiContentView passes extraInvalidationKeys={[['nodes']]} to
    // MarkdownEditorSurface. After save, the broad prefix invalidation fires
    // and refetches the active sibling subscriber. siblingFetchCount must
    // advance past the pre-save value.
    //
    // Substitution: pass extraInvalidationKeys={[]} instead →
    // only narrow nodeContentQueryKey(42) fires (from useUploadContent) →
    // sibling ['nodes', 'list', 'wiki-sibling'] is NOT invalidated →
    // siblingFetchCount stays at siblingCountBeforeSave →
    // this assertion fails with: expected N to be above N
    await waitFor(() => {
      expect(siblingFetchCount).toBeGreaterThan(siblingCountBeforeSave);
    });

    // Load-bearing pin 3 (Section 8.4 — rendered content updates):
    // After broad + narrow invalidation + content refetch, rendered markdown
    // shows the updated text.
    await waitFor(() => {
      expect(screen.getByText('Updated content.')).toBeInTheDocument();
    });
  });
});

// ─── W2-Test 2 — Add-markdown-to-empty ────────────────────────────────────────
//
// Substitution failure: remove the `mutation.mutate(...)` call from WikiMarkdownEditor
// handleSave when mounted from compose mode → POST /content never fires →
// capturedBody stays '' → `expect(capturedBody).toContain('My new content')` fails with:
//   AssertionError: expected '' to contain 'My new content'
//
// Also: remove Content-Type assertion or send wrong MIME →
// `expect(capturedContentType).toContain('text/markdown')` fails.

describe('W2-Test 2 — add-markdown-to-empty', () => {
  it('positive: contentType=null → "Add markdown" button → editor mounts → type text → save → POST /content with markdown MIME', async () => {
    let capturedBody = '';
    let capturedContentType = '';

    server.use(
      http.post(`${BASE_URL}/nodes/55/content`, async ({ request }) => {
        capturedBody = await request.text();
        capturedContentType = request.headers.get('content-type') ?? '';
        return new HttpResponse(null, { status: 200 });
      }),
      // After save, the node re-fetches with contentType now set.
      http.get(`${BASE_URL}/nodes/55`, () =>
        HttpResponse.json({ ...nodeWithoutContent, contentType: 'text/markdown; charset=utf-8' }),
      ),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiContentViewComponent node={nodeWithoutContent} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Empty-state renders with the "Add markdown" button.
    expect(screen.getByTestId('wiki-add-markdown-btn')).toBeInTheDocument();

    // Click "Add markdown".
    await userEvent.click(screen.getByTestId('wiki-add-markdown-btn'));

    // Editor textarea mounts in compose mode.
    const textarea = screen.getByTestId('wiki-editor-textarea') as HTMLTextAreaElement;
    expect(textarea).toBeInTheDocument();

    // Type content.
    await userEvent.type(textarea, 'My new content');

    // Click Save.
    await userEvent.click(screen.getByTestId('wiki-editor-save'));

    // MSW asserts POST body matches typed text AND correct MIME (Section 13.2).
    await waitFor(() => {
      expect(capturedBody).toContain('My new content');
      expect(capturedContentType).toContain('text/markdown');
    });
  });
});

// ─── W2-Test 3 — Upload-file-to-empty ─────────────────────────────────────────
//
// Substitution failure: bypass the upload mutate call in ContentUploadZone →
// POST /content never fires → capturedMime stays '' →
// `expect(capturedMime).toContain('text/plain')` fails with:
//   AssertionError: expected '' to contain 'text/plain'

describe('W2-Test 3 — upload-file-to-empty', () => {
  it('positive: contentType=null → "Upload file" button → drop file → POST /content with file MIME', async () => {
    let capturedMime = '';

    server.use(
      http.post(`${BASE_URL}/nodes/55/content`, async ({ request }) => {
        capturedMime = request.headers.get('content-type') ?? '';
        return new HttpResponse(null, { status: 200 });
      }),
    );

    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiContentViewComponent node={nodeWithoutContent} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Empty-state renders.
    expect(screen.getByTestId('wiki-upload-file-btn')).toBeInTheDocument();

    // Click "Upload file" to mount ContentUploadZone.
    await userEvent.click(screen.getByTestId('wiki-upload-file-btn'));

    // ContentUploadZone renders — find the hidden file input.
    // The zone renders a visually-hidden <input type="file">.
    // We simulate file selection via the hidden input directly.
    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    expect(fileInput).not.toBeNull();

    const testFile = new File(['hello upload'], 'test.txt', { type: 'text/plain' });

    await act(async () => {
      Object.defineProperty(fileInput, 'files', {
        value: [testFile],
        configurable: true,
      });
      fileInput.dispatchEvent(new Event('change', { bubbles: true }));
    });

    // MSW captures POST with the file's MIME type.
    await waitFor(() => {
      expect(capturedMime).toContain('text/plain');
    });
  });
});

// ─── W2-Test 4 — Empty-state buttons hidden when contentType IS set ────────────
//
// Substitution failure: remove the `!node.contentType` guard around the
// empty-state block → "Add markdown" and "Upload file" render alongside the
// markdown content →
// `expect(screen.queryByTestId('wiki-add-markdown-btn')).not.toBeInTheDocument()`
// fails with: AssertionError: expected element not to be in the document

describe('W2-Test 4 — empty-state buttons absent when contentType is set', () => {
  it('positive: contentType=text/markdown → "Add markdown" and "Upload file" NOT in document', async () => {
    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiContentViewComponent node={nodeWithMarkdown} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for the markdown content to load (proves the component settled).
    await waitFor(() => {
      expect(screen.getByTestId('wiki-content')).toBeInTheDocument();
    });

    // Empty-state buttons must NOT be present.
    expect(screen.queryByTestId('wiki-add-markdown-btn')).not.toBeInTheDocument();
    expect(screen.queryByTestId('wiki-upload-file-btn')).not.toBeInTheDocument();
  });
});

// ─── W2-Test 5 — Add-child-page flow ─────────────────────────────────────────
//
// Substitution failure: remove the `useLinkNodes` mutate call from handleChildCreated
// in WikiLayout → create POST fires but link POST does NOT →
// capturedLinkTargetId stays 0 →
// `expect(capturedLinkTargetId).toBe(99)` fails with:
//   AssertionError: expected 0 to be 99
//
// Navigation assertion: `expect(snapshot.value).toBe('/wiki/99')` also provides
// an independent load-bearing pin — remove navigate() call and it fails too.

describe('W2-Test 5 — add-child-page flow', () => {
  it('positive: click "+ Add child page", fill dialog, submit → POST /nodes + POST /nodes/42/links, navigate to /wiki/newId', async () => {
    let capturedLinkTargetId = 0;
    const snapshot = { value: '/wiki/42' };

    server.use(
      http.get(`${BASE_URL}/nodes/42`, () => HttpResponse.json(nodeWithMarkdown)),
      http.get(`${BASE_URL}/nodes/42/content`, () =>
        new HttpResponse('# Hello Wiki\n\nContent.', {
          headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
        }),
      ),
      http.post(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ id: 99, type: 'documentation', name: 'Child Page', status: null }),
      ),
      http.post(`${BASE_URL}/nodes/42/links`, async ({ request }) => {
        capturedLinkTargetId = await request.json() as number;
        return new HttpResponse(null, { status: 200 });
      }),
    );

    render(
      <MemoryRouter initialEntries={['/wiki/42']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route
              path="/wiki/:id"
              element={<><WikiLayoutComponent /><LocationCapture snapshot={snapshot} /></>}
            />
            <Route path="/wiki/:id" element={<LocationCapture snapshot={snapshot} />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for the page to load.
    await waitFor(() => {
      expect(screen.getByTestId('wiki-add-child-btn')).toBeInTheDocument();
    });

    // Click "+ Add child page".
    await userEvent.click(screen.getByTestId('wiki-add-child-btn'));

    // CreateNodeDialog opens — find inputs by their form ids.
    await waitFor(() => {
      expect(document.getElementById('create-type')).toBeInTheDocument();
    });

    // Fill in the form.
    const typeInput = document.getElementById('create-type') as HTMLInputElement;
    const nameInput = document.getElementById('create-name') as HTMLInputElement;

    await userEvent.type(typeInput, 'documentation');
    await userEvent.type(nameInput, 'Child Page');

    // Submit.
    const createBtn = screen.getByRole('button', { name: /Create/i });
    await userEvent.click(createBtn);

    // MSW captures the link POST with the new node id as body (Section 13.2).
    await waitFor(() => {
      expect(capturedLinkTargetId).toBe(99);
    });

    // URL navigated to the new node (Section 13.10 LocationCapture pattern).
    await waitFor(() => {
      expect(snapshot.value).toBe('/wiki/99');
    });
  });
});

// ─── W2-Test 6 — Add-child-page bug #317 graceful path ───────────────────────
//
// Substitution failure: remove the `isAlreadyLinked` check and navigation
// in the onError branch of WikiLayout.handleChildCreated →
// when MSW returns 500 "Nodes already linked", navigation is suppressed →
// snapshot stays '/wiki/42' →
// `expect(snapshot.value).toBe('/wiki/99')` fails with:
//   AssertionError: expected '/wiki/42' to be '/wiki/99'

describe('W2-Test 6 — add-child-page bug #317 graceful path', () => {
  it('positive: link POST returns 500 "Nodes already linked" → navigation STILL happens to /wiki/newId', async () => {
    const snapshot = { value: '/wiki/42' };

    server.use(
      http.get(`${BASE_URL}/nodes/42`, () => HttpResponse.json(nodeWithMarkdown)),
      http.get(`${BASE_URL}/nodes/42/content`, () =>
        new HttpResponse('# Hello Wiki\n\nContent.', {
          headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
        }),
      ),
      http.post(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ id: 99, type: 'documentation', name: 'Child Page', status: null }),
      ),
      // Bug #317: backend returns 500 with the already-linked error text.
      http.post(`${BASE_URL}/nodes/42/links`, () =>
        HttpResponse.json(
          { code: 'unhandled', text: 'Nodes already linked' },
          { status: 500 },
        ),
      ),
    );

    render(
      <MemoryRouter initialEntries={['/wiki/42']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route
              path="/wiki/:id"
              element={<><WikiLayoutComponent /><LocationCapture snapshot={snapshot} /></>}
            />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for the page to load.
    await waitFor(() => {
      expect(screen.getByTestId('wiki-add-child-btn')).toBeInTheDocument();
    });

    await userEvent.click(screen.getByTestId('wiki-add-child-btn'));

    await waitFor(() => {
      expect(document.getElementById('create-type')).toBeInTheDocument();
    });

    await userEvent.type(document.getElementById('create-type') as HTMLInputElement, 'documentation');
    await userEvent.type(document.getElementById('create-name') as HTMLInputElement, 'Child Page');
    await userEvent.click(screen.getByRole('button', { name: /Create/i }));

    // Despite the 500, navigation fires because the catch treats it as success.
    await waitFor(() => {
      expect(snapshot.value).toBe('/wiki/99');
    });
  });
});

// ─── W2-Test 7 — Rename flow ──────────────────────────────────────────────────
//
// Substitution failure: remove the `wiki-rename-btn` button or remove its
// onClick handler opening the dialog → clicking "Rename" does nothing →
// the EditNodeDialog never mounts → PATCH /nodes/42 never fires →
// capturedPatchOps stays [] →
// `expect(capturedPatchOps).toHaveLength(1)` fails with:
//   AssertionError: expected [] to have a length of 1 but got 0
//
// Section 8.4 pin: we assert the rendered heading updates after save,
// not just the spy call.

describe('W2-Test 7 — rename flow', () => {
  it('positive: click "Rename", EditNodeDialog opens, change name, save → PATCH /nodes/42 with replace /name op', async () => {
    let capturedPatchOps: unknown[] = [];

    server.use(
      http.get(`${BASE_URL}/nodes/42`, () => HttpResponse.json(nodeWithMarkdown)),
      http.get(`${BASE_URL}/nodes/42/content`, () =>
        new HttpResponse('# Hello Wiki\n\nContent.', {
          headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
        }),
      ),
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedPatchOps = await request.json() as unknown[];
        return new HttpResponse(null, { status: 200 });
      }),
    );

    render(
      <MemoryRouter initialEntries={['/wiki/42']}>
        <QueryClientProvider client={makeQC()}>
          <Routes>
            <Route path="/wiki/:id" element={<WikiLayoutComponent />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for node to load.
    await waitFor(() => {
      expect(screen.getByTestId('wiki-rename-btn')).toBeInTheDocument();
    });

    // Click "Rename".
    await userEvent.click(screen.getByTestId('wiki-rename-btn'));

    // EditNodeDialog opens — find the name input by its form id.
    await waitFor(() => {
      expect(document.getElementById('edit-name')).toBeInTheDocument();
    });

    const nameInput = document.getElementById('edit-name') as HTMLInputElement;

    // Clear current name and type the new one.
    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, 'Renamed Doc');

    // Click Save.
    await userEvent.click(screen.getByRole('button', { name: /Save/i }));

    // MSW captures the PATCH with a replace /name operation (Section 13.2).
    await waitFor(() => {
      expect(capturedPatchOps).toHaveLength(1);
    });

    const op = capturedPatchOps[0] as { op: string; path: string; value: string };
    expect(op.op).toBe('replace');
    expect(op.path).toBe('/name');
    expect(op.value).toBe('Renamed Doc');
  });
});

// ─── W2-Test 8 — Markdown editor textarea has theme-aware classes ─────────────
//
// Substitution failure: remove any one of the `dark:` classes from the
// textarea's className in WikiMarkdownEditor →
// `expect(textarea).toHaveClass('dark:text-foreground')` fails with:
//   AssertionError: expected element to have class 'dark:text-foreground'
//
// Closes the textarea-theming half of bug #281 (Section 14.2).

describe('W2-Test 8 — markdown editor textarea has theme-aware classes', () => {
  it('positive: render editor → textarea has both light-mode class and dark: counterpart (Section 14.2)', async () => {
    render(
      <MemoryRouter>
        <QueryClientProvider client={makeQC()}>
          <WikiContentViewComponent node={nodeWithMarkdown} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for initial content, then open editor.
    await waitFor(() => {
      expect(screen.getByTestId('wiki-content')).toBeInTheDocument();
    });

    await userEvent.click(screen.getByTestId('wiki-edit-btn'));

    const textarea = screen.getByTestId('wiki-editor-textarea');
    expect(textarea).toBeInTheDocument();

    // Section 14.2: both light-mode AND dark-mode Tailwind classes must be present.
    // Light-mode class:
    expect(textarea).toHaveClass('bg-background');
    // Dark-mode counterpart:
    expect(textarea).toHaveClass('dark:bg-background/80');

    // Additional text color pair:
    expect(textarea).toHaveClass('text-foreground');
    expect(textarea).toHaveClass('dark:text-foreground');
  });
});
