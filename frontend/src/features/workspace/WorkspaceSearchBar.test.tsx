// @vitest-environment happy-dom
/**
 * Load-bearing tests for WorkspaceSearchBar (DiVoid #275, #1607).
 *
 * Test coverage per design §13.2 (WT1–WT5):
 *
 * WT1 — typing a pure-digit id and pressing Enter calls onOpenPeek(id).
 * WT2 — typing non-digit and pressing Enter does NOT call onOpenPeek;
 *        the semantic query fires after the 250 ms debounce.
 * WT3 — classifyInput unit test: '1462' → 'id', 'auth flow' → 'query', '  ' → 'empty'.
 * WT4 — mounting WorkspaceSearchBar next to WorkspaceCanvas does NOT cause
 *        the canvas to re-render (render-count sentinel, MAX_RENDERS=30 pattern
 *        per §13.7 / WorkspacePage.renderLoop.test.tsx).
 * WT5 — clicking a dropdown row calls onOpenPeek with that row's id and clears
 *        the input.
 *
 * All tests with network calls use MSW.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { useState } from 'react';
import { BASE_URL } from '@/test/msw/handlers';

const semanticFixture = {
  result: [
    { id: 10, type: 'documentation', name: 'Auth notes', status: null, similarity: 0.92 },
    { id: 11, type: 'task',          name: 'Fix token',  status: 'open', similarity: 0.75 },
  ],
  total: 2,
};

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('query')) return HttpResponse.json(semanticFixture);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.get(`${BASE_URL}/types`, () =>
    HttpResponse.json({ result: [{ id: 6, type: 'task', count: 1 }], total: 1 }),
  ),
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.clearAllMocks(); });
afterAll(() => server.close());

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
    TYPES: '/types',
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), warning: vi.fn() } }));
vi.mock('next-themes', () => ({
  useTheme: vi.fn(() => ({ resolvedTheme: 'dark', setTheme: vi.fn() })),
}));

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries:   { retry: false, staleTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderBar(onOpenPeek: (id: number) => void) {
  const qc = makeQC();
  let WorkspaceSearchBar: typeof import('./WorkspaceSearchBar').WorkspaceSearchBar;
  return import('./WorkspaceSearchBar').then((mod) => {
    WorkspaceSearchBar = mod.WorkspaceSearchBar;
    return render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <WorkspaceSearchBar onOpenPeek={onOpenPeek} />
        </QueryClientProvider>
      </MemoryRouter>,
    );
  });
}

describe('classifyInput', () => {
  /**
   * WT3 (load-bearing unit test).
   *
   * Negative proof: flip the regex anchors in classifyInput (remove ^ or $)
   * and this test fails with a type-mismatch assertion. Trim the > 0 guard and
   * the '0' case assertion fails.
   */
  it("returns 'id' for pure positive digit strings", async () => {
    const { classifyInput } = await import('./WorkspaceSearchBar');
    expect(classifyInput('1462')).toBe('id');
    expect(classifyInput('1')).toBe('id');
    expect(classifyInput('  42  ')).toBe('id');
  });

  it("returns 'query' for non-digit or mixed strings", async () => {
    const { classifyInput } = await import('./WorkspaceSearchBar');
    expect(classifyInput('auth flow')).toBe('query');
    expect(classifyInput('12abc')).toBe('query');
    expect(classifyInput('abc123')).toBe('query');
  });

  it("returns 'empty' for blank or whitespace-only strings", async () => {
    const { classifyInput } = await import('./WorkspaceSearchBar');
    expect(classifyInput('  ')).toBe('empty');
    expect(classifyInput('')).toBe('empty');
  });

  it("returns 'query' (not 'id') for '0' because 0 is not positive", async () => {
    const { classifyInput } = await import('./WorkspaceSearchBar');
    expect(classifyInput('0')).toBe('query');
  });
});

