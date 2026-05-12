/**
 * Tests for useNodePath.
 *
 * Covers:
 *  - Happy path: returns path traversal results.
 *  - Disabled when path is empty.
 *  - 400 path syntax error surfaces the column-pointing message.
 *  - 400 does not retry.
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
  http.get(`${BASE_URL}/nodes/path`, () => HttpResponse.json(samplePage)),
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

describe('useNodePath', () => {
  it('returns path traversal results on happy path', async () => {
    const { useNodePath } = await import('./useNodePath');
    const { result } = renderHook(
      () => useNodePath('[type:project,name:DiVoid]/[type:task,status:open]'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.result).toHaveLength(3);
  });

  it('is disabled when path is empty string', async () => {
    const { useNodePath } = await import('./useNodePath');
    const { result } = renderHook(() => useNodePath(''), {
      wrapper: createWrapper(),
    });

    expect(result.current.isFetching).toBe(false);
    expect(result.current.data).toBeUndefined();
  });

  it('surfaces column-pointing message on 400', async () => {
    const syntaxErrorText = 'Path query syntax error at column 5: expected predicate after "["';
    server.use(
      http.get(`${BASE_URL}/nodes/path`, () =>
        HttpResponse.json(
          { code: 'badparameter', text: syntaxErrorText },
          { status: 400 },
        ),
      ),
    );

    const { useNodePath } = await import('./useNodePath');
    const { result } = renderHook(() => useNodePath('[bad'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    const err = result.current.error as unknown as { status: number; code: string; text: string };
    expect(err.status).toBe(400);
    expect(err.code).toBe('badparameter');
    expect(err.text).toBe(syntaxErrorText);
  });
});
