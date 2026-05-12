/**
 * useNodeListLinkedTo — one-hop neighbour walk hook.
 *
 * Wraps GET /api/nodes?linkedto=<id>&... — the workhorse for fetching
 * all nodes linked to a known anchor node, optionally filtered by type,
 * status, name, etc.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 * API reference: DiVoid node #8
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { createApiClient } from '@/lib/api';
import { API, API_BASE_URL } from '@/lib/constants';
import type { NodeFilter, Page, NodeDetails } from '@/types/divoid';

export function nodeLinkedToQueryKey(linkedToId: number, filter?: Omit<NodeFilter, 'linkedto'>) {
  return ['nodes', 'linkedto', linkedToId, filter ?? {}] as const;
}

/**
 * Returns all nodes linked to `linkedToId`, with optional additional filters.
 * Disabled when `linkedToId` is 0 or falsy.
 */
export function useNodeListLinkedTo(
  linkedToId: number,
  filter?: Omit<NodeFilter, 'linkedto'>,
) {
  const auth = useAuth();

  const client = createApiClient(
    () => auth.user?.access_token,
    () => auth.signinRedirect(),
    API_BASE_URL,
  );

  const params: NodeFilter = {
    ...filter,
    linkedto: [linkedToId],
  };

  return useQuery<Page<NodeDetails>>({
    queryKey: nodeLinkedToQueryKey(linkedToId, filter),
    queryFn: ({ signal }) =>
      client.get<Page<NodeDetails>>(API.NODES.LIST, params as Record<string, unknown>, signal),
    enabled: auth.isAuthenticated && linkedToId > 0,
    staleTime: 30_000,
  });
}
