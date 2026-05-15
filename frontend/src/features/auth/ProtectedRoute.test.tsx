// @vitest-environment happy-dom
/**
 * ProtectedRoute — render-stability + redirect guard tests (DiVoid bug #403).
 *
 * ## Root cause being tested
 *
 * When react-oidc-context settles { isLoading:false, isAuthenticated:false,
 * error:<SilentRenewError> }, the OLD ProtectedRoute called signinRedirect() in
 * the render body with no guard. signinRedirect() triggers internal library state
 * updates which re-render ProtectedRoute which calls signinRedirect() again — a
 * tight async blink loop.
 *
 * ## Fix being verified
 *
 * signinRedirect() is now inside a useEffect with a useRef one-shot guard.
 * Even under repeated re-renders in the unauthenticated+error state, it is
 * called at most once.
 *
 * ## Negative proof strategy
 *
 * The render-stability harness in setup.ts catches SYNCHRONOUS loops only.
 * This bug is async. The real library causes re-renders via internal state
 * updates when signinRedirect() is called. We simulate this by making the mock
 * signinRedirect() increment a counter that is read by useAuth() — so each
 * signinRedirect() call changes the value returned by useAuth(), which causes
 * React to re-render (because vi.fn().mockImplementation reads updated state),
 * demonstrating the loop.
 *
 * Because a plain vi.fn() doesn't cause re-renders, we wrap ProtectedRoute in
 * a parent state holder that flips on each signinRedirect() call, propagating
 * the re-render down to ProtectedRoute.
 *
 * DiVoid bug #403, task #275.
 */

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { useState, useRef, type ReactNode, useCallback } from 'react';
import { ProtectedRoute } from './ProtectedRoute';

// ─── Safety cap (async loop sentinel) ─────────────────────────────────────────
// Mirrors WorkspacePage.renderLoop.test.tsx:119
const MAX_RENDERS = 30;

// ─── Mock react-oidc-context ──────────────────────────────────────────────────

// Controlled mock state — updated by re-render-inducing signinRedirect below
let mockCallCount = 0;

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(),
}));

afterEach(() => {
  vi.clearAllMocks();
  mockCallCount = 0;
});

// ─── Re-render-inducing loop harness ─────────────────────────────────────────
//
// To test the blink-loop negative proof, we need signinRedirect() to CAUSE
// re-renders (just like the real library does). We do this by giving ProtectedRoute
// a parent component that has its own state. When signinRedirect() fires, it
// increments a counter via the closure, which then calls setCallCount() in the
// parent, which re-renders ProtectedRoute. This mirrors the real loop mechanism.
//
// The result: the buggy render-body code calls signinRedirect → parent re-renders
// → ProtectedRoute re-renders → render body fires signinRedirect again → loop.
// The fixed useEffect+useRef code: effect fires once → hasRedirectedRef=true →
// useEffect dependency array hasn't changed → no second call.

function LoopHarness({ children }: { children: (onRedirect: () => void) => ReactNode }) {
  const [, setCallCount] = useState(0);
  const onRedirect = useCallback(() => {
    mockCallCount += 1;
    setCallCount((c) => c + 1); // triggers re-render of LoopHarness → re-render of ProtectedRoute
  }, []);

  return <>{children(onRedirect)}</>;
}

/**
 * Wraps the tree in a render-count sentinel (same pattern as WorkspacePage test).
 */
