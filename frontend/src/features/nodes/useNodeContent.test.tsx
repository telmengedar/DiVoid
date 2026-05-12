/**
 * Tests for useNodeContent.
 *
 * Verifies that useNodeContent routes through apiClient.fetchRaw() and
 * therefore inherits the §6.3 reactive-401 contract:
 *
 *  - Happy path: returns text content.
 *  - Disabled when id is 0.
 *  - 401 + signinSilent succeeds → retry returns content, user sees no error,
 *    signinRedirect is NOT called.
 *  - 401 + signinSilent rejects → signinRedirect called exactly once.
 *  - 401 twice (retry also 401s) → signinRedirect called, no third attempt.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';

// ─── Hoisted mock refs ────────────────────────────────────────────────────────
// vi.mock() is hoisted to before imports, so we use vi.hoisted() to define
// mutable refs that both the mock factory and tests can access.

const { mockSigninSilent, mockSigninRedirect, mockGetToken } = vi.hoisted(() => {
  const mockSigninSilent = vi.fn<() => Promise<void>>(async () => { return; });
  const mockSigninRedirect = vi.fn<() => void>(() => undefined);
  const mockGetToken = vi.fn<() => string | undefined>(() => 'valid-token');
  return { mockSigninSilent, mockSigninRedirect, mockGetToken };
});

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    get user() {
      const t = mockGetToken();
      return t ? { access_token: t } : undefined;
    },
    signinSilent: mockSigninSilent,
    signinRedirect: mockSigninRedirect,
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

// ─── MSW server ───────────────────────────────────────────────────────────────

const CONTENT_URL = `${BASE_URL}/nodes/:id/content`;

const server = setupServer(
  http.get(CONTENT_URL, ({ request, params }) => {
    const id = parseInt(params.id as string, 10);
    const auth = request.headers.get('Authorization');
    if (!auth?.startsWith('Bearer valid-')) {
      return HttpResponse.json({ code: 'unauthorized', text: 'bad token' }, { status: 401 });
    }
    if (id === 42) {
      return new HttpResponse('# Hello markdown', {
        status: 200,
        headers: { 'Content-Type': 'text/markdown' },
      });
    }
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.clearAllMocks();
  // Reset to authenticated defaults after each test
  mockGetToken.mockReturnValue('valid-token');
  mockSigninSilent.mockResolvedValue(undefined);
  mockSigninRedirect.mockReturnValue(undefined);
});
afterAll(() => server.close());

// ─── Wrapper ──────────────────────────────────────────────────────────────────

function createWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('useNodeContent', () => {
  it('returns text content on happy path', async () => {
    mockGetToken.mockReturnValue('valid-token');

    const { useNodeContent } = await import('./useNodeContent');
    const { result } = renderHook(() => useNodeContent(42), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBe('# Hello markdown');
  });

  it('is disabled when id is 0', async () => {
    mockGetToken.mockReturnValue('valid-token');

    const { useNodeContent } = await import('./useNodeContent');
    const { result } = renderHook(() => useNodeContent(0), { wrapper: createWrapper() });

    expect(result.current.isFetching).toBe(false);
    expect(result.current.data).toBeUndefined();
  });

  it('§6.3: signinSilent called on 401; retry succeeds; signinRedirect NOT called', async () => {
    // Start with no token → 401.  After signinSilent resolves, token is updated → retry succeeds.
    mockGetToken.mockReturnValueOnce(undefined);
    mockSigninSilent.mockImplementation(async () => {
      mockGetToken.mockReturnValue('valid-refreshed');
    });

    const { useNodeContent } = await import('./useNodeContent');
    const { result } = renderHook(() => useNodeContent(42), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toBe('# Hello markdown');
    expect(mockSigninSilent).toHaveBeenCalledOnce();
    expect(mockSigninRedirect).not.toHaveBeenCalled();
  });

  it('§6.3: signinRedirect called when signinSilent rejects', async () => {
    mockGetToken.mockReturnValue(undefined);
    mockSigninSilent.mockRejectedValue(new Error('refresh token expired'));

    const { useNodeContent } = await import('./useNodeContent');
    const { result } = renderHook(() => useNodeContent(42), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(mockSigninSilent).toHaveBeenCalledOnce();
    expect(mockSigninRedirect).toHaveBeenCalledOnce();
  });

  it('§6.3: signinRedirect called after retry also 401s (no-loop guarantee)', async () => {
    // Server always 401s regardless of token; signinSilent resolves but server still rejects.
    let fetchCount = 0;
    server.use(
      http.get(CONTENT_URL, () => {
        fetchCount++;
        return HttpResponse.json({ code: 'unauthorized', text: 'still bad' }, { status: 401 });
      }),
    );

    mockGetToken.mockReturnValue(undefined);
    mockSigninSilent.mockResolvedValue(undefined); // resolves but server still rejects

    const { useNodeContent } = await import('./useNodeContent');
    const { result } = renderHook(() => useNodeContent(42), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(mockSigninSilent).toHaveBeenCalledOnce();
    expect(mockSigninRedirect).toHaveBeenCalledOnce();
    // Exactly 2 fetches: original + one retry. No third attempt.
    expect(fetchCount).toBe(2);
  });
});
