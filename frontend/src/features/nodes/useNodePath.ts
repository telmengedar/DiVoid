/**
 * useNodePath — graph path traversal hook.
 *
 * Wraps GET /api/nodes/path?path=<expression>&... — server-side multi-hop
 * traversal. Use when the topology is known ("open tasks under project X").
 *
 * 400 errors from path syntax problems carry the backend's column-pointing
 * message; callers should surface them directly to the user (not swallow them).
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 * API reference: DiVoid node #8 (path grammar)
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { createApiClient } from '@/lib/api';
import { API, API_BASE_URL } from '@/lib/constants';
import type { NodeFilter, Page, NodeDetails } from '@/types/divoid';

/** Filter params accepted by the path endpoint (excludes linkedto and query). */
export type PathFilter = Omit<NodeFilter, 'linkedto' | 'query'>;

export function nodePathQueryKey(path: string, filter?: PathFilter) {
  return ['nodes', 'path', path, filter ?? {}] as const;
}

/**
 * Executes a DiVoid path query and returns the terminal-hop nodes.
 * Disabled when `path` is empty.
 */
export function useNodePath(path: string, filter?: PathFilter) {
  const auth = useAuth();

  const client = createApiClient(
    () => auth.user?.access_token,
    () => auth.signinRedirect(),
    API_BASE_URL,
  );

  const params = {
    ...filter,
    path,
  };

  return useQuery<Page<NodeDetails>>({
    queryKey: nodePathQueryKey(path, filter),
    queryFn: ({ signal }) =>
      client.get<Page<NodeDetails>>(API.NODES.PATH, params as Record<string, unknown>, signal),
    enabled: auth.isAuthenticated && path.trim().length > 0,
    staleTime: 30_000,
    // Do not retry on 400 — syntax errors are permanent for a given path string.
    retry: (failureCount, error) => {
      if (error && typeof error === 'object' && 'status' in error) {
        if ((error as { status: number }).status === 400) return false;
      }
      return failureCount < 3;
    },
  });
}