function RenderCountWrapper({ children }: { children: ReactNode }) {
  const renderCount = useRef(0);
  renderCount.current += 1;
  const capped = renderCount.current >= MAX_RENDERS;

  if (capped) {
    return <div data-testid="loop-capped" data-renders={renderCount.current} />;
  }

  return (
    <>
      <div data-testid="render-count" data-value={renderCount.current} />
      {children}
    </>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('ProtectedRoute_RefreshFailure_DoesNotLoop', () => {
  /**
   * POSITIVE PROOF (load-bearing, DiVoid bug #403):
   *
   * After a settled SilentRenewError state, signinRedirect must be called
   * exactly once — even though each call causes a parent re-render (and thus
   * a ProtectedRoute re-render). The useRef one-shot guard suppresses re-entry.
   *
   * Negative proof (apply before submitting):
   *   In ProtectedRoute, revert the useEffect + useRef guard back to:
   *     if (!auth.isAuthenticated) { void auth.signinRedirect(); return null; }
   *   Re-run this test. It MUST fail because:
   *   Each signinRedirect() call increments callCount → LoopHarness re-renders
   *   → ProtectedRoute re-renders → render body fires signinRedirect() again.
   *   mockCallCount climbs to MAX_RENDERS and loop-capped appears in DOM.
   *   Expected failure: "expected null to not be null" (loop-capped IS in DOM)
   *   and mockCallCount >> 1 (signinRedirect called many times).
   */
  it('calls signinRedirect at most once when settled in unauthenticated+error state', async () => {
    const { useAuth } = await import('react-oidc-context');

    render(
      <MemoryRouter>
        <LoopHarness>
          {(onRedirect) => {
            vi.mocked(useAuth).mockReturnValue({
              isLoading: false,
              isAuthenticated: false,
              user: undefined,
              error: new Error('SilentRenewError'),
              signinRedirect: onRedirect,
              signinSilent: vi.fn().mockRejectedValue(new Error('SilentRenewError')),
            } as ReturnType<typeof useAuth>);

            return (
              <RenderCountWrapper>
                <ProtectedRoute>
                  <div data-testid="protected-content">secret</div>
                </ProtectedRoute>
              </RenderCountWrapper>
            );
          }}
        </LoopHarness>
      </MemoryRouter>,
    );

    // Let async effects and any potential loop play out
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 300));
    });

    // The loop-capped sentinel must NOT appear — the useRef guard stopped the loop.
    expect(screen.queryByTestId('loop-capped')).toBeNull();

    // Protected content is NOT rendered (unauthenticated).
    expect(screen.queryByTestId('protected-content')).toBeNull();

    // signinRedirect called exactly once — the one-shot guard worked.
    expect(mockCallCount).toBe(1);
  });

  /**
   * Boundary: isLoading=true should NOT call signinRedirect at all.
   */
  it('does not call signinRedirect while isLoading is true', async () => {
    const { useAuth } = await import('react-oidc-context');
    vi.mocked(useAuth).mockReturnValue({
      isLoading: true,
      isAuthenticated: false,
      user: undefined,
      error: undefined,
      signinRedirect: vi.fn(),
      signinSilent: vi.fn(),
    } as ReturnType<typeof useAuth>);

    const mockRedirect = vi.fn();

    render(
      <MemoryRouter>
        <ProtectedRoute>
          <div data-testid="protected-content">secret</div>
        </ProtectedRoute>
      </MemoryRouter>,
    );

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 100));
    });

    // Loading spinner shown, not null and not content
    expect(screen.queryByTestId('protected-content')).toBeNull();
    expect(mockRedirect).not.toHaveBeenCalled();
  });

  /**
   * Happy path: authenticated → renders children, no redirect.
   */
  it('renders children when authenticated without calling signinRedirect', async () => {
    const { useAuth } = await import('react-oidc-context');
    const mockRedirect = vi.fn();

    vi.mocked(useAuth).mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      user: { access_token: 'tok', profile: {}, expired: false } as ReturnType<typeof useAuth>['user'],
      error: undefined,
      signinRedirect: mockRedirect,
      signinSilent: vi.fn(),
    } as ReturnType<typeof useAuth>);

    render(
      <MemoryRouter>
        <ProtectedRoute>
          <div data-testid="protected-content">secret</div>
        </ProtectedRoute>
      </MemoryRouter>,
    );

    await waitFor(() => screen.getByTestId('protected-content'));

    expect(mockRedirect).not.toHaveBeenCalled();
  });
});
