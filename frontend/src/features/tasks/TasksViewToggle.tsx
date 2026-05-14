/**
 * TasksViewToggle — pill-style List / Board toggle.
 *
 * Persists the selected view to sessionStorage under
 * `divoid.tasks.view.<projectId>`. Defaults to "list".
 *
 * Matches the existing StatusPillRow aesthetic — no new Radix dependency.
 *
 * Task: DiVoid node #384
 */

import { List, LayoutGrid } from 'lucide-react';

export type TasksView = 'list' | 'board';

function storageKey(projectId: number): string {
  return `divoid.tasks.view.${projectId}`;
}

export function loadTasksView(projectId: number): TasksView {
  try {
    const raw = sessionStorage.getItem(storageKey(projectId));
    if (raw === 'list' || raw === 'board') return raw;
  } catch {
    // sessionStorage unavailable — fall back to default.
  }
  return 'list';
}

export function saveTasksView(projectId: number, view: TasksView): void {
  try {
    sessionStorage.setItem(storageKey(projectId), view);
  } catch {
    // Best-effort.
  }
}

interface TasksViewToggleProps {
  view: TasksView;
  onViewChange: (view: TasksView) => void;
}

const OPTIONS: { value: TasksView; label: string; Icon: typeof List }[] = [
  { value: 'list', label: 'List', Icon: List },
  { value: 'board', label: 'Board', Icon: LayoutGrid },
];

export function TasksViewToggle({ view, onViewChange }: TasksViewToggleProps) {
  return (
    <div
      className="inline-flex rounded-full border border-border overflow-hidden shrink-0"
      role="group"
      aria-label="View toggle"
      data-testid="tasks-view-toggle"
    >
      {OPTIONS.map(({ value, label, Icon }) => {
        const isSelected = view === value;
        return (
          <button
            key={value}
            type="button"
            onClick={() => onViewChange(value)}
            aria-pressed={isSelected}
            data-testid={`tasks-view-toggle-${value}`}
            className={[
              'inline-flex items-center gap-1.5 px-3 py-1 text-xs font-medium transition-colors',
              isSelected
                ? 'bg-primary text-primary-foreground'
                : 'bg-background text-muted-foreground hover:bg-muted',
            ].join(' ')}
          >
            <Icon size={13} aria-hidden="true" />
            {label}
          </button>
        );
      })}
    </div>
  );
}
