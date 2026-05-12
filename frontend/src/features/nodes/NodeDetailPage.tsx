/**
 * NodeDetailPage — /nodes/:id route.
 *
 * Shows four canonical regions:
 *  1. Metadata — id, type, name, status, contentType.
 *  2. Content blob — markdown rendered with rehype-sanitize; raw text fallback.
 *  3. Linked neighbours — via useNodeListLinkedTo (clicking through navigates).
 *  4. Error state — DivoidApiError.text via sonner toast; 404 shows inline.
 *
 * Write affordances (permission-gated via useWhoami):
 *  - "Edit" button → EditNodeDialog (patch name / status)
 *  - "Delete" button → DeleteNodeDialog (confirm + navigate away)
 *  - "Add link" button → LinkNodeDialog (semantic search + link)
 *  - "Unlink" per neighbour row → DELETE link
 *  - "Upload content" / drag-drop → ContentUploadZone
 *
 * Permission gating: write buttons are hidden when whoami.permissions lacks
 * "write". The backend remains the security boundary; this is UX only.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5, §5.6, §6.6, §9.3
 * Task: DiVoid node #229
 */

import { useState, useEffect } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { toast } from 'sonner';
import { ChevronLeft, Pencil, Trash2, Link2, Unlink, UploadCloud, X } from 'lucide-react';
import { useNode } from './useNode';
import { useNodeContent } from './useNodeContent';
import { useNodeListLinkedTo } from './useNodeListLinkedTo';
import { useUnlinkNodes } from './mutations';
import { EditNodeDialog } from './EditNodeDialog';
import { DeleteNodeDialog } from './DeleteNodeDialog';
import { LinkNodeDialog } from './LinkNodeDialog';
import { ContentUploadZone } from './ContentUploadZone';
import { MarkdownEditorSurface, isTextShaped } from './MarkdownEditorSurface';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { DivoidApiError } from '@/types/divoid';
import type { NodeDetails } from '@/types/divoid';
import { ROUTES } from '@/lib/constants';
import { useWhoami } from '@/features/auth/useWhoami';

// ─── Metadata region ──────────────────────────────────────────────────────────

function MetadataRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-start gap-3 py-1.5 border-b border-border last:border-0">
      <span className="w-28 shrink-0 text-xs font-medium text-muted-foreground">{label}</span>
      <span className="text-sm">{value}</span>
    </div>
  );
}

