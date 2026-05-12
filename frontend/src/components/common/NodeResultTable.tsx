/**
 * NodeResultTable — read-only table for displaying a page of NodeDetails.
 *
 * Columns: id, type, name, status, similarity (only when any result has it).
 * Each row's name cell is a link to /nodes/:id.
 *
 * Pure presentation: no data fetching, no logic beyond what is needed to render.
 */

import { Link } from 'react-router-dom';
import type { NodeDetails } from '@/types/divoid';
import { ROUTES } from '@/lib/constants';
import { cn } from '@/lib/cn';

interface NodeResultTableProps {
  nodes: NodeDetails[];
  /** When true, shows a skeleton loading state instead of rows. */
  loading?: boolean;
}

function StatusBadge({ status }: { status: string | null }) {
  if (!status) {
    return <span className="text-muted-foreground text-xs">—</span>;
  }

  const colorMap: Record<string, string> = {
    open: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400',
    'in-progress': 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400',
    closed: 'bg-muted text-muted-foreground',
    new: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
    fixed: 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400',
  };

  const classes = colorMap[status] ?? 'bg-muted text-muted-foreground';

  return (
    <span
      className={cn(
        'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium',
        classes,
      )}
    >
      {status}
    </span>
  );
}

function SkeletonRow() {
  return (
    <tr aria-hidden="true">
      {[...Array(4)].map((_, i) => (
        <td key={i} className="px-3 py-2">
          <div className="h-4 rounded bg-muted animate-pulse" style={{ width: `${60 + i * 10}%` }} />
        </td>
      ))}
    </tr>
  );
}

export function NodeResultTable({ nodes, loading = false }: NodeResultTableProps) {
  const hasSimilarity = nodes.some((n) => n.similarity !== undefined);

  return (
    <div className="overflow-x-auto rounded-md border border-border">
      <table className="w-full text-sm" role="table" aria-label="Node results">
        <thead>
          <tr className="border-b border-border bg-muted/40">
            <th
              scope="col"
              className="px-3 py-2 text-left font-medium text-muted-foreground w-16"
            >
              ID
            </th>
            <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground">
              Type
            </th>
            <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground">
              Name
            </th>
            <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground">
              Status
            </th>
            {hasSimilarity && (
              <th
                scope="col"
                className="px-3 py-2 text-right font-medium text-muted-foreground w-24"
              >
                Similarity
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {loading ? (
            <>
              <SkeletonRow />
              <SkeletonRow />
              <SkeletonRow />
            </>
          ) : nodes.length === 0 ? (
            <tr>
              <td
                colSpan={hasSimilarity ? 5 : 4}
                className="px-3 py-6 text-center text-muted-foreground"
              >
                No results
              </td>
            </tr>
          ) : (
            nodes.map((node) => (
              <tr
                key={node.id}
                className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
              >
                <td className="px-3 py-2 tabular-nums text-muted-foreground">{node.id}</td>
                <td className="px-3 py-2">
                  <span className="rounded bg-muted px-1.5 py-0.5 text-xs font-mono">
                    {node.type ?? '—'}
                  </span>
                </td>
                <td className="px-3 py-2 font-medium">
                  <Link
                    to={ROUTES.NODE_DETAIL(node.id)}
                    className="text-foreground hover:text-primary underline-offset-2 hover:underline transition-colors"
                  >
                    {node.name ?? `Node ${node.id}`}
                  </Link>
                </td>
                <td className="px-3 py-2">
                  <StatusBadge status={node.status} />
                </td>
                {hasSimilarity && (
                  <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">
                    {node.similarity !== undefined
                      ? (node.similarity * 100).toFixed(1) + '%'
                      : '—'}
                  </td>
                )}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}
