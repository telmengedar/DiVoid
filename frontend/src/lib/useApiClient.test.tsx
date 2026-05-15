// @vitest-environment happy-dom
/**
 * useApiClient — concurrent-401 short-circuit test (DiVoid bug #403).
 *
 * ## What is being tested
 *
 * useApiClient wraps createApiClient with a signinRedirect closure that reads
 * terminalAuthFailure from a ref. When terminalAuthFailure is true, that closure
 * is a no-op. This collapses the N-concurrent-query amplification where each
 * in-flight TanStack Query independently calls signinRedirect on 401.
 *
 * ## Test strategy
 *
 * We mock useDiVoidAuthContext to return terminalAuthFailure=true, then render
 * useApiClient via renderHook and fire 5 parallel .get() calls against an MSW
 * endpoint that always returns 401 with signinSilent rejecting. We assert that
 * signinRedirect is called zero times (short-circuited by the terminal flag).
 *
 * ## Negative proof
 *
 * Remove the `if (terminalRef.current) return;` guard in useApiClient.ts.
 * The test must then fail with signinRedirect called 5 times (once per
 * concurrent 401 after signinSilent rejects), not 0.
 *
 * DiVoid bug #403, task #275.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

// ─── Test server ──────────────────────────────────────────────────────────────

const BASE_URL = 'http://localhost:5007/api';

const server = setupServer(
  // All nodes requests return 401 — simulates expired session
  http.get(`${BASE_URL}/nodes`, () =>
    HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────

const mockSigninRedirect = vi.fn();
const mockSigninSilent = vi.fn().mockRejectedValue(new Error('refresh failed'));

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: false,
    user: undefined,
    signinRedirect: mockSigninRedirect,
    signinSilent: mockSigninSilent,
  })),
}));

vi.mock('@/lib/constants', () => ({
  API_BASE_URL: BASE_URL,
  KEYCLOAK_AUTHORITY: 'https://auth.mamgo.io/realms/master',
  KEYCLOAK_CLIENT_ID: 'DiVoid',
  OIDC_REDIRECT_URI: 'http://localhost:3000/callback',
  OIDC_POST_LOGOUT_REDIRECT_URI: 'http://localhost:3000/logout',
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

// Mock useDiVoidAuthContext so we can control terminalAuthFailure per test.
// The factory is a function so we can mutate the return value per test.
let mockTerminalAuthFailure = false;

vi.mock('@/features/auth/AuthProvider', () => ({
  useDiVoidAuthContext: vi.fn(() => ({ terminalAuthFailure: mockTerminalAuthFailure })),
  DiVoidAuthContext: { _currentValue: { terminalAuthFailure: false } },
}));

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('useApiClient_ConcurrentQueries_OnTerminalAuthFailure_RedirectOnce', () => {
  /**
   * POSITIVE PROOF (load-bearing, DiVoid #403):
   *
   * When terminalAuthFailure=true, signinRedirect must NOT be called by the
   * API client, even when 5 concurrent requests all get 401 and signinSilent
   * rejects. The session is conclusively dead; ProtectedRoute handles the
   * single redirect.
   *
   * Negative proof (apply before submitting):
   *   In useApiClient.ts, remove the `if (terminalRef.current) return;` guard.
   *   Then run this test — it MUST fail with signinRedirect called 5 times
   *   (once per concurrent 401 after signinSilent rejects), not 0.
   */
  it('does not call signinRedirect on concurrent 401s when terminalAuthFailure is true', async () => {
    vi.clearAllMocks();
    mockSigninSilent.mockRejectedValue(new Error('refresh failed'));
    mockTerminalAuthFailure = true;

    const { useDiVoidAuthContext } = await import('@/features/auth/AuthProvider');
    vi.mocked(useDiVoidAuthContext).mockReturnValue({ terminalAuthFailure: true });

    const { useApiClient } = await import('./useApiClient');
    const { result } = renderHook(() => useApiClient());

    // Fire 5 concurrent GET requests — all will 401, signinSilent rejects,
    // terminalAuthFailure short-circuit should suppress every signinRedirect call.
    await act(async () => {
      const requests = Array.from({ length: 5 }, (_, i) =>
        result.current.get(`/nodes?_test=${i}`).catch(() => {
          // Expected to reject with DivoidApiError(401) — swallow for assertion below
        }),
      );
      await Promise.allSettled(requests);
    });

    // The terminalAuthFailure guard must have prevented every signinRedirect call.
    expect(mockSigninRedirect).not.toHaveBeenCalled();
  });

  /**
   * Contrast / negative baseline:
   *
   * When terminalAuthFailure=false (normal operation), signinRedirect IS called
   * after signinSilent fails. This confirms the short-circuit only fires when
   * terminalAuthFailure is true — normal 401 handling is untouched.
   */
  it('calls signinRedirect on 401 when terminalAuthFailure is false (normal path)', async () => {
    vi.clearAllMocks();
    mockSigninSilent.mockRejectedValue(new Error('refresh failed'));
    mockTerminalAuthFailure = false;

    const { useDiVoidAuthContext } = await import('@/features/auth/AuthProvider');
    vi.mocked(useDiVoidAuthContext).mockReturnValue({ terminalAuthFailure: false });

    const { useApiClient } = await import('./useApiClient');
    const { result } = renderHook(() => useApiClient());

    // One 401 with signinSilent rejecting — normal path should redirect.
    await act(async () => {
      await result.current.get('/nodes').catch(() => {});
    });

    expect(mockSigninRedirect).toHaveBeenCalledTimes(1);
  });
});
