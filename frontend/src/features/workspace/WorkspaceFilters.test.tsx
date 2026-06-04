// @vitest-environment happy-dom
/**
 * Load-bearing tests for workspace type + status filters (DiVoid #318, #486, #275).
 *
 * ## What is tested
 *
 * 1. Filter-wiring positive proof (type filter):
 *    - Mount WorkspacePage with seeded data (viewportPageWithFilterFixtures).
 *    - Verify "First task" (type=task) renders by default.
 *    - Deselect "task" in the type filter → assert no task node renders.
 *    - Re-select "task" → assert "First task" reappears.
 *
 * 2. Default status exclusion positive proof:
 *    - Mount WorkspacePage with seeded data.
 *    - Without any user interaction, "Closed task" (status=closed) must NOT render.
 *    - This asserts the default status filter correctly excludes closed/fixed.
 *
 * 3. Negative proof (filter wiring revert):
 *    - When the filter is NOT wired into the hook (all nodes returned regardless
 *      of type/status), the closed task DOES render.
 *    - This test uses a different MSW handler that ignores type/status params.
 *    - It must FAIL if the closed node appears when it shouldn't.
 *
 * 4. Live type-filter positive proof (DiVoid task #486):
 *    - MSW serves a /types response containing both `task` and `product`.
 *    - After data loads, the type filter popover shows checkboxes for BOTH.
 *    - NEGATIVE PROOF: replace the live fetch with the hardcoded ALL_NODE_TYPES
 *      list; `product` is absent from that list, so the `product` checkbox
 *      does NOT appear — the test fails, proving #4 is load-bearing.
 *
 * 5. Type-absent rows map to untyped (DiVoid task #486, rewritten per Jenny #512):
 *    - MSW serves a /types fixture with ONLY a structural-group entry (no task, no doc).
 *    - After data loads: "untyped" IS in the popover; "task" is NOT (not in the live response).
 *    - NEGATIVE PROOF: revert the `?? UNTYPED_VALUE` mapping — "untyped" label disappears,
 *      test fails. Proves the mapping is load-bearing, not just a tautology via FALLBACK_OPTIONS.
 *
 * 6. Loading state — optimistic-default (option c):
 *    - While /types is pending, the type filter still shows checkboxes from the
 *      hardcoded fallback list (not an empty dropdown).
 *
 * 7. Known-types set preserves user-deselected types across /types refresh (Jenny #512):
 *    - Seed sessionStorage with selectedTypes missing `task` (user deselected it).
 *    - Seed knownTypes containing `task`, `documentation`, `bug` (user has seen them).
 *    - Mount the hook with liveTypeValues = [task, documentation, bug, product, meeting].
 *    - Assert: selectedTypes contains `product` and `meeting` (newly-discovered).
 *    - Assert: selectedTypes does NOT contain `task` or `bug` (known-but-deselected, stay off).
 *    - NEGATIVE PROOF: revert merge logic to prevSet-based (old code) — `task` and `bug`
 *      re-appear in selectedTypes, test fails. Proves the knownTypes set is load-bearing.
 *
 * ## Filter wiring
 *
 * WorkspaceCanvas passes selectedTypes/selectedStatuses into useNodesInViewport
 * and useUntypedNodesInViewport. The MSW handler simulates backend filtering
 * by checking ?status and ?type params. The negative proof overrides the
 * handler to return all nodes unconditionally.
 *
 * DiVoid task #318 / #486, design doc #283.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import {
  BASE_URL,
  viewportPageWithFilterFixtures,
  typeCatalogPage,
} from '@/test/msw/handlers';
import type { Page, PositionedNodeDetails } from '@/types/divoid';

// The handler simulates backend type/status filtering:
//  - If ?type is present, only nodes with matching types are returned.
//  - If ?status is present, only nodes with matching statuses are returned.
//  - If ?nostatus=true is present, null-status nodes are included too.
//  - Without filters, all nodes are returned (unfiltered viewport fetch).
//
// This mirrors the real backend behaviour so the filter wiring tests are
// testing actual API parameter passing, not just UI state.

function filterViewportPage(url: URL): Page<PositionedNodeDetails> {
  const typeParam   = url.searchParams.get('type');
  const statusParam = url.searchParams.get('status');
  const nostatus    = url.searchParams.get('nostatus') === 'true';

  let results = [...viewportPageWithFilterFixtures.result];

  if (typeParam) {
    const types = typeParam.split(',');
    results = results.filter((n) => n.type && types.includes(n.type));
  }

  if (statusParam || nostatus) {
    const statuses = statusParam ? statusParam.split(',') : [];
    results = results.filter((n) => {
      if (n.status === null) return nostatus;
      return statuses.includes(n.status);
    });
  }

  return { result: results, total: results.length };
}

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
      createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
    }),
  ),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('bounds')) {
      return HttpResponse.json(filterViewportPage(url));
    }
    return HttpResponse.json({ result: [], total: 0 });
  }),
  // Type catalog — fixture includes `task` and `product` (DiVoid #486 load-bearing tests).
  http.get(`${BASE_URL}/types`, () => HttpResponse.json(typeCatalogPage)),
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
  http.post(`${BASE_URL}/nodes`, () =>
    HttpResponse.json({ id: 99, type: 'task', name: 'New node', status: 'open' }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  // Clear sessionStorage between tests so filter state doesn't bleed across tests.
  sessionStorage.clear();
});
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

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

let WorkspacePage: typeof import('./WorkspacePage').WorkspacePage;

beforeAll(async () => {
  const mod = await import('./WorkspacePage');
  WorkspacePage = mod.WorkspacePage;
});

function renderPage() {
  const qc = makeQC();
  return render(
    <MemoryRouter initialEntries={['/workspace']}>
      <QueryClientProvider client={qc}>
        <WorkspacePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

describe('WorkspaceFilters — type filter wiring (load-bearing positive proof)', () => {
  /**
   * POSITIVE PROOF:
   *
   * Mount with all types selected (default). "First task" appears.
   * Open type filter, deselect "task" → backend receives ?type without task →
   * MSW returns only non-task nodes → "First task" disappears.
   * Re-select "task" → "First task" reappears.
   *
   * This test FAILS if filter selections are not passed to useNodesInViewport.
   */
  it('deselecting task type hides task nodes; re-selecting shows them again', async () => {
    renderPage();

    // Wait for canvas and first batch of nodes.
    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Default state: task nodes visible.
    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Find and click the "Type" filter trigger button.
    const typeBtn = screen.getByRole('button', { name: /type filter/i });
    fireEvent.click(typeBtn);

    // Popover should open — find the "task" checkbox.
    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: /type filter options/i })).toBeInTheDocument();
    }, { timeout: 3000 });

    const taskCheckbox = screen.getByRole('checkbox', { name: /^task$/i });
    expect(taskCheckbox).toBeChecked();

    // Deselect task.
    fireEvent.click(taskCheckbox);
    expect(taskCheckbox).not.toBeChecked();

    // MSW now returns only non-task nodes → "First task" should not render.
    await waitFor(() => {
      expect(screen.queryByText('First task')).not.toBeInTheDocument();
    }, { timeout: 5000 });

    // Re-select task.
    fireEvent.click(taskCheckbox);
    expect(taskCheckbox).toBeChecked();

    // "First task" should reappear.
    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    }, { timeout: 5000 });
  });
});

