/**
 * Mutation hooks for DiVoid node write paths.
 *
 * Hooks:
 *  - useCreateNode()        → POST /api/nodes
 *  - usePatchNode(id)       → PATCH /api/nodes/{id}
 *  - useDeleteNode(id)      → DELETE /api/nodes/{id}
 *  - useLinkNodes()         → POST /api/nodes/{id}/links
 *  - useUnlinkNodes()       → DELETE /api/nodes/{sourceId}/links/{targetId}
 *  - useUploadContent(id)   → POST /api/nodes/{id}/content (raw bytes)
 *
 * All mutations:
 *  - Inherit the §6.3 silent-refresh-then-redirect chain from createApiClient.
 *  - Surface errors as DivoidApiError via sonner toast (caller's onError is called
 *    after the toast; callers may add their own behaviour).
 *  - Invalidate relevant TanStack Query caches on success so UI auto-updates.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6
 * Task: DiVoid node #229
 */

import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { toast } from 'sonner';
import { createApiClient } from '@/lib/api';
import { API, API_BASE_URL } from '@/lib/constants';
import type { NodeDetails, PatchOperation } from '@/types/divoid';
import { DivoidApiError } from '@/types/divoid';
import { nodeQueryKey } from './useNode';
import { nodeContentQueryKey } from './useNodeContent';
import { nodeListQueryKey } from './useNodeList';
import { nodeLinkedToQueryKey } from './useNodeListLinkedTo';

// ─── Helpers ──────────────────────────────────────────────────────────────────

/**
 * Formats a DivoidApiError for display in a sonner toast.
 * Unknown errors fall back to a generic message.
 */
function toastError(error: unknown, fallback = 'Something went wrong. Please try again.'): void {
  if (error instanceof DivoidApiError) {
    if (error.status === 403) {
      toast.error("You don't have permission to do that.");
    } else {
      toast.error(`${error.code}: ${error.text}`);
    }
  } else {
    toast.error(fallback);
  }
}

/** Builds a fresh API client bound to the current OIDC context. */
function useApiClient() {
  const auth = useAuth();
  return createApiClient(
    () => auth.user?.access_token,
    () => auth.signinSilent(),
    () => auth.signinRedirect(),
    API_BASE_URL,
  );
}

// ─── Create ───────────────────────────────────────────────────────────────────

/** Body shape for POST /api/nodes. */
export interface CreateNodeInput {
  type: string;
  name: string;
  /** Only send status for types that carry one (task, bug). */
  status?: string;
}

/**
 * Creates a new node.
 *
 * On success: the returned NodeDetails is available in `data`; the node-list
 * queries are invalidated so browse pages refresh automatically.
 * On error: a sonner toast shows the backend error.
 *
 * @returns the created NodeDetails (id, type, name, status).
 */
export function useCreateNode() {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation<NodeDetails, DivoidApiError, CreateNodeInput>({
    mutationFn: (input) =>
      client.post<NodeDetails>(API.NODES.LIST, input),
    onSuccess: () => {
      // Invalidate all node-list variants so browse pages show the new node.
      queryClient.invalidateQueries({ queryKey: nodeListQueryKey() });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'list'] });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'linkedto'] });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'semantic'] });
    },
    onError: (error) => toastError(error),
  });
}

// ─── Patch ────────────────────────────────────────────────────────────────────

/**
 * Patches an existing node by id.
 *
 * Accepts a JSON-Patch array (PatchOperation[]). Supported backend paths:
 *   /name      — replace with a new string
 *   /status    — replace with a status string or null
 *
 * On success: the node detail query and any linked-to queries are invalidated.
 * On error: a sonner toast shows the backend error.
 */
export function usePatchNode(id: number) {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation<void, DivoidApiError, PatchOperation[]>({
    mutationFn: (ops) =>
      client.patch<void>(API.NODES.DETAIL(id), ops),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: nodeQueryKey(id) });
      // Invalidate linked-to and list queries that may show this node's fields.
      queryClient.invalidateQueries({ queryKey: ['nodes', 'list'] });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'linkedto'] });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'semantic'] });
    },
    onError: (error) => toastError(error),
  });
}

// ─── Delete ───────────────────────────────────────────────────────────────────

/**
 * Deletes a node and all its links.
 *
 * On success: the node detail query and all list/linkedto/semantic queries
 * are invalidated. Callers should navigate away after delete.
 * On error: a sonner toast shows the backend error.
 */