function StatusBadge({ status }: { status: string | null }) {
  if (!status) return <span className="text-muted-foreground">—</span>;

  const colorMap: Record<string, string> = {
    open: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400',
    'in-progress': 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400',
    closed: 'bg-muted text-muted-foreground',
    new: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
    fixed: 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400',
  };

  const classes =
    colorMap[status] ?? 'bg-muted text-muted-foreground';

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${classes}`}>
      {status}
    </span>
  );
}

// ─── Content region ───────────────────────────────────────────────────────────

interface ContentRegionProps {
  nodeId: number;
  contentType?: string;
  canWrite: boolean;
}

type ContentMode = 'read' | 'edit';

function ContentRegion({ nodeId, contentType, canWrite }: ContentRegionProps) {
  const { data: content, isFetching, error } = useNodeContent(nodeId);
  const [mode, setMode] = useState<ContentMode>('read');
  const [showUpload, setShowUpload] = useState(false);

  useEffect(() => {
    if (error instanceof DivoidApiError) {
      toast.error(`Content: ${error.code}: ${error.text}`);
    }
  }, [error]);

  if (isFetching && !content) {
    return (
      <div className="space-y-2 animate-pulse" aria-label="Loading content">
        {[...Array(6)].map((_, i) => (
          <div key={i} className="h-4 rounded bg-muted" style={{ width: `${70 + (i % 3) * 10}%` }} />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <p className="text-sm text-muted-foreground italic" role="alert">
        Content unavailable.
      </p>
    );
  }

  const isMarkdown =
    !contentType ||
    contentType.includes('markdown') ||
    contentType.includes('text/plain');

  const canEdit = canWrite && isTextShaped(contentType || '');

  return (
    <div className="flex flex-col gap-4">
      {/* Mode toggle for text-shaped content */}
      {canEdit && (
        <div className="flex items-center gap-2">
          {mode === 'read' ? (
            <button
              type="button"
              onClick={() => setMode('edit')}
              aria-label="Edit content"
              className="inline-flex items-center gap-1.5 self-start text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              <Pencil size={15} aria-hidden="true" />
              Edit content
            </button>
          ) : (
            <button
              type="button"
              onClick={() => setMode('read')}
              aria-label="Cancel editing"
              className="inline-flex items-center gap-1.5 self-start text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              <X size={15} aria-hidden="true" />
              Cancel
            </button>
          )}
        </div>
      )}

      {/* Editor surface (text-shaped content, edit mode) */}
      {mode === 'edit' && canEdit && (
        <MarkdownEditorSurface
          nodeId={nodeId}
          initialContent={content ?? ''}
        />
      )}

      {/* Read display (always visible in read mode) */}
      {mode === 'read' && (
        <>
          {!content ? (
            <p className="text-sm text-muted-foreground italic">No content.</p>
          ) : isMarkdown ? (
            <div
              className="prose prose-sm dark:prose-invert max-w-none"
              aria-label="Node content"
            >
              <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
                {content}
              </ReactMarkdown>
            </div>
          ) : (
            <pre className="text-sm font-mono whitespace-pre-wrap break-words bg-muted/40 rounded-md p-4 overflow-x-auto">
              {content}
            </pre>
          )}
        </>
      )}

      {/* Upload affordance (drag-drop, always available for write users) */}
      {canWrite && (
        <>
          <button
            type="button"
            onClick={() => setShowUpload((v) => !v)}
            className="inline-flex items-center gap-1.5 self-start text-sm text-muted-foreground hover:text-foreground transition-colors"
            aria-expanded={showUpload}
          >
            <UploadCloud size={15} aria-hidden="true" />
            {showUpload ? 'Hide upload' : 'Replace content via file'}
          </button>
          {showUpload && <ContentUploadZone nodeId={nodeId} />}
        </>
      )}
    </div>
  );
}

// ─── Neighbours region ────────────────────────────────────────────────────────

interface NeighboursRegionProps {
  nodeId: number;
  canWrite: boolean;
  onAddLink: () => void;
}

function UnlinkButton({ sourceId, targetId }: { sourceId: number; targetId: number }) {
  const mutation = useUnlinkNodes();

  const handleClick = () => {
    mutation.mutate({ sourceId, targetId });
  };

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={mutation.isPending}
      title="Unlink this node"
      aria-label={`Unlink node ${targetId}`}
      className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-destructive disabled:opacity-50 transition-colors px-1.5 py-0.5 rounded hover:bg-destructive/10"
    >
      <Unlink size={12} aria-hidden="true" />
      Unlink
    </button>
  );
}

function NeighboursRegion({ nodeId, canWrite, onAddLink }: NeighboursRegionProps) {
  const { data, isFetching, error } = useNodeListLinkedTo(nodeId, { count: 100 });

  useEffect(() => {
    if (error instanceof DivoidApiError) {
      toast.error(`Neighbours: ${error.code}: ${error.text}`);
    }
  }, [error]);

  const nodes = data?.result ?? [];

  return (
    <section aria-labelledby="neighbours-heading">
      <div className="flex items-center justify-between mb-3">
        <h2 id="neighbours-heading" className="text-sm font-semibold">
          Linked nodes
          {data && data.total >= 0 && (
            <span className="ml-2 text-muted-foreground font-normal">({data.total})</span>
          )}
        </h2>
        {canWrite && (
          <button
            type="button"
            onClick={onAddLink}
            className="inline-flex items-center gap-1.5 h-7 px-3 rounded-md border border-border text-xs font-medium hover:bg-muted transition-colors"
          >
            <Link2 size={13} aria-hidden="true" />
            Add link
          </button>
        )}
      </div>

      {/* Custom table with unlink affordance per row */}
      {isFetching ? (
        <NodeResultTable nodes={[]} loading />
      ) : nodes.length === 0 ? (
        <p className="text-sm text-muted-foreground">No linked nodes.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-border">
          <table className="w-full text-sm" role="table" aria-label="Linked nodes">
            <thead>
              <tr className="border-b border-border bg-muted/40">
                <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground w-16">ID</th>
                <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground">Type</th>
                <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground">Name</th>
                <th scope="col" className="px-3 py-2 text-left font-medium text-muted-foreground">Status</th>
                {canWrite && (
                  <th scope="col" className="px-3 py-2 text-right font-medium text-muted-foreground w-20">
                    <span className="sr-only">Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {nodes.map((n: NodeDetails) => (
                <tr key={n.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-3 py-2 tabular-nums text-muted-foreground">{n.id}</td>
                  <td className="px-3 py-2">
                    <span className="rounded bg-muted px-1.5 py-0.5 text-xs font-mono">{n.type ?? '—'}</span>
                  </td>
                  <td className="px-3 py-2 font-medium">
                    <Link
                      to={ROUTES.NODE_DETAIL(n.id)}
                      className="text-foreground hover:text-primary underline-offset-2 hover:underline transition-colors"
                    >
                      {n.name ?? `Node ${n.id}`}
                    </Link>
                  </td>
                  <td className="px-3 py-2">
                    <StatusBadge status={n.status} />
                  </td>
                  {canWrite && (
                    <td className="px-3 py-2 text-right">
                      <UnlinkButton sourceId={nodeId} targetId={n.id} />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export function NodeDetailPage() {
  const { id: idParam } = useParams<{ id: string }>();
  const nodeId = idParam ? parseInt(idParam, 10) : 0;
  const navigate = useNavigate();

  const { data: node, isFetching, error } = useNode(nodeId);
  const { data: whoami } = useWhoami();
  const canWrite = whoami?.permissions?.includes('write') ?? false;

  const [editOpen, setEditOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [linkOpen, setLinkOpen] = useState(false);

  useEffect(() => {
    if (error instanceof DivoidApiError && error.status !== 404) {
      toast.error(`${error.code}: ${error.text}`);
    }
  }, [error]);

  if (!idParam || isNaN(nodeId) || nodeId <= 0) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Invalid node ID.
        </p>
      </div>
    );
  }

  if (isFetching) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6 space-y-4">
        <div className="h-7 w-48 rounded bg-muted animate-pulse" />
        <div className="space-y-2">
          {[...Array(4)].map((_, i) => (
            <div key={i} className="h-5 rounded bg-muted animate-pulse" style={{ width: `${40 + i * 15}%` }} />
          ))}
        </div>
      </div>
    );
  }

  if (error instanceof DivoidApiError && error.status === 404) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-muted-foreground" role="alert">
          Node {nodeId} not found.
        </p>
      </div>
    );
  }

  if (error && !node) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Failed to load node. Please try again.
        </p>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-5xl px-4 py-6 flex flex-col gap-6">
      {/* Back link */}
      <Link
        to={ROUTES.SEARCH}
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
      >
        <ChevronLeft size={15} aria-hidden="true" />
        Back to search
      </Link>

      {/* Metadata region */}
      <section aria-labelledby="metadata-heading">
        <div className="flex items-start justify-between gap-3 mb-4">
          <h1 id="metadata-heading" className="text-xl font-semibold">
            {node?.name ?? `Node ${nodeId}`}
          </h1>

          {/* Write affordances — hidden when user lacks write permission */}
          {canWrite && node && (
            <div className="flex items-center gap-2 shrink-0">
              <button
                type="button"
                onClick={() => setEditOpen(true)}
                className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-border text-sm hover:bg-muted transition-colors"
                aria-label="Edit node"
              >
                <Pencil size={14} aria-hidden="true" />
                Edit
              </button>
              <button
                type="button"
                onClick={() => setDeleteOpen(true)}
                className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-destructive/50 text-destructive text-sm hover:bg-destructive/10 transition-colors"
                aria-label="Delete node"
              >
                <Trash2 size={14} aria-hidden="true" />
                Delete
              </button>
            </div>
          )}
        </div>

        <div className="rounded-md border border-border px-4 py-1">
          <MetadataRow label="ID" value={<span className="tabular-nums">{nodeId}</span>} />
          <MetadataRow
            label="Type"
            value={
              node?.type ? (
                <span className="rounded bg-muted px-1.5 py-0.5 text-xs font-mono">
                  {node.type}
                </span>
              ) : (
                '—'
              )
            }
          />
          <MetadataRow label="Status" value={<StatusBadge status={node?.status ?? null} />} />
          <MetadataRow
            label="Content type"
            value={
              <span className="text-muted-foreground text-xs font-mono">
                {node?.contentType ?? '—'}
              </span>
            }
          />
        </div>
      </section>

      {/* Content region */}
      <section aria-labelledby="content-heading">
        <h2 id="content-heading" className="text-sm font-semibold mb-3">
          Content
        </h2>
        <ContentRegion
          nodeId={nodeId}
          contentType={node?.contentType}
          canWrite={canWrite}
        />
      </section>

      {/* Neighbours region */}
      <NeighboursRegion
        nodeId={nodeId}
        canWrite={canWrite}
        onAddLink={() => setLinkOpen(true)}
      />

      {/* Dialogs — only mounted when node is loaded */}
      {node && (
        <>
          <EditNodeDialog open={editOpen} onOpenChange={setEditOpen} node={node} />
          <DeleteNodeDialog
            open={deleteOpen}
            onOpenChange={setDeleteOpen}
            node={node}
            onDeleted={() => navigate(ROUTES.SEARCH)}
          />
          <LinkNodeDialog open={linkOpen} onOpenChange={setLinkOpen} sourceId={nodeId} />
        </>
      )}
    </div>
  );
}
