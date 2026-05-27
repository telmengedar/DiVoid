/**
 * End-to-end reference-stability test for the viewport-refetch perf fix (#1261).
 *
 * Pins the invariant: the `nodes` and `edges` props passed to the mocked
 * <ReactFlow> are reference-equal across two refetches that return identical
 * data. This directly exercises the reconcileNodes / reconcileEdges bail-out
 * path inside the setNodes / setEdges functional updaters in WorkspaceCanvas.
 *
 * Why reference-equality on ReactFlow props (not NodeCardRenderer render count):
 * The xyflow mock ignores `nodeTypes`, so NodeCardRenderer is never instantiated
 * by it — the render counter stays at 0 regardless of what reconcileNodes does.
 * That made the original render-count assertion tautological (DiVoid #1262 /
 * Jenny W1 on PR #127). Capturing the prop reference is the correct observable.
 *
 * Mental-deletion check: revert reconcileNodes body to `return incoming.slice()`
 * → each refetch produces a fresh array → reference inequality → this test fails.
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

// ─── Hoisted prop-capture refs ────────────────────────────────────────────────
// vi.hoisted runs before vi.mock factory calls so the refs are available inside
// the xyflow mock factory. On each ReactFlow render the mock writes the current
// nodes/edges props; the test body reads them after each refetch settles.

const { lastNodesRef, lastEdgesRef } = vi.hoisted(() => ({
  lastNodesRef: { current: null as readonly unknown[] | null },
  lastEdgesRef: { current: null as readonly unknown[] | null },
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

// Mock xyflow — captures the nodes/edges props on every render so the test can
// assert reference-equality across refetches. Does NOT depend on nodeTypes.
vi.mock('@xyflow/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@xyflow/react')>();
  const { useState } = await import('react');
  return {
    ...actual,
    ReactFlow: ({
      nodes,
      edges,
      children,
    }: {
      nodes: readonly unknown[];
      edges?: readonly unknown[];
      children?: React.ReactNode;
    }) => {
      lastNodesRef.current = nodes;
      lastEdgesRef.current = edges ?? null;
      return (
        <div data-testid="mock-reactflow">
          {children}
        </div>
      );
    },
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

vi.mock('./NodeCardRenderer', async () => {
  const { memo } = await import('react');
  return {
    NodeCardRenderer: memo(
      ({ data }: { data: { id: number; name: string; type: string; status: string | null } }) => (
        <div data-testid={`node-card-${data.id}`}>{data.name}</div>
      ),
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
  lastNodesRef.current = null;
  lastEdgesRef.current = null;
});

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      // structuralSharing disabled so that each refetch produces a fresh data
      // reference even when the JSON is identical. Without this, TanStack Query's
      // default deep-equality check returns the same object, visibleDetails stays
      // reference-equal, xyNodes never changes, and setNodes is never called —
      // making the test insensitive to whether reconcileNodes is wired or not.
      queries: { retry: false, staleTime: 0, structuralSharing: false },
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
   * R5-E2E: the `nodes` and `edges` props passed to ReactFlow are reference-equal
   * across two refetches that return identical data.
   *
   * Flow:
   * 1. Mount WorkspaceCanvas — viewport query fires, returns SAMPLE_VIEWPORT_NODES.
   * 2. Wait for ReactFlow to receive the initial nodes/edges props.
   * 3. Capture the props references: nodesAfterFirstRefetch / edgesAfterFirstRefetch.
   * 4. Invalidate the query to trigger a second fetch (identical data returned).
   * 5. reconcileNodes returns prev (exact reference) → setNodes bails → ReactFlow
   *    receives the same nodes prop → lastNodesRef.current === nodesAfterFirstRefetch.
   *
   * Mental-deletion: revert reconcileNodes to `return incoming.slice()` → fresh
   * array each time → ReactFlow receives new prop reference → toBe fails.
   */
  it('R5-E2E: ReactFlow receives reference-equal nodes and edges props on identical-data refetch', async () => {
    const { qc } = renderCanvas();

    // Wait for initial data to load.
    await waitFor(() => expect(viewportRequestCount).toBeGreaterThanOrEqual(1));

    // Allow reconciliation + React to settle so lastNodesRef is populated.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 50));
    });

    const nodesAfterFirstRefetch = lastNodesRef.current;
    const edgesAfterFirstRefetch = lastEdgesRef.current;

    // nodesAfterFirstRefetch must be non-null (ReactFlow was rendered).
    expect(nodesAfterFirstRefetch).not.toBeNull();

    // Trigger a refetch with identical data.
    await act(async () => {
      await qc.invalidateQueries({ queryKey: ['nodes', 'viewport'] });
      await new Promise((r) => setTimeout(r, 50));
    });

    await waitFor(() => expect(viewportRequestCount).toBeGreaterThanOrEqual(2));

    // Allow reconciliation + React to settle.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 50));
    });

    // reconcileNodes returned prev → same array reference → ReactFlow prop unchanged.
    expect(lastNodesRef.current).toBe(nodesAfterFirstRefetch);
    expect(lastEdgesRef.current).toBe(edgesAfterFirstRefetch);
  });
});
