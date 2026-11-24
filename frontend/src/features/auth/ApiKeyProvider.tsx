import { useEffect, useRef, useState, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { createApiClient, type ApiClient } from '@/lib/api';
import { API, API_BASE_URL, ROUTES } from '@/lib/constants';
import { DivoidApiError, type UserDetails } from '@/types/divoid';
import { AppSessionContext } from './AppSession';

interface Boundary {
  client: ApiClient;
  queries: QueryClient;
  controller: AbortController;
  identity?: UserDetails;
  release: () => void;
}

/** Isolates a verified key, its requests and its query cache in application memory.
 * @param children Application routes that consume this authentication session.
 */
export function ApiKeyProvider({ children }: { children: ReactNode }) {
  const navigate = useNavigate();
  const [boundary, setBoundary] = useState(() => createBoundary());
  const current = useRef(boundary);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<Error>();
  const [generation, setGeneration] = useState(0);

  function createBoundary(candidate?: string): Boundary {
    let token = candidate;
    const controller = new AbortController();
    const queries = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: 5 * 60_000 } },
    });
    const next: Boundary = {
      controller,
      queries,
      client: createApiClient(() => token, undefined, undefined, API_BASE_URL, {
        signal: controller.signal,
        onUnauthorized: () => {
          if (current.current === next && next.identity) endSession();
        },
      }),
      release: () => {
        token = undefined;
        controller.abort();
        void queries.cancelQueries();
        queries.clear();
      },
    };
    return next;
  }

  function replace(next: Boundary) {
    const previous = current.current;
    current.current = next;
    previous.release();
    setBoundary(next);
    setGeneration((value) => value + 1);
  }

  function endSession() {
    replace(createBoundary());
    setChecking(false);
    setError(undefined);
  }

  async function login(value: string) {
    const candidate = value.trim();
    if (!candidate) {
      setError(new Error('Enter your API key.'));
      return;
    }
    const next = createBoundary(candidate);
    replace(next);
    setChecking(true);
    setError(undefined);
    try {
      const identity = await next.client.get<UserDetails>(API.USERS.ME);
      if (current.current !== next) return;
      if (!identity.enabled) throw new DivoidApiError(401, 'unauthorized', 'Account disabled');
      next.identity = identity;
      next.queries.setQueryData(['whoami'], identity);
      setChecking(false);
    } catch (failure) {
      if (current.current !== next) return;
      replace(createBoundary());
      setChecking(false);
      const message = failure instanceof DivoidApiError
        ? 'The API key was not accepted. Check your key and access permissions.'
        : 'Unable to reach DiVoid. Check the connection and try again.';
      setError(new Error(message));
    }
  }

  useEffect(() => {
    if (current.current.controller.signal.aborted) replace(createBoundary());
    return () => current.current.release();
  }, []);

  return (
    <QueryClientProvider client={boundary.queries} key={generation}>
      <AppSessionContext.Provider value={{
        mode: 'api-key',
        isAuthenticated: Boolean(boundary.identity),
        isLoading: checking,
        identity: boundary.identity,
        error,
        client: boundary.client,
        login,
        signinRedirect: () => navigate(ROUTES.WORKSPACE),
        signoutRedirect: endSession,
      }}>
        {children}
      </AppSessionContext.Provider>
    </QueryClientProvider>
  );
}
