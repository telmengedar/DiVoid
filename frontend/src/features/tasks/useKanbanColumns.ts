/**
 * useKanbanColumns — derives ordered column data for the Kanban board.
 *
 * Rules:
 *  - Column set comes from TASK_STATUSES (not TASK_FILTER_STATUSES; Kanban is task-only).
 *  - A column appears IFF its status is in selectedStatuses.
 *  - Column ORDER follows TASK_STATUSES array order, NOT selectedStatuses order.
 *  - optimisticOverrides shadows the real status for cards that were just dropped.
 *
 * Pure hook — no side effects, no API calls.
 *
 * Task: DiVoid node #384
 */

import { useMemo } from 'react';
import { TASK_STATUSES, type TaskStatus } from '@/features/nodes/schemas';
import type { NodeDetails } from '@/types/divoid';
import type { TaskFilterStatus } from './useTaskStatusFilter';

export interface KanbanColumn {
  status: TaskStatus;
  tasks: NodeDetails[];
}

export interface UseKanbanColumnsInput {
  tasks: NodeDetails[];
  selectedStatuses: TaskFilterStatus[];
  optimisticOverrides: Map<number, TaskStatus>;
}

/**
 * Returns columns in TASK_STATUSES order, filtered to selectedStatuses,
 * with optimistic status overrides applied before bucketing.
 */
export function useKanbanColumns({
  tasks,
  selectedStatuses,
  optimisticOverrides,
}: UseKanbanColumnsInput): KanbanColumn[] {
  return useMemo(() => {
    // Bucket tasks by effective status (override wins over real status).
    const buckets = new Map<TaskStatus, NodeDetails[]>();
    for (const status of TASK_STATUSES) {
      buckets.set(status, []);
    }

    for (const task of tasks) {
      const effectiveStatus =
        optimisticOverrides.get(task.id) ?? (task.status as TaskStatus);
      const bucket = buckets.get(effectiveStatus);
      if (bucket) {
        bucket.push(task);
      }
      // If effectiveStatus is not in TASK_STATUSES (e.g. 'fixed' bug slipped in),
      // we silently omit the card from the board per the out-of-scope rule.
    }

    // Produce columns in TASK_STATUSES order, intersected with selectedStatuses.
    const selected = new Set(selectedStatuses as string[]);
    const columns: KanbanColumn[] = [];
    for (const status of TASK_STATUSES) {
      if (!selected.has(status)) continue;
      const columnTasks = (buckets.get(status) ?? []).slice().sort((a, b) =>
        a.name.localeCompare(b.name),
      );
      columns.push({ status, tasks: columnTasks });
    }

    return columns;
  }, [tasks, selectedStatuses, optimisticOverrides]);
}
