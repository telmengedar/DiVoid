/**
 * useNodeAdjacency — fetch link adjacency rows for a set of visible node ids.
 *
 * Calls GET /api/nodes/links?ids=<id1>,<id2>,...
 * Returns every NodeLink where sourceId OR targetId is in the given ids set.
 * The caller (WorkspaceCanvas) filters client-side to draw only edges where
 * BOTH endpoints are in the visible set — this is intentional: the endpoint
 * returns all incident edges, the canvas renders only fully-visible ones.
 *
 * Query key includes a sorted copy of nodeIds so that order-changes in xyflow's
 * node array do not cause spurious refetches.
 *
 * Design: docs/architecture/workspace-mode.md §5.9
 * Task: DiVoid node #230
 */

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { Page, NodeLink } from '@/types/divoid';

/**
 * Returns adjacency rows for the given visible node ids.
 *
 * @param nodeIds Array of visible node ids. Pass an empty array to skip.
 */
export function useNodeAdjacency(nodeIds: number[]) {
  const client = useApiClient();

  // Sort ids so the query key is stable regardless of render order.
  const sortedIds = useMemo(() => [...nodeIds].sort((a, b) => a - b), [nodeIds]);

  return useQuery<Page<NodeLink>>({
    queryKey: nodeAdjacencyQueryKey(sortedIds),
    queryFn: ({ signal }) =>
      client.get<Page<NodeLink>>(
        API.NODES.ADJACENCY,
        { ids: sortedIds, count: 500 },
        signal,
      ),
    enabled: sortedIds.length > 0,
    staleTime: 1_000,
  });
}

/** TanStack Query key factory for adjacency queries. */
export function nodeAdjacencyQueryKey(sortedIds: number[]) {
  return ['nodes', 'links', 'incident', sortedIds] as const;
}
