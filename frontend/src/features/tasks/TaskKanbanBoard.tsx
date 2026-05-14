/**
 * TaskKanbanBoard — the full Kanban board for task-status management.
 *
 * Responsibilities:
 *  - Owns DndContext with PointerSensor (distance:5) + KeyboardSensor.
 *  - Owns the optimistic-move Map<nodeId, TaskStatus>.
 *  - On drag end: writes optimistic override → fires usePatchNode PATCH.
 *  - On success: clears the override (cache invalidation delivers real status).
 *  - On error: clears override (revert) + toast.error.
 *  - Renders one TaskKanbanColumn per visible status via useKanbanColumns.
 *  - DragOverlay renders the dragged card cleanly without layout thrash.
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
  closestCenter,
  type DragEndEvent,
} from '@dnd-kit/core';
import { sortableKeyboardCoordinates } from '@dnd-kit/sortable';
import { toast } from 'sonner';
import type { NodeDetails } from '@/types/divoid';
import { TASK_STATUSES, type TaskStatus } from '@/features/nodes/schemas';
import { usePatchNode } from '@/features/nodes/mutations';
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

  // usePatchNode requires a fixed id at hook call time; we use a mutable
  // ref pattern via a callback that instantiates the mutation per node.
  // However, React rules prohibit conditional hook calls, so we instead
  // expose a generic patch factory approach. Since each drag resolves to
  // exactly one node id, we track the "pending" id in state and call
  // usePatchNode unconditionally with it (defaulting to 0 when idle).
  const [patchTargetId, setPatchTargetId] = useState<number>(0);
  const patchMutation = usePatchNode(patchTargetId);

  const columns = useKanbanColumns({ tasks, selectedStatuses, optimisticOverrides });

  const handleDragStart = useCallback(
    (event: { active: { id: string | number } }) => {
      const task = tasks.find((t) => t.id === event.active.id);
      setActiveTask(task ?? null);
    },
    [tasks],
  );

  const handleDragEnd = useCallback(
    (event: DragEndEvent) => {
      const { active, over } = event;
      setActiveTask(null);

      if (!over) return;

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

      // 2. Set the patch target id, then fire the mutation.
      setPatchTargetId(nodeId);
      patchMutation.mutate(
        [{ op: 'replace', path: '/status', value: targetStatus }],
        {
          onSuccess: () => {
            // Clear this override — the invalidate-refetch will deliver the
            // canonical status. Keeping the override would cause a flicker.
            setOptimisticOverrides((prev) => {
              const next = new Map(prev);
              next.delete(nodeId);
              return next;
            });
          },
          onError: (error) => {
            // Revert: clear the override so the card returns to its real column.
            setOptimisticOverrides((prev) => {
              const next = new Map(prev);
              next.delete(nodeId);
              return next;
            });
            // Show the error (toastError is internal to usePatchNode, but we
            // want an explicit contextual message here too).
            const message =
              error && typeof error === 'object' && 'text' in error
                ? (error as { text: string }).text
                : 'Failed to update task status.';
            toast.error(message);
          },
        },
      );
    },
    [tasks, optimisticOverrides, patchMutation],
  );

  return (
    <DndContext
      sensors={sensors}
      collisionDetection={closestCenter}
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
