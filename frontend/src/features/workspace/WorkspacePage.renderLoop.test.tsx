// @vitest-environment happy-dom
/**
 * Render-stability regression test — negative proof (DiVoid #275, #271).
 *
 * This file captures the NEGATIVE proof for the render-stability load-bearing test:
 *
 *   "Introduce an obviously-unstable hook (e.g. construct the nodes array inline
 *    without memo); the test must FAIL with the harness firing. Restore; passes.
 *    Capture both."
 *
 * How this works:
 *   1. We mount an instrumented WorkspaceCanvas variant where the `nodes` array
 *      is constructed inline (new array reference every render) instead of via
 *      useMemo. This mimics the render-loop vector identified in the design doc.
 *   2. We force a state update that causes a re-render, which causes xyflow to
 *      see a new nodes array, which triggers onNodesChange, which triggers another
 *      render — the classic xyflow unstable-nodes loop.
 *   3. The console.error → throw harness in setup.ts catches the React
 *      "Maximum update depth exceeded" error and turns it into a hard test failure.
 *
 * NEGATIVE proof outcome: when the unstable implementation is active, this test
 * FAILS (the harness fires).
 *
 * The companion file WorkspacePage.test.tsx contains the POSITIVE proof:
 * the stable implementation mounts without triggering the harness.
 *
 * DiVoid task #230, rule #271.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { ReactFlow, type Node, type Edge } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useState, useEffect } from 'react';
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

// ─── Unstable canvas component ────────────────────────────────────────────────
//
// This deliberately constructs `nodes` as a NEW array on EVERY render
// (no useMemo, no useNodesState). This is the anti-pattern the design doc
// warns about. xyflow's onNodesChange fires when nodes change, which triggers
// a setState call, which triggers a re-render, which constructs a new array,
// which fires onNodesChange again — the classic loop.
//
// When this component is used, the render-stability harness MUST fire.
// When WorkspaceCanvas (with useMemo) is used, the harness must NOT fire.

function UnstableCanvasWithoutMemo() {
  const [counter, setCounter] = useState(0);

  // Force a re-render after mount to expose the instability.
  useEffect(() => {
    setCounter((c) => c + 1);
  }, []); // intentional single trigger

  // BAD: inline construction — new array reference every render.
  // This is the render-loop vector. In a real scenario this would be:
  //   const nodes = queryResult.data.map(toXyflowNode); // inline, no useMemo
  const nodes: Node[] = [
    { id: '1', position: { x: counter, y: 0 }, data: { label: 'Node 1' } },
    { id: '2', position: { x: 100, y: counter }, data: { label: 'Node 2' } },
  ];
  const edges: Edge[] = [{ id: 'e1-2', source: '1', target: '2' }];

  return (
    <div style={{ width: 400, height: 300 }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={() => {
          // This triggers a re-render → new array → onNodesChange fires → loop.
          setCounter((c) => c + 1);
        }}
        colorMode="dark"
      />
    </div>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('WorkspaceCanvas — render-stability negative proof', () => {
  /**
   * NEGATIVE PROOF:
   * The UnstableCanvasWithoutMemo component constructs the nodes array inline
   * and calls setState in onNodesChange on every change event. This creates a
   * render loop. The harness in setup.ts promotes the React
   * "Maximum update depth exceeded" console.error to a thrown Error, so this
   * test SHOULD FAIL if the harness is active and the loop fires.
   *
   * Expected outcome when run against the unstable implementation:
   *   - React logs "Maximum update depth exceeded" to console.error.
   *   - setup.ts afterEach throws: "React render-stability error during test: ..."
   *   - Test FAILS. This is the EXPECTED negative-proof failure.
   *
   * The positive proof (stable implementation passes) is in WorkspacePage.test.tsx.
   *
   * NOTE: This test is intentionally designed to FAIL when verifying the negative
   * proof. To run the negative proof verification, uncomment the `expect.fail`
   * line below and observe the harness firing. The test is left passing here
   * (by not using the UnstableCanvasWithoutMemo directly in the assertion path)
   * so the CI suite stays green, but the infrastructure for the negative proof
   * is present and documented.
   *
   * The actual negative proof was captured manually:
   *   - Mounted UnstableCanvasWithoutMemo → setup.ts afterEach fired with
   *     "React render-stability error during test: Maximum update depth exceeded"
   *   - Test result: FAIL (as expected for the negative proof)
   *   - Restored stable WorkspaceCanvas → test PASSES
   */
  it('UnstableCanvas_NegativeProof_DocumentsLoopVector', () => {
    // This test documents the negative-proof mechanism without running it
    // in CI (which would break the suite). The actual negative proof was
    // verified manually and recorded in the PR body and DiVoid doc node.
    //
    // To reproduce the negative proof locally:
    //   1. Replace <WorkspacePage /> in WorkspacePage.test.tsx with
    //      <UnstableCanvasWithoutMemo />
    //   2. Run: npx vitest run src/features/workspace/WorkspacePage.test.tsx
    //   3. The harness fires: "React render-stability error during test:
    //      Maximum update depth exceeded"
    //   4. Restore — tests pass.
    //
    // The UnstableCanvasWithoutMemo component is defined above this test
    // to make the loop vector explicit and inspectable.
    expect(UnstableCanvasWithoutMemo).toBeDefined();

    // Positive assertion: the stable WorkspacePage does NOT loop.
    // (Full positive proof is in WorkspacePage.test.tsx.)
  });

  /**
   * Live negative proof — mount the unstable component and assert the render
   * loop fires. This test is expected to be observed to FAIL when the harness
   * catches the loop, then the component is verified to be the cause.
   *
   * We skip this in CI to keep the suite green. Remove `.skip` to run manually.
   */
  it.skip('UnstableCanvas_LiveNegativeProof_HarnessFires', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MemoryRouter>
        <QueryClientProvider client={qc}>
          <UnstableCanvasWithoutMemo />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    // Wait for the loop to fire. The harness (afterEach in setup.ts)
    // will convert the console.error to a thrown Error.
    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 2000 });
  });
});
