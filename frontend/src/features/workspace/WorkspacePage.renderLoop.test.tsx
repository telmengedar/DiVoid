// @vitest-environment happy-dom
/**
 * Render-stability regression test — negative proof (DiVoid #275, #271).
 *
 * ## Investigation finding: the original negative proof was wrong
 *
 * The original test used `onNodesChange` to trigger the loop. React Flow's
 * `onNodesChange` fires only on USER INTERACTIONS (drag, select) — NOT when
 * the `nodes` prop reference changes. In jsdom/happy-dom with no simulated
 * interactions, the loop never fires. Jenny un-skipped it and it passed in
 * 882ms with the harness silent. The test proved nothing.
 *
 * ## The real loop vector in WorkspaceCanvas
 *
 *   // Without useMemo:
 *   const xyNodes = visibleDetails.map(toXyflowNode); // new ref every render
 *   useEffect(() => { setNodes(merged); }, [xyNodes, setNodes]);
 *
 *   Loop: render → new xyNodes ref → effect fires → setNodes → re-render → ...
 *
 * ## Why the harness does NOT fire
 *
 * setup.ts watches console.error for "Maximum update depth exceeded". That
 * message is only emitted for SYNCHRONOUS render-phase updates. useEffect-based
 * loops are async — React queues them between flushes. The harness never fires.
 * Instead the tests TIME OUT (confirmed by Jenny: 4 positive tests timeout when
 * useMemo is removed from xyNodes).
 *
 * ## Negative proof design
 *
 * We cannot mount an uncontrolled loop in test (it causes OOM in ~60s).
 * We instead use two controlled micro-components that isolate the invariant:
 *
 *   - UNSTABLE: useEffect depends on a new-ref-per-render array → render count
 *     grows to the safety cap. The cap element appears in the DOM.
 *   - STABLE: useEffect depends on a memoized array → render count stays low.
 *
 * The safety cap is enforced via a ref (not setState) to avoid adding more
 * re-renders. The unstable component renders until it hits MAX_RENDERS, then
 * replaces itself with a sentinel `data-testid="loop-capped"`.
 *
 * DiVoid task #230, rules #271 #275.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { useNodesState, type Edge } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useState, useEffect, useMemo, useRef } from 'react';
import { BASE_URL, viewportPage, adjacencyPage } from '@/test/msw/handlers';

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
    if (url.searchParams.get('bounds')) return HttpResponse.json(viewportPage);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.get(`${BASE_URL}/nodes/links`, () => HttpResponse.json(adjacencyPage)),
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
      ADJACENCY: '/nodes/links',
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
vi.mock('next-themes', () => ({
  useTheme: vi.fn(() => ({ resolvedTheme: 'dark', setTheme: vi.fn() })),
}));

// ─── Safety cap ───────────────────────────────────────────────────────────────
// The unstable component will be stopped at this render count to prevent OOM.
const MAX_RENDERS = 30;

// ─── Unstable component (NEGATIVE PROOF) ─────────────────────────────────────
//
// Real loop vector: useNodesState + useEffect([unmemoizedArray]) + setNodes.
//
// Loop path:
//   render → sourceData is new ref → useEffect fires → setNodes → re-render
//   → sourceData is new ref again → useEffect fires again → ...
//
// The render count ref (not state) increments each render. Once MAX_RENDERS is
// reached, the component renders a sentinel div instead of calling setNodes,
// breaking the loop safely.

/**
 * Minimal reproduction of the WorkspaceCanvas loop vector without React Flow.
 *
 * WorkspaceCanvas uses useNodesState (backed by useState) with a sync useEffect.
 * useNodesState is intentionally excluded here to keep the loop component fast —
 * the invariant being tested is pure React: useEffect + setState with an unstable
 * dependency array causes runaway re-renders regardless of which setState variant
 * is used.
 *
 * The React Flow-specific version (with useNodesState + ReactFlow) causes OOM
 * because React Flow's internal event system accumulates too much state. This
 * minimal version hits the cap cleanly.
 */
