/**
 * Load-bearing tests for the workspace peek modal (DiVoid #275 / #1253).
 *
 * Pins behavioural contract from design §10:
 *  T1. Clicking a node card opens the modal (URL shows ?peek=<id>).
 *  T2. Close button removes the modal.
 *  T3. Deep-link /workspace?peek=42 opens modal pre-opened on first render.
 *  T4. Neighbour-row click in modal swaps the peek (modal stays open).
 *  T5. Delete from modal closes modal (onClose called).
 *  T6. Canvas render-stability — WorkspaceCanvas does NOT re-render when peek
 *      state changes (the load-bearing invariant from design §9).
 *  T7. Modal absent when no peekId.
 *
 * T6 is the most critical. The stub is wrapped in React.memo: if onPeek were
 * not referentially stable (missing useCallback in usePeekState), the memoised
 * canvas would still re-render on peek-state changes because its prop changed.
 * The canvasRenderCountRef counter exposes this.
 *
 * Mental-deletion checks are documented per test.
 *
 * Fiber-walk ban (§13.4 of #420) respected — pinned via DOM state only.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode } from '@/test/msw/handlers';

// ─── Hoisted render-count ref ─────────────────────────────────────────────────
// vi.mock is hoisted before any imports or let/const declarations. vi.hoisted()
// allows us to share a mutable ref between the mock factory and the test body.

const { canvasRenderCountRef } = vi.hoisted(() => ({
  canvasRenderCountRef: { current: 0 },
}));

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(sampleNode);
    if (id === 7) return HttpResponse.json({ id: 7, type: 'task', name: 'Task Seven', status: 'open' });
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, () =>
    new HttpResponse('# Content', { headers: { 'Content-Type': 'text/markdown' } }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('linkedto')) {
      return HttpResponse.json({ result: [{ id: 7, type: 'task', name: 'Task Seven', status: 'open' }], total: 1 });
    }
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.delete(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }));

vi.mock('@/features/nodes/EditNodeDialog', () => ({
  EditNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Edit node"><button onClick={() => onOpenChange(false)}>Close edit</button></div> : null,
}));

vi.mock('@/features/nodes/DeleteNodeDialog', () => ({
  DeleteNodeDialog: ({ open, onOpenChange, onDeleted }: { open: boolean; onOpenChange: (v: boolean) => void; onDeleted: () => void }) =>
    open ? (
      <div role="dialog" aria-label="Delete node">
        <button onClick={() => { onOpenChange(false); onDeleted(); }}>Confirm delete</button>
      </div>
    ) : null,
}));

vi.mock('@/features/nodes/LinkNodeDialog', () => ({
  LinkNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Add link"><button onClick={() => onOpenChange(false)}>Close link</button></div> : null,
}));

vi.mock('@/features/nodes/ContentUploadZone', () => ({
  ContentUploadZone: () => <div data-testid="upload-zone">Upload zone</div>,
}));

vi.mock('@/features/nodes/MarkdownEditorSurface', () => ({
  MarkdownEditorSurface: () => <div data-testid="markdown-editor" />,
  isTextShaped: (ct: string | null | undefined): boolean => {
    if (!ct) return false;
    return ct.startsWith('text/') || ct.startsWith('application/json');
  },
}));

/**
 * WorkspaceCanvas stub — wrapped in React.memo so re-renders only occur when
 * props change. This mirrors the render-stability guarantee in design §9:
 * because onPeek is a stable useCallback reference from usePeekState, the
 * memoised canvas should NOT re-render when peek state changes.
 *
 * canvasRenderCountRef tracks how many times the canvas function ran. A stable
 * onPeek → render count stays at 1 after initial mount. An unstable onPeek →
 * render count increments on every peek-state change, failing T6.
 */
vi.mock('./WorkspaceCanvas', async () => {
  const { memo } = await import('react');
  return {
    WorkspaceCanvas: memo(
      ({ onPeek }: { onPeek: (id: number) => void }) => {
        canvasRenderCountRef.current += 1;
        return (
          <div data-testid="workspace-canvas">
            <button data-testid="card-1" aria-label="Node: First task (task)" onClick={() => onPeek(42)}>
              First task
            </button>
            <button data-testid="card-7" aria-label="Node: Task Seven (task)" onClick={() => onPeek(7)}>
              Task Seven
            </button>
          </div>
        );
      },
    ),
  };
});

// ─── Helpers ──────────────────────────────────────────────────────────────────

beforeEach(() => { canvasRenderCountRef.current = 0; });

function makeQC() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
}

let WorkspacePage: typeof import('./WorkspacePage').WorkspacePage;

beforeAll(async () => {
  const mod = await import('./WorkspacePage');
  WorkspacePage = mod.WorkspacePage;
});

