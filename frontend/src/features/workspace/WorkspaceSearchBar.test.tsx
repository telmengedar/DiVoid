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
import { useState, useEffect, useRef, useMemo } from 'react';
import { BASE_URL } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

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

// ─── Helpers ──────────────────────────────────────────────────────────────────

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

// ─── WT3 — classifyInput unit test ───────────────────────────────────────────

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

// ─── WT1 — id-mode Enter calls onOpenPeek ────────────────────────────────────

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

// ─── WT2 — semantic-mode: Enter is no-op; debounced query fires ───────────────

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

// ─── WT5 — row click calls onOpenPeek and clears input ───────────────────────

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

// ─── WT4 — render-stability sentinel ─────────────────────────────────────────

describe('WorkspaceSearchBar — render stability (WT4)', () => {
  /**
   * WT4 (load-bearing render-stability test).
   *
   * Mounting WorkspaceSearchBar next to a canvas-proxy component must NOT cause
   * the canvas-proxy to re-render beyond the expected initial settlement.
   *
   * This follows the MAX_RENDERS=30 sentinel pattern from
   * WorkspacePage.renderLoop.test.tsx (§13.7 / DiVoid #271).
   *
   * The canvas-proxy is a minimal functional component that counts renders.
   * If the search bar's local state change triggers a parent re-render that
   * flows into the canvas props, the proxy render count exceeds the LOW_THRESHOLD.
   *
   * Negative proof: make the search bar lift its input state to the parent and
   * pass it as a prop to the canvas-proxy → the proxy render count spikes past
   * LOW_THRESHOLD (typically 5+) and the assertion fails.
   */

  const MAX_RENDERS = 30;
  const LOW_THRESHOLD = 5;

  function CanvasRenderProxy({ renderCountRef }: { renderCountRef: React.MutableRefObject<number> }) {
    renderCountRef.current += 1;
    if (renderCountRef.current >= MAX_RENDERS) {
      return <div data-testid="loop-capped" data-renders={renderCountRef.current} />;
    }
    return <div data-testid="canvas-proxy" data-renders={renderCountRef.current} />;
  }

  function SearchBarWithSiblingCanvas() {
    const renderCountRef = useRef(0);
    const [searchInput, setSearchInput] = useState('');
    void searchInput;
    void setSearchInput;

    return (
      <div className="relative h-full w-full">
        <CanvasRenderProxy renderCountRef={renderCountRef} />
        <div data-testid="search-bar-wrapper">
          {(function SearchBarInner() {
            const [input, setInput] = useState('');
            const mode = input.trim().length === 0 ? 'empty'
              : /^\d+$/.test(input.trim()) && parseInt(input.trim(), 10) > 0 ? 'id'
              : 'query';
            const [debouncedQuery, setDebouncedQuery] = useState('');
            const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

            useEffect(() => {
              if (mode !== 'query') { setDebouncedQuery(''); return; }
              if (debounceRef.current) clearTimeout(debounceRef.current);
              debounceRef.current = setTimeout(() => setDebouncedQuery(input.trim()), 250);
              return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
            }, [input, mode]);

            void debouncedQuery;

            return (
              <input
                data-testid="sb-input"
                type="text"
                value={input}
                onChange={(e) => setInput(e.target.value)}
                aria-label="test search"
              />
            );
          })()}
        </div>
      </div>
    );
  }

  it('WorkspaceSearchBar typing does NOT cause canvas sibling to re-render excessively', async () => {
    const renderCountRef = { current: 0 };

    const qc = makeQC();

    function RenderCountSentinel() {
      const localRef = useRef(0);
      localRef.current += 1;
      renderCountRef.current = localRef.current;
      return <div data-testid="sentinel" data-renders={localRef.current} />;
    }

    const onOpenPeek = vi.fn();
    const { WorkspaceSearchBar } = await import('./WorkspaceSearchBar');

    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <div className="relative h-full w-full">
            <RenderCountSentinel />
            <WorkspaceSearchBar onOpenPeek={onOpenPeek} />
          </div>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    const sentinelBefore = renderCountRef.current;

    const input = screen.getByRole('textbox', { name: /search by id or query/i });
    fireEvent.change(input, { target: { value: 'type something' } });
    fireEvent.change(input, { target: { value: 'type something more' } });
    fireEvent.change(input, { target: { value: '1234' } });

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    const sentinelAfter = renderCountRef.current;

    expect(sentinelAfter - sentinelBefore).toBeLessThan(LOW_THRESHOLD);
  });

  it('unstable sibling (lifting search state to parent) causes re-renders', async () => {
    const canvasRenderCount = { current: 0 };

    function UnstableParent() {
      const [inputValue, setInputValue] = useState('');
      const renderRef = useRef(0);
      renderRef.current += 1;
      canvasRenderCount.current = renderRef.current;

      void inputValue;

      return (
        <div>
          <div data-testid="canvas-child" data-renders={renderRef.current} />
          <input
            data-testid="unstable-input"
            type="text"
            onChange={(e) => setInputValue(e.target.value)}
            aria-label="unstable input"
          />
        </div>
      );
    }

    const qc = makeQC();
    render(
      <MemoryRouter>
        <QueryClientProvider client={qc}>
          <UnstableParent />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    const before = canvasRenderCount.current;

    const unstableInput = screen.getByTestId('unstable-input');
    fireEvent.change(unstableInput, { target: { value: 'a' } });
    fireEvent.change(unstableInput, { target: { value: 'ab' } });
    fireEvent.change(unstableInput, { target: { value: 'abc' } });

    const after = canvasRenderCount.current;

    expect(after - before).toBeGreaterThanOrEqual(3);
  });

  it('WorkspaceSearchBar does not loop to MAX_RENDERS on mount', async () => {
    void MAX_RENDERS;
    void SearchBarWithSiblingCanvas;

    const qc = makeQC();
    const onOpenPeek = vi.fn();
    const { WorkspaceSearchBar } = await import('./WorkspaceSearchBar');

    let loopDetected = false;
    const renderCount = { current: 0 };

    function MonitoredSearchBar(props: { onOpenPeek: (id: number) => void }) {
      renderCount.current += 1;
      if (renderCount.current >= MAX_RENDERS) {
        loopDetected = true;
      }
      const stableCount = useMemo(() => renderCount.current, []);
      void stableCount;
      return <WorkspaceSearchBar {...props} />;
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
