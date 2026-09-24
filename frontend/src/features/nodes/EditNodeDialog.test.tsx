/**
 * EditNodeDialog tests — load-bearing per DiVoid #275.
 *
 * Covers:
 *  - Owner can edit Access: select changes to 'None', PATCH body contains
 *    `{op:'replace', path:'/access', value:'None'}`.
 *  - Admin (non-owner) can edit Access on someone else's node: same PATCH shape.
 *  - Non-owner non-admin sees no Access selector at all.
 *
 * Load-bearing substitution proof per contract §13.1 and §13.7:
 *
 *  T1 (owner edits): revert the `canEditAccess` guard block inside `onSubmit`
 *     (the `if (canEditAccess && values.access !== undefined && ...)` branch).
 *     The PATCH body contains no `/access` op → `capturedBody` assertion fails.
 *
 *  T2 (admin edits): same revert; plus if `canEditAccess` logic were wired to
 *     `canWrite` instead of owner/admin, admin on someone else's node would still
 *     pass but a non-owner write-user would also see the selector, breaking T3.
 *
 *  T3 (non-owner non-admin sees no selector): revert the `{canEditAccess && (...)}` JSX
 *     guard in EditNodeDialog. The select renders → `queryByLabelText` returns
 *     an element instead of null → T3 fails with a concrete assertion.
 *
 * See also: NodeAuthorization.BuildOwnerPredicate (the backend gate this mirrors).
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { NodeDetails, PatchOperation } from '@/types/divoid';
import { BASE_URL } from '@/test/msw/handlers';

/** Node owned by user id 1 (matches the owner fixture user). */
const ownerNode: NodeDetails = {
  id: 42,
  type: 'documentation',
  name: 'Test Document',
  status: 'open',
  contentType: 'text/markdown; charset=utf-8',
  ownerId: 1,
  access: 'Read, Write',
};

/** Node owned by user id 99 — a different user than the logged-in ones. */
const otherOwnerNode: NodeDetails = {
  ...ownerNode,
  ownerId: 99,
};

/** User who is the node owner (id: 1, write but not admin). */
const ownerUser = {
  id: 1,
  name: 'Toni',
  email: 'toni@mamgo.io',
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['read', 'write'],
};

/** User who is admin but NOT the node owner (id: 2). */
const adminUser = {
  id: 2,
  name: 'Admin',
  email: 'admin@mamgo.io',
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['read', 'write', 'admin'],
};

/** User who is neither owner nor admin (id: 3). */
const nonOwnerNonAdminUser = {
  id: 3,
  name: 'Bob',
  email: 'bob@example.com',
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['read', 'write'],
};

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(ownerUser)),
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
  http.get(`${BASE_URL}/nodes/:id`, () => HttpResponse.json(ownerNode)),
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

function renderDialog(node: NodeDetails) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={qc}>
      <EditNodeDialog open onOpenChange={vi.fn()} node={node} />
    </QueryClientProvider>,
  );
}

let EditNodeDialog: typeof import('./EditNodeDialog').EditNodeDialog;

beforeAll(async () => {
  const mod = await import('./EditNodeDialog');
  EditNodeDialog = mod.EditNodeDialog;
});

describe('EditNodeDialog — Access field', () => {
  it('T1: owner sees the Access selector and submitted PATCH body contains /access op', async () => {
    const user = userEvent.setup();

    let capturedBody: PatchOperation[] | null = null;
    server.use(
      http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(ownerUser)),
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedBody = await request.json() as PatchOperation[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDialog(ownerNode);

    await waitFor(() => {
      expect(screen.getByLabelText('Access')).toBeInTheDocument();
    });

    await user.selectOptions(screen.getByLabelText('Access'), 'None');

    await user.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });

    expect(capturedBody).toContainEqual({ op: 'replace', path: '/access', value: 'None' });
  });

  it('T2: admin (non-owner) sees the Access selector and can submit /access PATCH op', async () => {
    const user = userEvent.setup();

    let capturedBody: PatchOperation[] | null = null;
    server.use(
      http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(adminUser)),
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedBody = await request.json() as PatchOperation[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDialog(otherOwnerNode);

    await waitFor(() => {
      expect(screen.getByLabelText('Access')).toBeInTheDocument();
    });

    await user.selectOptions(screen.getByLabelText('Access'), 'Read');

    await user.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });

    expect(capturedBody).toContainEqual({ op: 'replace', path: '/access', value: 'Read' });
  });

  it('T3: non-owner non-admin sees no Access selector', async () => {
    server.use(
      http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(nonOwnerNonAdminUser)),
    );

    renderDialog(otherOwnerNode);

    // We assert something else IS visible (the Name field) so the absence of
    // the Access selector is a real absence, not a load-timing false negative.
    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: /name/i })).toBeInTheDocument();
    });

    expect(screen.queryByLabelText('Access')).toBeNull();
  });
});