function renderWorkspace(initialPath = '/workspace') {
  const qc = makeQC();
  return {
    qc,
    ...render(
      <MemoryRouter initialEntries={[initialPath]}>
        <QueryClientProvider client={qc}>
          <Routes>
            <Route path="/workspace" element={<WorkspacePage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>,
    ),
  };
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('WorkspacePeekModal — open on click', () => {
  /**
   * T1: Clicking a node card opens the peek modal showing NodeDetailView.
   *
   * The canvas stub calls onPeek(42) → WorkspacePage sets ?peek=42 → modal
   * opens → NodeDetailView fetches node 42 → "Test Document" visible.
   *
   * Mental-deletion check: remove data.onPeek(data.id) from NodeCardRenderer or
   * fail to pass onPeek from WorkspacePage → modal never opens → fails at
   * "workspace-peek-modal" assertion.
   */
  it('T1: clicking a node card opens the peek modal', async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await waitFor(() => expect(screen.getByTestId('workspace-canvas')).toBeInTheDocument());

    await user.click(screen.getByTestId('card-1'));

    await waitFor(() => {
      expect(screen.getByTestId('workspace-peek-modal')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });
  });
});

describe('WorkspacePeekModal — close', () => {
  /**
   * T2: The X close button closes the modal.
   *
   * Mental-deletion check: remove onClose() from X button handler in
   * WorkspaceNodePeekModal → modal stays open after click → fails at
   * "workspace-peek-modal absent" assertion.
   */
  it('T2: close button removes the modal', async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await waitFor(() => expect(screen.getByTestId('workspace-canvas')).toBeInTheDocument());
    await user.click(screen.getByTestId('card-1'));
    await waitFor(() => expect(screen.getByTestId('workspace-peek-modal')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /close preview/i }));

    await waitFor(() => {
      expect(screen.queryByTestId('workspace-peek-modal')).not.toBeInTheDocument();
    });
  });
});

describe('WorkspacePeekModal — deep-link', () => {
  /**
   * T3: Opening /workspace?peek=42 directly pre-opens the modal.
   *
   * Mental-deletion check: if usePeekState returned fixed peekId=null instead
   * of reading URL → modal does not open → fails.
   */
  it('T3: deep-link /workspace?peek=42 pre-opens the modal', async () => {
    renderWorkspace('/workspace?peek=42');

    await waitFor(() => {
      expect(screen.getByTestId('workspace-peek-modal')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });
  });
});

describe('WorkspacePeekModal — neighbour peek swap', () => {
  /**
   * T4: Clicking a neighbour row inside the modal calls onPeekChange(7).
   * The modal stays open; nodeId prop changes to 7; heading shows "Task Seven".
   *
   * Mental-deletion check: if onNeighbourClick is not wired from WorkspacePage
   * to WorkspaceNodePeekModal to NodeDetailView → neighbour click does nothing →
   * heading stays "Test Document" (node 42) → fails at "Task Seven" heading check.
   */
  it('T4: clicking a neighbour row swaps the peek to the neighbour', async () => {
    const user = userEvent.setup();
    renderWorkspace('/workspace?peek=42');

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /open peek for node 7/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /open peek for node 7/i }));

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Task Seven' })).toBeInTheDocument();
    });

    expect(screen.getByTestId('workspace-peek-modal')).toBeInTheDocument();
  });
});

describe('WorkspacePeekModal — delete from modal closes modal', () => {
  /**
   * T5: Confirming delete from inside the modal calls onClose (closePeek),
   * closing the modal.
   *
   * Mental-deletion check: if NodeDetailView's DeleteNodeDialog.onDeleted does
   * not call onClose() → modal stays open after delete → fails.
   */
  it('T5: confirming delete from modal closes the modal', async () => {
    const user = userEvent.setup();
    renderWorkspace('/workspace?peek=42');

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /delete node/i }));
    await waitFor(() => expect(screen.getByRole('dialog', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /confirm delete/i }));

    await waitFor(() => {
      expect(screen.queryByTestId('workspace-peek-modal')).not.toBeInTheDocument();
    });
  });
});

describe('WorkspacePeekModal — canvas render-stability (DiVoid #271)', () => {
  /**
   * T6: WorkspaceCanvas does NOT re-render when peek state changes.
   *
   * The canvas stub is wrapped in React.memo. With a stable onPeek callback
   * (useCallback with [setSearchParams] deps in usePeekState), the memo sees
   * identical props across peek state changes and skips re-rendering.
   *
   * canvasRenderCountRef.current starts at 0 (reset per beforeEach). After
   * WorkspacePage mounts, it should be 1 (initial render). After opening and
   * closing the peek, it must still be 1.
   *
   * Mental-deletion check: if openPeek / closePeek were NOT wrapped in
   * useCallback, they'd be new function references on every WorkspacePage
   * render, causing the memoised canvas to receive a new prop and re-render.
   * canvasRenderCountRef.current would exceed 1, failing this test.
   */
  it('T6: memoised WorkspaceCanvas does not re-render when peek opens or closes', async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await waitFor(() => expect(screen.getByTestId('workspace-canvas')).toBeInTheDocument());

    const countAfterMount = canvasRenderCountRef.current;
    expect(countAfterMount).toBe(1);

    await user.click(screen.getByTestId('card-1'));
    await waitFor(() => expect(screen.getByTestId('workspace-peek-modal')).toBeInTheDocument());

    expect(canvasRenderCountRef.current).toBe(countAfterMount);

    await user.click(screen.getByRole('button', { name: /close preview/i }));
    await waitFor(() => expect(screen.queryByTestId('workspace-peek-modal')).not.toBeInTheDocument());

    expect(canvasRenderCountRef.current).toBe(countAfterMount);
  });
});

describe('WorkspacePeekModal — modal absent when no peek', () => {
  /**
   * T7: When no ?peek param is present, the modal content is not in the DOM.
   * Guards against NodeDetailView accidentally mounting with nodeId=0.
   */
  it('T7: modal is not rendered when peekId is null', async () => {
    renderWorkspace('/workspace');

    await waitFor(() => expect(screen.getByTestId('workspace-canvas')).toBeInTheDocument());

    expect(screen.queryByTestId('workspace-peek-modal')).not.toBeInTheDocument();
    expect(screen.queryByTestId('node-detail-view')).not.toBeInTheDocument();
  });
});
