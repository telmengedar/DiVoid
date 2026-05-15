// @vitest-environment happy-dom
/**
 * AuthProvider — terminalAuthFailure flag lifecycle tests (DiVoid bug #403).
 *
 * ## What is being tested
 *
 * Fix (b) adds event handler wiring to DiVoidAuthEventWatcher:
 *   - addSilentRenewError   → sets terminalAuthFailure=true
 *   - addUserSignedOut      → sets terminalAuthFailure=true
 *   - addUserLoaded         → resets terminalAuthFailure=false
 *
 * These are exposed via DiVoidAuthContext / useDiVoidAuthContext().
 *
 * ## Negative proof strategy
 *
 * For each handler: remove the `setTerminalAuthFailure(true)` call inside the
 * respective event callback in DiVoidAuthEventWatcher. The corresponding test
 * must then fail with `expected false to be true` — proving the event wiring
 * is load-bearing.
 *
 * DiVoid bug #403, task #275.
 */

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';
import { useContext } from 'react';

// ─── Event callback registry ──────────────────────────────────────────────────
// We expose refs to the registered callbacks so tests can trigger them manually.

type EventCallback = () => void;

const eventRegistry: {
  silentRenewError: EventCallback | null;
  accessTokenExpired: EventCallback | null;
  userSignedOut: EventCallback | null;
  userLoaded: EventCallback | null;
} = {
  silentRenewError: null,
  accessTokenExpired: null,
  userSignedOut: null,
  userLoaded: null,
};

vi.mock('react-oidc-context', () => ({
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  useAuth: vi.fn(() => ({
    isAuthenticated: false,
    user: undefined,
    events: {
      addSilentRenewError: vi.fn((cb: EventCallback) => {
        eventRegistry.silentRenewError = cb;
        return () => { eventRegistry.silentRenewError = null; };
      }),
      addAccessTokenExpired: vi.fn((cb: EventCallback) => {
        eventRegistry.accessTokenExpired = cb;
        return () => { eventRegistry.accessTokenExpired = null; };
      }),
      addUserSignedOut: vi.fn((cb: EventCallback) => {
        eventRegistry.userSignedOut = cb;
        return () => { eventRegistry.userSignedOut = null; };
      }),
      addUserLoaded: vi.fn((cb: EventCallback) => {
        eventRegistry.userLoaded = cb;
        return () => { eventRegistry.userLoaded = null; };
      }),
    },
  })),
}));

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

afterEach(() => {
  // Reset the event registry between tests
  eventRegistry.silentRenewError = null;
  eventRegistry.accessTokenExpired = null;
  eventRegistry.userSignedOut = null;
  eventRegistry.userLoaded = null;
});

// ─── Consumer component ───────────────────────────────────────────────────────
// Reads DiVoidAuthContext and exposes it via data attributes for assertions.

async function renderConsumer() {
  const { AuthProvider, DiVoidAuthContext } = await import('./AuthProvider');

  function Consumer() {
    const ctx = useContext(DiVoidAuthContext);
    return (
      <div
        data-testid="ctx-consumer"
        data-terminal={String(ctx.terminalAuthFailure)}
      />
    );
  }

  const utils = render(
    <AuthProvider>
      <Consumer />
    </AuthProvider>,
  );

  return utils;
}

function getTerminalFlag(): boolean {
  const el = screen.getByTestId('ctx-consumer');
  return el.getAttribute('data-terminal') === 'true';
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('AuthProvider_TerminalFailureFlag_TogglesOnEvents', () => {
  /**
   * POSITIVE PROOF (load-bearing, DiVoid #403):
   *
   * addSilentRenewError fires → terminalAuthFailure becomes true.
   *
   * Negative proof (apply before submitting):
   *   In DiVoidAuthEventWatcher, remove `setTerminalAuthFailure(true)` from
   *   the addSilentRenewError callback. This test must fail with:
   *     "expected false to be true"
   */
  it('sets terminalAuthFailure=true when addSilentRenewError fires', async () => {
    await renderConsumer();

    // Initial state: false
    expect(getTerminalFlag()).toBe(false);

    // Simulate silent renew error event from oidc-client-ts
    await act(async () => {
      eventRegistry.silentRenewError?.();
    });

    expect(getTerminalFlag()).toBe(true);
  });

  /**
   * POSITIVE PROOF (load-bearing, DiVoid #403):
   *
   * addUserSignedOut fires → terminalAuthFailure becomes true.
   *
   * Negative proof (apply before submitting):
   *   In DiVoidAuthEventWatcher, remove `setTerminalAuthFailure(true)` from
   *   the addUserSignedOut callback. This test must fail with:
   *     "expected false to be true"
   */
  it('sets terminalAuthFailure=true when addUserSignedOut fires', async () => {
    await renderConsumer();

    expect(getTerminalFlag()).toBe(false);

    await act(async () => {
      eventRegistry.userSignedOut?.();
    });

    expect(getTerminalFlag()).toBe(true);
  });

  /**
   * POSITIVE PROOF (load-bearing, DiVoid #403):
   *
   * After terminalAuthFailure=true, addUserLoaded fires → flag resets to false.
   * This ensures successful re-authentication after a redirect clears the flag.
   *
   * Negative proof (apply before submitting):
   *   In DiVoidAuthEventWatcher, remove `setTerminalAuthFailure(false)` from
   *   the addUserLoaded callback. This test must fail with:
   *     "expected true to be false"
   */
  it('resets terminalAuthFailure=false when addUserLoaded fires after failure', async () => {
    await renderConsumer();

    // First, trigger a terminal failure
    await act(async () => {
      eventRegistry.silentRenewError?.();
    });
    expect(getTerminalFlag()).toBe(true);

    // Now simulate successful re-authentication (user loaded after redirect)
    await act(async () => {
      eventRegistry.userLoaded?.();
    });

    await waitFor(() => expect(getTerminalFlag()).toBe(false));
  });

  /**
   * Baseline: flag starts false and stays false when no events fire.
   */
  it('starts with terminalAuthFailure=false and stays false with no events', async () => {
    await renderConsumer();

    expect(getTerminalFlag()).toBe(false);

    // No events → still false after a tick
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    expect(getTerminalFlag()).toBe(false);
  });
});
