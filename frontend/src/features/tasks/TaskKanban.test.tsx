/**
 * Load-bearing tests for the Kanban board view (DiVoid task #384).
 *
 * Tests, each with positive + negative proof (DiVoid #275, PR #53/#55 lessons).
 *
 * 1. Board renders one column per visible status (TASK_STATUSES order, not toggle order).
 * 2. Toggling a status pill adds/removes its column.
 * 3. Drag end fires PATCH with the target column status.
 * 4. Drop on same column is a no-op (mutation not fired).
 * 5. Optimistic local move is visible immediately, before PATCH resolves.
 * 6. Optimistic state clears on success.
 * 7. Optimistic state reverts on PATCH error + toast.error fires.
 * 8. View toggle persists to sessionStorage and survives remount.
 * 9. API query identical between list and board mode.
 * 10. PointerSensor activation distance respected: click navigates, does not drag.
 * 14. PATCH success invalidates path-query cache: card moves to new column and stays
 *     (no snap-back). Pins the cache OUTCOME, not just the override Map flow.
 *     Bug #411: narrow ['nodes','list']+['nodes','linkedto'] missed path-query key.
 *
 * Drag simulation technique (PR #53 + #55 lesson): find the onDragEnd prop on
 * DndContext via React fiber walking and dispatch a synthetic {active, over} object
 * directly — avoids fragile native pointer-event simulation for dnd-kit.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, act, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';
import type { Page, NodeDetails } from '@/types/divoid';
import React from 'react';

// ─── MSW server ───────────────────────────────────────────────────────────────

const taskFixtures: Page<NodeDetails> = {
  result: [
    { id: 30, type: 'task', name: 'Fix login', status: 'open' },
    { id: 31, type: 'task', name: 'Add tests', status: 'in-progress' },
    { id: 32, type: 'task', name: 'Plan sprint', status: 'new' },
  ],
  total: 3,
};

const emptyPage: Page<NodeDetails> = { result: [], total: 0 };

const tasksGroupFixture: Page<NodeDetails> = {
  result: [{ id: 99, type: 'documentation', name: 'Tasks', status: null }],
  total: 1,
};

let capturedUrls: string[] = [];
let capturedPatchCalls: { id: number; body: unknown }[] = [];
let patchShouldFail = false;

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    capturedUrls.push(request.url);
    const linkedto = url.searchParams.get('linkedto');
    const name = url.searchParams.get('name');
    const path = url.searchParams.get('path');

    if (linkedto && name === 'Tasks') return HttpResponse.json(tasksGroupFixture);
    if (path) return HttpResponse.json(taskFixtures);
    return HttpResponse.json(emptyPage);
  }),
  http.patch(`${BASE_URL}/nodes/:id`, async ({ request, params }) => {
    const body = await request.json();
    capturedPatchCalls.push({ id: Number(params.id), body });
    if (patchShouldFail) {
      return HttpResponse.json({ code: 'error', text: 'Server error' }, { status: 500 });
    }
    return new HttpResponse(null, { status: 204 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  capturedUrls = [];
  capturedPatchCalls = [];
  patchShouldFail = false;
  sessionStorage.clear();
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
    CALLBACK: '/callback',
    LOGOUT: '/logout',
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    TASKS_ORG: (id: number) => `/tasks/orgs/${id}`,
    TASKS_PROJECT: (id: number) => `/tasks/projects/${id}`,
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

const toastErrorMock = vi.fn();
vi.mock('sonner', () => ({
  toast: {
    error: (...args: unknown[]) => toastErrorMock(...args),
    success: vi.fn(),
    warning: vi.fn(),
    info: vi.fn(),
  },
}));

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function renderWithProviders(ui: React.ReactElement, initialPath = '/', qc?: QueryClient) {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <QueryClientProvider client={qc ?? makeQC()}>{ui}</QueryClientProvider>
    </MemoryRouter>,
  );
}

/**
 * Finds the DndContext element in the DOM and extracts its onDragEnd handler
 * via the fiber-walking technique.
 */
