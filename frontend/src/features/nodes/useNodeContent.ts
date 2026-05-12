/**
 * useNodeContent — node content blob hook.
 *
 * Wraps GET /api/nodes/{id}/content.
 * Returns the raw content as a string. The caller decides how to render it
 * based on the `contentType` field from useNode (e.g. "text/markdown" → ReactMarkdown).
 *
 * The response is treated as text. Binary blobs (images, etc.) are out of
 * scope for PR 2; the detail page will show a placeholder for non-text content.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 */

import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { DivoidApiError } from '@/types/divoid';
import { API_BASE_URL, API } from '@/lib/constants';

export function nodeContentQueryKey(id: number) {
  return ['nodes', 'content', id] as const;
}

/**
 * Fetches the content blob for a node as a UTF-8 string.
 * Disabled when `id` is 0 or falsy or when not authenticated.
 */
export function useNodeContent(id: number) {
  const auth = useAuth();
  const token = auth.user?.access_token;

  return useQuery<string>({
    queryKey: nodeContentQueryKey(id),
    queryFn: async ({ signal }) => {
      const headers: Record<string, string> = {};
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }

      const response = await fetch(`${API_BASE_URL}${API.NODES.CONTENT(id)}`, {
        headers,
        signal: signal ?? undefined,
      });

      if (!response.ok) {
        const raw = await response.text().catch(() => '');
        let code = 'unknown';
        let text = response.statusText || `HTTP ${response.status}`;
        if (raw) {
          try {
            const parsed = JSON.parse(raw) as Record<string, unknown>;
            if (typeof parsed.code === 'string') code = parsed.code;
            if (typeof parsed.text === 'string') text = parsed.text;
          } catch {
            text = raw;
          }
        }
        if (response.status === 401) {
          auth.signinRedirect();
        }
        throw new DivoidApiError(response.status, code, text);
      }

      return response.text();
    },
    enabled: auth.isAuthenticated && id > 0,
    staleTime: 5 * 60_000,
  });
}
