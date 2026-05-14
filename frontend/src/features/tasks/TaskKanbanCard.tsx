/**
 * TaskKanbanCard — a single draggable task card for the Kanban board.
 *
 * Responsibilities:
 *  - Wraps with useDraggable({ id: nodeId }) from @dnd-kit/core.
 *  - Renders: type badge + name (line-clamp-2).
 *  - NO status badge — the column IS the status.
 *  - Click navigates to ROUTES.NODE_DETAIL(node.id).
 *
 * Task: DiVoid node #384
 */

import { useDraggable } from '@dnd-kit/core';
import { useNavigate } from 'react-router-dom';
import { ROUTES } from '@/lib/constants';
import type { NodeDetails } from '@/types/divoid';

interface TaskKanbanCardProps {
  task: NodeDetails;
  /** When true (used inside DragOverlay), disables drag interaction. */
  overlay?: boolean;
}

export function TaskKanbanCard({ task, overlay = false }: TaskKanbanCardProps) {
  const navigate = useNavigate();
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({
    id: task.id,
    disabled: overlay,
  });

  function handleClick(e: React.MouseEvent) {
    // Only navigate on a real click — not at end of a drag.
    // dnd-kit fires pointer events even when dragging starts; the
    // activationConstraint distance guard on PointerSensor ensures short
    // movements are still clicks, so we navigate freely here.
    e.stopPropagation();
    navigate(ROUTES.NODE_DETAIL(task.id));
  }

  return (
    <div
      ref={setNodeRef}
      {...listeners}
      {...attributes}
      onClick={handleClick}
      data-testid={`kanban-card-${task.id}`}
      className={[
        'group rounded-md border border-border bg-card p-2.5 cursor-pointer',
        'hover:border-foreground/30 hover:shadow-sm transition-all',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        isDragging ? 'opacity-40' : '',
      ]
        .filter(Boolean)
        .join(' ')}
      tabIndex={0}
      role="button"
      aria-label={`Task: ${task.name}`}
    >
      <span className="mb-1 inline-flex items-center rounded bg-muted px-1.5 py-0.5 text-xs font-mono text-muted-foreground">
        {task.type ?? 'task'}
      </span>
      <p className="line-clamp-2 text-sm font-medium text-foreground leading-snug mt-0.5">
        {task.name}
      </p>
    </div>
  );
}