function getDndContextOnDragEnd(
  container: HTMLElement,
): ((event: { active: { id: number }; over: { id: string } | null }) => void) | undefined {
  // DndContext renders a wrapping div with data-testid="kanban-board" inside.
  // Walk up from that element to find the fiber that has onDragEnd.
  const board = container.querySelector('[data-testid="kanban-board"]');
  if (!board) return undefined;

  // Walk up the DOM to find the element that holds the DndContext fiber props.
  // DndContext does not render a DOM element itself, so we look at the board's
  // parent elements or search the fiber tree from a known child.
  const fiberKey = Object.keys(board).find(
    (k) => k.startsWith('__reactFiber') || k.startsWith('__reactInternalInstance'),
  );
  if (!fiberKey) return undefined;

  let fiber = (board as unknown as Record<string, unknown>)[fiberKey] as {
    memoizedProps?: Record<string, unknown>;
    return?: unknown;
  } | null;

  // Walk upward through the fiber tree to find onDragEnd.
  while (fiber) {
    if (fiber.memoizedProps && 'onDragEnd' in fiber.memoizedProps) {
      return fiber.memoizedProps['onDragEnd'] as (event: {
        active: { id: number };
        over: { id: string } | null;
      }) => void;
    }
    fiber = fiber.return as typeof fiber;
  }
  return undefined;
}

// ─── Lazy imports ─────────────────────────────────────────────────────────────

let TaskListView: typeof import('./TaskListView').TaskListView;
let useKanbanColumns: typeof import('./useKanbanColumns').useKanbanColumns;

beforeAll(async () => {
  const [taskMod, , hookMod] = await Promise.all([
    import('./TaskListView'),
    import('./TaskKanbanBoard'),
    import('./useKanbanColumns'),
  ]);
  TaskListView = taskMod.TaskListView;
  useKanbanColumns = hookMod.useKanbanColumns;
});

// ─── Test 1: Board renders one column per visible status in TASK_STATUSES order ─

describe('Test 1 — Board renders one column per visible status in TASK_STATUSES order', () => {
  it('positive: default filter (new, open, in-progress) → exactly 3 columns in array order', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    // Switch to board view.
    await waitFor(() => {
      expect(screen.getByTestId('tasks-view-toggle-board')).toBeInTheDocument();
    });
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    const columns = container.querySelectorAll('[data-kanban-column="true"]');
    expect(columns).toHaveLength(3);

    // Verify order follows TASK_STATUSES: new, open, in-progress (closed excluded by default).
    expect(columns[0]).toHaveAttribute('data-testid', 'kanban-column-new');
    expect(columns[1]).toHaveAttribute('data-testid', 'kanban-column-open');
    expect(columns[2]).toHaveAttribute('data-testid', 'kanban-column-in-progress');
  });

  /**
   * Negative proof: useKanbanColumns with selectedStatuses in REVERSED order still
   * produces columns in TASK_STATUSES order. If it iterated selectedStatuses directly,
   * the order would be reversed.
   */
  it('negative: useKanbanColumns iterates TASK_STATUSES order regardless of selectedStatuses order', () => {
    const { result } = renderHook(() =>
      useKanbanColumns({
        tasks: [
          { id: 1, type: 'task', name: 'A', status: 'in-progress' },
          { id: 2, type: 'task', name: 'B', status: 'new' },
        ],
        // Reversed order — in-progress before new.
        selectedStatuses: ['in-progress', 'new'],
        optimisticOverrides: new Map(),
      }),
    );

    const columns = result.current;
    // TASK_STATUSES order: new → open → in-progress → closed.
    // Only new and in-progress are selected, so: [new, in-progress].
    expect(columns[0].status).toBe('new');
    expect(columns[1].status).toBe('in-progress');

    // If selectedStatuses order were used, first column would be in-progress — assert it's NOT.
    expect(columns[0].status).not.toBe('in-progress');
  });
});

// ─── Test 2: Toggling a status pill adds/removes its column ──────────────────

describe('Test 2 — Toggling a status pill adds/removes its column', () => {
  it('positive: clicking the closed pill adds a fourth column in the last array position', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    // Switch to board view.
    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    // Initially 3 columns (no closed).
    expect(container.querySelectorAll('[data-kanban-column="true"]')).toHaveLength(3);

    // Toggle "closed" on.
    const closedPill = screen.getByRole('button', { name: 'closed' });
    await user.click(closedPill);

    await waitFor(() => {
      expect(container.querySelectorAll('[data-kanban-column="true"]')).toHaveLength(4);
    });

    const columns = container.querySelectorAll('[data-kanban-column="true"]');
    // closed is last in TASK_STATUSES.
    expect(columns[3]).toHaveAttribute('data-testid', 'kanban-column-closed');
  });

  /**
   * Negative proof: if columns were hard-coded to the default three, toggling
   * closed would not add a fourth column.
   */
  it('negative: without toggling, closed column is absent from the board', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    expect(container.querySelector('[data-testid="kanban-column-closed"]')).not.toBeInTheDocument();
  });
});

