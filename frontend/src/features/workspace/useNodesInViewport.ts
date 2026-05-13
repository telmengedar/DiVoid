/**
 * useNodesInViewport — fetch nodes whose canvas position falls inside the
 * given viewport rectangle, optionally filtered by type and status.
 *
 * Calls GET /api/nodes?bounds=xMin,yMin,xMax,yMax&fields=id,type,name,status,x,y
 * with optional ?type=task,bug,...&status=open,in-progress,...&nostatus=true
 *
 * ## Type-null handling (DiVoid #318)
 *
 * The backend has no `notype=true` parameter symmetric to `nostatus=true`.
 * We use option (b) from the spec: client-side merge.
 *
 *   When UNTYPED_VALUE is in the selectedTypes list:
 *   1. Fetch the typed subset with ?type=<real types> (this query).
 *   2. Also fetch the full unfiltered set (useUntypedNodesInViewport) and
 *      client-side filter to type-null only.
 *   3. The WorkspaceCanvas merges both result sets.
 *
 * When UNTYPED_VALUE is NOT selected AND the only types in the selection are
 * real types, we pass only ?type=<real types> — the untyped fetch is skipped.
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
 * at high zoom-out levels. Users can zoom in to see more.
 *
 * Design: docs/architecture/workspace-mode.md §5.8
 * Task: DiVoid node #230 / #318
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

/** Derived query params sent to the backend (real types + nostatus flag). */
interface BackendParams {
  types: string[];       // real (non-synthetic) types
  statuses: string[];    // real (non-synthetic) statuses
  nostatus: boolean;     // true if NO_STATUS_VALUE is selected
  includeUntyped: boolean; // true if UNTYPED_VALUE is selected
}

/** Extract the backend-compatible params from filter selections. */
export function deriveBackendParams(filters: ViewportFilterParams): BackendParams {
  const realTypes    = filters.selectedTypes.filter((t) => t !== UNTYPED_VALUE);
  const includeUntyped = filters.selectedTypes.includes(UNTYPED_VALUE);

  const realStatuses = filters.selectedStatuses.filter((s) => s !== NO_STATUS_VALUE);
  const nostatus     = filters.selectedStatuses.includes(NO_STATUS_VALUE);

  return { types: realTypes, statuses: realStatuses, nostatus, includeUntyped };
}

/**
 * Returns nodes positioned inside the given viewport bounds, filtered by
 * the current type and status selections (real types only — caller handles
 * the untyped merge).
 *
 * @param bounds Padded viewport rectangle [xMin, yMin, xMax, yMax].
 *               Pass null to skip the query.
 * @param filters Current filter selections.
 * @param enabled Whether to fetch typed nodes at all (false when all real types
 *                are deselected but untyped is still selected).
 */
export function useNodesInViewport(
  bounds: ViewportBounds | null,
  filters?: ViewportFilterParams,
  enabled: boolean = true,
) {
  const client = useApiClient();
  const params = filters ? deriveBackendParams(filters) : null;

  // Build type param: if types is empty and we have a real filter, skip the typed query.
  // If no filter, pass all (no type= param).
  const typeParam   = params && params.types.length > 0 ? params.types : undefined;
  const statusParam = params && params.statuses.length > 0 ? params.statuses : undefined;
  const nostatusParam = params?.nostatus ?? undefined;

  // Should we actually run this query?
  // - bounds must be non-null
  // - enabled must be true
  // - When filtering is active, at least some real types must be selected (else skip)
  const shouldFetch =
    bounds !== null &&
    enabled &&
    (params === null || params.types.length > 0);

  return useQuery<Page<PositionedNodeDetails>>({
    queryKey: nodesInViewportQueryKey(bounds, filters),
    queryFn: ({ signal }) =>
      client.get<Page<PositionedNodeDetails>>(
        API.NODES.LIST,
        {
          bounds: bounds ?? undefined,
          fields: ['id', 'type', 'name', 'status', 'x', 'y'],
          count: MAX_VIEWPORT_NODES,
          nototal: true,
          ...(typeParam   !== undefined && { type:     typeParam }),
          ...(statusParam !== undefined && { status:   statusParam }),
          ...(nostatusParam              && { nostatus: true }),
        },
        signal,
      ),
    enabled: shouldFetch,
    staleTime: 1_000,
  });
}

/**
 * Returns nodes inside the viewport that have a null/empty type.
 * Used for the client-side "untyped" merge (option b from #318).
 * Only runs when includeUntyped is true.
 */
export function useUntypedNodesInViewport(
  bounds: ViewportBounds | null,
  filters: ViewportFilterParams,
) {
  const client = useApiClient();
  const { includeUntyped, statuses, nostatus } = deriveBackendParams(filters);
  const statusParam   = statuses.length > 0 ? statuses : undefined;
  const nostatusParam = nostatus || undefined;

  return useQuery<Page<PositionedNodeDetails>>({
    queryKey: ['nodes', 'viewport', 'untyped', bounds, statusParam, nostatusParam] as const,
    queryFn: ({ signal }) =>
      client.get<Page<PositionedNodeDetails>>(
        API.NODES.LIST,
        {
          bounds: bounds ?? undefined,
          fields: ['id', 'type', 'name', 'status', 'x', 'y'],
          count: MAX_VIEWPORT_NODES,
          nototal: true,
          // No type param → fetches all types, we filter client-side to null type
          ...(statusParam   !== undefined && { status:   statusParam }),
          ...(nostatusParam              && { nostatus: true }),
        },
        signal,
      ),
    enabled: bounds !== null && includeUntyped,
    staleTime: 1_000,
    // Client-side select: keep only nodes with null/empty type
    select: (page) => ({
      ...page,
      result: page.result.filter((n) => !n.type),
    }),
  });
}

/** TanStack Query key factory for viewport node queries. */
export function nodesInViewportQueryKey(
  bounds: ViewportBounds | null,
  filters?: ViewportFilterParams,
) {
  return ['nodes', 'viewport', bounds, filters ?? null] as const;
}
