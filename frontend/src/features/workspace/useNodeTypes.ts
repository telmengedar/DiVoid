/**
 * useNodeTypes — fetch the live node-type catalog from GET /api/types and
 * convert it into FilterOption[] for the workspace type-filter popover.
 *
 * ## Response quirk (DiVoid task #486)
 *
 * GET /api/types returns 28 entries today. 13 of those have NO `type` field —
 * they correspond to structural group nodes (Tasks group, Docs group, etc.)
 * whose underlying NodeType row has a null/empty name. These are exactly the
 * nodes the existing UNTYPED_VALUE synthetic option was designed to match.
 *
 * Decision: map type-absent rows → UNTYPED_VALUE instead of filtering them out.
 * The UNTYPED_VALUE option already exists in the workspace type filter and shows
 * these group nodes by default. Including these rows in the mapping is the most
 * honest representation of what is actually in the graph. If all 13 absent-type
 * rows land on the same synthetic key, they merge into one checkbox — correct.
 *
 * ## Sort order
 *
 * The backend returns entries sorted count DESC, type ASC (nulls last). We
 * re-sort alphabetically for the dropdown because A→Z scanning is friendlier
 * for a checkbox list than "most common first". UNTYPED_VALUE is appended last
 * to preserve the existing convention from #318.
 *
 * ## Loading state — option (c) optimistic-default (per #486)
 *
 * While the query is in-flight, `options` returns the hardcoded fallback list
 * (ALL_NODE_TYPES mapped to FilterOption[]). The UI never shows an empty list,
 * and once live data arrives the list updates in place with any newly-discovered
 * types (e.g. `product`, `meeting`, `qa-note`). Zero flash, zero degradation.
 *
 * ## Default-select for new types
 *
 * The hook exposes `liveTypeValues: string[]` so callers (useWorkspaceFilters)
 * can merge newly-discovered types into the current selection. Any type present
 * in liveTypeValues but absent from the saved sessionStorage selection gets
 * auto-selected (all-on-by-default rule from #318).
 *
 * Task: DiVoid node #486
 */

import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { NodeTypeEntry, Page } from '@/types/divoid';
import type { FilterOption } from './WorkspaceFilterPopover';
import { ALL_NODE_TYPES, UNTYPED_VALUE } from './useWorkspaceFilters';

// ─── Fallback list (option c: optimistic-default while loading) ───────────────

/** Human-readable labels for synthetic / abbreviated type values. */
const TYPE_LABELS: Record<string, string> = {
  [UNTYPED_VALUE]: 'untyped',
};

/** Maps the hardcoded ALL_NODE_TYPES to FilterOption[] for the loading state. */
const FALLBACK_OPTIONS: FilterOption[] = ALL_NODE_TYPES.map((t) => ({
  value: t,
  label: TYPE_LABELS[t] ?? t,
}));

// ─── Query key ────────────────────────────────────────────────────────────────

/**
 * TanStack query key for the type catalog.
 * Shape: ['types'] — no selectors needed (catalog is global, not per-user/project).
 * See §8.1 of the frontend code contracts for the key-shape convention.
 */
export const nodeTypesQueryKey = ['types'] as const;

// ─── Hook ─────────────────────────────────────────────────────────────────────

export interface NodeTypesResult {
  /**
   * Filter options ready to pass to WorkspaceFilterPopover.
   * Falls back to FALLBACK_OPTIONS until the live data resolves.
   */
  options: FilterOption[];

  /**
   * The raw type values from the live catalog (excluding UNTYPED_VALUE synthetic).
   * Includes newly-discovered types (e.g. `product`, `meeting`).
   * Empty while loading; use for merging into the current selection.
   */
  liveTypeValues: string[];

  /** True while the types query is in-flight on first load. */
  isLoading: boolean;
}

/**
 * Fetch the live node-type catalog and convert it into checkbox options for the
 * workspace type filter.
 *
 * Returns FALLBACK_OPTIONS (the existing hardcoded list) until data arrives so
 * the dropdown is never empty (optimistic-default loading strategy, option c).
 */
export function useNodeTypes(): NodeTypesResult {
  const client = useApiClient();

  const { data, isLoading } = useQuery<Page<NodeTypeEntry>>({
    queryKey: nodeTypesQueryKey,
    queryFn: ({ signal }) =>
      client.get<Page<NodeTypeEntry>>(API.TYPES, {}, signal),
    // Types change rarely — a 5-minute stale window keeps the UI fresh without
    // hammering the endpoint on every workspace visit.
    staleTime: 5 * 60 * 1_000,
    // Types endpoint is always reachable — no guard needed.
    enabled: true,
    // 404 / 500 are permanent — don't retry.
    retry: false,
  });

  if (!data) {
    return { options: FALLBACK_OPTIONS, liveTypeValues: [], isLoading };
  }

  // Map each entry: rows with no `type` field → UNTYPED_VALUE.
  // Use a Set to deduplicate (all 13 absent-type rows collapse into one
  // UNTYPED_VALUE entry; any future duplicates in the real-type set are safe too).
  const typeSet = new Set<string>();
  for (const entry of data.result) {
    typeSet.add(entry.type ?? UNTYPED_VALUE);
  }

  // Real types (everything except the synthetic key), sorted alphabetically.
  const realTypes = [...typeSet]
    .filter((t) => t !== UNTYPED_VALUE)
    .sort((a, b) => a.localeCompare(b));

  // UNTYPED_VALUE appended last (existing convention from #318).
  const allValues = typeSet.has(UNTYPED_VALUE)
    ? [...realTypes, UNTYPED_VALUE]
    : realTypes;

  const options: FilterOption[] = allValues.map((t) => ({
    value: t,
    label: TYPE_LABELS[t] ?? t,
  }));

  return {
    options,
    liveTypeValues: realTypes,
    isLoading,
  };
}
