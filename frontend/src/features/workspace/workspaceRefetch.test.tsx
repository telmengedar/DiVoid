/**
 * End-to-end render-count test for the viewport-refetch perf fix (#1261).
 *
 * Pins the invariant: NodeCardRenderer does NOT re-render when a viewport
 * refetch returns identical node data (the "scroll-blink" regression).
 *
 * Pattern: vi.hoisted() + React.memo counter on NodeCardRenderer stub,
 * same as WorkspacePeekModal.test.tsx T6, but applied to the refetch surface.
 * The real WorkspaceCanvas is rendered (not mocked) so the reconcileNodes
 * path is exercised end-to-end.
 *
 * Mental-deletion check: revert the reconciliation in the setNodes sync effect
 * (replace `reconcileNodes(prev, xyNodes, draggingIds)` with the old
 * wholesale-replace logic that returns a new array every time) → React sees
 * new xyflow-node objects → xyflow updates its store → NodeCardRenderer
 * receives new props → propsAreEqual is called but returns true (same values);
 * however xyflow's own internal processing still causes a re-render pass →
 * render count climbs → assertion `toBe(countAfterMount)` fails.
 *
 * DiVoid task: #1261 / DiVoid rule: #275
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll, beforeEach } from 'vitest';
import { render, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';

// ─── Hoisted render-count ref ─────────────────────────────────────────────────
// vi.hoisted runs before vi.mock factory calls, so the ref is available inside
// the mock factory that wraps NodeCardRenderer.

const { nodeCardRenderCountRef } = vi.hoisted(() => ({
  nodeCardRenderCountRef: { current: 0 },
}));

// ─── MSW server ───────────────────────────────────────────────────────────────

// Two identical viewport responses — simulates a refetch with no data change.
const SAMPLE_VIEWPORT_NODES = [
  {
    id: 100, type: 'task', name: 'Node Alpha', status: 'open',
    x: 10, y: 20, contentType: null, links: [101],
  },
  {
    id: 101, type: 'documentation', name: 'Node Beta', status: null,
    x: 100, y: 200, contentType: null, links: [100],
  },
];

let viewportRequestCount = 0;

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  // Viewport query — always returns identical data.
  http.get(`${BASE_URL}/nodes`, () => {
    viewportRequestCount += 1;
    return HttpResponse.json({
      result: SAMPLE_VIEWPORT_NODES,
      total: SAMPLE_VIEWPORT_NODES.length,
    });
  }),
  http.get(`${BASE_URL}/types`, () =>
    HttpResponse.json({
      result: [{ id: 1, name: 'task' }, { id: 2, name: 'documentation' }],
      total: 2,
    }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  viewportRequestCount = 0;
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

vi.mock('sonner', () => ({
  toast: { error: vi.fn(), success: vi.fn(), info: vi.fn(), warning: vi.fn() },
  Toaster: () => null,
}));

// Mock xyflow — the real @xyflow/react requires a DOM environment that
// jsdom does not fully provide (ResizeObserver, SVG layout). We stub
// ReactFlow to render its children by node id so the test can still assert
// on NodeCardRenderer render counts without a full canvas environment.
vi.mock('@xyflow/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@xyflow/react')>();
  const { useState } = await import('react');
  return {
    ...actual,
    ReactFlow: ({ nodes, children }: { nodes: { id: string }[]; children?: React.ReactNode }) => (
      <div data-testid="mock-reactflow">
        {nodes.map((n) => (
          <div key={n.id} data-node-id={n.id} />
        ))}
        {children}
      </div>
    ),
    Background: () => null,
    Controls: () => null,
    MiniMap: () => null,
    useNodesState: (init: unknown[]) => {
      const [nodes, setNodes] = useState(init);
      return [nodes, setNodes, () => {}];
    },
    useEdgesState: (init: unknown[]) => {
      const [edges, setEdges] = useState(init);
      return [edges, setEdges, () => {}];
    },
    addEdge: actual.addEdge,
    ConnectionMode: actual.ConnectionMode,
  };
});

/**
 * NodeCardRenderer stub — wrapped in React.memo so re-renders only happen
 * when propsAreEqual would return false (same contract as the real component).
 * The render counter lets us assert that no extra renders occurred on a
 * no-data-change refetch.
 */
