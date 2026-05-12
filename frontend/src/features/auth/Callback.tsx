/**
 * Callback route handler — /callback
 *
 * react-oidc-context processes the OIDC auth-code exchange automatically
 * when the component tree renders. The onSigninCallback in AuthProvider
 * strips the OIDC params from the URL; this component just shows a brief
 * loading state while that happens, then redirects to /.
 *
 * This component must NOT be auth-gated (it handles the unauthenticated
 * redirect from Keycloak).
 */

import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import { ROUTES } from '@/lib/constants';

export function Callback() {
  const auth = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    // Once authentication has completed (or if already authenticated),
    // redirect to the landing page.
    if (!auth.isLoading && !auth.error) {
      navigate(ROUTES.HOME, { replace: true });
    }
  }, [auth.isLoading, auth.error, navigate]);

  if (auth.error) {
    return (
      <div className="flex h-screen items-center justify-center">
        <div className="text-center">
          <p className="text-destructive font-medium mb-2">Authentication failed</p>
          <p className="text-muted-foreground text-sm">{auth.error.message}</p>
          <button
            className="mt-4 text-sm underline text-muted-foreground hover:text-foreground"
            onClick={() => auth.signinRedirect()}
          >
            Try again
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-screen items-center justify-center">
      <div className="text-muted-foreground text-sm">Signing in…</div>
    </div>
  );
}
