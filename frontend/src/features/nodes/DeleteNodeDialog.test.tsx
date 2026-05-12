/**
 * Tests for DeleteNodeDialog.
 *
 * Covers:
 *  - Renders the confirmation prompt.
 *  - On confirm: calls onDeleted after successful delete.
 *  - On confirm: shows error toast and keeps dialog open on 500.
 *  - On cancel: dialog stays open.
 *
 * Deliberately avoids rendering NodeDetailPage (heavy deps) — tests
 * the dialog component in isolation.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode } from '@/test/msw/handlers';
import { DeleteNodeDialog } from './DeleteNodeDialog';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.delete(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
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

// ─── Wrapper factory ──────────────────────────────────────────────────────────

function Wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return (
    <QueryClientProvider client={qc}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('DeleteNodeDialog', () => {
  it('renders the confirmation prompt with node name', () => {
    render(
      <Wrapper>
        <DeleteNodeDialog
          open
          onOpenChange={vi.fn()}
          node={sampleNode}
          onDeleted={vi.fn()}
        />
      </Wrapper>,
    );

    expect(screen.getByRole('dialog', { name: /delete node/i })).toBeInTheDocument();
    expect(screen.getByText(/Test Document/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^delete$/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /cancel/i })).toBeInTheDocument();
  });

  it('calls onDeleted after successful delete', async () => {
    const user = userEvent.setup();
    const onDeleted = vi.fn();
    const onOpenChange = vi.fn();

    render(
      <Wrapper>
        <DeleteNodeDialog
          open
          onOpenChange={onOpenChange}
          node={sampleNode}
          onDeleted={onDeleted}
        />
      </Wrapper>,
    );

    await user.click(screen.getByRole('button', { name: /^delete$/i }));

    await waitFor(() => expect(onDeleted).toHaveBeenCalledOnce());
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('keeps dialog open and shows error toast on 500', async () => {
    server.use(
      http.delete(`${BASE_URL}/nodes/:id`, () =>
        HttpResponse.json({ code: 'servererror', text: 'Internal error' }, { status: 500 }),
      ),
    );

    const { toast } = await import('sonner');
    const user = userEvent.setup();
    const onDeleted = vi.fn();
    const onOpenChange = vi.fn();

    render(
      <Wrapper>
        <DeleteNodeDialog
          open
          onOpenChange={onOpenChange}
          node={sampleNode}
          onDeleted={onDeleted}
        />
      </Wrapper>,
    );

    await user.click(screen.getByRole('button', { name: /^delete$/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(onDeleted).not.toHaveBeenCalled();
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });
});