/**
 * Refinement field tests (DiVoid #14811) — load-bearing per contract §13.1.
 *
 * Substitution proof:
 *
 *  T4 (free-text edit produces a PATCH op): revert the `newRefinement !== ...`
 *     block inside `onSubmit`. The PATCH body carries no `/refinement` op →
 *     `capturedBody` assertion fails with a concrete mismatch.
 *
 *  T5 (shown for a type with no status vocabulary): revert the JSX so the
 *     Refinement input is wrapped in `{statusOptions && (...)}` alongside
 *     Status. For a `documentation`-typed node (no status dropdown), the
 *     input would disappear → `getByLabelText('Refinement')` throws. This is
 *     the concrete pin for "refinement is not type-gated like status" (#14810).
 *
 *  T6 (open vocabulary — no allow-list): typing a value the schema/UI has
 *     never seen still reaches the PATCH body verbatim. There is no enum in
 *     `editNodeSchema.refinement` to violate, so this is the regression
 *     trip-wire for anyone later adding one.
 */
describe('EditNodeDialog — Refinement field (DiVoid #14811)', () => {
  it('T5: Refinement input is shown for a type with no status vocabulary (not type-gated)', async () => {
    const documentationNode: NodeDetails = {
      id: 42,
      type: 'documentation',
      name: 'Test Document',
      status: null,
      ownerId: 1,
    };
    server.use(http.get(`${BASE_URL}/nodes/42`, () => HttpResponse.json(documentationNode)));

    renderDialog(documentationNode);

    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: /name/i })).toBeInTheDocument();
    });

    expect(screen.queryByLabelText('Status')).toBeNull();
    expect(screen.getByLabelText('Refinement')).toBeInTheDocument();
  });

  it('T4/T6: submitting a novel free-text refinement value PATCHes /refinement verbatim', async () => {
    const user = userEvent.setup();

    let capturedBody: PatchOperation[] | null = null;
    server.use(
      http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(ownerUser)),
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedBody = await request.json() as PatchOperation[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDialog(ownerNode);

    await waitFor(() => {
      expect(screen.getByLabelText('Refinement')).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText('Refinement'), 'a-value-nobody-coined-yet');
    await user.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });

    expect(capturedBody).toContainEqual({
      op: 'replace',
      path: '/refinement',
      value: 'a-value-nobody-coined-yet',
    });
  });

  it('clearing an existing refinement PATCHes /refinement with null (unclassified)', async () => {
    const user = userEvent.setup();
    const refinedNode: NodeDetails = { ...ownerNode, refinement: 'ready' };

    let capturedBody: PatchOperation[] | null = null;
    server.use(
      http.get(`${BASE_URL}/nodes/42`, () => HttpResponse.json(refinedNode)),
      http.patch(`${BASE_URL}/nodes/42`, async ({ request }) => {
        capturedBody = await request.json() as PatchOperation[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDialog(refinedNode);

    await waitFor(() => {
      expect(screen.getByLabelText('Refinement')).toHaveValue('ready');
    });

    await user.clear(screen.getByLabelText('Refinement'));
    await user.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });

    expect(capturedBody).toContainEqual({ op: 'replace', path: '/refinement', value: null });
  });
});
