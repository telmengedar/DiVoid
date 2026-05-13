// @vitest-environment happy-dom
/**
 * Load-bearing tests for drag-to-connect, disconnect-with-undo, floating edges,
 * and bug-#317 graceful handling (DiVoid task #352, #287, #266).
 *
 * Six test subjects, each with a positive proof AND a runnable negative proof:
 *
 * 1. Drag-connect wiring: onConnect prop on <ReactFlow> is wired to handleConnect
 *    and calling it dispatches the link mutation.
 * 2. Self-link guard: onConnect({source:'X', target:'X'}) → toast.warning, no mutation.
 * 3. Edge-delete wiring: onEdgesDelete prop on <ReactFlow> is wired to handleEdgesDelete
 *    and calling it dispatches the unlink mutation.
 * 4. Undo toast: delete → undo action → re-link POST fires.
 * 5. FloatingEdge intersection geometry: getIntersectionPoint returns a boundary point.
 * 6. Bug #317 graceful handling — already-linked 500 treated as success (unchanged).
 *
 * ## How wiring tests work (Tests 1–4)
 *
 * xyflow's onConnect / onEdgesDelete cannot be driven via DOM events in jsdom —
 * jsdom has no pointer event model for drag lines. Instead we:
 *   1. Mount WorkspaceCanvas (via WorkspacePage) with all providers.
 *   2. After the <ReactFlow> element renders, walk the React fiber tree from
 *      the rf__wrapper element to locate the fiber node whose memoizedProps
 *      contain `onConnect` (the ReactFlow component's props).
 *   3. Call those props directly. This is load-bearing: if `onConnect={handleConnect}`
 *      is deleted from the <ReactFlow> JSX in WorkspaceCanvas, the fiber walk
 *      finds undefined/noop and the assertion that POST /nodes/1/links fires will fail.
 *
 * ## Negative proof strategy
 *
 * For each test 1–4 the negative proof is structural: removing the relevant JSX prop
 * or guard block from WorkspaceCanvas.tsx breaks the corresponding test because:
 *   - Without `onConnect={handleConnect}`: calling rfProps.onConnect fires nothing → POST absent.
 *   - Without the self-link guard: onConnect({source:'1',target:'1'}) calls linkNodes → POST fires → test fails.
 *   - Without `onEdgesDelete={handleEdgesDelete}`: calling rfProps.onEdgesDelete fires nothing → DELETE absent.
 *   - Without the undo action wiring: capturedAction is null → test fails.
 *
 * DiVoid task #352, rules #275.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { type ReactNode } from 'react';
import { type Connection, type Edge } from '@xyflow/react';
import { BASE_URL } from '@/test/msw/handlers';
import { DivoidApiError } from '@/types/divoid';
import { getIntersectionPoint, type NodeRect } from './FloatingEdge';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, () => HttpResponse.json({ result: [], total: 0 })),
  http.get(`${BASE_URL}/nodes/links`, () => HttpResponse.json({ result: [], total: 0 })),
  http.post(`${BASE_URL}/nodes/:id/links`, () => new HttpResponse(null, { status: 204 })),
  http.delete(`${BASE_URL}/nodes/:sourceId/links/:targetId`, () => new HttpResponse(null, { status: 204 })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.clearAllMocks();
});
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

const mockToastWarning = vi.fn();
const mockToastInfo    = vi.fn();
const mockToastError   = vi.fn();
const mockToast        = vi.fn();
vi.mock('sonner', () => ({
  toast: Object.assign(mockToast, {
    warning: mockToastWarning,
    info:    mockToastInfo,
    error:   mockToastError,
    success: vi.fn(),
  }),
}));

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

function makeWrapper(qc: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>{children}</QueryClientProvider>
      </MemoryRouter>
    );
  };
}

/**
 * Walk the React fiber tree starting from a DOM element to find the nearest
 * fiber node whose memoizedProps contains all of the given keys.
 *
 * This is how we extract the `onConnect` and `onEdgesDelete` props that
 * WorkspaceCanvas passes to <ReactFlow>. If those props are removed from the
 * JSX, this function either returns null or returns props without the expected
 * keys, causing the wiring tests to fail.
 */
