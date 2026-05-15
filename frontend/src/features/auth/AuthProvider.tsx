/**
 * DiVoid OIDC auth provider.
 *
 * Wraps react-oidc-context's AuthProvider with DiVoid-specific configuration:
 *  - Keycloak master realm, DiVoid client, PKCE S256
 *  - In-memory token storage only (NEVER localStorage for access/refresh/id tokens)
 *  - Silent refresh via refresh-token grant (~30s before expiry)
 *  - OIDC state/nonce/verifier stored in sessionStorage (required for the redirect dance)
 *
 * Fix (b) — DiVoid bug #403:
 * Wires the missing addSilentRenewError, addAccessTokenExpired, and addUserSignedOut
 * events. When silent renew fails or the user is signed out, a terminalAuthFailure
 * flag is set in a React context so that concurrent in-flight API queries can detect
 * "the session is conclusively dead" and short-circuit instead of each calling
 * signinRedirect independently (N-query amplification).
 *
 * Design: docs/architecture/frontend-bootstrap.md §9.1, §9.2
 */

import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { AuthProvider as OidcAuthProvider, useAuth } from 'react-oidc-context';
import { WebStorageStateStore, InMemoryWebStorage } from 'oidc-client-ts';
import {
  KEYCLOAK_AUTHORITY,
  KEYCLOAK_CLIENT_ID,
  OIDC_REDIRECT_URI,
  OIDC_POST_LOGOUT_REDIRECT_URI,
} from '@/lib/constants';

// ─── DiVoid auth context ──────────────────────────────────────────────────────

/**
 * Extended auth context that includes DiVoid-specific state beyond react-oidc-context.
 *
 * terminalAuthFailure: true when silent renew has failed or the user has been signed
 * out by the IdP and there is no valid session. API callers read this to short-circuit
 * the normal signinRedirect path — when true, all in-flight 401 retries throw immediately
 * instead of each independently triggering a redirect.
 *
 * Reset to false on addUserLoaded so a successful re-authentication clears the flag.
 */
export interface DiVoidAuthContextValue {
  terminalAuthFailure: boolean;
}

export const DiVoidAuthContext = createContext<DiVoidAuthContextValue>({
  terminalAuthFailure: false,
});

export function useDiVoidAuthContext(): DiVoidAuthContextValue {
  return useContext(DiVoidAuthContext);
}

// ─── Inner provider — needs to be a child of OidcAuthProvider ────────────────

/**
 * Inner component that wires the missing OIDC lifecycle events.
 * Must be rendered inside OidcAuthProvider so useAuth() is available.
 */
function DiVoidAuthEventWatcher({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const [terminalAuthFailure, setTerminalAuthFailure] = useState(false);

  useEffect(() => {
    // addSilentRenewError: refresh token grant failed — session is conclusively dead.
    const offRenew = auth.events.addSilentRenewError(() => {
      setTerminalAuthFailure(true);
    });

    // addAccessTokenExpired: access token expired without a successful silent renew.
    // Log only; the silent renew error path handles the terminal state.
    const offExpired = auth.events.addAccessTokenExpired(() => {
      if (import.meta.env.DEV) {
        console.debug('[DiVoid Auth] access token expired');
      }
    });

    // addUserSignedOut: IdP indicates the user has signed out (back-channel/front-channel).
    const offSignedOut = auth.events.addUserSignedOut(() => {
      setTerminalAuthFailure(true);
    });

    // addUserLoaded: successful (re-)authentication — reset the failure flag so
    // post-redirect recovery works correctly.
    const offLoaded = auth.events.addUserLoaded(() => {
      setTerminalAuthFailure(false);
    });

    return () => {
      offRenew();
      offExpired();
      offSignedOut();
      offLoaded();
    };
  }, [auth]);

  return (
    <DiVoidAuthContext.Provider value={{ terminalAuthFailure }}>
      {children}
    </DiVoidAuthContext.Provider>
  );
}

// ─── Public provider ──────────────────────────────────────────────────────────

interface AuthProviderProps {
  children: ReactNode;
}

/**
 * Provides the OIDC session and DiVoid auth state to the component tree.
 * Must be placed above any component that calls useAuth(), useWhoami(), or
 * useDiVoidAuthContext().
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
      <DiVoidAuthEventWatcher>
        {children}
      </DiVoidAuthEventWatcher>
    </OidcAuthProvider>
  );
}