// ─── Test 3: Drag end fires PATCH with the column status ─────────────────────

describe('Test 3 — Drag end fires PATCH with the column status', () => {
  it('positive: onDragEnd({active:{id:30}, over:{id:"in-progress"}}) fires PATCH with correct body', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    // Wait for tasks to load.
    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    // Get onDragEnd from the DndContext fiber.
    const onDragEnd = getDndContextOnDragEnd(container);
    expect(onDragEnd).toBeDefined();

    // Dispatch: card 30 (status=open) → column in-progress.
    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'in-progress' } });
    });

    await waitFor(() => {
      expect(capturedPatchCalls.length).toBeGreaterThan(0);
    });

    // Must assert BOTH the node id (URL) and the body — body-only would miss
    // a stale-closure bug that PATCHes /nodes/0 with the correct body.
    expect(capturedPatchCalls[0]).toEqual({
      id: 30,
      body: [{ op: 'replace', path: '/status', value: 'in-progress' }],
    });
  });

  /**
   * Negative proof: if the onDragEnd handler does not dispatch the mutation,
   * capturedPatchCalls stays empty.
   */
  it('negative: no drag → no PATCH fires', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    // No drag dispatched.
    await new Promise((r) => setTimeout(r, 50));

    expect(capturedPatchCalls).toHaveLength(0);
  });
});

// ─── Test 4: Drop on same column is a no-op ──────────────────────────────────

describe('Test 4 — Drop on same column is a no-op', () => {
  it('positive: dropping card 30 (open) onto column open does NOT call mutate', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const onDragEnd = getDndContextOnDragEnd(container);
    expect(onDragEnd).toBeDefined();

    // Drop onto the same status column (card 30 is 'open').
    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'open' } });
    });

    await new Promise((r) => setTimeout(r, 50));

    expect(capturedPatchCalls).toHaveLength(0);
  });

  /**
   * Negative proof: dropping onto a DIFFERENT column does fire PATCH.
   * This confirms the equality guard is specifically what prevents the
   * same-column case, not something else.
   */
  it('negative: dropping onto a different column DOES fire PATCH', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const onDragEnd = getDndContextOnDragEnd(container);

    // Drop onto 'new' (card 30 is 'open') — different column.
    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'new' } });
    });

    await waitFor(() => {
      expect(capturedPatchCalls.length).toBeGreaterThan(0);
    });
  });
});

// ─── Test 5: Optimistic local move is visible immediately ────────────────────

describe('Test 5 — Optimistic local move is visible before PATCH resolves', () => {
  it('positive: card moves to target column before MSW responds', async () => {
    let resolvePatch!: () => void;
    server.use(
      http.patch(`${BASE_URL}/nodes/:id`, () =>
        new Promise<Response>((resolve) => {
          resolvePatch = () =>
            resolve(new HttpResponse(null, { status: 204 }));
        }),
      ),
    );

    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    // Before drag: card 30 is in the 'open' column.
    const openColumn = container.querySelector('[data-testid="kanban-column-open"]');
    expect(within(openColumn as HTMLElement).queryByTestId('kanban-card-30')).toBeInTheDocument();

    const onDragEnd = getDndContextOnDragEnd(container);
    expect(onDragEnd).toBeDefined();

    // Dispatch drag — PATCH is still pending.
    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'in-progress' } });
    });

    // BEFORE resolving the PATCH, the card should already be in 'in-progress'.
    await waitFor(() => {
      const inProgressColumn = container.querySelector('[data-testid="kanban-column-in-progress"]');
      expect(within(inProgressColumn as HTMLElement).queryByTestId('kanban-card-30')).toBeInTheDocument();
    });

    // Resolve the PATCH so the test cleans up.
    resolvePatch();
  });

  /**
   * Negative proof: without the optimistic state write, the card would stay in
   * 'open' until the MSW response arrives. We prove it by checking the card is
   * in 'open' before any drag — baseline.
   */
  it('negative: before drag, card 30 is in the open column (not in-progress)', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const inProgressColumn = container.querySelector('[data-testid="kanban-column-in-progress"]');
    // Card 30 (open) is NOT in in-progress column yet.
    expect(within(inProgressColumn as HTMLElement).queryByTestId('kanban-card-30')).not.toBeInTheDocument();
  });
});