vi.mock('./NodeCardRenderer', async () => {
  const { memo } = await import('react');
  return {
    NodeCardRenderer: memo(
      ({ data }: { data: { id: number; name: string; type: string; status: string | null } }) => {
        nodeCardRenderCountRef.current += 1;
        return <div data-testid={`node-card-${data.id}`}>{data.name}</div>;
      },
      (prev, next) =>
        prev.data.id === next.data.id &&
        prev.data.name === next.data.name &&
        prev.data.type === next.data.type &&
        prev.data.status === next.data.status,
    ),
  };
});

vi.mock('./WorkspaceNodePeekModal', () => ({
  WorkspaceNodePeekModal: () => null,
}));

vi.mock('@/features/nodes/CreateNodeDialog', () => ({
  CreateNodeDialog: () => null,
}));

vi.mock('./WorkspaceFilterPopover', () => ({
  WorkspaceFilterPopover: () => null,
}));

// ─── Helpers ──────────────────────────────────────────────────────────────────

beforeEach(() => {
  nodeCardRenderCountRef.current = 0;
});

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0 },
      mutations: { retry: false },
    },
  });
}

let WorkspaceCanvas: typeof import('./WorkspaceCanvas').WorkspaceCanvas;

beforeAll(async () => {
  const mod = await import('./WorkspaceCanvas');
  WorkspaceCanvas = mod.WorkspaceCanvas;
});

function renderCanvas() {
  const qc = makeQC();
  const onPeek = vi.fn();
  return {
    qc,
    onPeek,
    ...render(
      <MemoryRouter initialEntries={['/workspace']}>
        <QueryClientProvider client={qc}>
          <WorkspaceCanvas onPeek={onPeek} />
        </QueryClientProvider>
      </MemoryRouter>,
    ),
  };
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('Viewport refetch render-stability (DiVoid #1261)', () => {
  /**
   * R5-E2E: NodeCardRenderer does NOT re-render on identical-data viewport
   * refetches after reconcileNodes is wired into the setNodes sync effect.
   *
   * Flow:
   * 1. Mount WorkspaceCanvas — viewport query fires, returns SAMPLE_VIEWPORT_NODES.
   * 2. NodeCardRenderer mounts for each node (render count = N).
   * 3. Force a refetch with identical data via invalidateQueries.
   * 4. reconcileNodes returns `prev` (exact reference) → React bails out of
   *    setNodes update → NodeCardRenderer receives no new props → memo never
   *    even evaluates propsAreEqual → render count stays at N.
   *
   * Mental-deletion: change `reconcileNodes(prev, xyNodes, draggingIds)` in
   * WorkspaceCanvas back to the old array-rebuild path → incoming is always a
   * new array → xyflow updates its store → NodeCardRenderer renders again →
   * count > N → this test fails.
   */
  it('R5-E2E: NodeCardRenderer does not re-render on identical-data viewport refetch', async () => {
    const { qc } = renderCanvas();

    // Wait for initial data to load and NodeCardRenderer to mount.
    await waitFor(() => expect(viewportRequestCount).toBeGreaterThanOrEqual(1));

    // Allow all state updates to settle.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 50));
    });

    const countAfterMount = nodeCardRenderCountRef.current;

    // Trigger a refetch that returns identical data.
    await act(async () => {
      await qc.invalidateQueries({ queryKey: ['nodes', 'viewport'] });
      await new Promise((r) => setTimeout(r, 50));
    });

    await waitFor(() => expect(viewportRequestCount).toBeGreaterThanOrEqual(2));

    // Allow reconciliation + React to settle.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 50));
    });

    // NodeCardRenderer must NOT have re-rendered — reconcileNodes returned prev.
    expect(nodeCardRenderCountRef.current).toBe(countAfterMount);
  });
});