describe('WorkspaceFilters — default status exclusion (load-bearing positive proof)', () => {
  /**
   * POSITIVE PROOF:
   *
   * Without any user interaction, "Closed task" (status=closed) must NOT appear
   * on the canvas. The default status filter excludes closed/fixed.
   *
   * The MSW handler respects ?status params and will not return closed nodes
   * when status=closed is absent from the query.
   *
   * This test FAILS if the default status filter is not wired correctly.
   */
  it('closed task is hidden without user interaction (default status filter)', async () => {
    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Wait long enough for data to load.
    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    }, { timeout: 5000 });

    // "Closed task" must NOT be visible (excluded by default status filter).
    expect(screen.queryByText('Closed task')).not.toBeInTheDocument();
  });
});

describe('WorkspaceFilters — negative proof (filter not wired)', () => {
  /**
   * NEGATIVE PROOF:
   *
   * Override MSW to return ALL nodes unconditionally (ignores type/status params).
   * This simulates what would happen if filter params were NOT passed to the hook.
   *
   * Expected: "Closed task" IS in the DOM — proving the default status filter
   * is the only thing preventing it from appearing in the positive test.
   *
   * Observable: the closed node appears when the backend ignores the filter.
   * If this test FAILS (closed task not in DOM), it means something else is
   * filtering it — investigate.
   */
  it('closed task renders when backend ignores status filter (negative proof)', async () => {
    // Override handler to return all nodes unconditionally.
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('bounds')) {
          return HttpResponse.json(viewportPageWithFilterFixtures);
        }
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // With the unfiltered handler, the closed task IS returned by the backend.
    // It will appear in the DOM because the frontend does not post-filter.
    await waitFor(() => {
      expect(screen.getByText('Closed task')).toBeInTheDocument();
    }, { timeout: 5000 });
  });
});