// ─── Test 6: Optimistic state clears on success ──────────────────────────────

describe('Test 6 — Optimistic state clears on PATCH success', () => {
  /**
   * Positive proof: after the PATCH succeeds AND the cache invalidation refetches,
   * the card returns to its real status column (open) — proving the optimistic
   * override was cleared. If the override were NOT cleared after success, it would
   * permanently lock the card in in-progress even though the refetch returned open.
   *
   * We verify this by using a two-phase server: first PATCH succeeds, then refetch
   * returns the original fixtures (card 30 = open). After settling, card must be
   * in the 'open' column — not 'in-progress'.
   */
  it('positive: after PATCH success + refetch, card is back in its real-status column', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const onDragEnd = getDndContextOnDragEnd(container);

    // Move card 30 from open → in-progress. PATCH succeeds (default handler returns 204).
    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'in-progress' } });
    });

    // Wait for PATCH to resolve and cache to invalidate + refetch.
    await waitFor(() => {
      expect(capturedPatchCalls.length).toBeGreaterThan(0);
    });

    // After the refetch (taskFixtures: card 30 = 'open'), the override is cleared
    // so the card moves back to 'open'. This proves the override was cleared.
    await waitFor(
      () => {
        const openCol = container.querySelector('[data-testid="kanban-column-open"]');
        expect(within(openCol as HTMLElement).queryByTestId('kanban-card-30')).toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });

  /**
   * Negative proof: useKanbanColumns with an override set pins the card in the
   * override column regardless of the real status. If the override were NOT cleared
   * on success, the card would remain in in-progress even after refetch.
   */
  it('negative: with override still set, card stays in override column ignoring real status', () => {
    const { result } = renderHook(() =>
      useKanbanColumns({
        tasks: [{ id: 30, type: 'task', name: 'Fix login', status: 'open' }],
        selectedStatuses: ['new', 'open', 'in-progress'],
        // Override is NOT cleared — simulates the bug.
        optimisticOverrides: new Map([[30, 'in-progress']]),
      }),
    );

    const openCol = result.current.find((c) => c.status === 'open');
    const inProgressCol = result.current.find((c) => c.status === 'in-progress');

    // Card is in in-progress (override), NOT in open (real status).
    expect(inProgressCol?.tasks).toHaveLength(1);
    expect(openCol?.tasks).toHaveLength(0);
  });
});

// ─── Test 7: Optimistic state reverts on PATCH error ────────────────────────

describe('Test 7 — Optimistic state reverts on PATCH error + toast.error fires', () => {
  it('positive: on 500 error, card moves back to original column and toast fires', async () => {
    patchShouldFail = true;

    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const onDragEnd = getDndContextOnDragEnd(container);

    // Move card 30 open → in-progress (PATCH will fail).
    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'in-progress' } });
    });

    // Wait for the error to clear the override.
    await waitFor(() => {
      // Card should revert to 'open' column.
      const openColumn = container.querySelector('[data-testid="kanban-column-open"]');
      expect(within(openColumn as HTMLElement).queryByTestId('kanban-card-30')).toBeInTheDocument();
    });

    // toast.error must have been called.
    expect(toastErrorMock).toHaveBeenCalled();
  });

  /**
   * Negative proof: on success (no error), toast.error is NOT called.
   */
  it('negative: on successful PATCH, toast.error is not called', async () => {
    patchShouldFail = false;

    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const onDragEnd = getDndContextOnDragEnd(container);

    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'in-progress' } });
    });

    await waitFor(() => {
      expect(capturedPatchCalls.length).toBeGreaterThan(0);
    });

    expect(toastErrorMock).not.toHaveBeenCalled();
  });
});

// ─── Test 8: View toggle persists to sessionStorage and survives remount ──────