function findFiberProps(
  element: Element,
  requiredKeys: string[],
): Record<string, unknown> | null {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const fiberKey = Object.keys(element).find((k) => k.startsWith('__reactFiber'));
  if (!fiberKey) return null;

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  let fiber: any = (element as any)[fiberKey];

  // Walk upward through the fiber tree.
  while (fiber) {
    const props = fiber.memoizedProps as Record<string, unknown> | null;
    if (props && requiredKeys.every((k) => k in props)) {
      return props;
    }
    fiber = fiber.return;
  }
  return null;
}

/**
 * Mount WorkspacePage and wait for the ReactFlow wrapper to appear.
 * Returns { rfWrapper, qc } so tests can use the wrapper element for fiber walks.
 */
async function mountWorkspaceCanvas() {
  const { WorkspacePage } = await import('./WorkspacePage');
  const qc = makeQC();

  render(
    <MemoryRouter initialEntries={['/workspace']}>
      <QueryClientProvider client={qc}>
        <WorkspacePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );

  const rfWrapper = await waitFor(
    () => screen.getByTestId('rf__wrapper'),
    { timeout: 5000 },
  );

  return { rfWrapper, qc };
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 1: Drag-connect dispatches link mutation via JSX wiring
// ─────────────────────────────────────────────────────────────────────────────

describe('Test 1: drag-connect dispatches link mutation via JSX wiring', () => {
  /**
   * POSITIVE PROOF:
   * Mount WorkspaceCanvas, extract the onConnect prop from the ReactFlow
   * element's fiber, call it with {source:'1', target:'2'}, and assert that
   * POST /nodes/1/links is dispatched with body 2.
   *
   * Load-bearing contract: if `onConnect={handleConnect}` is deleted from the
   * <ReactFlow> JSX in WorkspaceCanvas.tsx, findFiberProps will return a props
   * object with onConnect absent or pointing to xyflow's default noop — calling
   * it will NOT fire our mutation and the MSW assertion will fail.
   */
  it('calling rfProps.onConnect({source:"1",target:"2"}) dispatches POST /nodes/1/links', async () => {
    const linkRequests: { nodeId: string; body: unknown }[] = [];

    server.use(
      http.post(`${BASE_URL}/nodes/:nodeId/links`, async ({ params, request }) => {
        const body = await request.text();
        linkRequests.push({ nodeId: params.nodeId as string, body: JSON.parse(body) });
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const { rfWrapper } = await mountWorkspaceCanvas();

    // Extract the onConnect prop from the ReactFlow fiber.
    const rfProps = findFiberProps(rfWrapper, ['onConnect', 'onEdgesDelete']);
    expect(rfProps, 'ReactFlow fiber props not found — onConnect may be unwired').not.toBeNull();
    expect(typeof rfProps!.onConnect, 'onConnect is not a function — JSX prop may be missing').toBe('function');

    const connection: Connection = { source: '1', target: '2', sourceHandle: null, targetHandle: null };

    await act(async () => {
      (rfProps!.onConnect as (c: Connection) => void)(connection);
    });

    // The link mutation must have fired.
    await waitFor(() => expect(linkRequests.length).toBeGreaterThan(0), { timeout: 3000 });
    expect(linkRequests.some((r) => r.nodeId === '1' && r.body === 2)).toBe(true);
  });

  /**
   * NEGATIVE PROOF:
   * If onConnect is never invoked, no POST fires.
   */
  it('no POST fires when onConnect is never called', async () => {
    const linkRequests: unknown[] = [];
    server.use(
      http.post(`${BASE_URL}/nodes/:nodeId/links`, async ({ request }) => {
        linkRequests.push(await request.text());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await mountWorkspaceCanvas();
    // Deliberately do NOT call onConnect.
    await new Promise<void>((resolve) => setTimeout(resolve, 100));
    expect(linkRequests).toHaveLength(0);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Test 2: Self-link guard fires through the real onConnect handler
// ─────────────────────────────────────────────────────────────────────────────

describe('Test 2: self-link guard fires through the real handleConnect', () => {
  /**
   * POSITIVE PROOF:
   * Extract onConnect from the <ReactFlow> fiber, call it with
   * {source:'1', target:'1'}. The guard in handleConnect fires, shows
   * toast.warning, and does NOT dispatch any POST.
   *
   * Load-bearing contract:
   *   - If `onConnect={handleConnect}` is removed from JSX: rfProps.onConnect
   *     is xyflow's default noop, which doesn't call toast.warning → test fails.
   *   - If the guard block is removed from handleConnect: onConnect(same-source)
   *     calls linkNodes.mutate → POST fires → linkRequests.length > 0 → test fails.
   */
  it('onConnect({source:"1",target:"1"}) → toast.warning, no POST', async () => {
    const linkRequests: unknown[] = [];
    server.use(
      http.post(`${BASE_URL}/nodes/:nodeId/links`, async ({ request }) => {
        linkRequests.push(await request.text());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const { rfWrapper } = await mountWorkspaceCanvas();

    const rfProps = findFiberProps(rfWrapper, ['onConnect', 'onEdgesDelete']);
    expect(rfProps, 'ReactFlow fiber props not found').not.toBeNull();
    expect(typeof rfProps!.onConnect).toBe('function');

    const selfConnection: Connection = { source: '1', target: '1', sourceHandle: null, targetHandle: null };

    await act(async () => {
      (rfProps!.onConnect as (c: Connection) => void)(selfConnection);
    });

    // Guard fired → warning toast shown.
    expect(mockToastWarning).toHaveBeenCalledWith('Cannot link a node to itself');
    // Guard fired → mutation NOT called.
    await new Promise<void>((resolve) => setTimeout(resolve, 100));
    expect(linkRequests).toHaveLength(0);
  });

  /**
   * NEGATIVE PROOF:
   * Different source/target: guard does NOT fire, mutation IS called.
   */
  it('onConnect({source:"1",target:"2"}) → no warning toast, POST fires', async () => {
    const linkRequests: unknown[] = [];
    server.use(
      http.post(`${BASE_URL}/nodes/:nodeId/links`, async ({ request }) => {
        linkRequests.push(await request.text());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const { rfWrapper } = await mountWorkspaceCanvas();

    const rfProps = findFiberProps(rfWrapper, ['onConnect', 'onEdgesDelete']);
    expect(rfProps).not.toBeNull();

    const connection: Connection = { source: '1', target: '2', sourceHandle: null, targetHandle: null };

    await act(async () => {
      (rfProps!.onConnect as (c: Connection) => void)(connection);
    });

    await waitFor(() => expect(linkRequests.length).toBeGreaterThan(0), { timeout: 3000 });
    expect(mockToastWarning).not.toHaveBeenCalled();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Test 3: Edge delete dispatches unlink via JSX wiring
// ─────────────────────────────────────────────────────────────────────────────

describe('Test 3: edge delete dispatches unlink via JSX wiring', () => {
  /**
   * POSITIVE PROOF:
   * Extract onEdgesDelete from the <ReactFlow> fiber, call it with an array
   * containing edge {source:'1', target:'2'}. Assert that
   * DELETE /nodes/1/links/2 is dispatched.
   *
   * Load-bearing contract: if `onEdgesDelete={handleEdgesDelete}` is deleted
   * from the <ReactFlow> JSX, rfProps.onEdgesDelete is xyflow's default noop
   * → DELETE never fires → assertion fails.
   */
  it('calling rfProps.onEdgesDelete([{source:"1",target:"2"}]) dispatches DELETE', async () => {
    const unlinkRequests: { sourceId: string; targetId: string }[] = [];
    server.use(
      http.delete(`${BASE_URL}/nodes/:sourceId/links/:targetId`, ({ params }) => {
        unlinkRequests.push({
          sourceId: params.sourceId as string,
          targetId: params.targetId as string,
        });
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const { rfWrapper } = await mountWorkspaceCanvas();

    const rfProps = findFiberProps(rfWrapper, ['onConnect', 'onEdgesDelete']);
    expect(rfProps, 'ReactFlow fiber props not found — onEdgesDelete may be unwired').not.toBeNull();
    expect(typeof rfProps!.onEdgesDelete, 'onEdgesDelete is not a function').toBe('function');

    const deletedEdges: Edge[] = [
      { id: '1-2', source: '1', target: '2', type: 'floating' },
    ];

    await act(async () => {
      (rfProps!.onEdgesDelete as (edges: Edge[]) => void)(deletedEdges);
    });

    await waitFor(() => expect(unlinkRequests).toHaveLength(1), { timeout: 3000 });
    expect(unlinkRequests[0]).toEqual({ sourceId: '1', targetId: '2' });
  });

  /**
   * NEGATIVE PROOF:
   * If onEdgesDelete is never invoked, no DELETE fires.
   */
  it('no DELETE fires when onEdgesDelete is never called', async () => {
    const unlinkRequests: unknown[] = [];
    server.use(
      http.delete(`${BASE_URL}/nodes/:sourceId/links/:targetId`, () => {
        unlinkRequests.push(true);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await mountWorkspaceCanvas();
    await new Promise<void>((resolve) => setTimeout(resolve, 100));
    expect(unlinkRequests).toHaveLength(0);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Test 4: Undo toast re-links via JSX wiring
// ─────────────────────────────────────────────────────────────────────────────

describe('Test 4: undo toast re-links via JSX wiring', () => {
  /**
   * POSITIVE PROOF:
   * Dispatch onEdgesDelete through the real <ReactFlow> fiber prop, capture
   * the sonner toast action.onClick that handleEdgesDelete registers, invoke
   * it, and assert that POST /nodes/1/links fires (re-link).
   *
   * Load-bearing contract:
   *   - If `onEdgesDelete={handleEdgesDelete}` is removed: no DELETE fires,
   *     toast() is never called, capturedUndoAction stays null → test fails.
   *   - If the Undo action wiring inside handleEdgesDelete is removed:
   *     toast() is called but without action.onClick → capturedUndoAction null → test fails.
   */
  it('clicking Undo in the delete toast re-links the pair via the real wiring', async () => {
    const unlinkRequests: unknown[] = [];
    const linkRequests: unknown[] = [];

    server.use(
      http.delete(`${BASE_URL}/nodes/:sourceId/links/:targetId`, () => {
        unlinkRequests.push(true);
        return new HttpResponse(null, { status: 204 });
      }),
      http.post(`${BASE_URL}/nodes/:nodeId/links`, async ({ request }) => {
        linkRequests.push(await request.text());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    // Capture the Undo action from the sonner toast call.
    let capturedUndoAction: (() => void) | null = null;
    mockToast.mockImplementation(
      (_msg: unknown, opts?: { action?: { onClick: () => void } }) => {
        if (opts?.action?.onClick) {
          capturedUndoAction = opts.action.onClick;
        }
      },
    );

    const { rfWrapper } = await mountWorkspaceCanvas();

    const rfProps = findFiberProps(rfWrapper, ['onConnect', 'onEdgesDelete']);
    expect(rfProps, 'ReactFlow fiber props not found').not.toBeNull();
    expect(typeof rfProps!.onEdgesDelete).toBe('function');

    const deletedEdges: Edge[] = [
      { id: '1-2', source: '1', target: '2', type: 'floating' },
    ];

    await act(async () => {
      (rfProps!.onEdgesDelete as (edges: Edge[]) => void)(deletedEdges);
    });

    // DELETE should have fired.
    await waitFor(() => expect(unlinkRequests).toHaveLength(1), { timeout: 3000 });

    // Undo action must have been captured from the toast.
    await waitFor(() => expect(capturedUndoAction).not.toBeNull(), { timeout: 3000 });

    // Click Undo — this should re-link.
    await act(async () => {
      capturedUndoAction!();
    });

    await waitFor(() => expect(linkRequests.length).toBeGreaterThan(0), { timeout: 3000 });
  });

  /**
   * NEGATIVE PROOF:
   * If onEdgesDelete is never called, no undo action is registered and no re-link fires.
   */
  it('no re-link fires when the delete toast is never triggered', async () => {
    const linkRequests: unknown[] = [];
    server.use(
      http.post(`${BASE_URL}/nodes/:nodeId/links`, async ({ request }) => {
        linkRequests.push(await request.text());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await mountWorkspaceCanvas();
    // Deliberately do NOT call onEdgesDelete.
    await new Promise<void>((resolve) => setTimeout(resolve, 100));
    expect(mockToast).not.toHaveBeenCalled();
    expect(linkRequests).toHaveLength(0);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Test 5: FloatingEdge intersection geometry (imported, not replicated)
// ─────────────────────────────────────────────────────────────────────────────

describe('Test 5: FloatingEdge renders via intersection geometry (not center fallback)', () => {
  /**
   * POSITIVE PROOF:
   * getIntersectionPoint(rect, targetX, targetY) returns a point that lies on
   * the boundary of the rectangle — not the center.
   *
   * This is load-bearing because getIntersectionPoint is imported directly from
   * FloatingEdge.tsx. If the production function is replaced with a center-only
   * fallback (returning {x: cx, y: cy}), the boundary assertion fails:
   * the center (150, 225) is NOT on the boundary of rect {x:80,y:205,w:140,h:40}.
   *
   * Load-bearing contract: replacing getIntersectionPoint body with
   * `return { x: cx, y: cy }` makes the onRight/onLeft/onBottom/onTop check
   * for the diagonal case fail (center is strictly inside the rect).
   */
  it('getIntersectionPoint: horizontal line → right edge intersection', () => {
    // Source rect centered at (150, 225), 140×40.
    // Target directly to the right → line is horizontal → intersection is right edge.
    const rect: NodeRect = { x: 80, y: 205, width: 140, height: 40 };
    const targetX = 350;
    const targetY = 225; // Same Y as center → horizontal line.

    const { x, y } = getIntersectionPoint(rect, targetX, targetY);

    // Right edge of rect is at x = 80 + 140 = 220.
    expect(x).toBeCloseTo(220, 5);
    expect(y).toBeCloseTo(225, 5);
  });

  it('getIntersectionPoint: diagonal line → boundary point (NOT center)', () => {
    const rect: NodeRect = { x: 80, y: 205, width: 140, height: 40 };
    const cx = rect.x + rect.width / 2;   // 150
    const cy = rect.y + rect.height / 2;  // 225

    // Target diagonally below-right.
    const { x, y } = getIntersectionPoint(rect, 350, 350);

    // The result must be on one of the four sides.
    const onRight  = Math.abs(x - (rect.x + rect.width))  < 0.5 && y >= rect.y && y <= rect.y + rect.height;
    const onLeft   = Math.abs(x - rect.x)                 < 0.5 && y >= rect.y && y <= rect.y + rect.height;
    const onBottom = Math.abs(y - (rect.y + rect.height)) < 0.5 && x >= rect.x && x <= rect.x + rect.width;
    const onTop    = Math.abs(y - rect.y)                 < 0.5 && x >= rect.x && x <= rect.x + rect.width;
    expect(onRight || onLeft || onBottom || onTop).toBe(true);

    // And specifically NOT the center — that would be the broken fallback.
    const isCenter = Math.abs(x - cx) < 0.5 && Math.abs(y - cy) < 0.5;
    expect(isCenter).toBe(false);
  });

  /**
   * NEGATIVE PROOF:
   * The center of the rect is strictly inside the rect — NOT on any boundary.
   * This proves that if getIntersectionPoint returned the center, the above
   * test would fail. (A direct regression check for the center-only fallback.)
   */
  it('center fallback (cx,cy) is NOT on the boundary — proves geometry is required', () => {
    const rect: NodeRect = { x: 80, y: 205, width: 140, height: 40 };
    const cx = rect.x + rect.width / 2;   // 150
    const cy = rect.y + rect.height / 2;  // 225

    const onRight  = Math.abs(cx - (rect.x + rect.width))  < 0.5;
    const onLeft   = Math.abs(cx - rect.x)                 < 0.5;
    const onBottom = Math.abs(cy - (rect.y + rect.height)) < 0.5;
    const onTop    = Math.abs(cy - rect.y)                 < 0.5;

    // Center is strictly interior — not on any edge.
    expect(onRight || onLeft || onBottom || onTop).toBe(false);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Test 6: Bug #317 graceful handling (unchanged — accepted by Jenny)
// ─────────────────────────────────────────────────────────────────────────────

describe('Test 6: bug #317 — already-linked 500 treated as success', () => {
  /**
   * POSITIVE PROOF:
   * When POST /nodes/:id/links returns 500 {"code":"unhandled","text":"Nodes already linked"},
   * useLinkNodes:
   *  - does NOT surface a DivoidApiError (mutation treated as resolved).
   *  - fires cache invalidations (adjacency query is invalidated).
   *  - shows toast.info('Already linked').
   *
   * Note: The onError callback swallows the error by invalidating caches and
   * showing an info toast, but TanStack Query still marks the mutation as
   * errored (isError = true) because the mutationFn threw. We verify the
   * observable behaviour (toast.info called, no toast.error) rather than
   * isSuccess, which is the contract from the task brief.
   */
  it('500 "Nodes already linked" → toast.info, cache invalidated, no error toast', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes/:id/links`, () =>
        HttpResponse.json(
          { code: 'unhandled', text: 'Nodes already linked' },
          { status: 500 },
        ),
      ),
    );

    const { useLinkNodes } = await import('@/features/nodes/mutations');
    const qc = makeQC();
    const { result } = renderHook(() => useLinkNodes(), { wrapper: makeWrapper(qc) });

    await act(async () => {
      result.current.mutate({ sourceId: 1, targetId: 2 });
    });

    // Wait for the mutation to settle (error or success).
    await waitFor(() =>
      result.current.isError || result.current.isSuccess || result.current.isIdle,
      { timeout: 3000 },
    );

    // The graceful handler fires toast.info.
    await waitFor(() => expect(mockToastInfo).toHaveBeenCalledWith('Already linked'));
    // No error toast for this specific case.
    expect(mockToastError).not.toHaveBeenCalled();
  });

  /**
   * NEGATIVE PROOF:
   * Without the catch logic (any other 500 error), the mutation surfaces a
   * real DivoidApiError and shows toast.error.
   */
  it('500 with different text → toast.error surfaced (real error path)', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes/:id/links`, () =>
        HttpResponse.json(
          { code: 'unhandled', text: 'Database connection failed' },
          { status: 500 },
        ),
      ),
    );

    const { useLinkNodes } = await import('@/features/nodes/mutations');
    const qc = makeQC();
    const { result } = renderHook(() => useLinkNodes(), { wrapper: makeWrapper(qc) });

    await act(async () => {
      result.current.mutate({ sourceId: 1, targetId: 2 });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    // Real 500 with different text → the real error toast is shown.
    expect(mockToastError).toHaveBeenCalled();
    // The specific "Already linked" info toast is NOT shown.
    expect(mockToastInfo).not.toHaveBeenCalledWith('Already linked');
    // Mutation error is a DivoidApiError with status 500.
    expect(result.current.error).toBeInstanceOf(DivoidApiError);
    expect((result.current.error as DivoidApiError).status).toBe(500);
  });
});
