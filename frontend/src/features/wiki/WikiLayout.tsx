/**
 * WikiLayout — three-column wiki shell at `/wiki/:id`.
 *
 * Columns:
 *  LEFT  (~280px)  — WikiSideNav: linked neighbours + inline semantic search
 *  MAIN  (flex-1)  — WikiContentView: gated markdown content
 *  RIGHT (~240px)  — optional metadata strip (deferred — not in W1 scope)
 *
 * Desktop-only (`max-w-7xl`). Mobile/responsive deferred to a later PR.
 *
 * Invalid-id guard mirrors NodeDetailPage.tsx:366-374.
 *
 * Task: DiVoid node #413
 */

import { useParams } from 'react-router-dom';
import { useNode } from '@/features/nodes/useNode';
import { WikiSideNav } from './WikiSideNav';
import { WikiContentView } from './WikiContentView';
import { StatusBadge } from '@/components/common/StatusBadge';
import { DivoidApiError } from '@/types/divoid';

export function WikiLayout() {
  const { id: idParam } = useParams<{ id: string }>();
  const nodeId = idParam ? parseInt(idParam, 10) : 0;

  const { data: node, isFetching, error } = useNode(nodeId);

  if (!idParam || isNaN(nodeId) || nodeId <= 0) {
    return (
      <div className="mx-auto max-w-7xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Invalid node ID.
        </p>
      </div>
    );
  }

  if (isFetching && !node) {
    return (
      <div className="flex h-full">
        {/* Side-nav skeleton */}
        <div className="w-70 shrink-0 border-r border-border p-3 space-y-2">
          {[...Array(6)].map((_, i) => (
            <div key={i} className="h-8 rounded bg-muted animate-pulse" />
          ))}
        </div>
        {/* Content skeleton */}
        <div className="flex-1 px-6 py-6 space-y-3">
          <div className="h-7 w-56 rounded bg-muted animate-pulse" />
          {[...Array(5)].map((_, i) => (
            <div key={i} className="h-4 rounded bg-muted animate-pulse" style={{ width: `${60 + i * 8}%` }} />
          ))}
        </div>
      </div>
    );
  }

  if (error instanceof DivoidApiError && error.status === 404) {
    return (
      <div className="mx-auto max-w-7xl px-4 py-6">
        <p className="text-sm text-muted-foreground" role="alert">
          Node {nodeId} not found.
        </p>
      </div>
    );
  }

  if (error && !node) {
    return (
      <div className="mx-auto max-w-7xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Failed to load node. Please try again.
        </p>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-7xl flex h-full overflow-hidden">
      {/* ── Left: side-nav (~280px) ── */}
      <aside className="w-[280px] shrink-0 border-r border-border overflow-y-auto">
        <WikiSideNav nodeId={nodeId} />
      </aside>

      {/* ── Main: content (flex-1) ── */}
      <main className="flex-1 overflow-y-auto px-6 py-6" aria-label="Wiki article">
        {/* Article heading */}
        <h1 className="text-xl font-semibold mb-1">
          {node?.name ?? `Node ${nodeId}`}
        </h1>

        {/* Inline status + type metadata */}
        <div className="flex items-center gap-3 mb-6 text-xs text-muted-foreground">
          {node?.type && (
            <span className="rounded bg-muted px-1.5 py-0.5 font-mono">{node.type}</span>
          )}
          <StatusBadge status={node?.status ?? null} />
        </div>

        {/* Content */}
        {node && <WikiContentView node={node} />}
      </main>
    </div>
  );
}
