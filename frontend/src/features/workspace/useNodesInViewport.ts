/**
 * useNodesInViewport — fetch nodes whose canvas position falls inside the
 * given viewport rectangle.
 *
 * Calls GET /api/nodes?bounds=xMin,yMin,xMax,yMax&fields=id,type,name,status,x,y
 * The x/y fields are explicitly requested so the DTO includes position data.
 *
 * Bounds-padding is applied by the caller (WorkspaceCanvas) before passing to
 * this hook. The hook stores the raw padded bounds as the TanStack query key so
 * that panning automatically triggers a new fetch via key change.
 *
 * Node cap: count is capped at MAX_VIEWPORT_NODES (250) to prevent xyflow choke
 * at high zoom-out levels. Users can zoom in to see more.
 *
 * Design: docs/architecture/workspace-mode.md §5.8
 * Task: DiVoid node #230
 */

import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { Page, PositionedNodeDetails } from '@/types/divoid';

/** Maximum nodes rendered in one viewport to keep xyflow responsive. */
export const MAX_VIEWPORT_NODES = 250;

/** Viewport bounding box in canvas world coordinates. */
export type ViewportBounds = [xMin: number, yMin: number, xMax: number, yMax: number];

/**
 * Returns nodes positioned inside the given viewport bounds.
 *
 * @param bounds Padded viewport rectangle [xMin, yMin, xMax, yMax].
 *               Pass null to skip the query (e.g. when canvas is not yet sized).
 */
export function useNodesInViewport(bounds: ViewportBounds | null) {
  const client = useApiClient();

  return useQuery<Page<PositionedNodeDetails>>({
    queryKey: nodesInViewportQueryKey(bounds),
    queryFn: ({ signal }) =>
      client.get<Page<PositionedNodeDetails>>(
        API.NODES.LIST,
        {
          bounds: bounds ?? undefined,
          fields: ['id', 'type', 'name', 'status', 'x', 'y'],
          count: MAX_VIEWPORT_NODES,
          nototal: true,
        },
        signal,
      ),
    enabled: bounds !== null,
    staleTime: 1_000,
  });
}

/** TanStack Query key factory for viewport node queries. */
export function nodesInViewportQueryKey(bounds: ViewportBounds | null) {
  return ['nodes', 'viewport', bounds] as const;
}
