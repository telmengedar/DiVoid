/**
 * Tests for useNodeSemantic.
 *
 * Covers:
 *  - Happy path: results returned with similarity scores.
 *  - Disabled when query is empty.
 *  - Error path: DivoidApiError thrown on non-2xx.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, semanticPage } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('query')) {
      return HttpResponse.json(semanticPage);
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

describe('useNodeSemantic', () => {
  it('returns nodes with similarity scores on happy path', async () => {
    const { useNodeSemantic } = await import('./useNodeSemantic');
    const { result } = renderHook(() => useNodeSemantic('auth token validation'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.result).toHaveLength(2);
    expect(result.current.data?.result[0].similarity).toBe(0.92);
  });

  it('is disabled when query is empty string', async () => {
    const { useNodeSemantic } = await import('./useNodeSemantic');
    const { result } = renderHook(() => useNodeSemantic(''), {
      wrapper: createWrapper(),
    });

    expect(result.current.isFetching).toBe(false);
    expect(result.current.data).toBeUndefined();
  });

  it('is disabled when query is only whitespace', async () => {
    const { useNodeSemantic } = await import('./useNodeSemantic');
    const { result } = renderHook(() => useNodeSemantic('   '), {
      wrapper: createWrapper(),
    });

    expect(result.current.isFetching).toBe(false);
    expect(result.current.data).toBeUndefined();
  });

  it('throws DivoidApiError on 500', async () => {
    server.use(
      http.get(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ code: 'internal', text: 'Server error' }, { status: 500 }),
      ),
    );

    const { useNodeSemantic } = await import('./useNodeSemantic');
    const { result } = renderHook(() => useNodeSemantic('broken query'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    const err = result.current.error as unknown as { status: number; code: string; text: string };
    expect(err.status).toBe(500);
    expect(err.code).toBe('internal');
  });
});