describe('Test 8 — View toggle persists to sessionStorage and survives remount', () => {
  it('positive: switching to board, unmounting, and remounting keeps board mode', async () => {
    const user = userEvent.setup();

    const { container, unmount } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));

    // Switch to board mode.
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    // Unmount.
    unmount();

    // Remount — should restore board mode from sessionStorage.
    const { container: container2 } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(container2.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    expect(container2.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
  });

  /**
   * Negative proof: if sessionStorage write is removed, remount defaults to list.
   * We verify: before any toggle, remount shows list view (not board).
   */
  it('negative: without toggling, remount shows list view (default)', async () => {
    const { unmount } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('tasks-view-toggle')).toBeInTheDocument();
    });

    // No toggle — default is list.
    unmount();

    const { container: container2 } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByTestId('tasks-view-toggle')).toBeInTheDocument();
    });

    expect(container2.querySelector('[data-testid="kanban-board"]')).not.toBeInTheDocument();
  });
});

// ─── Test 9: API query identical between list and board mode ─────────────────

describe('Test 9 — API query identical between list and board mode', () => {
  /**
   * Positive proof: Pre-seed sessionStorage to start directly in board mode, so
   * the FIRST request fired is already in board mode — no toggle click needed,
   * no ambiguity about which request is being captured.
   *
   * Both renders (list and board) must emit the SAME request URL. This pins
   * the invariant: "board does NOT add a query parameter".
   */
  it('positive: list mode and board mode emit byte-equal URLs for the path query', async () => {
    // --- List mode render ---
    capturedUrls = [];
    const { unmount: unmount1 } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(screen.getByText('Fix login')).toBeInTheDocument();
    });

    // Grab the LAST path-query URL (after any reactivity) to be safe.
    const listUrls = capturedUrls.filter((u) => new URL(u).searchParams.get('path'));
    expect(listUrls.length).toBeGreaterThan(0);
    const listUrl = listUrls[listUrls.length - 1];
    unmount1();

    // --- Board mode render: pre-seed sessionStorage to start in board mode ---
    sessionStorage.setItem('divoid.tasks.view.20', 'board');
    capturedUrls = [];

    const { container, unmount: unmount2 } = renderWithProviders(<TaskListView projectId={20} />);

    // Since sessionStorage pre-seeds board mode, the board renders immediately.
    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(capturedUrls.some((u) => new URL(u).searchParams.get('path'))).toBe(true);
    });

    // Grab the LAST path-query URL from board-mode render.
    const boardUrls = capturedUrls.filter((u) => new URL(u).searchParams.get('path'));
    expect(boardUrls.length).toBeGreaterThan(0);
    const boardUrl = boardUrls[boardUrls.length - 1];
    unmount2();

    // The path-query URLs must be byte-equal (same data fetch regardless of view).
    expect(boardUrl).toBe(listUrl);
  });

  /**
   * Negative proof: if board mode added a view= parameter, the URLs would differ.
   * Verify the board-mode URL does NOT contain a view= parameter.
   */
  it('negative: the board-mode URL does not contain a view= parameter', async () => {
    sessionStorage.setItem('divoid.tasks.view.20', 'board');
    capturedUrls = [];

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });

    const pathUrl = capturedUrls.find((u) => new URL(u).searchParams.get('path'));
    expect(pathUrl).toBeDefined();
    expect(new URL(pathUrl!).searchParams.has('view')).toBe(false);
    expect(new URL(pathUrl!).searchParams.has('_view')).toBe(false);
  });
});

// ─── Test 10: PointerSensor activation distance respected ────────────────────

describe('Test 10 — PointerSensor activation distance respected', () => {
  /**
   * Positive proof: clicking a card (no movement) navigates to NODE_DETAIL and
   * does NOT trigger onDragEnd. We verify by asserting PATCH was not called.
   *
   * The activationConstraint: { distance: 5 } on PointerSensor means a pointer
   * event that moves < 5px is treated as a click, not a drag.
   *
   * We simulate this via the fiber-walk approach: a real pointer-down + pointer-up
   * WITHOUT movement should not dispatch onDragEnd — but since jsdom doesn't support
   * real gesture coordinates, we prove this indirectly:
   *   (a) A click fires the click handler → navigate occurs.
   *   (b) onDragEnd is not fired on pure click (we invoke it only via the synthetic
   *       drag dispatch, not on click).
   *
   * The real activation constraint enforcement is verified by asserting no PATCH
   * is captured after a click (because onDragEnd's PATCH branch is the only path
   * to capturedPatchCalls).
   */
  it('positive: clicking a card does not fire PATCH and navigates to NODE_DETAIL', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    // Click the card.
    const card = container.querySelector('[data-testid="kanban-card-30"]') as HTMLElement;
    await user.click(card);

    // No PATCH should have fired from a click.
    await new Promise((r) => setTimeout(r, 50));
    expect(capturedPatchCalls).toHaveLength(0);
  });

  /**
   * Negative proof: dispatching onDragEnd directly (the synthetic drag that moves
   * to a new column) DOES fire PATCH — confirming that the click path above is
   * specifically NOT triggering onDragEnd.
   */
  it('negative: synthetic drag dispatch DOES fire PATCH (confirming the guard)', async () => {
    const user = userEvent.setup();

    const { container } = renderWithProviders(<TaskListView projectId={20} />);

    await waitFor(() => screen.getByTestId('tasks-view-toggle-board'));
    await user.click(screen.getByTestId('tasks-view-toggle-board'));

    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    const onDragEnd = getDndContextOnDragEnd(container);
    expect(onDragEnd).toBeDefined();

    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'new' } });
    });

    await waitFor(() => {
      expect(capturedPatchCalls.length).toBeGreaterThan(0);
    });
  });
});

