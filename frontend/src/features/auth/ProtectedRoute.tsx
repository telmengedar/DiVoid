/**
 * ProtectedRoute — wraps any auth-gated route.
 *
 * While loading: shows a spinner.
 * Unauthenticated: initiates the Keycloak redirect (once — useEffect + useRef guard).
 * Authenticated: renders children.
 *
 * Fix (a) — DiVoid bug #403:
 * signinRedirect() was previously called in the render body with no guard.
 * When react-oidc-context settles { isLoading:false, isAuthenticated:false,
 * error:<SilentRenewError> } after a failed silent renew, every render re-fires
 * the redirect, which triggers state updates that cause re-renders, closing a
 * tight blink loop. The useEffect + useRef guard ensures we call signinRedirect
 * at most once per mount.
 */

import { type ReactNode, useEffect, useRef } from 'react';
import { useAuth } from '@/features/auth/AppSession';

import { ApiKeyLogin } from './ApiKeyLogin';
import type { AppSession } from './AppSession';

interface ProtectedRouteProps {
  children: ReactNode;
}

/** Renders protected content or the selected protocol's login flow.
 * @param children Content requiring a verified application session.
 */
export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const auth = useAuth();
  if (auth.mode === 'api-key') {
    return auth.isAuthenticated ? <>{children}</> : <ApiKeyLogin session={auth} />;
  }
  return <OidcProtectedRoute auth={auth}>{children}</OidcProtectedRoute>;
}

function OidcProtectedRoute({ auth, children }: ProtectedRouteProps & { auth: Exclude<AppSession, { mode: 'api-key' }> }) {
  const hasRedirectedRef = useRef(false);
  useEffect(() => {
    if (!auth.isLoading && !auth.isAuthenticated && !hasRedirectedRef.current) {
      hasRedirectedRef.current = true;
      void auth.signinRedirect();
    }
  }, [auth]);
  if (auth.isLoading) {
    return <div className="flex h-screen items-center justify-center"><div className="text-muted-foreground text-sm">Loading…</div></div>;
  }
  return auth.isAuthenticated ? <>{children}</> : null;
}
