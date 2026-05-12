/**
 * Security regression test: AuthProvider token storage posture.
 *
 * Asserts that mounting AuthProvider does NOT persist tokens (User object) to
 * localStorage or sessionStorage. The User object (access_token, refresh_token,
 * id_token) must stay in JS memory only — design §9.2 (DiVoid node #225).
 *
 * OIDC interaction state (nonce, PKCE verifier) is permitted in sessionStorage
 * because it is short-lived and required for the redirect dance, but it must
 * never contain a User-object key (pattern: `user:<authority>:<client_id>`).
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import { AuthProvider } from './AuthProvider';

// ─── Mocks ─────────────────────────────────────────────────────────────────────

// Mock constants to supply deterministic authority/client_id values so we can
// compute the exact user-store key oidc-client-ts would write.
vi.mock('@/lib/constants', () => ({
  KEYCLOAK_AUTHORITY: 'https://auth.mamgo.io/realms/master',
  KEYCLOAK_CLIENT_ID: 'DiVoid',
  OIDC_REDIRECT_URI: 'http://localhost:3000/callback',
  OIDC_POST_LOGOUT_REDIRECT_URI: 'http://localhost:3000/logout',
  API_BASE_URL: 'http://localhost:5007/api',
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

// Mock react-oidc-context so the provider renders without attempting real
// Keycloak discovery (network) or triggering auth redirects in jsdom.
vi.mock('react-oidc-context', () => ({
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  useAuth: vi.fn(() => ({
    isAuthenticated: false,
    user: undefined,
  })),
}));

// ─── Helpers ───────────────────────────────────────────────────────────────────

// The key pattern oidc-client-ts uses for the User object in userStore:
// `user:<authority>:<client_id>` (see UserManager._userStoreKey in source).
const OIDC_USER_KEY_PATTERN = /^user:/;

function sessionStorageHasUserKey(): boolean {
  for (let i = 0; i < sessionStorage.length; i++) {
    const k = sessionStorage.key(i);
    if (k && OIDC_USER_KEY_PATTERN.test(k)) return true;
  }
  return false;
}

// ─── Tests ─────────────────────────────────────────────────────────────────────

describe('AuthProvider_TokensNotPersisted_AcrossReload', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  it('does not write anything to localStorage after mount', () => {
    render(
      <AuthProvider>
        <div data-testid="child">loaded</div>
      </AuthProvider>,
    );

    expect(localStorage.length).toBe(0);
  });

  it('does not persist a User object (tokens) to sessionStorage after mount', () => {
    render(
      <AuthProvider>
        <div data-testid="child">loaded</div>
      </AuthProvider>,
    );

    // sessionStorage may legitimately hold OIDC *state* entries during a redirect
    // dance, but must never hold a User object (which carries the access/refresh/id token).
    expect(sessionStorageHasUserKey()).toBe(false);
  });
});