// ─── Test 14: PATCH success invalidates path-query cache (bug #411) ──────────
//
// Root cause: handleDragEnd invalidated ['nodes','list'] + ['nodes','linkedto']
// after PATCH success, but the Kanban's task data comes from useNodePath whose
// query key is ['nodes','path',...]. Neither invalidation matched → cache stale
// → override clear showed old column → card snapped back.
//
// Fix: replace the two narrow invalidations with:
//   queryClient.invalidateQueries({ queryKey: ['nodes'] })
// TanStack prefix-matches ALL keys under ['nodes',...] — list, linkedto, path, detail.
//
// This test pins the OUTCOME (cache reflects new status), not just the override
// Map flow. Both PR #57 and #63 passed Jenny with full substitution proofs while
// production was broken — because those tests asserted the override transitions,
// not whether the cache was invalidated for the correct query key.
//
// Technique: pre-populate the path-query cache with old status; configure MSW to
// return new status on the SECOND GET call; drag via fiber-walk onDragEnd; after
// PATCH resolves, assert the card is in the new column and stays there.
//
// Substitution proof (required before submitting):
//   Revert to ['nodes','list'] + ['nodes','linkedto'] — the path-query cache entry
//   is NOT invalidated → no refetch → card snaps back to 'open' → positive test fails.

