/**
 * useNodeContent — node content blob hook.
 *
 * Wraps GET /api/nodes/{id}/content via apiClient.fetchRaw().
 * Returns the raw content as a string. The caller decides how to render it
 * based on the `contentType` field from useNode (e.g. "text/markdown" → ReactMarkdown).
 *
 * The response is treated as text. Binary blobs (images, etc.) are out of
 * scope for PR 2; the detail page will show a placeholder for non-text content.
 *
 * Routing through apiClient.fetchRaw() ensures all client guarantees apply:
 *  - Bearer token injection
 *  - 30s AbortController timeout
 *  - Combined caller + timeout AbortSignal
 *  - Dev-mode request logging
 *  - §6.3 silent-refresh-then-redirect 401 chain (no raw auth.signinRedirect())
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5, §6.3
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';

export function nodeContentQueryKey(id: number) {
  return ['nodes', 'content', id] as const;
}

/**
 * Fetches the content blob for a node as a UTF-8 string.
 * Disabled when `id` is 0 or falsy or when not authenticated.
 *
 * Pass `options.enabled = false` to suppress the fetch (e.g. when the node
 * has no contentType and the backend would return an error). The default
 * behaviour (enabled = true) is unchanged so existing callers are unaffected.
 *
 * Empty-content gating (task #294): callers should pass
 *   `options: { enabled: isTextShaped(node.contentType) }`
 * so the hook never fires for nodes with no text-shaped content.
 */
export function useNodeContent(id: number, options?: { enabled?: boolean }) {
  const auth = useAuth();
  const client = useApiClient();

  const callerEnabled = options?.enabled ?? true;

  return useQuery<string>({
    queryKey: nodeContentQueryKey(id),
    queryFn: async ({ signal }) => {
      const response = await client.fetchRaw(API.NODES.CONTENT(id), signal);
      return response.text();
    },
    enabled: auth.isAuthenticated && id > 0 && callerEnabled,
    staleTime: 5 * 60_000,
  });
}
