/**
 * useNodeList — bare node listing hook.
 *
 * Wraps GET /api/nodes with no scoping constraint. This is the last-resort
 * fallback per design §5.5 / onboarding node #9 guidance. Prefer the
 * scoped hooks (useNodeListLinkedTo, useNodePath, useNodeSemantic) when
 * the retrieval context is known.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { createApiClient } from '@/lib/api';
import { API, API_BASE_URL } from '@/lib/constants';
import type { NodeFilter, Page, NodeDetails } from '@/types/divoid';

export function nodeListQueryKey(filter?: NodeFilter) {
  return ['nodes', 'list', filter ?? {}] as const;
}

/**
 * Returns a paged list of nodes matching the given filter.
 * No linkedto, no semantic query — bare listing.
 */
export function useNodeList(filter?: NodeFilter) {
  const auth = useAuth();

  const client = createApiClient(
    () => auth.user?.access_token,
    () => auth.signinSilent(),
    () => auth.signinRedirect(),
    API_BASE_URL,
  );

  return useQuery<Page<NodeDetails>>({
    queryKey: nodeListQueryKey(filter),
    queryFn: ({ signal }) =>
      client.get<Page<NodeDetails>>(API.NODES.LIST, filter as Record<string, unknown>, signal),
    enabled: auth.isAuthenticated,
    staleTime: 30_000,
  });
}
