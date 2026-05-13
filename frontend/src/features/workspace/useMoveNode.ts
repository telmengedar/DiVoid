/**
 * useMoveNode — PATCH /api/nodes/{id} with new X and Y on drag-end.
 *
 * Fires a single PATCH with both /X and /Y replace operations so position
 * is always persisted atomically. Called only on drag-END (not on every tick)
 * so we do not spam the backend with intermediate drag positions.
 *
 * On success: no cache invalidation. The local xyflow controlled state already
 * reflects the new position (xyflow handles drag optimistically). The server
 * now agrees with the client — refetching to re-confirm what both sides already
 * know caused spurious re-renders and "blink" after every drag. The viewport
 * query's normal stale/refetch cycle picks up any concurrent mutations.
 *
 * On error: sonner toast. The viewport query refetches and snaps the node
 * back to its server-side position on next poll.
 *
 * Design: docs/architecture/workspace-mode.md §5.10
 * Task: DiVoid node #230, perf task #343
 */

import { useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import { DivoidApiError } from '@/types/divoid';
import { nodesInViewportQueryKey } from './useNodesInViewport';

export interface MoveNodeInput {
  id: number;
  x: number;
  y: number;
}

/**
 * Returns a mutation for persisting a node's new canvas position.
 * Fire on drag-end; do not call on every drag tick.
 */
export function useMoveNode() {
  const client = useApiClient();

  return useMutation<void, DivoidApiError, MoveNodeInput>({
    mutationFn: ({ id, x, y }) =>
      client.patch<void>(API.NODES.DETAIL(id), [
        { op: 'replace', path: '/X', value: x },
        { op: 'replace', path: '/Y', value: y },
      ]),
    // No onSuccess invalidation: local xyflow state already reflects the new
    // position and the server now agrees. Invalidating here caused spurious
    // refetches and a visible "blink" on every drag-end (task #343 win #1).
    onError: (error) => {
      if (error instanceof DivoidApiError) {
        if (error.status === 403) {
          toast.error("You don't have permission to move nodes.");
        } else {
          toast.error(`Failed to save position: ${error.text}`);
        }
      } else {
        toast.error('Failed to save node position.');
      }
    },
  });
}

/** Re-export so WorkspaceCanvas can call the same key factory for consistency. */
export { nodesInViewportQueryKey };