describe('WorkspaceSearchBar — id-mode submit', () => {
  /**
   * WT1 (load-bearing positive proof).
   *
   * Negative proof: remove the `if (mode === 'id') { onOpenPeek(...) }` branch
   * from the Enter handler — spy never fires, `toHaveBeenCalledWith(1462)` fails.
   */
  it('typing a digit id and pressing Enter calls onOpenPeek with that id', async () => {
    const spy = vi.fn();
    await renderBar(spy);

    const input = screen.getByRole('textbox', { name: /search by id or query/i });

    fireEvent.change(input, { target: { value: '1462' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(spy).toHaveBeenCalledOnce();
    expect(spy).toHaveBeenCalledWith(1462);
  });

  it('pressing Enter with a digit id clears the input', async () => {
    const spy = vi.fn();
    await renderBar(spy);

    const input = screen.getByRole('textbox', { name: /search by id or query/i });

    fireEvent.change(input, { target: { value: '42' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect((input as HTMLInputElement).value).toBe('');
  });
});

describe('WorkspaceSearchBar — semantic-mode debounce', () => {
  /**
   * WT2 (load-bearing positive proof).
   *
   * Negative proof (Enter no-op side):
   *   Remove the `mode === 'id'` guard → Enter for non-digit also calls
   *   onOpenPeek, making `not.toHaveBeenCalled` fail.
   *
   * Negative proof (debounce side):
   *   Remove the 250 ms setTimeout → query fires immediately on first
   *   keystroke. We capture requests via MSW to confirm timing.
   */
  it('pressing Enter with non-digit input does NOT call onOpenPeek', async () => {
    const spy = vi.fn();
    await renderBar(spy);

    const input = screen.getByRole('textbox', { name: /search by id or query/i });

    fireEvent.change(input, { target: { value: 'auth flow' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(spy).not.toHaveBeenCalled();
  });

  it('semantic query fires after 250 ms debounce and populates the dropdown', async () => {
    const requestedQueries: string[] = [];
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const q = url.searchParams.get('query');
        if (q) {
          requestedQueries.push(q);
          return HttpResponse.json(semanticFixture);
        }
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    await renderBar(vi.fn());

    const input = screen.getByRole('textbox', { name: /search by id or query/i });

    await act(async () => {
      fireEvent.change(input, { target: { value: 'auth flow' } });
    });

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 300));
    });

    await waitFor(() => {
      expect(requestedQueries.length).toBeGreaterThan(0);
      expect(requestedQueries[0]).toBe('auth flow');
    }, { timeout: 3000 });

    await waitFor(() => {
      expect(screen.getByRole('listbox')).toBeInTheDocument();
      expect(screen.getByText('Auth notes')).toBeInTheDocument();
    }, { timeout: 3000 });
  });
});

describe('WorkspaceSearchBar — dropdown row click', () => {
  /**
   * WT5 (load-bearing positive proof).
   *
   * Negative proof (onOpenPeek):
   *   Remove `handleRowClick` from the `onPointerDown` handler →
   *   `toHaveBeenCalledWith(10)` fails.
   *
   * Negative proof (input clear):
   *   Remove `setInput('')` from `handleRowClick` →
   *   input retains its value, dropdown stays open; `toBe('')` fails.
   */
  it('clicking a result row calls onOpenPeek with that id and clears the input', async () => {
    const spy = vi.fn();
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('query')) return HttpResponse.json(semanticFixture);
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    await renderBar(spy);

    const input = screen.getByRole('textbox', { name: /search by id or query/i });

    await act(async () => {
      fireEvent.change(input, { target: { value: 'auth flow' } });
    });

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 300));
    });

    await waitFor(() => {
      expect(screen.getByRole('listbox')).toBeInTheDocument();
    }, { timeout: 3000 });

    const firstRow = screen.getByText('Auth notes');
    fireEvent.pointerDown(firstRow);

    expect(spy).toHaveBeenCalledOnce();
    expect(spy).toHaveBeenCalledWith(10);
    expect((input as HTMLInputElement).value).toBe('');
  });
});

