/**
 * ProtectedRoute — wraps any auth-gated route.
 *
 * While loading: shows a spinner.
 * Unauthenticated: initiates the Keycloak redirect.
 * Authenticated: renders children.
 */

import { type ReactNode } from 'react';
import { useAuth } from 'react-oidc-context';

interface ProtectedRouteProps {
  children: ReactNode;
}

export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const auth = useAuth();

  if (auth.isLoading) {
    return (
      <div className="flex h-screen items-center justify-center">
        <div className="text-muted-foreground text-sm">Loading…</div>
      </div>
    );
  }

  if (!auth.isAuthenticated) {
    // Trigger the Keycloak redirect. The component returns null immediately;
    // the browser navigates to Keycloak before the next render.
    void auth.signinRedirect();
    return null;
  }

  return <>{children}</>;
}
