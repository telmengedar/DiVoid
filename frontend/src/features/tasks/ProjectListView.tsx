/**
 * ProjectListView — lists all project nodes linked to the given organisation.
 *
 * Each row links to /tasks/projects/:id to drill into that project's tasks.
 */

import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { ROUTES } from '@/lib/constants';
import type { NodeDetails } from '@/types/divoid';

interface ProjectListViewProps {
  orgId: number;
}

export function ProjectListView({ orgId }: ProjectListViewProps) {
  const { data, isLoading, isError, error } = useNodeListLinkedTo(orgId, {
    type: ['project'],
    sort: 'name',
    count: 100,
  });

  if (isError) {
    const msg = error && typeof error === 'object' && 'text' in error
      ? (error as { text: string }).text
      : 'Failed to load projects.';
    return (
      <div role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
        {msg}
      </div>
    );
  }

  return (
    <NodeResultTable
      nodes={data?.result ?? []}
      loading={isLoading}
      getRowHref={(node: NodeDetails) => ROUTES.TASKS_PROJECT(node.id)}
    />
  );
}
