/**
 * TaskListView — lists/boards tasks reachable via path query:
 *   [id:<projectId>]/[name:Tasks]/[type:task]
 *
 * Sub-changes (DiVoid task #384):
 *  1. List/Board toggle above the toolbar. Selected view persisted to
 *     sessionStorage `divoid.tasks.view.<projectId>`.
 *  2. In board mode, renders TaskKanbanBoard with the same data fetch.
 *  3. The toolbar (status pills + + New task + toggle) is shared across modes —
 *     both views receive the same URL query (invariant pinned by test 9).
 *
 * Earlier sub-changes (DiVoid task #369):
 *  1. Status filter pill row above the table. Default: new, open, in-progress.
 *     Persisted to sessionStorage; empty selection omits the ?status= param.
 *  2. "+ New task" button opens CreateTaskDialog pre-populated with
 *     type=task, status=new, linked to the project's Tasks group.
 *     If no Tasks group exists, the dialog shows a blocking message.
 *
 * When the path returns zero results, renders a topology-specific message
 * (not the generic "No results") to signal that the project's Tasks group
 * may not be linked correctly.
 *
 * Each row links to the generic /nodes/:id detail page (no task-specific
 * detail view in step 1).
 */

import { useState } from 'react';
import { Plus } from 'lucide-react';
import { useNodePath } from '@/features/nodes/useNodePath';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { ROUTES } from '@/lib/constants';
import {
  useTaskStatusFilter,
  TASK_FILTER_STATUSES,
  type TaskFilterStatus,
} from './useTaskStatusFilter';
import { useProjectTasksGroup } from './useProjectTasksGroup';
import { CreateTaskDialog } from './CreateTaskDialog';
import { TasksViewToggle, loadTasksView, saveTasksView, type TasksView } from './TasksViewToggle';
import { TaskKanbanBoard } from './TaskKanbanBoard';

interface TaskListViewProps {
  projectId: number;
}

// ─── Status pill row ──────────────────────────────────────────────────────────

interface StatusPillRowProps {
  statuses: readonly TaskFilterStatus[];
  selected: TaskFilterStatus[];
  onToggle: (s: TaskFilterStatus) => void;
}

function StatusPillRow({ statuses, selected, onToggle }: StatusPillRowProps) {
  return (
    <div
      className="flex flex-wrap gap-2"
      role="group"
      aria-label="Status filter"
      data-testid="status-filter-pills"
    >
      {statuses.map((s) => {
        const isSelected = selected.includes(s);
        return (
          <button
            key={s}
            type="button"
            onClick={() => onToggle(s)}
            aria-pressed={isSelected}
            className={[
              'inline-flex items-center rounded-full px-3 py-1 text-xs font-medium border transition-colors',
              isSelected
                ? 'bg-primary text-primary-foreground border-primary'
                : 'bg-background text-muted-foreground border-border hover:border-foreground/40',
            ].join(' ')}
          >
            {s}
          </button>
        );
      })}
    </div>
  );
}

// ─── TaskListView ─────────────────────────────────────────────────────────────

export function TaskListView({ projectId }: TaskListViewProps) {
  const { selectedStatuses, toggleStatus, statusParam } = useTaskStatusFilter();
  const tasksGroup = useProjectTasksGroup(projectId);
  const [createOpen, setCreateOpen] = useState(false);
  const [view, setView] = useState<TasksView>(() => loadTasksView(projectId));

  const path = `[id:${projectId}]/[name:Tasks]/[type:task]`;

  // Pass the status filter to the path query. undefined statusParam = omit the
  // parameter entirely (show all statuses when user deselects everything).
  const statusFilter = statusParam ? { status: statusParam.split(',') } : {};

  const { data, isLoading, isError, error } = useNodePath(path, {
    count: 100,
    sort: 'status',
    ...statusFilter,
  });

  function handleViewChange(next: TasksView) {
    saveTasksView(projectId, next);
    setView(next);
  }

  return (
    <div className="flex flex-col gap-4" data-testid="task-list">
      {/* Toolbar: status filter + new task button + view toggle */}
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <StatusPillRow
          statuses={TASK_FILTER_STATUSES}
          selected={selectedStatuses}
          onToggle={toggleStatus}
        />
        <div className="flex items-center gap-2 shrink-0">
          <TasksViewToggle view={view} onViewChange={handleViewChange} />
          <button
            type="button"
            onClick={() => setCreateOpen(true)}
            className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-border text-sm font-medium hover:bg-muted transition-colors shrink-0"
            data-testid="new-task-button"
          >
            <Plus size={14} aria-hidden="true" />
            New task
          </button>
        </div>
      </div>

      {/* Error state */}
      {isError && (
        <div
          role="alert"
          className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          {error && typeof error === 'object' && 'text' in error
            ? (error as { text: string }).text
            : 'Failed to load tasks.'}
        </div>
      )}

      {/* Topology-empty signal: distinguish "no tasks" from generic empty state.
          Shown instead of the table/board when we have a resolved-empty result.   */}
      {!isError && !isLoading && data && data.result.length === 0 ? (
        <p className="text-sm text-muted-foreground" data-testid="topology-empty">
          No tasks reachable via <code className="font-mono">{path}</code>. Tasks not linked under
          this project&apos;s Tasks group are not shown here — that is by design.
        </p>
      ) : !isError ? (
        view === 'board' ? (
          /* Board view */
          <TaskKanbanBoard
            tasks={data?.result ?? []}
            selectedStatuses={selectedStatuses}
          />
        ) : (
          /* List view (table) — shown while loading (skeleton) and once data arrives */
          <NodeResultTable
            nodes={data?.result ?? []}
            loading={isLoading}
            getRowHref={(node) => ROUTES.NODE_DETAIL(node.id)}
          />
        )
      ) : null}

      {/* Create task dialog */}
      <CreateTaskDialog
        open={createOpen}
        onOpenChange={setCreateOpen}
        tasksGroupId={tasksGroup.id}
      />
    </div>
  );
}
