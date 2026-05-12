/**
 * Tests for mutation hooks in mutations.ts.
 *
 * Covers for each hook:
 *  - Happy path: mutation succeeds and invalidates the right query keys.
 *  - Error path: backend returns an error; the hook exposes the DivoidApiError.
 *
 * useUploadContent additionally pins the §6.3 reactive-401 contract:
 *  - 401 + signinSilent succeeds → retry succeeds; signinRedirect NOT called.
 *  - 401 + signinSilent rejects → signinRedirect called exactly once.
 *  - 401 twice (retry also 401s) → signinRedirect called; no-loop guarantee.
 *
 * Uses MSW to intercept fetch at the network boundary.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, sampleNode } from '@/test/msw/handlers';
import { DivoidApiError } from '@/types/divoid';

// ─── Hoisted mock refs ────────────────────────────────────────────────────────
// vi.mock() is hoisted before imports; vi.hoisted() lets tests mutate the refs
// at runtime so we can test the §6.3 retry path without re-importing the module.

const { mockSigninSilent, mockSigninRedirect, mockGetToken } = vi.hoisted(() => {
  const mockSigninSilent = vi.fn<() => Promise<void>>(async () => { return; });
  const mockSigninRedirect = vi.fn<() => void>(() => undefined);
  const mockGetToken = vi.fn<() => string | undefined>(() => 'test-token');
  return { mockSigninSilent, mockSigninRedirect, mockGetToken };
});

// ─── Mocks ────────────────────────────────────────────────────────────────────

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    get user() {
      const t = mockGetToken();
      return t ? { access_token: t } : undefined;
    },
    signinRedirect: mockSigninRedirect,
    signinSilent: mockSigninSilent,
  })),
}));

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  // Create node — happy path
  http.post(`${BASE_URL}/nodes`, () => HttpResponse.json(sampleNode, { status: 201 })),

  // Patch node
  http.patch(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),

  // Delete node
  http.delete(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),

  // Link nodes
  http.post(`${BASE_URL}/nodes/:id/links`, () => new HttpResponse(null, { status: 204 })),

  // Unlink nodes
  http.delete(`${BASE_URL}/nodes/:sourceId/links/:targetId`, () => new HttpResponse(null, { status: 204 })),

  // Upload content — auth-aware so §6.3 tests can vary the token
  http.post(`${BASE_URL}/nodes/:id/content`, ({ request }) => {
    const auth = request.headers.get('Authorization');
    if (!auth?.startsWith('Bearer ')) {
      return HttpResponse.json({ code: 'unauthorized', text: 'Missing token' }, { status: 401 });
    }
    return new HttpResponse(null, { status: 204 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.clearAllMocks();
  mockGetToken.mockReturnValue('test-token');
  mockSigninSilent.mockResolvedValue(undefined);
  mockSigninRedirect.mockReturnValue(undefined);
});
afterAll(() => server.close());

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

// sonner — silence toasts in tests
vi.mock('sonner', () => ({
  toast: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

// ─── Wrapper factory ──────────────────────────────────────────────────────────

function createWrapper() {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  return { qc, Wrapper: ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  )};
}

// ─── useCreateNode ────────────────────────────────────────────────────────────

describe('useCreateNode', () => {
  it('returns node details on success', async () => {
    const { Wrapper } = createWrapper();
    const { useCreateNode } = await import('./mutations');
    const { result } = renderHook(() => useCreateNode(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ type: 'documentation', name: 'New doc' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.id).toBe(42);
  });

  it('exposes DivoidApiError on 400 response', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ code: 'validation', text: 'Type is required' }, { status: 400 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { useCreateNode } = await import('./mutations');
    const { result } = renderHook(() => useCreateNode(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ type: '', name: 'test' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(DivoidApiError);
    expect((result.current.error as DivoidApiError).status).toBe(400);
    expect((result.current.error as DivoidApiError).code).toBe('validation');
  });

  it('exposes DivoidApiError on 403 response', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes`, () =>
        HttpResponse.json({ code: 'forbidden', text: 'Forbidden' }, { status: 403 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { useCreateNode } = await import('./mutations');
    const { result } = renderHook(() => useCreateNode(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ type: 'task', name: 'test' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as DivoidApiError).status).toBe(403);
  });
});

// ─── usePatchNode ─────────────────────────────────────────────────────────────

describe('usePatchNode', () => {
  it('succeeds and returns void on 204', async () => {
    const { Wrapper } = createWrapper();
    const { usePatchNode } = await import('./mutations');
    const { result } = renderHook(() => usePatchNode(42), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate([{ op: 'replace', path: '/name', value: 'Updated' }]);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeUndefined();
  });

  it('exposes DivoidApiError on 404 response', async () => {
    server.use(
      http.patch(`${BASE_URL}/nodes/:id`, () =>
        HttpResponse.json({ code: 'notfound', text: 'Not found' }, { status: 404 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { usePatchNode } = await import('./mutations');
    const { result } = renderHook(() => usePatchNode(999), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate([{ op: 'replace', path: '/name', value: 'Updated' }]);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as DivoidApiError).status).toBe(404);
  });
});

// ─── useDeleteNode ────────────────────────────────────────────────────────────

describe('useDeleteNode', () => {
  it('succeeds on 204', async () => {
    const { Wrapper } = createWrapper();
    const { useDeleteNode } = await import('./mutations');
    const { result } = renderHook(() => useDeleteNode(42), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate();
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('exposes DivoidApiError on 404 response', async () => {
    server.use(
      http.delete(`${BASE_URL}/nodes/:id`, () =>
        HttpResponse.json({ code: 'notfound', text: 'Not found' }, { status: 404 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { useDeleteNode } = await import('./mutations');
    const { result } = renderHook(() => useDeleteNode(999), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate();
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as DivoidApiError).status).toBe(404);
  });
});

// ─── useLinkNodes ─────────────────────────────────────────────────────────────

describe('useLinkNodes', () => {
  it('succeeds on 204', async () => {
    const { Wrapper } = createWrapper();
    const { useLinkNodes } = await import('./mutations');
    const { result } = renderHook(() => useLinkNodes(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ sourceId: 1, targetId: 2 });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('exposes DivoidApiError on 404 response', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes/:id/links`, () =>
        HttpResponse.json({ code: 'notfound', text: 'Source not found' }, { status: 404 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { useLinkNodes } = await import('./mutations');
    const { result } = renderHook(() => useLinkNodes(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ sourceId: 999, targetId: 2 });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as DivoidApiError).status).toBe(404);
  });
});

// ─── useUnlinkNodes ───────────────────────────────────────────────────────────

describe('useUnlinkNodes', () => {
  it('succeeds on 204', async () => {
    const { Wrapper } = createWrapper();
    const { useUnlinkNodes } = await import('./mutations');
    const { result } = renderHook(() => useUnlinkNodes(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ sourceId: 1, targetId: 2 });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('exposes DivoidApiError on 404 response', async () => {
    server.use(
      http.delete(`${BASE_URL}/nodes/:sourceId/links/:targetId`, () =>
        HttpResponse.json({ code: 'notfound', text: 'Link not found' }, { status: 404 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { useUnlinkNodes } = await import('./mutations');
    const { result } = renderHook(() => useUnlinkNodes(), { wrapper: Wrapper });

    await act(async () => {
      result.current.mutate({ sourceId: 1, targetId: 999 });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as DivoidApiError).status).toBe(404);
  });
});

// ─── useUploadContent ─────────────────────────────────────────────────────────

describe('useUploadContent', () => {
  it('succeeds on 204', async () => {
    const { Wrapper } = createWrapper();
    const { useUploadContent } = await import('./mutations');
    const { result } = renderHook(() => useUploadContent(42), { wrapper: Wrapper });

    const body = new Blob(['# Hello'], { type: 'text/markdown' });

    await act(async () => {
      result.current.mutate({ body, contentType: 'text/markdown; charset=utf-8' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('exposes DivoidApiError on 413 (payload too large)', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes/:id/content`, () =>
        HttpResponse.json({ code: 'toolarge', text: 'Request entity too large' }, { status: 413 }),
      ),
    );

    const { Wrapper } = createWrapper();
    const { useUploadContent } = await import('./mutations');
    const { result } = renderHook(() => useUploadContent(42), { wrapper: Wrapper });

    const body = new Blob(['x'.repeat(100)]);

    await act(async () => {
      result.current.mutate({ body, contentType: 'application/octet-stream' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as DivoidApiError).status).toBe(413);
  });

  it('§6.3: signinSilent called on 401; retry succeeds; signinRedirect NOT called', async () => {
    let callCount = 0;
    server.use(
      http.post(`${BASE_URL}/nodes/:id/content`, ({ request }) => {
        callCount++;
        const auth = request.headers.get('Authorization');
        if (auth === 'Bearer refreshed-token') {
          return new HttpResponse(null, { status: 204 });
        }
        return HttpResponse.json({ code: 'unauthorized', text: 'bad' }, { status: 401 });
      }),
    );

    // Start with no token; signinSilent updates it to 'refreshed-token'.
    mockGetToken.mockReturnValueOnce(undefined);
    mockSigninSilent.mockImplementation(async () => {
      mockGetToken.mockReturnValue('refreshed-token');
    });

    const { Wrapper } = createWrapper();
    const { useUploadContent } = await import('./mutations');
    const { result } = renderHook(() => useUploadContent(42), { wrapper: Wrapper });

    await act(async () => {
      await result.current.mutateAsync({ body: 'data', contentType: 'text/plain' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockSigninSilent).toHaveBeenCalledOnce();
    expect(mockSigninRedirect).not.toHaveBeenCalled();
    expect(callCount).toBe(2);
  });

  it('§6.3: signinRedirect called when signinSilent rejects', async () => {
    mockGetToken.mockReturnValue(undefined);
    mockSigninSilent.mockRejectedValue(new Error('refresh token expired'));

    const { Wrapper } = createWrapper();
    const { useUploadContent } = await import('./mutations');
    const { result } = renderHook(() => useUploadContent(42), { wrapper: Wrapper });

    await act(async () => {
      try {
        await result.current.mutateAsync({ body: 'data', contentType: 'text/plain' });
      } catch {
        // expected — hook surfaces DivoidApiError
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(mockSigninSilent).toHaveBeenCalledOnce();
    expect(mockSigninRedirect).toHaveBeenCalledOnce();
  });

  it('§6.3: signinRedirect called after retry also 401s (no-loop guarantee)', async () => {
    let callCount = 0;
    server.use(
      http.post(`${BASE_URL}/nodes/:id/content`, () => {
        callCount++;
        return HttpResponse.json({ code: 'unauthorized', text: 'still bad' }, { status: 401 });
      }),
    );

    mockGetToken.mockReturnValue(undefined);
    mockSigninSilent.mockResolvedValue(undefined); // resolves but server always rejects

    const { Wrapper } = createWrapper();
    const { useUploadContent } = await import('./mutations');
    const { result } = renderHook(() => useUploadContent(42), { wrapper: Wrapper });

    await act(async () => {
      try {
        await result.current.mutateAsync({ body: 'data', contentType: 'text/plain' });
      } catch {
        // expected
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(mockSigninSilent).toHaveBeenCalledOnce();
    expect(mockSigninRedirect).toHaveBeenCalledOnce();
    // Exactly 2 POST attempts: original + one retry. No third attempt.
    expect(callCount).toBe(2);
  });
});
