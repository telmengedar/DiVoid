/**
 * useNodeSemantic — semantic (vector similarity) search hook.
 *
 * Wraps GET /api/nodes?query=<plain-text>&... — each result carries a
 * `similarity` field (0–1) indicating relevance. Use when the retrieval
 * context is a plain-language question rather than a known topology.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 * API reference: DiVoid node #8 and onboarding node #9
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { createApiClient } from '@/lib/api';
import { API, API_BASE_URL } from '@/lib/constants';
import type { NodeFilter, Page, NodeDetails } from '@/types/divoid';

/** Filter params valid alongside semantic query (excludes linkedto). */
export type SemanticFilter = Omit<NodeFilter, 'linkedto' | 'query'>;

export function nodeSemanticQueryKey(query: string, filter?: SemanticFilter) {
  return ['nodes', 'semantic', query, filter ?? {}] as const;
}

/**
 * Runs a semantic search over DiVoid nodes.
 * Disabled when `query` is empty.
 */
export function useNodeSemantic(query: string, filter?: SemanticFilter) {
  const auth = useAuth();

  const client = createApiClient(
    () => auth.user?.access_token,
    () => auth.signinRedirect(),
    API_BASE_URL,
  );

  const params: NodeFilter = {
    ...filter,
    query,
  };

  return useQuery<Page<NodeDetails>>({
    queryKey: nodeSemanticQueryKey(query, filter),
    queryFn: ({ signal }) =>
      client.get<Page<NodeDetails>>(API.NODES.LIST, params as Record<string, unknown>, signal),
    enabled: auth.isAuthenticated && query.trim().length > 0,
    staleTime: 60_000,
  });
}