describe('WorkspaceFilters — sessionStorage persistence', () => {
  /**
   * Verify that filter selections survive a simulated remount (sessionStorage round-trip).
   *
   * This tests the loadSet/saveSet logic in useWorkspaceFilters without
   * exercising the full canvas — simpler and faster.
   */
  it('persists type filter selection to sessionStorage and reloads it', async () => {
    // Pre-seed sessionStorage with a partial selection (task deselected).
    const partial = ['bug', 'documentation', 'session-log', 'project', 'organization',
                     'agent', 'person', 'chat', 'feature', 'status', '__untyped__'];
    sessionStorage.setItem('divoid.workspace.typeFilter', JSON.stringify(partial));

    // Dynamically import the hook to get the sessionStorage-seeded value.
    const { useWorkspaceFilters: hook } = await import('./useWorkspaceFilters');

    // We test the hook in isolation via a tiny React component
    let capturedTypes: string[] | null = null;

    const { renderHook } = await import('@testing-library/react');
    const { result } = renderHook(() => hook());

    await act(async () => {
      capturedTypes = result.current.selectedTypes;
    });

    expect(capturedTypes).not.toBeNull();
    expect(capturedTypes).not.toContain('task');
    expect(capturedTypes).toContain('documentation');
    // typeFilterActive: selection differs from default (all selected)
    expect(result.current.typeFilterActive).toBe(true);
  });
});