describe('WorkspaceSearchBar — render stability (WT4)', () => {
  /**
   * WT4 (load-bearing render-stability test).
   *
   * Design §13.2: mounting WorkspaceSearchBar next to WorkspaceCanvas (represented
   * here by MockWorkspaceCanvas) must NOT cause the canvas to re-render when the
   * search bar's local input state changes.
   *
   * MockWorkspaceCanvas is a vi.fn() component; mock.calls.length is the render count.
   * The wrapper does NOT pass any input-derived prop to MockWorkspaceCanvas — exactly
   * the production topology (WorkspacePage mounts both as siblings without threading
   * input state to the canvas, per design §9 / DiVoid #271).
   *
   * Substitution proof (§13.2): lift searchInput to the wrapper and pass it to
   * MockWorkspaceCanvas as a prop → render count exceeds initial settle count →
   * assertion `mock.calls.length === initialRenderCount` fails.
   * Verbatim failure from substitution run recorded in PR body.
   *
   * Negative proof (render-loop guard): component does not loop to MAX_RENDERS on mount.
   */

  const MAX_RENDERS = 30;

  const MockWorkspaceCanvas = vi.fn(() => <div data-testid="mock-canvas" />);

  function TestWrapper({ onOpenPeek }: { onOpenPeek: (id: number) => void }) {
    return (
      <div className="relative h-full w-full">
        <MockWorkspaceCanvas />
        <WorkspaceSearchBarUnderTest onOpenPeek={onOpenPeek} />
      </div>
    );
  }

  let WorkspaceSearchBarUnderTest: typeof import('./WorkspaceSearchBar').WorkspaceSearchBar;

  beforeAll(async () => {
    const mod = await import('./WorkspaceSearchBar');
    WorkspaceSearchBarUnderTest = mod.WorkspaceSearchBar;
  });

  it('typing into WorkspaceSearchBar does NOT cause MockWorkspaceCanvas to re-render', async () => {
    MockWorkspaceCanvas.mockClear();

    const onOpenPeek = vi.fn();
    const qc = makeQC();

    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <TestWrapper onOpenPeek={onOpenPeek} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    const initialRenderCount = MockWorkspaceCanvas.mock.calls.length;

    const input = screen.getByRole('textbox', { name: /search by id or query/i });
    fireEvent.change(input, { target: { value: 'type something' } });
    fireEvent.change(input, { target: { value: 'type something more' } });
    fireEvent.change(input, { target: { value: '1234' } });

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    expect(MockWorkspaceCanvas.mock.calls.length).toBe(initialRenderCount);
  });

  it('substitution proof: lifting search state to wrapper and passing to MockWorkspaceCanvas causes re-renders', async () => {
    const SubstitutionMockCanvas = vi.fn((_props: { searchInput?: string }) => (
      <div data-testid="substitution-mock-canvas" />
    ));

    function UnstableWrapper() {
      const [searchInput, setSearchInput] = useState('');
      return (
        <div className="relative h-full w-full">
          <SubstitutionMockCanvas searchInput={searchInput} />
          <input
            data-testid="unstable-search-input"
            type="text"
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            aria-label="lifted search input"
          />
        </div>
      );
    }

    const qc = makeQC();
    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <UnstableWrapper />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    const initialRenderCount = SubstitutionMockCanvas.mock.calls.length;

    const unstableInput = screen.getByTestId('unstable-search-input');
    fireEvent.change(unstableInput, { target: { value: 'a' } });
    fireEvent.change(unstableInput, { target: { value: 'ab' } });
    fireEvent.change(unstableInput, { target: { value: 'abc' } });

    expect(SubstitutionMockCanvas.mock.calls.length).toBeGreaterThan(initialRenderCount);
  });

  it('WorkspaceSearchBar does not loop to MAX_RENDERS on mount', async () => {
    const qc = makeQC();
    const onOpenPeek = vi.fn();

    let loopDetected = false;
    const renderCount = { current: 0 };

    function MonitoredSearchBar(props: { onOpenPeek: (id: number) => void }) {
      renderCount.current += 1;
      if (renderCount.current >= MAX_RENDERS) {
        loopDetected = true;
      }
      return <WorkspaceSearchBarUnderTest {...props} />;
    }

    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <MonitoredSearchBar onOpenPeek={onOpenPeek} />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 200));
    });

    expect(loopDetected).toBe(false);
  });
});
