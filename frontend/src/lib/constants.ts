/**
 * Application constants derived from Vite environment variables.
 *
 * All VITE_* vars are baked into the bundle at build time. The getEnv
 * helper returns an empty string in test environments so tests can
 * mock this module without errors at module evaluation time.
 * Tests that exercise code paths depending on these values should mock
 * this module via vi.mock('@/lib/constants', ...).
 */

function getEnv(name: string): string {
  // During Vitest runs, import.meta.env may not have the VITE_* vars.
  // Tests that need real values should mock this module.
  return (import.meta.env[name as keyof ImportMetaEnv] as string) ?? '';
}

/** Keycloak OIDC authority (issuer URL). */
export const KEYCLOAK_AUTHORITY = getEnv('VITE_KEYCLOAK_AUTHORITY');

/** Keycloak public client ID for this SPA. */
export const KEYCLOAK_CLIENT_ID = getEnv('VITE_KEYCLOAK_CLIENT_ID');

/** DiVoid backend API base URL (no trailing slash). */
export const API_BASE_URL = getEnv('VITE_API_BASE_URL');

/** OIDC redirect URI (must match Keycloak client config). */
export const OIDC_REDIRECT_URI = getEnv('VITE_OIDC_REDIRECT_URI');

/** OIDC post-logout redirect URI (must match Keycloak client config). */
export const OIDC_POST_LOGOUT_REDIRECT_URI = getEnv('VITE_OIDC_POST_LOGOUT_REDIRECT_URI');

/** Application routes */
export const ROUTES = {
  HOME: '/',
  CALLBACK: '/callback',
  LOGOUT: '/logout',
  // Future routes (PRs 2–5) — listed here so they can be referenced in nav stubs
  SEARCH: '/search',
  NODE_DETAIL: (id: number) => `/nodes/${id}`,
  WORKSPACE: '/workspace',
  TASKS: '/tasks',
  PROJECT_TASKS: (projectId: number) => `/tasks/${projectId}`,
} as const;

/** DiVoid API endpoint paths (relative to API_BASE_URL). */
export const API = {
  USERS: {
    ME: '/users/me',
  },
  NODES: {
    LIST: '/nodes',
    PATH: '/nodes/path',
    DETAIL: (id: number) => `/nodes/${id}`,
    CONTENT: (id: number) => `/nodes/${id}/content`,
    LINKS: (id: number) => `/nodes/${id}/links`,
    UNLINK: (sourceId: number, targetId: number) =>
      `/nodes/${sourceId}/links/${targetId}`,
  },
  HEALTH: '/health',
} as const;
