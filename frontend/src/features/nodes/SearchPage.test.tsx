/**
 * Component tests for SearchPage.
 *
 * Covers:
 *  - Semantic tab: entering a query and submitting updates displayed results.
 *  - Linked tab: entering an id and browsing shows neighbours.
 *  - Path tab: a 400 error shows the column-pointing message inline.
 *  - Tab switching renders correct panel.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

const { navigateSpy } = vi.hoisted(() => ({ navigateSpy: vi.fn() }));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateSpy };
});
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, semanticPage, samplePage } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('query')) return HttpResponse.json(semanticPage);
    if (url.searchParams.get('linkedto')) return HttpResponse.json(samplePage);
    return HttpResponse.json({ result: [], total: 0 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => navigateSpy.mockClear());
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

// sonner toast is a no-op in tests
vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

// Mock heavy dialog to prevent jsdom OOM — tested in CreateNodeDialog.test.tsx
vi.mock('./CreateNodeDialog', () => ({
  CreateNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Create node"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));

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
  it('renders the four tab buttons', () => {
    renderPage();
    expect(screen.getByRole('tab', { name: /semantic/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /linked/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /path/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /by id/i })).toBeInTheDocument();
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

  it('ST1: switching to By ID tab renders the input and a disabled Open button', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /by id/i }));

    expect(screen.getByRole('spinbutton', { name: /node id to open/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /open/i })).toBeDisabled();
  });

  it('ST2: typing a positive id enables Open and submitting navigates to /nodes/{id}', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /by id/i }));
    const input = screen.getByRole('spinbutton', { name: /node id to open/i });
    await user.type(input, '1462');

    const openBtn = screen.getByRole('button', { name: /open/i });
    expect(openBtn).toBeEnabled();

    await user.click(openBtn);

    expect(navigateSpy).toHaveBeenCalledWith('/nodes/1462');
  });

  it('ST3: typing 0 keeps Open disabled (non-positive guard)', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /by id/i }));
    const input = screen.getByRole('spinbutton', { name: /node id to open/i });
    await user.type(input, '0');

    expect(screen.getByRole('button', { name: /open/i })).toBeDisabled();
  });

  it('ST4: Open stays disabled when input is empty', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('tab', { name: /by id/i }));
    expect(screen.getByRole('button', { name: /open/i })).toBeDisabled();
    expect(navigateSpy).not.toHaveBeenCalledWith(expect.stringMatching(/^\/nodes\//));
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