describe('WorkspaceFilters — live type catalog from /api/types (DiVoid #486)', () => {
  /**
   * POSITIVE PROOF — load-bearing test for DiVoid task #486.
   *
   * The MSW handler serves typeCatalogPage which contains both `task` (a type
   * already in ALL_NODE_TYPES) and `product` (a type NOT in ALL_NODE_TYPES).
   *
   * After the /types query resolves, BOTH must appear as checkboxes in the
   * type filter popover. If this test fails, the live-fetch is not wired.
   *
   * NEGATIVE PROOF: revert useNodeTypes to return FALLBACK_OPTIONS (the
   * hardcoded ALL_NODE_TYPES). `product` does NOT appear in ALL_NODE_TYPES,
   * so `screen.getByRole('checkbox', { name: /^product$/i })` throws → test fails.
   * Documented in PR body per §13.1 of the frontend code contracts (#420).
   */
  it('shows task AND product checkboxes after /types resolves (positive proof)', async () => {
    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    // Open the Type filter popover.
    const typeBtn = screen.getByRole('button', { name: /type filter/i });
    fireEvent.click(typeBtn);

    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: /type filter options/i })).toBeInTheDocument();
    }, { timeout: 3000 });

    // Both `task` (hardcoded) and `product` (live-only) must appear.
    await waitFor(() => {
      expect(screen.getByRole('checkbox', { name: /^task$/i })).toBeInTheDocument();
      expect(screen.getByRole('checkbox', { name: /^product$/i })).toBeInTheDocument();
    }, { timeout: 5000 });
  });

  /**
   * Type-absent rows map to "untyped" checkbox — load-bearing rewrite (DiVoid #486, Jenny #512).
   *
   * This test overrides /types with a fixture that contains ONLY a structural-group
   * entry (no `type` field, no `task`, no `documentation`). After the live data
   * resolves, the popover must show "untyped" AND must NOT show "task" — because
   * `task` is not in the live response, only in the loading-state FALLBACK_OPTIONS.
   *
   * NEGATIVE PROOF: revert the `entry.type ?? UNTYPED_VALUE` mapping to just
   * `entry.type`. The structural entry maps to undefined, which is not UNTYPED_VALUE,
   * so typeSet contains undefined rather than '__untyped__'. The rendered label is not
   * "untyped" (TYPE_LABELS[undefined] is undefined, label falls back to undefined which
   * does not produce a "untyped" accessible name). The `getByRole('checkbox', {name:
   * /^untyped$/i})` assertion throws — test fails, proving the ?? mapping is load-bearing.
   */
  it('type-absent /types entries map to the untyped checkbox (not task)', async () => {
    // Override /types with a fixture containing ONLY the structural-group entry.
    // No `task`, no `documentation` — only the type-absent entry.
    server.use(
      http.get(`${BASE_URL}/types`, () =>
        HttpResponse.json({ result: [{ id: 29, count: 1 }], total: 1 }),
      ),
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    const typeBtn = screen.getByRole('button', { name: /type filter/i });
    fireEvent.click(typeBtn);

    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: /type filter options/i })).toBeInTheDocument();
    }, { timeout: 3000 });

    // After live data arrives: "untyped" must appear (type-absent → UNTYPED_VALUE mapping).
    await waitFor(() => {
      expect(screen.getByRole('checkbox', { name: /^untyped$/i })).toBeInTheDocument();
    }, { timeout: 5000 });

    // "task" must NOT appear — it is not in the live response.
    // If this assertion fails, FALLBACK_OPTIONS is leaking through after live data loaded.
    expect(screen.queryByRole('checkbox', { name: /^task$/i })).not.toBeInTheDocument();
  });

  /**
   * Loading state uses optimistic-default (option c, DiVoid task #486).
   *
   * While /types is pending, the type filter shows the hardcoded fallback list
   * (ALL_NODE_TYPES) so the dropdown is never empty. The `task` checkbox must
   * appear even before /types resolves.
   *
   * This is tested by overriding the /types handler with a never-resolving one,
   * then opening the popover and asserting `task` is present.
   */
  it('shows fallback type options while /types is loading', async () => {
    // Override /types with a handler that never resolves (simulates slow network).
    server.use(
      http.get(`${BASE_URL}/types`, () => new Promise(() => { /* never resolves */ })),
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId('rf__wrapper')).toBeInTheDocument();
    }, { timeout: 5000 });

    const typeBtn = screen.getByRole('button', { name: /type filter/i });
    fireEvent.click(typeBtn);

    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: /type filter options/i })).toBeInTheDocument();
    }, { timeout: 3000 });

    // Fallback list must be present before live data arrives.
    // `task` is in ALL_NODE_TYPES (the fallback), so it must appear.
    expect(screen.getByRole('checkbox', { name: /^task$/i })).toBeInTheDocument();
  });
});

