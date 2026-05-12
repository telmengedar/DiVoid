/**
 * useWhoami — canonical DiVoid identity hook.
 *
 * Calls GET /api/users/me and returns the DiVoid user details including
 * name, email, and effective permissions array.
 *
 * This is the single source of truth for "who is logged in to DiVoid".
 * Do NOT read permissions from the JWT claims — always use this hook.
 * The backend may have narrowed permissions relative to the Keycloak token.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.3
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { createApiClient } from '@/lib/api';
import { API, API_BASE_URL } from '@/lib/constants';
import type { UserDetails } from '@/types/divoid';

export const WHOAMI_QUERY_KEY = ['whoami'] as const;

/**
 * Returns the DiVoid user details for the currently authenticated principal.
 *
 * The query is only enabled when the user is authenticated.
 * staleTime is 5 minutes; refetchOnWindowFocus=true so permission changes
 * are reflected promptly when the user tabs back in.
 */
export function useWhoami() {
  const auth = useAuth();

  const client = createApiClient(
    () => auth.user?.access_token,
    () => auth.signinSilent(),
    () => auth.signinRedirect(),
    API_BASE_URL,
  );

  return useQuery<UserDetails>({
    queryKey: WHOAMI_QUERY_KEY,
    queryFn: () => client.get<UserDetails>(API.USERS.ME),
    enabled: auth.isAuthenticated,
    staleTime: 5 * 60 * 1_000,
    refetchOnWindowFocus: true,
  });
}
