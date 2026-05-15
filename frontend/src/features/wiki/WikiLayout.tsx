/**
 * WikiLayout — three-column wiki shell at `/wiki/:id`.
 *
 * Columns:
 *  LEFT  (~280px)  — WikiSideNav: linked neighbours + inline semantic search
 *  MAIN  (flex-1)  — WikiContentView: gated markdown content with edit surfaces
 *  RIGHT (~240px)  — optional metadata strip (deferred — not in W1/W2 scope)
 *
 * W2 additions in the article header:
 *  - "+ Add child page" → opens CreateNodeDialog → links new node → navigates.
 *    Navigate fires in a mutation onSuccess callback (Section 10.2).
 *    Bug #317 graceful path: 500 "Nodes already linked" treated as success
 *    (Section 11.5).
 *  - "Rename" → opens EditNodeDialog.
 *
 * Desktop-only (`max-w-7xl`). Mobile/responsive deferred.
 *
 * Invalid-id guard mirrors NodeDetailPage.tsx:366-374.
 *
 * Task: DiVoid node #421
 */

import { useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { FilePlus, Pencil } from 'lucide-react';
import { useNode } from '@/features/nodes/useNode';
import { useLinkNodes } from '@/features/nodes/mutations';
import { WikiSideNav } from './WikiSideNav';
import { WikiContentView } from './WikiContentView';
import { StatusBadge } from '@/components/common/StatusBadge';
import { CreateNodeDialog } from '@/features/nodes/CreateNodeDialog';
import { EditNodeDialog } from '@/features/nodes/EditNodeDialog';
import { DivoidApiError } from '@/types/divoid';
import { ROUTES } from '@/lib/constants';

/** Backend error text returned when linking an already-linked pair (bug #317). */
const ALREADY_LINKED_TEXT = 'Nodes already linked';

export function WikiLayout() {
  const { id: idParam } = useParams<{ id: string }>();
  const nodeId = idParam ? parseInt(idParam, 10) : 0;
  const navigate = useNavigate();

  const { data: node, isFetching, error } = useNode(nodeId);

  const [createOpen, setCreateOpen] = useState(false);
  const [renameOpen, setRenameOpen] = useState(false);

  const linkMutation = useLinkNodes();

  /**
   * Called when CreateNodeDialog successfully creates a new node.
   * Links the new node to the current wiki page, then navigates to it.
   *
   * Per Section 10.2: navigation fires inside the mutation callback,
   * never in the render body.
   *
   * Bug #317 graceful path (Section 11.5): useLinkNodes already handles
   * 500 "Nodes already linked" as success — the onSuccess here fires in
   * both the real-success and already-linked cases.
   */
  function handleChildCreated(newNodeId: number) {
    linkMutation.mutate(
      { sourceId: nodeId, targetId: newNodeId },
      {
        onSuccess: () => {
          navigate(ROUTES.WIKI_NODE(newNodeId));
        },
        onError: (error) => {
          // Bug #317 graceful path (Section 11.5): useLinkNodes already handled
          // 500 "Nodes already linked" in its global onError and showed toast.info.
          // The call-site onError still fires, so we check for the same shape and
          // navigate regardless — the pair is linked either way.
          const isAlreadyLinked =
            error instanceof DivoidApiError &&
            error.status === 500 &&
            error.text === ALREADY_LINKED_TEXT;
          if (isAlreadyLinked) {
            navigate(ROUTES.WIKI_NODE(newNodeId));
          }
          // Real link errors: useLinkNodes showed a toast; don't navigate.
        },
      },
    );
  }

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
        {/* Article heading + write affordances */}
        <div className="flex items-start justify-between gap-3 mb-1">
          <h1 className="text-xl font-semibold">
            {node?.name ?? `Node ${nodeId}`}
          </h1>

          {/* Write affordances — top-right of main pane */}
          {node && (
            <div className="flex items-center gap-2 shrink-0">
              <button
                type="button"
                onClick={() => setCreateOpen(true)}
                aria-label="Add child page"
                data-testid="wiki-add-child-btn"
                className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-border text-sm hover:bg-muted transition-colors"
              >
                <FilePlus size={14} aria-hidden="true" />
                + Add child page
              </button>
              <button
                type="button"
                onClick={() => setRenameOpen(true)}
                aria-label="Rename node"
                data-testid="wiki-rename-btn"
                className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-border text-sm hover:bg-muted transition-colors"
              >
                <Pencil size={14} aria-hidden="true" />
                Rename
              </button>
            </div>
          )}
        </div>

        {/* Inline status + type metadata */}
        <div className="flex items-center gap-3 mb-6 text-xs text-muted-foreground">
          {node?.type && (
            <span className="rounded bg-muted px-1.5 py-0.5 font-mono">{node.type}</span>
          )}
          <StatusBadge status={node?.status ?? null} />
        </div>

        {/* Content */}
        {node && <WikiContentView node={node} />}

        {/* Dialogs — only mounted when node is loaded */}
        {node && (
          <>
            <CreateNodeDialog
              open={createOpen}
              onOpenChange={setCreateOpen}
              onCreated={handleChildCreated}
              data-testid="wiki-create-node-dialog"
            />
            <EditNodeDialog
              open={renameOpen}
              onOpenChange={setRenameOpen}
              node={node}
            />
          </>
        )}
      </main>
    </div>
  );
}
