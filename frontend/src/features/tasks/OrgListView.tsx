/**
 * OrgListView — lists all organisation nodes.
 *
 * Each row links to /tasks/orgs/:id to drill into that org's projects.
 */

import { useNodeList } from '@/features/nodes/useNodeList';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { ROUTES } from '@/lib/constants';
import type { NodeDetails } from '@/types/divoid';

export function OrgListView() {
  const { data, isLoading, isError, error } = useNodeList({
    type: ['organization'],
    sort: 'name',
    count: 100,
  });

  if (isError) {
    const msg = error && typeof error === 'object' && 'text' in error
      ? (error as { text: string }).text
      : 'Failed to load organisations.';
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
      getRowHref={(node: NodeDetails) => ROUTES.TASKS_ORG(node.id)}
    />
  );
}