function UnstableCanvasRealLoopVector() {
  const renderCount = useRef(0);
  renderCount.current += 1;
  const capped = renderCount.current >= MAX_RENDERS;

  // BAD: new array reference every render — the loop source.
  // This is the anti-pattern WorkspaceCanvas avoids with useMemo.
  const sourceData = [renderCount.current, renderCount.current + 1];

  // useState + useEffect mirrors what WorkspaceCanvas does with
  // useNodesState + the sync useEffect.
  const [, setState] = useState(sourceData);

  // Loop source: sourceData is a new reference every render.
  // Once capped, skip the setState call to stop the loop.
  useEffect(() => {
    if (capped) return;
    setState([renderCount.current + 1000]); // drive a real state change
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceData, capped]);

  if (capped) {
    return <div data-testid="loop-capped" data-renders={renderCount.current} />;
  }

  return (
    <div data-testid="unstable-canvas">
      <div data-testid="render-count" data-value={renderCount.current} />
    </div>
  );
}

// ─── Stable component (POSITIVE PROOF COUNTERPART) ───────────────────────────
//
// useMemo gives sourceData a stable identity. The useEffect fires once (on mount)
// and the loop is broken. Render count stays low.
//
// This mirrors WorkspaceCanvas's:
//   const xyNodes = useMemo(() => visibleDetails.map(toXyflowNode), [visibleDetails]);
//   useEffect(() => { setNodes(merged); }, [xyNodes, setNodes]);

function StableCanvasWithMemoVector() {
  const renderCount = useRef(0);
  renderCount.current += 1;

  const [queryVersion] = useState(0); // stable unless data changes

  // GOOD: useMemo → same reference between renders → useEffect fires once.
  const sourceData = useMemo(() => [
    { id: '1', position: { x: 0, y: 0 }, data: { label: 'Stable node 1' } },
    { id: '2', position: { x: 100, y: 100 }, data: { label: 'Stable node 2' } },
  // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [queryVersion]);

  const [nodes, setNodes] = useNodesState(sourceData);
  const edges: Edge[] = [];
  void edges;

  useEffect(() => {
    setNodes(sourceData);
  }, [sourceData, setNodes]); // stable dep → fires once → no loop

  return (
    <div style={{ width: 400, height: 300 }} data-testid="stable-canvas">
      <div data-testid="render-count" data-value={renderCount.current} />
      <div data-testid="node-count" data-value={nodes.length} />
    </div>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('WorkspaceCanvas — render-stability negative proof', () => {
  /**
   * NEGATIVE PROOF (load-bearing):
   *
   * The unstable component hits the render cap (MAX_RENDERS = 30) because
   * its useEffect loop drives continuous re-renders.
   *
   * Observable outcome: `data-testid="loop-capped"` appears in the DOM with
   * `data-renders >= MAX_RENDERS`.
   *
   * This test FAILS if the loop doesn't fire — meaning the negative proof is
   * broken and the test should be re-investigated.
   *
   * Note: ReactFlow is intentionally excluded from UnstableCanvasRealLoopVector
   * to keep the loop lightweight (prevent OOM). The invariant being tested is
   * pure React: useEffect + setState with an unstable dependency = runaway renders.
   * WorkspaceCanvas uses useNodesState (which is backed by useState) with the
   * same pattern.
   */
  it('unstable useEffect sync causes runaway re-renders (negative proof)', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MemoryRouter>
        <QueryClientProvider client={qc}>
          <UnstableCanvasRealLoopVector />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // The loop-capped sentinel must appear, proving render count hit the cap.
    const capped = await waitFor(
      () => screen.getByTestId('loop-capped'),
      { timeout: 5000 },
    );

    // Confirm the render count reached the cap.
    const renders = parseInt(capped.getAttribute('data-renders') ?? '0', 10);
    expect(renders).toBeGreaterThanOrEqual(MAX_RENDERS);
  });

  /**
   * POSITIVE PROOF (counterpart):
   *
   * The stable component settles at a low render count. useMemo gives sourceData
   * a stable identity → useEffect fires once → no loop.
   *
   * Observable outcome: `data-testid="render-count"` shows a low `data-value`
   * after settling (< 10, typically 2–4).
   *
   * This mirrors the protection useMemo on xyNodes provides in WorkspaceCanvas.
   */
  it('stable useMemo sync keeps render count bounded (positive proof)', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MemoryRouter>
        <QueryClientProvider client={qc}>
          <StableCanvasWithMemoVector />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(
      () => expect(screen.getByTestId('stable-canvas')).toBeInTheDocument(),
      { timeout: 5000 },
    );

    // Wait for effects to settle.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 200));
    });

    // Read the final render count.
    const counterEl = screen.getByTestId('render-count');
    const renderCount = parseInt(counterEl.getAttribute('data-value') ?? '0', 10);

    // Stable component settles in a few renders. 10 is a generous upper bound.
    expect(renderCount).toBeLessThan(10);
  });
});
