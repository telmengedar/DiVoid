/**
 * Logout landing page — /logout
 *
 * Rendered after Keycloak redirects back post-logout.
 * Shows a "signed out" confirmation and a sign-in-again button.
 *
 * Must NOT be auth-gated.
 */

import { useAuth } from 'react-oidc-context';
import { LogIn } from 'lucide-react';

export function LogoutLanding() {
  const auth = useAuth();

  return (
    <div className="flex h-screen items-center justify-center">
      <div className="text-center max-w-sm px-4">
        <h1 className="text-xl font-medium mb-2">You have been signed out</h1>
        <p className="text-muted-foreground text-sm mb-6">
          Your session has ended. Sign in again to continue.
        </p>
        <button
          onClick={() => auth.signinRedirect()}
          className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity"
          aria-label="Sign in to DiVoid"
        >
          <LogIn size={16} aria-hidden="true" />
          Sign in
        </button>
      </div>
    </div>
  );
}
