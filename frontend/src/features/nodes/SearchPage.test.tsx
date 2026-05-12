/**
 * Component tests for SearchPage.
 *
 * Covers:
 *  - Semantic tab: entering a query and submitting updates displayed results.
 *  - Linked tab: entering an id and browsing shows neighbours.
 *  - Path tab: a 400 error shows the column-pointing message inline.
 *  - Tab switching renders correct panel.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, semanticPage, samplePage } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('query')) return HttpResponse.json(semanticPage);
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

// sonner toast is a no-op in tests
vi.mock('sonner', () => ({ toast: { error: vi.fn() } }));

// ─── Helpers ──────────────────────────────────────────────────────────────────

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={qc}>
        <SearchPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

// Import lazily so mocks are registered first
let SearchPage: typeof import('./SearchPage').SearchPage;

beforeAll(async () => {
  const mod = await import('./SearchPage');
  SearchPage = mod.SearchPage;
});

describe('SearchPage', () => {
  it('renders the three tab buttons', () => {
    renderPage();
    expect(screen.getByRole('tab', { name: /semantic/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /linked/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /path/i })).toBeInTheDocument();
  });

  it('semantic tab shows results after submitting a query', async () => {
    const user = userEvent.setup();
    renderPage();

    const input = screen.getByRole('searchbox', { name: /semantic search query/i });
    await user.type(input, 'auth token');
    await user.click(screen.getByRole('button', { name: /search/i }));

    await waitFor(() => {
      expect(screen.getByText('Auth notes')).toBeInTheDocument();
    });
  });

  it('switching to linked tab shows a node id input', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /linked/i }));

    expect(screen.getByRole('spinbutton', { name: /anchor node id/i })).toBeInTheDocument();
  });

  it('linked tab shows neighbours after submitting an id', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /linked/i }));
    const input = screen.getByRole('spinbutton', { name: /anchor node id/i });
    await user.type(input, '3');
    await user.click(screen.getByRole('button', { name: /browse/i }));

    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    });
  });

  it('path tab shows column-pointing error message on 400', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('path')) {
          return HttpResponse.json(
            { code: 'badparameter', text: 'Path query syntax error at column 5' },
            { status: 400 },
          );
        }
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /path/i }));
    const input = screen.getByRole('textbox', { name: /path expression/i });
    // fireEvent.change avoids userEvent's keyboard-descriptor parsing of "[" characters
    fireEvent.change(input, { target: { value: '[bad' } });
    await user.click(screen.getByRole('button', { name: /traverse/i }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Path query syntax error at column 5',
      );
    });
  });
});
