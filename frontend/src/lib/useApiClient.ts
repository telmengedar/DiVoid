/**
 * useApiClient — stable API client hook.
 *
 * Wraps createApiClient() so the returned client object is the same
 * reference across renders. Without this, every hook that called
 * createApiClient() inline produced a new client every render, which
 * changed queryFn identity on every render, which caused TanStack Query
 * to refetch continuously, which looped (DiVoid bug #257).
 *
 * Implementation strategy — ref-forwarding:
 *
 *   A single client is created once (via useMemo with [] deps). Its three
 *   closures (getToken, signinSilent, signinRedirect) do NOT capture `auth`
 *   at creation time. Instead they read from a `useRef` that is updated to
 *   the latest `auth` object on every render. This means:
 *
 *    1. The client reference is stable — useMemo never recomputes.
 *    2. Token and callbacks always reflect the current auth state —
 *       no stale closure problem.
 *    3. The useMemo dependency array is empty, so it does NOT call
 *       auth.user?.access_token during render, which would consume
 *       mockReturnValueOnce() calls in unit tests before the fetch runs.
 *
 * Fix (b) — DiVoid bug #403:
 *   The signinRedirect closure reads terminalAuthFailure from a ref. When true,
 *   it is a no-op: the session is conclusively dead and ProtectedRoute will fire
 *   a single redirect. This collapses the N-concurrent-query amplification where
 *   each in-flight TanStack Query independently calls signinRedirect on 401.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.4
 * Fixes: DiVoid bug #257 — per-render reconstruction looped.
 *        DiVoid bug #403 — concurrent 401s each called signinRedirect independently.
 */

import { useMemo, useRef } from 'react';
import { useAuth } from 'react-oidc-context';
import { createApiClient } from '@/lib/api';
import { API_BASE_URL } from '@/lib/constants';
import { useDiVoidAuthContext } from '@/features/auth/AuthProvider';
import type { ApiClient } from '@/lib/api';
import type { AuthContextProps } from 'react-oidc-context';

/**
 * Returns a stable API client bound to the current OIDC session.
 *
 * The client object reference never changes after the first render.
 * Its internal closures always read the latest auth state via a ref,
 * so token rotation and callback identity remain correct across silent
 * refreshes.
 */
export function useApiClient(): ApiClient {
  const auth = useAuth();
  const { terminalAuthFailure } = useDiVoidAuthContext();

  // Keep a mutable ref to the latest auth object so the stable closures
  // below can always read the freshest token and callables without
  // needing to be recreated.
  const authRef = useRef<AuthContextProps>(auth);
  authRef.current = auth;

  // Keep a mutable ref to the latest terminalAuthFailure flag so the stable
  // signinRedirect closure can check it without being recreated on each change.
  const terminalRef = useRef<boolean>(terminalAuthFailure);
  terminalRef.current = terminalAuthFailure;

  // Create the client exactly once. The three closures delegate to authRef
  // so they are always up-to-date without recomputing the memo.
  return useMemo(
    () =>
      createApiClient(
        () => authRef.current.user?.access_token,
        () => authRef.current.signinSilent(),
        () => {
          // Short-circuit: when the session is conclusively dead, do not call
          // signinRedirect from each concurrent in-flight query. ProtectedRoute
          // will fire one redirect via its own useEffect guard (bug #403 fix a).
          if (terminalRef.current) return;
          authRef.current.signinRedirect();
        },
        API_BASE_URL,
      ),
    // Empty deps: client reference is intentionally stable for the lifetime
    // of the component. Freshness is handled by authRef and terminalRef.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );
}
