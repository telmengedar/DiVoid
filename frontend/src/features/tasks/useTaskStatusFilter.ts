/**
 * useTaskStatusFilter — status filter for the task list view.
 *
 * Multi-select status pills above the task table. Default: new, open,
 * in-progress. Does NOT include a nostatus synthetic value — tasks always
 * have a status by topology convention (DiVoid node #9).
 *
 * Persisted to sessionStorage under divoid.tasks.statusFilter.
 *
 * Mirrors the pattern of useWorkspaceFilters, simplified to status only.
 *
 * Task: DiVoid node #369
 */

import { useState, useCallback } from 'react';

// ─── Vocabulary ───────────────────────────────────────────────────────────────

/**
 * All status values valid in the task list (includes bug-only "fixed" since
 * tasks typed "bug" may appear in the same list).
 */
export const TASK_FILTER_STATUSES = ['new', 'open', 'in-progress', 'closed', 'fixed'] as const;
export type TaskFilterStatus = (typeof TASK_FILTER_STATUSES)[number];

/** Statuses excluded by default (done/terminal states clutter the active view). */
export const TASK_FILTER_DEFAULT_EXCLUDED = new Set<string>(['closed', 'fixed']);

/** Default selection: all active statuses. */
export const TASK_FILTER_DEFAULT_SELECTION: TaskFilterStatus[] = TASK_FILTER_STATUSES.filter(
  (s) => !TASK_FILTER_DEFAULT_EXCLUDED.has(s),
);

// ─── sessionStorage helpers ───────────────────────────────────────────────────

const STORAGE_KEY = 'divoid.tasks.statusFilter';

function loadStatusFilter(): TaskFilterStatus[] {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (
        Array.isArray(parsed) &&
        parsed.every((v): v is TaskFilterStatus =>
          typeof v === 'string' &&
          (TASK_FILTER_STATUSES as readonly string[]).includes(v),
        )
      ) {
        return parsed;
      }
    }
  } catch {
    // sessionStorage unavailable or corrupt — fall back to default.
  }
  return TASK_FILTER_DEFAULT_SELECTION;
}

function saveStatusFilter(values: TaskFilterStatus[]): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(values));
  } catch {
    // Best-effort.
  }
}

// ─── Hook ─────────────────────────────────────────────────────────────────────

export interface TaskStatusFilter {
  /** Currently selected status values. */
  selectedStatuses: TaskFilterStatus[];
  /** Toggle a single status value on or off. */
  toggleStatus: (value: TaskFilterStatus) => void;
  /**
   * The `?status=<comma-list>` API parameter value, or undefined when
   * the selection is empty (omit the parameter entirely — see contract).
   */
  statusParam: string | undefined;
  /** True when the selection differs from the default. */
  filterActive: boolean;
}

export function useTaskStatusFilter(): TaskStatusFilter {
  const [selectedStatuses, setSelectedStatuses] = useState<TaskFilterStatus[]>(loadStatusFilter);

  const toggleStatus = useCallback((value: TaskFilterStatus) => {
    setSelectedStatuses((prev) => {
      const next = prev.includes(value)
        ? prev.filter((v) => v !== value)
        : [...prev, value];
      saveStatusFilter(next);
      return next;
    });
  }, []);

  // Empty selection → omit the parameter (show all). Non-empty → comma-joined.
  const statusParam =
    selectedStatuses.length === 0 ? undefined : selectedStatuses.join(',');

  const filterActive =
    selectedStatuses.length !== TASK_FILTER_DEFAULT_SELECTION.length ||
    !TASK_FILTER_DEFAULT_SELECTION.every((s) => selectedStatuses.includes(s));

  return { selectedStatuses, toggleStatus, statusParam, filterActive };
}
