/**
 * RetypeNodeDialog tests — load-bearing per DiVoid #275.
 *
 * Covers:
 *  T1  Happy-path retype: user enters a new type name; PATCH body contains
 *      { op:'replace', path:'/type', value:'documentation' }.
 *  T2  Retype to untyped: user selects "(untyped)"; PATCH value is ''.
 *  T3  No-op: user submits the same type that is already set; no PATCH issued.
 *  T4  Warning visible when node has status AND target type not in allowlist.
 *  T5  No warning when target type IS in allowlist (task/bug).
 *  T6  No warning when node has NO status, even targeting a non-lifecycle type.
 *  T7  Warning disappears when user changes selection back to a lifecycle type.
 *  T8  shouldWarnOnRetype — pure-function unit tests covering all heuristic branches
 *      (T8a–T8f: status branch; T8g–T8h: severity branch per design #2014 §9.1).
 *  T9  toApiValue — unit tests for the form-value → API-value conversion.
 *
 * Load-bearing substitution proof per contract §13.1:
 *
 *  T1: revert the `{ op:'replace', path:'/type' }` push inside onSubmit.
 *      The `capturedBody` assertion fails — no /type op in the array.
 *
 *  T2: revert the UNTYPED_DISPLAY branch inside toApiValue.
 *      The patch value becomes '(untyped)' instead of '' → T2 assertion fails.
 *
 *  T3: revert the `if (apiValue === currentApiValue)` early-return.
 *      A PATCH is issued with an empty ops array → capturedBody is [] not null,
 *      but waitFor(capturedBody !== null) would time out because the spy records
 *      nothing meaningful — see assertion shape.
 *
 *  T4: revert `shouldWarnOnRetype` to return `false` always.
 *      The warning alert disappears from the DOM → findByRole('alert') rejects.
 *
 *  T5: revert the `!targetBearsCycle` condition to `true` always.
 *      Warning shows even for 'task' → T5 fails with "expected null, got element".
 *
 *  T6: revert the `node.status != null` guard to `true` always.
 *      Warning shows even when status is null → T6 fails.
 *
 *  T7: T7 depends on T4/T5/T6 logic being correct, validated by their tests.
 *
 *  T8g: severity set + status null + non-lifecycle target → warns.
 *       Load-bearing: remove `|| node.severity != null` from hasLifecycleState.
 *       T8g assertion `toBe(true)` becomes `toBe(false)` → fails.
 *
 *  T8h: severity set + lifecycle target → does NOT warn.
 *       Load-bearing: change `!targetBearsCycle` to `true` always.
 *       T8h assertion `toBe(false)` becomes `toBe(true)` → fails.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { NodeDetails, PatchOperation } from '@/types/divoid';
import { BASE_URL } from '@/test/msw/handlers';
import { shouldWarnOnRetype, toApiValue, UNTYPED_DISPLAY } from './RetypeNodeDialog';

const taskNode: NodeDetails = {
  id: 42,
  type: 'task',
  name: 'My Task',
  status: 'open',
  ownerId: 1,
  access: 'Read, Write',
};

const docNode: NodeDetails = {
  id: 55,
  type: 'documentation',
  name: 'A Doc',
  status: null,
  ownerId: 1,
  access: 'Read, Write',
};

const statusDocNode: NodeDetails = {
  ...docNode,
  status: 'open',
};

const typeCatalog = {
  result: [
    { id: 6, type: 'task', count: 10 },
    { id: 8, type: 'documentation', count: 5 },
    { id: 11, type: 'bug', count: 2 },
  ],
  total: 3,
};

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1,
      name: 'Toni',
      email: 'toni@mamgo.io',
      enabled: true,
      createdAt: '2026-01-01T00:00:00Z',
      permissions: ['read', 'write'],
    }),
  ),
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
  http.get(`${BASE_URL}/types`, () => HttpResponse.json(typeCatalog)),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.clearAllMocks();
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

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }));

let RetypeNodeDialog: typeof import('./RetypeNodeDialog').RetypeNodeDialog;

beforeAll(async () => {
  const mod = await import('./RetypeNodeDialog');
  RetypeNodeDialog = mod.RetypeNodeDialog;
});

function renderDialogSync(node: NodeDetails) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={qc}>
      <RetypeNodeDialog open onOpenChange={vi.fn()} node={node} />
    </QueryClientProvider>,
  );
}

describe('RetypeNodeDialog — PATCH body shape', () => {
  it('T1: submitting a new type name sends { op:replace, path:/type, value:documentation }', async () => {
    const user = userEvent.setup();
    let capturedBody: PatchOperation[] | null = null;

    server.use(
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedBody = (await request.json()) as PatchOperation[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDialogSync(taskNode);

    const input = await screen.findByLabelText('Type');
    await user.clear(input);
    await user.type(input, 'documentation');
    await user.click(screen.getByRole('button', { name: /change type/i }));

    await waitFor(() => expect(capturedBody).not.toBeNull());
    expect(capturedBody).toContainEqual({ op: 'replace', path: '/type', value: 'documentation' });
  });

  it('T2: selecting "(untyped)" sends value:""', async () => {
    const user = userEvent.setup();
    let capturedBody: PatchOperation[] | null = null;

    server.use(
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedBody = (await request.json()) as PatchOperation[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDialogSync(taskNode);

    const input = await screen.findByLabelText('Type');
    await user.clear(input);
    await user.type(input, UNTYPED_DISPLAY);
    await user.click(screen.getByRole('button', { name: /change type/i }));

    await waitFor(() => expect(capturedBody).not.toBeNull());
    expect(capturedBody).toContainEqual({ op: 'replace', path: '/type', value: '' });
  });

  it('T3: submitting the same type as current issues no PATCH', async () => {
    const user = userEvent.setup();
    let patchCalled = false;

    server.use(
      http.patch(`${BASE_URL}/nodes/42`, () => {
        patchCalled = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const onOpenChange = vi.fn();
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
      <QueryClientProvider client={qc}>
        <RetypeNodeDialog open onOpenChange={onOpenChange} node={taskNode} />
      </QueryClientProvider>,
    );

    await screen.findByLabelText('Type');
    await user.click(screen.getByRole('button', { name: /change type/i }));

    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
    expect(patchCalled).toBe(false);
  });
});

describe('RetypeNodeDialog — soft warning heuristic', () => {
  it('T4: warning visible when node has status and target type is outside allowlist', async () => {
    const user = userEvent.setup();

    renderDialogSync(statusDocNode);

    const input = await screen.findByLabelText('Type');
    await user.clear(input);
    await user.type(input, 'project');

    await screen.findByRole('alert');
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });

  it('T5: no warning when target type IS in allowlist (task)', async () => {
    const user = userEvent.setup();

    renderDialogSync(statusDocNode);

    const input = await screen.findByLabelText('Type');
    await user.clear(input);
    await user.type(input, 'task');

    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  });

  it('T6: no warning when node has no status', async () => {
    const user = userEvent.setup();

    renderDialogSync(docNode);

    const input = await screen.findByLabelText('Type');
    await user.clear(input);
    await user.type(input, 'project');

    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  });

  it('T7: warning disappears after user switches back to a lifecycle-bearing type', async () => {
    const user = userEvent.setup();

    renderDialogSync(statusDocNode);

    const input = await screen.findByLabelText('Type');
    await user.clear(input);
    await user.type(input, 'project');

    await screen.findByRole('alert');

    await user.clear(input);
    await user.type(input, 'bug');

    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  });
});

describe('shouldWarnOnRetype — pure function', () => {
  it('T8a: status set + non-lifecycle target → true', () => {
    expect(shouldWarnOnRetype({ ...taskNode, status: 'open' }, 'documentation')).toBe(true);
  });

  it('T8b: status set + lifecycle target (task) → false', () => {
    expect(shouldWarnOnRetype({ ...taskNode, status: 'open' }, 'task')).toBe(false);
  });

  it('T8c: status set + lifecycle target (bug) → false', () => {
    expect(shouldWarnOnRetype({ ...taskNode, status: 'open' }, 'bug')).toBe(false);
  });

  it('T8d: status null + non-lifecycle target → false', () => {
    expect(shouldWarnOnRetype({ ...taskNode, status: null }, 'documentation')).toBe(false);
  });

  it('T8e: status null + lifecycle target → false', () => {
    expect(shouldWarnOnRetype({ ...taskNode, status: null }, 'task')).toBe(false);
  });

  it('T8f: status set + empty string target (untyped) → true', () => {
    expect(shouldWarnOnRetype({ ...taskNode, status: 'open' }, '')).toBe(true);
  });

  it('T8g: severity set, status null, non-lifecycle target → true (severity counts as lifecycle state)', () => {
    expect(shouldWarnOnRetype({ ...docNode, status: null, severity: 2 }, 'documentation')).toBe(true);
  });

  it('T8h: severity set, lifecycle target (bug) → false (target bears lifecycle)', () => {
    expect(shouldWarnOnRetype({ ...docNode, status: null, severity: 2 }, 'bug')).toBe(false);
  });
});

describe('toApiValue — pure function', () => {
  it('T9a: UNTYPED_DISPLAY → empty string', () => {
    expect(toApiValue(UNTYPED_DISPLAY)).toBe('');
  });

  it('T9b: "__untyped__" sentinel → empty string', () => {
    expect(toApiValue('__untyped__')).toBe('');
  });

  it('T9c: real type name passthrough', () => {
    expect(toApiValue('documentation')).toBe('documentation');
  });

  it('T9d: whitespace-padded value is trimmed', () => {
    expect(toApiValue('  task  ')).toBe('task');
  });

  it('T9e: already empty string stays empty', () => {
    expect(toApiValue('')).toBe('');
  });
});
