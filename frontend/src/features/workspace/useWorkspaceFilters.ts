/**
 * useWorkspaceFilters — manages the type and status filter selections for the
 * workspace canvas, persisted via sessionStorage.
 *
 * ## Filter contract
 *
 * Type filter:
 *  - Options: a known set of observed types + synthetic "untyped" for type-null nodes.
 *  - Default: all selected.
 *  - sessionStorage key: divoid.workspace.typeFilter
 *
 * Status filter:
 *  - Options: new, open, in-progress, closed, fixed + synthetic "nostatus" for null.
 *  - Default: all EXCEPT closed and fixed (they clutter the active workspace).
 *  - sessionStorage key: divoid.workspace.statusFilter
 *
 * ## Synthetic values
 *  - UNTYPED_VALUE = "__untyped__" — synthetic key for type=null nodes.
 *  - NO_STATUS_VALUE = "__nostatus__" — synthetic key for status=null nodes.
 *
 * Both are NOT sent to the API directly; the caller must check for them and
 * translate to the correct API parameters (nostatus=true / separate notype fetch).
 *
 * Task: DiVoid node #318
 */

import { useState, useCallback } from 'react';

// ─── Constants ────────────────────────────────────────────────────────────────

/** Synthetic option value representing nodes with null/empty type. */
export const UNTYPED_VALUE = '__untyped__';

/** Synthetic option value representing nodes with null/empty status. */
export const NO_STATUS_VALUE = '__nostatus__';

/** All known node types in the DiVoid graph (vocabulary per node #9). */
export const ALL_NODE_TYPES: string[] = [
  'task',
  'bug',
  'documentation',
  'session-log',
  'project',
  'organization',
  'agent',
  'person',
  'chat',
  'feature',
  'status',
  UNTYPED_VALUE,
];

/** All known status values (vocabulary per node #9). */
export const ALL_STATUS_VALUES: string[] = [
  'new',
  'open',
  'in-progress',
  NO_STATUS_VALUE,
  'closed',
  'fixed',
];

/**
 * Status values excluded by default to keep the active workspace uncluttered.
 * The user can opt in by toggling them.
 */
export const DEFAULT_EXCLUDED_STATUSES = new Set(['closed', 'fixed']);

// ─── sessionStorage helpers ───────────────────────────────────────────────────

const TYPE_FILTER_KEY   = 'divoid.workspace.typeFilter';
const STATUS_FILTER_KEY = 'divoid.workspace.statusFilter';

function loadSet(key: string, fallback: string[]): string[] {
  try {
    const raw = sessionStorage.getItem(key);
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (Array.isArray(parsed) && parsed.every((v) => typeof v === 'string')) {
        return parsed as string[];
      }
    }
  } catch {
    // sessionStorage unavailable or corrupt — fall back.
  }
  return fallback;
}

function saveSet(key: string, values: string[]): void {
  try {
    sessionStorage.setItem(key, JSON.stringify(values));
  } catch {
    // Best-effort.
  }
}

// ─── Default selections ───────────────────────────────────────────────────────

/** Default type selection: all types selected. */
export const DEFAULT_TYPE_SELECTION: string[] = [...ALL_NODE_TYPES];

/** Default status selection: all except closed/fixed. */
export const DEFAULT_STATUS_SELECTION: string[] = ALL_STATUS_VALUES.filter(
  (s) => !DEFAULT_EXCLUDED_STATUSES.has(s),
);

// ─── Hook ─────────────────────────────────────────────────────────────────────

export interface WorkspaceFilters {
  /** Currently selected type values (includes UNTYPED_VALUE if selected). */
  selectedTypes: string[];
  /** Currently selected status values (includes NO_STATUS_VALUE if selected). */
  selectedStatuses: string[];

  /** Toggle a single type value on or off. */
  toggleType: (value: string) => void;
  /** Toggle a single status value on or off. */
  toggleStatus: (value: string) => void;

  /**
   * Whether the type filter is in its default (all-selected) state.
   * Used to decide whether to show the badge on the trigger button.
   */
  typeFilterActive: boolean;
  /**
   * Whether the status filter is in its default state.
   * Used to decide whether to show the badge on the trigger button.
   */
  statusFilterActive: boolean;
}

export function useWorkspaceFilters(): WorkspaceFilters {
  const [selectedTypes, setSelectedTypes] = useState<string[]>(() =>
    loadSet(TYPE_FILTER_KEY, DEFAULT_TYPE_SELECTION),
  );

  const [selectedStatuses, setSelectedStatuses] = useState<string[]>(() =>
    loadSet(STATUS_FILTER_KEY, DEFAULT_STATUS_SELECTION),
  );

  const toggleType = useCallback((value: string) => {
    setSelectedTypes((prev) => {
      const next = prev.includes(value)
        ? prev.filter((v) => v !== value)
        : [...prev, value];
      saveSet(TYPE_FILTER_KEY, next);
      return next;
    });
  }, []);

  const toggleStatus = useCallback((value: string) => {
    setSelectedStatuses((prev) => {
      const next = prev.includes(value)
        ? prev.filter((v) => v !== value)
        : [...prev, value];
      saveSet(STATUS_FILTER_KEY, next);
      return next;
    });
  }, []);

  // Badge / active state: is the filter NOT in its default state?
  const typeFilterActive =
    selectedTypes.length !== DEFAULT_TYPE_SELECTION.length ||
    !DEFAULT_TYPE_SELECTION.every((t) => selectedTypes.includes(t));

  const statusFilterActive =
    selectedStatuses.length !== DEFAULT_STATUS_SELECTION.length ||
    !DEFAULT_STATUS_SELECTION.every((s) => selectedStatuses.includes(s));

  return {
    selectedTypes,
    selectedStatuses,
    toggleType,
    toggleStatus,
    typeFilterActive,
    statusFilterActive,
  };
}
