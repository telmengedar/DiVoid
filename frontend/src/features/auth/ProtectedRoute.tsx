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
import { useAuth } from 'react-oidc-context';

interface ProtectedRouteProps {
  children: ReactNode;
}

export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const auth = useAuth();

  // One-shot guard: ensures signinRedirect() is called at most once per mount,
  // even if the component re-renders multiple times in the unauthenticated state.
  const hasRedirectedRef = useRef(false);

  useEffect(() => {
    if (!auth.isLoading && !auth.isAuthenticated && !hasRedirectedRef.current) {
      hasRedirectedRef.current = true;
      void auth.signinRedirect();
    }
  }, [auth.isLoading, auth.isAuthenticated, auth]);

  if (auth.isLoading) {
    return (
      <div className="flex h-screen items-center justify-center">
        <div className="text-muted-foreground text-sm">Loading…</div>
      </div>
    );
  }

  if (!auth.isAuthenticated) {
    // Redirect is firing in the background (useEffect above).
    // Return null while the browser navigates to Keycloak.
    return null;
  }

  return <>{children}</>;
}