export function useDeleteNode(id: number) {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation<void, DivoidApiError, void>({
    mutationFn: () =>
      client.delete<void>(API.NODES.DETAIL(id)),
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: nodeQueryKey(id) });
      queryClient.removeQueries({ queryKey: nodeContentQueryKey(id) });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'list'] });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'linkedto'] });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'semantic'] });
    },
    onError: (error) => toastError(error),
  });
}

// ─── Link ─────────────────────────────────────────────────────────────────────

/** Input for linking two nodes. */
export interface LinkNodesInput {
  sourceId: number;
  targetId: number;
}

/**
 * Links two nodes. Body: target id as a bare long (per API reference node #8).
 *
 * On success: linkedto queries for both source and target are invalidated.
 * On error: a sonner toast shows the backend error.
 */
export function useLinkNodes() {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation<void, DivoidApiError, LinkNodesInput>({
    mutationFn: ({ sourceId, targetId }) =>
      client.post<void>(API.NODES.LINKS(sourceId), targetId),
    onSuccess: (_, { sourceId, targetId }) => {
      queryClient.invalidateQueries({ queryKey: nodeLinkedToQueryKey(sourceId) });
      queryClient.invalidateQueries({ queryKey: nodeLinkedToQueryKey(targetId) });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'linkedto'] });
    },
    onError: (error) => toastError(error),
  });
}

// ─── Unlink ───────────────────────────────────────────────────────────────────

/** Input for removing a link between two nodes. */
export interface UnlinkNodesInput {
  sourceId: number;
  targetId: number;
}

/**
 * Removes a link between two nodes.
 *
 * On success: linkedto queries for both nodes are invalidated.
 * On error: a sonner toast shows the backend error.
 */
export function useUnlinkNodes() {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation<void, DivoidApiError, UnlinkNodesInput>({
    mutationFn: ({ sourceId, targetId }) =>
      client.delete<void>(API.NODES.UNLINK(sourceId, targetId)),
    onSuccess: (_, { sourceId, targetId }) => {
      queryClient.invalidateQueries({ queryKey: nodeLinkedToQueryKey(sourceId) });
      queryClient.invalidateQueries({ queryKey: nodeLinkedToQueryKey(targetId) });
      queryClient.invalidateQueries({ queryKey: ['nodes', 'linkedto'] });
    },
    onError: (error) => toastError(error),
  });
}

// ─── Upload content ───────────────────────────────────────────────────────────

/** Input for uploading raw content bytes. */
export interface UploadContentInput {
  /** Raw bytes — from a File, a Blob, or a UTF-8-encoded string as ArrayBuffer. */
  body: BodyInit;
  /** MIME type to send as Content-Type (e.g. "text/markdown; charset=utf-8"). */
  contentType: string;
}

/**
 * Uploads content bytes to a node.
 *
 * Uses a direct fetch() call because createApiClient.post() always sets
 * Content-Type to application/json and serialises the body as JSON. For raw
 * bytes (file upload, markdown text) we need to send the caller-supplied
 * Content-Type and body verbatim.
 *
 * All client guarantees still apply: Bearer token, 30s timeout, dev logging.
 * §6.3 silent-refresh is handled by the same _fetch logic wired into the
 * token accessor.
 *
 * On success: the node-content query and node-detail query are invalidated.
 * On error: a sonner toast shows the backend error.
 */
export function useUploadContent(id: number) {
  // We need the raw auth object to get the current token for the direct fetch.
  const auth = useAuth();
  const queryClient = useQueryClient();

  return useMutation<void, DivoidApiError, UploadContentInput>({
    mutationFn: async ({ body, contentType }) => {
      const token = auth.user?.access_token;
      const baseUrl = API_BASE_URL;

      // 30-second timeout — mirrors createApiClient._fetch behaviour.
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 30_000);

      let response: Response;
      try {
        response = await fetch(`${baseUrl}${API.NODES.CONTENT(id)}`, {
          method: 'POST',
          headers: {
            'Content-Type': contentType,
            ...(token ? { Authorization: `Bearer ${token}` } : {}),
          },
          body,
          signal: controller.signal,
        });
      } finally {
        clearTimeout(timeoutId);
      }

      if (!response.ok) {
        // Mirror createApiClient's error mapping.
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
        throw new DivoidApiError(response.status, code, text);
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: nodeContentQueryKey(id) });
      queryClient.invalidateQueries({ queryKey: nodeQueryKey(id) });
    },
    onError: (error) => toastError(error),
  });
}
