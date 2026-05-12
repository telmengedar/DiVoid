/**
 * DiVoid OIDC auth provider.
 *
 * Wraps react-oidc-context's AuthProvider with DiVoid-specific configuration:
 *  - Keycloak master realm, DiVoid client, PKCE S256
 *  - In-memory token storage only (NEVER localStorage for access/refresh/id tokens)
 *  - Silent refresh via refresh-token grant (~30s before expiry)
 *  - OIDC state/nonce/verifier stored in sessionStorage (required for the redirect dance)
 *
 * Design: docs/architecture/frontend-bootstrap.md §9.1, §9.2
 */

import { type ReactNode } from 'react';
import { AuthProvider as OidcAuthProvider } from 'react-oidc-context';
import { WebStorageStateStore, InMemoryWebStorage } from 'oidc-client-ts';
import {
  KEYCLOAK_AUTHORITY,
  KEYCLOAK_CLIENT_ID,
  OIDC_REDIRECT_URI,
  OIDC_POST_LOGOUT_REDIRECT_URI,
} from '@/lib/constants';

interface AuthProviderProps {
  children: ReactNode;
}

/**
 * Provides the OIDC session to the component tree.
 * Must be placed above any component that calls useAuth() or useWhoami().
 */
export function AuthProvider({ children }: AuthProviderProps) {
  return (
    <OidcAuthProvider
      authority={KEYCLOAK_AUTHORITY}
      client_id={KEYCLOAK_CLIENT_ID}
      redirect_uri={OIDC_REDIRECT_URI}
      post_logout_redirect_uri={OIDC_POST_LOGOUT_REDIRECT_URI}
      scope="openid profile email"
      // PKCE S256 (oidc-client-ts default; explicit for clarity)
      response_type="code"
      // userStore: persists the User object (access token, refresh token, id token).
      // InMemoryWebStorage keeps tokens in JS memory only — never written to localStorage
      // or sessionStorage. Reload = logged out (forced re-auth via SSO cookie). §9.2.
      userStore={new WebStorageStateStore({ store: new InMemoryWebStorage() })}
      // stateStore: persists OIDC interaction state (nonce, state param, PKCE code_verifier).
      // sessionStorage is correct here — state must survive the redirect dance but must NOT
      // persist across browser restarts or tabs. Cleared when tab closes. §9.2.
      stateStore={new WebStorageStateStore({ store: window.sessionStorage })}
      // Silent refresh: attempt via refresh-token grant 30s before expiry.
      // automaticSilentRenew=true makes oidc-client-ts schedule this automatically.
      automaticSilentRenew={true}
      // Do NOT use the silent iframe renew (third-party-cookie deprecated).
      // The refresh-token grant is the modern equivalent for SPAs.
      includeIdTokenInSilentRenew={false}
      // Persist user info across page loads within the session (sessionStorage only).
      loadUserInfo={true}
      // On silent renew failure, trigger a full redirect so the user can log back in
      // rather than silently degrading into a state where all API calls fail.
      onSigninCallback={() => {
        // Remove the OIDC params from the URL after the callback is processed.
        window.history.replaceState({}, document.title, window.location.pathname);
      }}
    >
      {children}
    </OidcAuthProvider>
  );
}
