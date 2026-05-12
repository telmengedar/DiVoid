/**
 * Component tests for NodeDetailPage.
 *
 * Covers:
 *  - Renders all four canonical regions: metadata, content, neighbours, back link.
 *  - 404 shows the backend error text inline.
 *  - Markdown content is rendered (not shown as raw source).
 *  - Linked neighbours are shown as clickable links.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode, samplePage } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
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
    NODES: {
      LIST: '/nodes',
      PATH: '/nodes/path',
      DETAIL: (id: number) => `/nodes/${id}`,
      CONTENT: (id: number) => `/nodes/${id}/content`,
    },
  },
  ROUTES: {
    HOME: '/',
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn() } }));

// ─── Helpers ──────────────────────────────────────────────────────────────────

function renderAtId(id: number | string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={[`/nodes/${id}`]}>
      <QueryClientProvider client={qc}>
        <Routes>
          <Route path="/nodes/:id" element={<NodeDetailPage />} />
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

describe('NodeDetailPage', () => {
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
      // The markdown "# Hello" should render as a heading, not a literal "#"
      expect(screen.getByRole('heading', { name: 'Hello', level: 1 })).toBeInTheDocument();
    });

    // Rendered bold text
    expect(screen.getByText('markdown')).toBeInTheDocument();
  });

  it('renders linked neighbours region', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: /linked nodes/i })).toBeInTheDocument();
    });

    // samplePage has 'First task', 'Some doc', 'DiVoid'
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