describe('WorkspaceFilters — known-types set preserves user-deselected types (Jenny #512)', () => {
  /**
   * POSITIVE PROOF — load-bearing test for Jenny QA review at DiVoid #512, Finding 1.
   *
   * The bug: the old merge effect compared liveTypeValues against selectedTypes
   * (prevSet). A type the user deselected was absent from selectedTypes, so it
   * was classified as "new" and re-added — silently overriding the user's intent.
   *
   * The fix: persist divoid.workspace.typeFilter.known in sessionStorage. The merge
   * computes newlyDiscovered = liveTypeValues \ knownTypes (set-minus against KNOWN,
   * not selection). A type that is known-but-deselected stays deselected.
   *
   * Seed state (simulates a user who deselected `task` and `bug` in a prior session):
   *   selectedTypes = ['documentation']      (task and bug were deselected)
   *   knownTypes    = ['task', 'documentation', 'bug']   (user has seen all three)
   *
   * Live catalog: [task, documentation, bug, product, meeting]
   *   — `product` and `meeting` are genuinely new (not in knownTypes)
   *   — `task` and `bug` are known-and-deselected (must stay OFF)
   *
   * NEGATIVE PROOF: revert the merge to the old prevSet-based logic
   * (`liveTypeValues.filter((t) => !prevSet.has(t))`). With prev = ['documentation'],
   * prevSet = {documentation}. newTypes = [task, bug, product, meeting] — task and bug
   * are included because they are absent from selectedTypes, not knownTypes.
   * selectedTypes becomes ['documentation', 'task', 'bug', 'product', 'meeting'].
   * The assertion `expect(selectedTypes).not.toContain('task')` fails.
   */
  it('previously-deselected types stay deselected after live catalog refresh', async () => {
    // Seed sessionStorage: user deselected task and bug (only documentation selected).
    sessionStorage.setItem(
      'divoid.workspace.typeFilter',
      JSON.stringify(['documentation']),
    );
    // User has already been offered task, documentation, and bug in prior sessions.
    sessionStorage.setItem(
      'divoid.workspace.typeFilter.known',
      JSON.stringify(['task', 'documentation', 'bug']),
    );

    const { useWorkspaceFilters: hook } = await import('./useWorkspaceFilters');
    const { renderHook, act: hookAct } = await import('@testing-library/react');

    // Mount the hook with a live catalog that includes the known types plus two new ones.
    const liveTypeValues = ['task', 'documentation', 'bug', 'product', 'meeting'];
    const { result } = renderHook(() => hook({ liveTypeValues }));

    // Allow the merge effect to run.
    await hookAct(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    const { selectedTypes } = result.current;

    // Newly-discovered types must be auto-selected.
    expect(selectedTypes).toContain('product');
    expect(selectedTypes).toContain('meeting');

    // Previously-known-and-deselected types must NOT be re-added.
    expect(selectedTypes).not.toContain('task');
    expect(selectedTypes).not.toContain('bug');

    // The user's surviving selection must be present.
    expect(selectedTypes).toContain('documentation');
  });
});

describe('WorkspaceFilters — no-duplicate invariant on first visit (Jenny #514)', () => {
  /**
   * POSITIVE PROOF — load-bearing test for Jenny re-review at DiVoid #514.
   *
   * Bug: on a fresh visit (knownTypes = [], sessionStorage empty), the merge effect
   * prepends all live types to the DEFAULT_TYPE_SELECTION array, which already
   * contains the overlapping types. Result: duplicate entries in selectedTypes —
   * visible as duplicate ?type= params in the URL query string.
   *
   * Fix: wrap the merge with Set-based dedup:
   *   const next = [...new Set([...prev, ...newlyDiscovered])];
   *
   * Seed state: sessionStorage empty (first visit, no knownTypes, no selectedTypes).
   * Live catalog: [{type: 'task', count: 5}, {type: 'product', count: 1}].
   *   — `task` is already in ALL_NODE_TYPES (the default)
   *   — `product` is genuinely new
   *
   * Assert: after merge, `selectedTypes` has no duplicate entries.
   *   selectedTypes.length === new Set(selectedTypes).size
   *   selectedTypes.filter(t => t === 'task').length === 1
   *
   * NEGATIVE PROOF: revert the Set-wrap (`const next = [...prev, ...newlyDiscovered]`).
   * With prev = ALL_NODE_TYPES (contains 'task') and newlyDiscovered = ['task'] (task is
   * absent from empty knownTypes, so it is "newly discovered"), `task` appears twice.
   * The assertion `filter(t => t === 'task').length === 1` fails — proving the Set-wrap
   * is load-bearing, not cosmetic.
   */
  it('no duplicate entries in selectedTypes when live catalog overlaps ALL_NODE_TYPES (first visit)', async () => {
    // Seed: first visit — sessionStorage is clear (done by afterEach, but be explicit).
    sessionStorage.removeItem('divoid.workspace.typeFilter');
    sessionStorage.removeItem('divoid.workspace.typeFilter.known');

    const { useWorkspaceFilters: hook } = await import('./useWorkspaceFilters');
    const { renderHook, act: hookAct } = await import('@testing-library/react');

    // Live catalog overlaps with ALL_NODE_TYPES on `task`; `product` is new.
    const liveTypeValues = ['task', 'product'];
    const { result } = renderHook(() => hook({ liveTypeValues }));

    // Allow the merge effect to run.
    await hookAct(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    const { selectedTypes } = result.current;

    // Primary invariant: no duplicates in the persisted selection.
    expect(selectedTypes.length).toBe(new Set(selectedTypes).size);

    // Spot-check: 'task' appears exactly once (it was in prev AND in newlyDiscovered).
    expect(selectedTypes.filter((t) => t === 'task').length).toBe(1);

    // 'product' is genuinely new and must be present.
    expect(selectedTypes).toContain('product');
  });
});
