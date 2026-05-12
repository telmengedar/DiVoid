/**
 * Tests for useWhoami — the canonical DiVoid identity hook.
 *
 * Verifies:
 *  - Happy path: returns the user from GET /api/users/me
 *  - Query is disabled when not authenticated
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

// ─── Fixtures ─────────────────────────────────────────────────────────────────

const BASE_URL = 'http://localhost:5007/api';

const defaultUser = {
  id: 1,
  name: 'Toni',
  email: 'toni@mamgo.io',
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['read', 'write', 'admin'],
};

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(defaultUser)),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────

// Mock react-oidc-context so tests don't need a full OIDC provider setup.
vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    user: { access_token: 'test-access-token' },
  })),
}));

// Mock constants so API_BASE_URL points at the test server.
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

// ─── Test wrapper ─────────────────────────────────────────────────────────────

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('useWhoami', () => {
  it('returns user data when authenticated', async () => {
    const { useWhoami } = await import('./useWhoami');
    const { result } = renderHook(() => useWhoami(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(defaultUser);
  });

  it('is disabled when not authenticated', async () => {
    const { useAuth } = await import('react-oidc-context');
    vi.mocked(useAuth).mockReturnValueOnce({
      isAuthenticated: false,
      user: undefined,
    } as ReturnType<typeof useAuth>);

    const { useWhoami } = await import('./useWhoami');
    const { result } = renderHook(() => useWhoami(), { wrapper: createWrapper() });

    // When disabled, query stays in idle state — data is undefined, not fetching
    expect(result.current.isFetching).toBe(false);
    expect(result.current.data).toBeUndefined();
  });
});
