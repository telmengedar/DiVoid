/**
 * Tests for CreateNodeDialog.
 *
 * Covers:
 *  - Renders correctly when open.
 *  - Shows status dropdown only for task/bug types.
 *  - Submits the form and calls onCreated with the returned id (typed create).
 *  - Untyped create: submitting with no type succeeds and omits the `type`
 *    field from the POST body (DiVoid #2011 / design #2014).
 *  - Validation: blocks submit on empty name; type is no longer required.
 *  - Error: mutation error does not close the dialog.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode } from '@/test/msw/handlers';
import { CreateNodeDialog } from './CreateNodeDialog';

let lastPostBody: unknown = undefined;

const server = setupServer(
  http.post(`${BASE_URL}/nodes`, async ({ request }) => {
    lastPostBody = await request.json();
    return HttpResponse.json(sampleNode, { status: 201 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  lastPostBody = undefined;
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
    CALLBACK: '/callback',
    LOGOUT: '/logout',
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

function Wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

describe('CreateNodeDialog', () => {
  it('renders the form when open', () => {
    const onOpenChange = vi.fn();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={onOpenChange} onCreated={vi.fn()} />
      </Wrapper>,
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByLabelText(/type/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/name/i)).toBeInTheDocument();
    // Status should NOT be visible until type is task/bug
    expect(screen.queryByLabelText(/status/i)).not.toBeInTheDocument();
  });

  it('shows status dropdown when type is "task"', async () => {
    const user = userEvent.setup();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={vi.fn()} onCreated={vi.fn()} />
      </Wrapper>,
    );

    await user.type(screen.getByLabelText(/type/i), 'task');
    expect(screen.getByLabelText(/status/i)).toBeInTheDocument();
  });

  it('shows status dropdown when type is "bug"', async () => {
    const user = userEvent.setup();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={vi.fn()} onCreated={vi.fn()} />
      </Wrapper>,
    );

    await user.type(screen.getByLabelText(/type/i), 'bug');
    expect(screen.getByLabelText(/status/i)).toBeInTheDocument();
  });

  it('does not show status dropdown for other types', async () => {
    const user = userEvent.setup();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={vi.fn()} onCreated={vi.fn()} />
      </Wrapper>,
    );

    await user.type(screen.getByLabelText(/type/i), 'documentation');
    expect(screen.queryByLabelText(/status/i)).not.toBeInTheDocument();
  });

  it('calls onCreated with new node id on successful typed submit', async () => {
    const user = userEvent.setup();
    const onCreated = vi.fn();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={vi.fn()} onCreated={onCreated} />
      </Wrapper>,
    );

    await user.type(screen.getByLabelText(/type/i), 'documentation');
    await user.type(screen.getByLabelText(/name/i), 'My new doc');
    await user.click(screen.getByRole('button', { name: /create/i }));

    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(42));
    expect((lastPostBody as Record<string, unknown>).type).toBe('documentation');
    expect((lastPostBody as Record<string, unknown>).name).toBe('My new doc');
  });

  it(
    'allows submit with no type and omits `type` from POST body — ' +
    'LOAD-BEARING: reverting the conditional spread in onSubmit sends type:"" to the backend; ' +
    're-adding .min(1) to the schema blocks submit instead of allowing untyped create (DiVoid #2011)',
    async () => {
      const user = userEvent.setup();
      const onCreated = vi.fn();
      render(
        <Wrapper>
          <CreateNodeDialog open onOpenChange={vi.fn()} onCreated={onCreated} />
        </Wrapper>,
      );

      await user.type(screen.getByLabelText(/name/i), 'Untyped node');
      await user.click(screen.getByRole('button', { name: /create/i }));

      await waitFor(() => expect(onCreated).toHaveBeenCalledWith(42));
      expect(lastPostBody as Record<string, unknown>).not.toHaveProperty('type');
      expect((lastPostBody as Record<string, unknown>).name).toBe('Untyped node');
    },
  );

  it('shows validation error when name is empty', async () => {
    const user = userEvent.setup();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={vi.fn()} onCreated={vi.fn()} />
      </Wrapper>,
    );

    await user.click(screen.getByRole('button', { name: /create/i }));

    await waitFor(() =>
      expect(screen.getByText(/name is required/i)).toBeInTheDocument(),
    );
  });

  it('keeps dialog open and shows error toast on server error', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ code: 'servererror', text: 'Internal error' }, { status: 500 }),
      ),
    );

    const { toast } = await import('sonner');
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    render(
      <Wrapper>
        <CreateNodeDialog open onOpenChange={onOpenChange} onCreated={vi.fn()} />
      </Wrapper>,
    );

    await user.type(screen.getByLabelText(/type/i), 'documentation');
    await user.type(screen.getByLabelText(/name/i), 'My doc');
    await user.click(screen.getByRole('button', { name: /create/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    // Dialog should remain open
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });
});
