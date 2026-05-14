/**
 * TaskKanbanColumn — a single Kanban column (one status).
 *
 * Responsibilities:
 *  - Registers as a drop target via useDroppable({ id: status }).
 *  - Renders column header: status name + count badge, coloured per the
 *    existing status colour palette (sourced from NodeResultTable.tsx:31).
 *  - Renders TaskKanbanCards sorted by name.
 *
 * Task: DiVoid node #384
 */

import { useDroppable } from '@dnd-kit/core';
import type { TaskStatus } from '@/features/nodes/schemas';
import type { NodeDetails } from '@/types/divoid';
import { TaskKanbanCard } from './TaskKanbanCard';

/** Status → header colour classes, mirrored from NodeResultTable.tsx:31 colorMap. */
const STATUS_COLORS: Record<TaskStatus, string> = {
  new: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
  open: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400',
  'in-progress': 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400',
  closed: 'bg-muted text-muted-foreground',
};

interface TaskKanbanColumnProps {
  status: TaskStatus;
  tasks: NodeDetails[];
}

export function TaskKanbanColumn({ status, tasks }: TaskKanbanColumnProps) {
  const { setNodeRef, isOver } = useDroppable({ id: status });

  return (
    <div
      className="flex flex-col gap-2 min-w-[220px] flex-1"
      data-testid={`kanban-column-${status}`}
      data-kanban-column="true"
    >
      {/* Column header */}
      <div className="flex items-center justify-between px-1">
        <span
          className={[
            'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium capitalize',
            STATUS_COLORS[status] ?? 'bg-muted text-muted-foreground',
          ].join(' ')}
        >
          {status}
        </span>
        <span className="text-xs text-muted-foreground tabular-nums">
          {tasks.length}
        </span>
      </div>

      {/* Drop zone */}
      <div
        ref={setNodeRef}
        data-testid={`kanban-column-drop-${status}`}
        className={[
          'flex flex-col gap-2 rounded-lg border border-dashed p-2 min-h-[120px] transition-colors',
          isOver
            ? 'border-primary/60 bg-primary/5'
            : 'border-border bg-muted/20',
        ].join(' ')}
      >
        {tasks.map((task) => (
          <TaskKanbanCard key={task.id} task={task} />
        ))}
      </div>
    </div>
  );
}
