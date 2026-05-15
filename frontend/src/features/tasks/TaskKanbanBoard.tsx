/**
 * TaskKanbanBoard — the full Kanban board for task-status management.
 *
 * Responsibilities:
 *  - Owns DndContext with PointerSensor (distance:5) + KeyboardSensor.
 *  - Owns the optimistic-move Map<nodeId, TaskStatus>.
 *  - On drag end: writes optimistic override → fires PATCH via useApiClient directly.
 *  - On success: clears the override (cache invalidation delivers real status).
 *  - On error: clears override (revert) + toast.error.
 *  - Renders one TaskKanbanColumn per visible status via useKanbanColumns.
 *  - DragOverlay renders the dragged card cleanly without layout thrash.
 *
 * NOTE: We call useApiClient().patch() directly in handleDragEnd rather than
 * usePatchNode(id) because usePatchNode binds id into a closure at hook-call time.
 * setPatchTargetId(nodeId) only schedules the next render, so patchMutation on the
 * current render was built with the OLD id (initially 0) — every drag would PATCH
 * /nodes/0 → 404. Passing id at call time avoids this stale-closure trap entirely.
 * The principled refactor (accept id at mutate() time) is tracked as DiVoid task #393.
 *
 * Task: DiVoid node #384
 */

import { useState, useCallback } from 'react';
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  KeyboardSensor,
  useSensor,
  useSensors,
  pointerWithin,
  type DragEndEvent,
} from '@dnd-kit/core';
import { sortableKeyboardCoordinates } from '@dnd-kit/sortable';
import { toast } from 'sonner';
import { useQueryClient } from '@tanstack/react-query';
import type { NodeDetails } from '@/types/divoid';
import { TASK_STATUSES, type TaskStatus } from '@/features/nodes/schemas';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { TaskFilterStatus } from './useTaskStatusFilter';
import { useKanbanColumns } from './useKanbanColumns';
import { TaskKanbanColumn } from './TaskKanbanColumn';
import { TaskKanbanCard } from './TaskKanbanCard';

interface TaskKanbanBoardProps {
  tasks: NodeDetails[];
  selectedStatuses: TaskFilterStatus[];
}

export function TaskKanbanBoard({ tasks, selectedStatuses }: TaskKanbanBoardProps) {
  const [optimisticOverrides, setOptimisticOverrides] = useState<Map<number, TaskStatus>>(
    new Map(),
  );
  const [activeTask, setActiveTask] = useState<NodeDetails | null>(null);

  // Use the API client and query client directly so the id is captured at
  // call time (not at hook-call time) — avoids the stale-closure /nodes/0 trap.
  const client = useApiClient();
  const queryClient = useQueryClient();

  const sensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        // Require 5px movement before a drag starts so clicks-to-navigate
        // are not accidentally treated as drags (test 10 depends on this).
        distance: 5,
      },
    }),
    useSensor(KeyboardSensor, {
      coordinateGetter: sortableKeyboardCoordinates,
    }),
  );

  const columns = useKanbanColumns({ tasks, selectedStatuses, optimisticOverrides });

  const handleDragStart = useCallback(
    (event: { active: { id: string | number } }) => {
      const task = tasks.find((t) => t.id === event.active.id);
      setActiveTask(task ?? null);
    },
    [tasks],
  );

  const handleDragEnd = useCallback(
    async (event: DragEndEvent) => {
      const { active, over } = event;
      setActiveTask(null);

      // Fix B (bug #406): surface a visible warning when the drop misses every
      // column (over===null). This turns the "silent snap-back" into actionable
      // feedback for the user. Also emit a dev-only console.warn for diagnostics.
      // Same-column bails are intentional no-ops and do NOT get this warning.
      if (!over) {
        if (import.meta.env.DEV) {
          console.warn('[Kanban] drag released outside any droppable column — over===null');
        }
        toast.warning('Drop missed a column — try again with more movement');
        return;
      }

      const nodeId = active.id as number;
      const targetStatus = over.id as TaskStatus;

      // Guard: only valid TASK_STATUSES accepted as drop targets.
      if (!(TASK_STATUSES as readonly string[]).includes(targetStatus)) return;

      // Find the current effective status of the dragged card.
      const currentStatus =
        optimisticOverrides.get(nodeId) ??
        (tasks.find((t) => t.id === nodeId)?.status as TaskStatus | undefined);

      // No-op: dropped on the same column.
      if (currentStatus === targetStatus) return;

      // 1. Write optimistic override (immutable Map replace).
      const newOverrides = new Map(optimisticOverrides);
      newOverrides.set(nodeId, targetStatus);
      setOptimisticOverrides(newOverrides);

      // 2. PATCH the node — id is known at call time, no stale-closure risk.
      try {
        await client.patch<void>(API.NODES.DETAIL(nodeId), [
          { op: 'replace', path: '/status', value: targetStatus },
        ]);
        // Success: clear the override — cache invalidation delivers real status.
        setOptimisticOverrides((prev) => {
          const next = new Map(prev);
          next.delete(nodeId);
          return next;
        });
        // Invalidate the entire 'nodes' prefix — list, linkedto, path, detail — so
        // ALL views (including the path-query that backs this Kanban) pick up the
        // new status. TanStack matches by prefix; ~10 queries in practice. Cheap.
        // Bug #411: the old narrow invalidations ['nodes','list'] + ['nodes','linkedto']
        // missed the path-query key ['nodes','path',...] used by useNodePath, leaving
        // the cache stale and snapping the card back to its pre-drag column.
        queryClient.invalidateQueries({ queryKey: ['nodes'] });
      } catch (error) {
        // Revert: clear the override so the card returns to its real column.
        setOptimisticOverrides((prev) => {
          const next = new Map(prev);
          next.delete(nodeId);
          return next;
        });
        const message =
          error && typeof error === 'object' && 'text' in error
            ? (error as { text: string }).text
            : 'Failed to update task status.';
        toast.error(message);
      }
    },
    [tasks, optimisticOverrides, client, queryClient],
  );

  return (
    // Fix C (bug #406): pointerWithin returns the droppable whose rect contains
    // the pointer, or null if none. Replaces closestCenter which would pick the
    // nearest droppable even when the cursor was between columns — causing
    // same-column false positives and masking genuine missed drops.
    <DndContext
      sensors={sensors}
      collisionDetection={pointerWithin}
      onDragStart={handleDragStart}
      onDragEnd={handleDragEnd}
    >
      <div
        className="flex gap-4 overflow-x-auto pb-2"
        data-testid="kanban-board"
        aria-label="Kanban board"
      >
        {columns.map((col) => (
          <TaskKanbanColumn key={col.status} status={col.status} tasks={col.tasks} />
        ))}
      </div>

      {/* DragOverlay: renders the card following the cursor without layout thrash */}
      <DragOverlay>
        {activeTask ? <TaskKanbanCard task={activeTask} overlay /> : null}
      </DragOverlay>
    </DndContext>
  );
}
