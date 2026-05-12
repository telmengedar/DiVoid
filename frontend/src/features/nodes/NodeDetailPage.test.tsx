/**
 * Component tests for NodeDetailPage.
 *
 * Covers:
 *  - Renders all four canonical regions: metadata, content, neighbours, back link.
 *  - 404 shows the backend error text inline.
 *  - Markdown content is rendered (not shown as raw source).
 *  - Linked neighbours are shown as clickable links.
 *  - Write buttons shown/hidden based on whoami permissions.
 *  - Edit and Delete dialogs are triggered correctly.
 *
 * The dialog components themselves (EditNodeDialog, DeleteNodeDialog,
 * LinkNodeDialog, ContentUploadZone) are mocked here to avoid OOM from
 * rendering heavy Radix + react-markdown trees in jsdom.
 * Each dialog component has its own dedicated test file.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode, samplePage } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const writeUser = {
  id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
  createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
};

const readOnlyUser = { ...writeUser, permissions: ['read'] };

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(writeUser)),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(sampleNode);
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) {
      return new HttpResponse('# Hello\n\nThis is **markdown** content.', {
        headers: { 'Content-Type': 'text/markdown' },
      });
    }
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('linkedto')) return HttpResponse.json(samplePage);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.delete(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

// Mock heavy dialog components to prevent jsdom OOM in unit tests.
// Each component has its own dedicated isolated test file.
vi.mock('./EditNodeDialog', () => ({
  EditNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Edit node"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));

vi.mock('./DeleteNodeDialog', () => ({
  DeleteNodeDialog: ({ open, onOpenChange, onDeleted }: { open: boolean; onOpenChange: (v: boolean) => void; onDeleted: () => void }) =>
    open ? (
      <div role="dialog" aria-label="Delete node">
        <button onClick={() => { onOpenChange(false); onDeleted(); }}>Delete</button>
        <button onClick={() => onOpenChange(false)}>Cancel</button>
      </div>
    ) : null,
}));

vi.mock('./LinkNodeDialog', () => ({
  LinkNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Add link"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));

vi.mock('./ContentUploadZone', () => ({
  ContentUploadZone: () => <div data-testid="upload-zone">Upload zone</div>,
}));

// ─── Helpers ──────────────────────────────────────────────────────────────────

function renderAtId(id: number | string) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter initialEntries={[`/nodes/${id}`]}>
      <QueryClientProvider client={qc}>
        <Routes>
          <Route path="/nodes/:id" element={<NodeDetailPage />} />
          <Route path="/search" element={<div>Search page</div>} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Import lazily so mocks are registered first ──────────────────────────────

let NodeDetailPage: typeof import('./NodeDetailPage').NodeDetailPage;

beforeAll(async () => {
  const mod = await import('./NodeDetailPage');
  NodeDetailPage = mod.NodeDetailPage;
});

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('NodeDetailPage — read regions', () => {
  it('renders metadata region with id, type, name, status, contentType', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('documentation')).toBeInTheDocument();
    expect(screen.getByText('open')).toBeInTheDocument();
    expect(screen.getByText('text/markdown; charset=utf-8')).toBeInTheDocument();
  });

  it('renders markdown content (not raw source)', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Hello', level: 1 })).toBeInTheDocument();
    });

    expect(screen.getByText('markdown')).toBeInTheDocument();
  });

  it('renders linked neighbours region', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: /linked nodes/i })).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    });
  });

  it('neighbour names are clickable links', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    });

    const link = screen.getByRole('link', { name: 'First task' });
    expect(link).toHaveAttribute('href', '/nodes/1');
  });

  it('renders back link to search', async () => {
    renderAtId(42);

    await waitFor(() => {
      const backLink = screen.getByRole('link', { name: /back to search/i });
      expect(backLink).toHaveAttribute('href', '/search');
    });
  });

  it('shows 404 message for unknown node', async () => {
    renderAtId(999);

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Node 999 not found');
    });
  });

  it('shows invalid id message for non-numeric id', () => {
    renderAtId('not-a-number');

    expect(screen.getByRole('alert')).toHaveTextContent('Invalid node ID');
  });
});

describe('NodeDetailPage — write affordances', () => {
  it('shows Edit and Delete buttons for write users', async () => {
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /edit node/i })).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /delete node/i })).toBeInTheDocument();
  });

  it('hides Edit and Delete buttons for read-only users', async () => {
    server.use(http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(readOnlyUser)));
    renderAtId(42);
    await waitFor(() => expect(screen.getByText('Test Document')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /edit node/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete node/i })).not.toBeInTheDocument();
  });

  it('shows Add link button for write users', async () => {
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /add link/i })).toBeInTheDocument());
  });

  it('hides Add link button for read-only users', async () => {
    server.use(http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(readOnlyUser)));
    renderAtId(42);
    await waitFor(() => expect(screen.getByText('Test Document')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /add link/i })).not.toBeInTheDocument();
  });

  it('opens delete confirmation dialog when Delete is clicked', async () => {
    const user = userEvent.setup();
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /delete node/i }));

    await waitFor(() =>
      expect(screen.getByRole('dialog', { name: /delete node/i })).toBeInTheDocument(),
    );
  });

  it('opens edit dialog when Edit is clicked', async () => {
    const user = userEvent.setup();
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /edit node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /edit node/i }));

    await waitFor(() =>
      expect(screen.getByRole('dialog', { name: /edit node/i })).toBeInTheDocument(),
    );
  });

  it('navigates to /search after successful delete', async () => {
    const user = userEvent.setup();
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /delete node/i }));
    await waitFor(() => expect(screen.getByRole('dialog', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /^delete$/i }));

    await waitFor(() => expect(screen.getByText('Search page')).toBeInTheDocument());
  });
});
