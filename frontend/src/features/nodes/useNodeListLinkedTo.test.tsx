/**
 * Tests for useNodeListLinkedTo.
 *
 * Covers:
 *  - Happy path: returns one-hop neighbours.
 *  - Disabled when linkedToId is 0.
 *  - Error path: DivoidApiError on non-2xx.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, samplePage } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    const linkedto = url.searchParams.get('linkedto');
    if (linkedto) {
      return HttpResponse.json(samplePage);
    }
    return HttpResponse.json({ result: [], total: 0 });
  }),
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
  })),
}));

vi.mock('@/lib/constants', () => ({
  API_BASE_URL: BASE_URL,
  API: {
    USERS: { ME: '/users/me' },
    NODES: {
      LIST: '/nodes',
      PATH: '/nodes/path',
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

// ─── Wrapper ──────────────────────────────────────────────────────────────────

function createWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('useNodeListLinkedTo', () => {
  it('returns neighbours on happy path', async () => {
    const { useNodeListLinkedTo } = await import('./useNodeListLinkedTo');
    const { result } = renderHook(() => useNodeListLinkedTo(3), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.result).toHaveLength(3);
    expect(result.current.data?.total).toBe(3);
  });

  it('is disabled when linkedToId is 0', async () => {
    const { useNodeListLinkedTo } = await import('./useNodeListLinkedTo');
    const { result } = renderHook(() => useNodeListLinkedTo(0), {
      wrapper: createWrapper(),
    });

    expect(result.current.isFetching).toBe(false);
    expect(result.current.data).toBeUndefined();
  });

  it('throws DivoidApiError on non-2xx', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ code: 'forbidden', text: 'Access denied' }, { status: 403 }),
      ),
    );

    const { useNodeListLinkedTo } = await import('./useNodeListLinkedTo');
    const { result } = renderHook(() => useNodeListLinkedTo(99), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    const err = result.current.error as { status: number; code: string };
    expect(err.status).toBe(403);
    expect(err.code).toBe('forbidden');
  });
});