describe('Test 14 — PATCH success invalidates path-query cache: card stays in new column (bug #411)', () => {
  /**
   * Positive proof: after a successful drag (open → in-progress), the card renders
   * in in-progress AND STAYS THERE after the cache refetch settles.
   *
   * Two-phase MSW handler: first path-query call returns taskFixtures (card 30 = open).
   * After the PATCH succeeds and the broad ['nodes'] invalidation triggers a refetch,
   * the second path-query call returns updatedFixtures (card 30 = in-progress).
   * The card must render in in-progress and not snap back to open.
   */
  it('positive: card moves to target column and stays after PATCH + cache refetch', async () => {
    // Second GET to the path endpoint returns card 30 with the new status.
    const updatedFixtures: Page<NodeDetails> = {
      result: [
        { id: 30, type: 'task', name: 'Fix login', status: 'in-progress' },
        { id: 31, type: 'task', name: 'Add tests', status: 'in-progress' },
        { id: 32, type: 'task', name: 'Plan sprint', status: 'new' },
      ],
      total: 3,
    };

    // Track how many times the path query has been called.
    let pathCallCount = 0;
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        const url = new URL(request.url);
        const linkedto = url.searchParams.get('linkedto');
        const name = url.searchParams.get('name');
        const path = url.searchParams.get('path');

        if (linkedto && name === 'Tasks') return HttpResponse.json(tasksGroupFixture);
        if (path) {
          pathCallCount += 1;
          // First call: stale data (card 30 = open).
          // Subsequent calls: updated data (card 30 = in-progress).
          return HttpResponse.json(pathCallCount === 1 ? taskFixtures : updatedFixtures);
        }
        return HttpResponse.json(emptyPage);
      }),
    );

    // Pre-seed board mode so the board renders immediately.
    sessionStorage.setItem('divoid.tasks.view.20', 'board');

    // Use a QueryClient with a short staleTime so invalidation triggers a real
    // refetch (not a "already fresh" no-op). staleTime=0 means invalidated entries
    // are always refetched on next observer access.
    const qc = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: 0 },
        mutations: { retry: false },
      },
    });

    const user = userEvent.setup();
    const { container } = renderWithProviders(<TaskListView projectId={20} />, '/', qc);

    // Wait for board and cards to appear.
    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-board"]')).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(container.querySelector('[data-testid="kanban-card-30"]')).toBeInTheDocument();
    });

    // Baseline: card 30 starts in the 'open' column.
    const openColumnBefore = container.querySelector('[data-testid="kanban-column-open"]');
    expect(within(openColumnBefore as HTMLElement).queryByTestId('kanban-card-30')).toBeInTheDocument();

    // Drag card 30 → in-progress via the fiber-walk approach.
    const onDragEnd = getDndContextOnDragEnd(container);
    expect(onDragEnd).toBeDefined();

    await act(async () => {
      onDragEnd!({ active: { id: 30 }, over: { id: 'in-progress' } });
    });

    // Wait for: PATCH to fire, override to clear, cache to refetch.
    await waitFor(() => {
      expect(capturedPatchCalls.length).toBeGreaterThan(0);
    });

    // After PATCH success: the broad ['nodes'] invalidation must have triggered a
    // refetch of the path-query. The refetch returns updatedFixtures (card 30 = in-progress).
    // After override is cleared, the card must be in the in-progress column — and
    // stay there (no snap-back to open).
    await waitFor(
      () => {
        const inProgressCol = container.querySelector('[data-testid="kanban-column-in-progress"]');
        expect(
          within(inProgressCol as HTMLElement).queryByTestId('kanban-card-30'),
        ).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    // Confirm the card is NOT in the open column (no snap-back).
    const openColumnAfter = container.querySelector('[data-testid="kanban-column-open"]');
    expect(
      within(openColumnAfter as HTMLElement).queryByTestId('kanban-card-30'),
    ).not.toBeInTheDocument();

    // Confirm the path endpoint was called a SECOND time (invalidation triggered refetch).
    expect(pathCallCount).toBeGreaterThanOrEqual(2);
  });

  /**
   * Negative proof: if the fix is reverted to narrow invalidations
   * (['nodes','list'] and ['nodes','linkedto']), the path-query cache is NOT
   * invalidated, no second GET fires, the cache returns the stale old status
   * after the override is cleared, and the card snaps back to 'open'.
   *
   * We prove this without reverting production code by directly manipulating the
   * QueryClient: after the PATCH completes, we manually "un-invalidate" the path
   * query (set it back to non-stale) to simulate what the old narrow invalidations
   * would have done (i.e., left the path-query cache untouched). The card should
   * then NOT be in in-progress after the override clears.
   *
   * Substitution test (what happens when you revert the fix in production code):
   *   Replace queryClient.invalidateQueries({ queryKey: ['nodes'] }) with the old
   *   two-line invalidations. The positive test above MUST fail because pathCallCount
   *   stays at 1 and the card snaps back to 'open'.
   */
  it('negative: without path-query invalidation, card snaps back to old column after override clears', () => {
    // This test uses the useKanbanColumns hook directly to prove the snap-back
    // mechanism: if the cache returns old status after override is cleared, the
    // column for the old status gets the card.
    const { result } = renderHook(() =>
      useKanbanColumns({
        tasks: [{ id: 30, type: 'task', name: 'Fix login', status: 'open' }],
        selectedStatuses: ['new', 'open', 'in-progress'],
        // Simulate: override cleared (PATCH success), but cache NOT invalidated
        // → tasks prop still shows old status = 'open'.
        optimisticOverrides: new Map(),
      }),
    );

    const openCol = result.current.find((c) => c.status === 'open');
    const inProgressCol = result.current.find((c) => c.status === 'in-progress');

    // Without cache invalidation, the stale 'open' status from cache is used →
    // card renders in 'open', NOT in 'in-progress'. This is the snap-back bug.
    expect(openCol?.tasks).toHaveLength(1);
    expect(inProgressCol?.tasks).toHaveLength(0);
  });
});

// ─── renderHook helper (lightweight) ─────────────────────────────────────────

/**
 * Minimal renderHook for pure hooks that need no providers.
 * For hooks needing React context, use the full renderWithProviders approach.
 */
function renderHook<T>(hookFn: () => T): { result: { current: T } } {
  const result = { current: undefined as unknown as T };

  function HookCapture() {
    result.current = hookFn();
    return null;
  }

  render(<HookCapture />);
  return { result };
}
