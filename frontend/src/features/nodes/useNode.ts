/**
 * useNode — single node detail hook.
 *
 * Wraps GET /api/nodes/{id}.
 * Used by the node detail page to load metadata (id, type, name, status,
 * contentType). For the content blob itself, see useNodeContent.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { NodeDetails } from '@/types/divoid';

export function nodeQueryKey(id: number) {
  return ['nodes', 'detail', id] as const;
}

/**
 * Fetches a single node by id.
 * Disabled when `id` is 0 or falsy.
 */
export function useNode(id: number) {
  const auth = useAuth();
  const client = useApiClient();

  return useQuery<NodeDetails>({
    queryKey: nodeQueryKey(id),
    queryFn: ({ signal }) =>
      client.get<NodeDetails>(API.NODES.DETAIL(id), undefined, signal),
    enabled: auth.isAuthenticated && id > 0,
    staleTime: 2 * 60_000,
  });
}
