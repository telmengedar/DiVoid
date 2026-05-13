/**
 * TaskListView — lists tasks reachable via path query:
 *   [id:<projectId>]/[name:Tasks]/[type:task]
 *
 * When the path returns zero results, renders a topology-specific message
 * (not the generic "No results") to signal that the project's Tasks group
 * may not be linked correctly.
 *
 * Each row links to the generic /nodes/:id detail page (no task-specific
 * detail view in step 1).
 */

import { useNodePath } from '@/features/nodes/useNodePath';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { ROUTES } from '@/lib/constants';

interface TaskListViewProps {
  projectId: number;
}

export function TaskListView({ projectId }: TaskListViewProps) {
  const path = `[id:${projectId}]/[name:Tasks]/[type:task]`;

  const { data, isLoading, isError, error } = useNodePath(path, {
    count: 100,
    sort: 'status',
  });

  if (isError) {
    const msg = error && typeof error === 'object' && 'text' in error
      ? (error as { text: string }).text
      : 'Failed to load tasks.';
    return (
      <div role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
        {msg}
      </div>
    );
  }

  // Topology-empty signal: distinguish "no tasks" from the generic empty state.
  // This means the project has no Tasks group linked, or the group has no tasks.
  if (!isLoading && data && data.total === 0 && data.result.length === 0) {
    return (
      <p className="text-sm text-muted-foreground" data-testid="topology-empty">
        No tasks reachable via <code className="font-mono">{path}</code>. Tasks not linked under
        this project&apos;s Tasks group are not shown here — that is by design.
      </p>
    );
  }

  return (
    <NodeResultTable
      nodes={data?.result ?? []}
      loading={isLoading}
      getRowHref={(node) => ROUTES.NODE_DETAIL(node.id)}
    />
  );
}
