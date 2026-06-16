/**
 * useNodesInViewport — fetch nodes whose canvas position falls inside the
 * given viewport rectangle, filtered by type and status server-side.
 *
 * Calls GET /api/nodes?bounds=xMin,yMin,xMax,yMax&fields=id,type,name,status,x,y,links
 * with optional ?type=task,bug,...&notype=true&status=open,...&nostatus=true
 *
 * Each row carries links: number[] (neighbor ids), so WorkspaceCanvas can render
 * edges without a second round-trip. Design: DiVoid #310 / #1213.
 *
 * ## Type-null handling (DiVoid #1976)
 *
 * The backend now exposes `notype=true` symmetric to `nostatus=true` (PR #146).
 * When UNTYPED_VALUE is in selectedTypes, notype=true is added to the query
 * alongside any real type params — the backend returns (real types) ∪ (null-type)
 * in a single response. The two-query client-side merge from #318 option (b) is
 * replaced by this single server-side filter.
 *
 * The query runs whenever bounds are non-null. A "deselect everything including
 * untyped" case yields zero rows from the backend — this is correct.
 *
 * ## Status-null handling
 *
 * When NO_STATUS_VALUE is in selectedStatuses, we pass &nostatus=true to include
 * null-status nodes alongside the named statuses.
 *
 * The bounds-padding is applied by the caller (WorkspaceCanvas) before passing to
 * this hook. The hook stores the raw padded bounds + filter params as the TanStack
 * query key so that changes automatically trigger a new fetch.
 *
 * Node cap: count is capped at MAX_VIEWPORT_NODES (250) to prevent xyflow choke
 * at high zoom-out levels. When total > MAX_VIEWPORT_NODES the caller surfaces
 * a truncation badge so users know to zoom in.
 *
 * Design: docs/architecture/workspace-mode.md §5.8
 * Task: DiVoid node #230 / #318 / #1976
 */

import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { Page, PositionedNodeDetails } from '@/types/divoid';
import { UNTYPED_VALUE, NO_STATUS_VALUE } from './useWorkspaceFilters';

/** Maximum nodes rendered in one viewport to keep xyflow responsive. */
export const MAX_VIEWPORT_NODES = 250;

/** Viewport bounding box in canvas world coordinates. */
export type ViewportBounds = [xMin: number, yMin: number, xMax: number, yMax: number];

/** Filter selections passed into the viewport query. */
export interface ViewportFilterParams {
  /** Selected type values; may include UNTYPED_VALUE. */
  selectedTypes: string[];
  /** Selected status values; may include NO_STATUS_VALUE. */
  selectedStatuses: string[];
}

/** Derived query params sent to the backend. */
interface BackendParams {
  types: string[];       // real (non-synthetic) types
  statuses: string[];    // real (non-synthetic) statuses
  nostatus: boolean;     // true if NO_STATUS_VALUE is selected
  notype: boolean;       // true if UNTYPED_VALUE is selected (#1976)
}

/** Extract the backend-compatible params from filter selections. */
export function deriveBackendParams(filters: ViewportFilterParams): BackendParams {
  const realTypes = filters.selectedTypes.filter((t) => t !== UNTYPED_VALUE);
  const notype    = filters.selectedTypes.includes(UNTYPED_VALUE);

  const realStatuses = filters.selectedStatuses.filter((s) => s !== NO_STATUS_VALUE);
  const nostatus     = filters.selectedStatuses.includes(NO_STATUS_VALUE);

  return { types: realTypes, statuses: realStatuses, nostatus, notype };
}

/**
 * Returns nodes positioned inside the given viewport bounds, filtered by
 * the current type and status selections fully server-side.
 *
 * When UNTYPED_VALUE is in filters.selectedTypes, notype=true is sent to the
 * backend alongside any real type values — the backend unions (typed) and
 * (null-type) rows in a single response (DiVoid #1976 / PR #146).
 *
 * @param bounds Padded viewport rectangle [xMin, yMin, xMax, yMax].
 *               Pass null to skip the query.
 * @param filters Current filter selections.
 */
export function useNodesInViewport(
  bounds: ViewportBounds | null,
  filters?: ViewportFilterParams,
) {
  const client = useApiClient();
  const params = filters ? deriveBackendParams(filters) : null;

  const typeParam     = params && params.types.length > 0 ? params.types : undefined;
  const statusParam   = params && params.statuses.length > 0 ? params.statuses : undefined;
  const nostatusParam = params?.nostatus ?? false;
  const notypeParam   = params?.notype   ?? false;

  return useQuery<Page<PositionedNodeDetails>>({
    queryKey: nodesInViewportQueryKey(bounds, filters),
    queryFn: ({ signal }) =>
      client.get<Page<PositionedNodeDetails>>(
        API.NODES.LIST,
        {
          bounds: bounds ?? undefined,
          fields: ['id', 'type', 'name', 'status', 'x', 'y', 'links'],
          count: MAX_VIEWPORT_NODES,
          ...(typeParam     !== undefined && { type:     typeParam }),
          ...(statusParam   !== undefined && { status:   statusParam }),
          ...(nostatusParam               && { nostatus: true }),
          ...(notypeParam                 && { notype:   true }),
        },
        signal,
      ),
    enabled: bounds !== null,
    staleTime: 1_000,
  });
}

/** TanStack Query key factory for viewport node queries. */
export function nodesInViewportQueryKey(
  bounds: ViewportBounds | null,
  filters?: ViewportFilterParams,
) {
  return ['nodes', 'viewport', bounds, filters ?? null] as const;
}
