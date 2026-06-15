/**
 * NodeDetailView — shared detail view for a DiVoid node.
 *
 * Renders four canonical regions (metadata, content, neighbours, dialogs)
 * identically whether mounted in the /nodes/:id route or the workspace peek
 * modal. Container-specific behaviour is injected via two optional callbacks:
 *
 *  - onClose: called when an action implies the view should dismiss (Delete
 *    success). Undefined in route context; supplied by modal container.
 *  - onNeighbourClick: called when a neighbour row is clicked. Undefined in
 *    route context (rows are <Link> elements); supplied by modal container
 *    (rows become buttons that swap the peek).
 *
 * The view does NOT own: route-param parsing, the back button, or container
 * chrome (padding, max-width). Those belong to the caller.
 *
 * Design: docs/architecture/workspace-modal-preview.md §5.1
 * Task: DiVoid node #1253
 * Extracted from: NodeDetailPage.tsx (DiVoid node #229)
 */

import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { toast } from 'sonner';
import { Pencil, Trash2, Link2, Unlink, UploadCloud, X, FileText } from 'lucide-react';
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
import { StatusBadge } from '@/components/common/StatusBadge';
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

// ─── Content region ───────────────────────────────────────────────────────────

interface ContentRegionProps {
  nodeId: number;
  contentType?: string;
  canWrite: boolean;
}

type ContentMode = 'read' | 'edit';

/**
 * Empty-state card shown when a node has no content (contentType is null/empty).
 *
 * Surfaces both write affordances so the user can add the first content blob:
 *  - "Add markdown" → opens the MarkdownEditorSurface in compose mode.
 *  - "Upload file" → exposes the ContentUploadZone drag-target.
 *
 * Read-only users (canWrite=false) see a neutral "No content" message without
 * the write affordances — the backend remains the security boundary.
 *
 * Task: DiVoid node #294
 */
interface EmptyContentCardProps {
  nodeId: number;
  canWrite: boolean;
}

function EmptyContentCard({ nodeId, canWrite }: EmptyContentCardProps) {
  const [mode, setMode] = useState<'idle' | 'compose' | 'upload'>('idle');

  return (
    <div
      className="flex flex-col gap-4 rounded-lg border border-dashed border-border bg-muted/20 px-6 py-8 items-center text-center"
      data-testid="empty-content-card"
    >
      <FileText size={32} className="text-muted-foreground/50" aria-hidden="true" />
      <p className="text-sm text-muted-foreground">This node has no content yet.</p>

      {canWrite && mode === 'idle' && (
        <div className="flex flex-wrap items-center justify-center gap-3">
          <button
            type="button"
            aria-label="Add markdown content"
            onClick={() => setMode('compose')}
            className="inline-flex items-center gap-1.5 h-8 px-4 rounded-md border border-border text-sm font-medium hover:bg-muted transition-colors"
          >
            <Pencil size={14} aria-hidden="true" />
            Add markdown
          </button>
          <button
            type="button"
            aria-label="Upload file"
            onClick={() => setMode('upload')}
            className="inline-flex items-center gap-1.5 h-8 px-4 rounded-md border border-border text-sm font-medium hover:bg-muted transition-colors"
          >
            <UploadCloud size={14} aria-hidden="true" />
            Upload file
          </button>
        </div>
      )}

      {canWrite && mode === 'compose' && (
        <div className="w-full text-left">
          <MarkdownEditorSurface
            nodeId={nodeId}
            initialContent=""
            onCancel={() => setMode('idle')}
          />
        </div>
      )}

      {canWrite && mode === 'upload' && (
        <div className="w-full">
          <ContentUploadZone nodeId={nodeId} />
          <button
            type="button"
            onClick={() => setMode('idle')}
            className="mt-3 inline-flex items-center gap-1.5 self-start text-sm text-muted-foreground hover:text-foreground transition-colors"
            aria-label="Cancel upload"
          >
            <X size={14} aria-hidden="true" />
            Cancel
          </button>
        </div>
      )}
    </div>
  );
}

function ContentRegion({ nodeId, contentType, canWrite }: ContentRegionProps) {
  // Gate the content fetch on contentType being present and text-shaped.
  // When contentType is null/empty the node has no content blob — do not fetch.
  // See §8.3 (enabled gates conditional fetches) and task #294.
  const hasContent = Boolean(contentType);
  const { data: content, isFetching, error } = useNodeContent(nodeId, { enabled: isTextShaped(contentType) });
  const [mode, setMode] = useState<ContentMode>('read');
  const [showUpload, setShowUpload] = useState(false);

  useEffect(() => {
    if (error instanceof DivoidApiError) {
      toast.error(`Content: ${error.code}: ${error.text}`);
    }
  }, [error]);

  // Empty state: node has no content type — show the empty-state card with
  // affordances to add content. No fetch is issued; no error can be shown.
  // Task #294.
  if (!hasContent) {
    return <EmptyContentCard nodeId={nodeId} canWrite={canWrite} />;
  }

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
  /** When provided, neighbour name clicks call this instead of navigating. */
  onNeighbourClick?: (neighbourId: number) => void;
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

function NeighboursRegion({ nodeId, canWrite, onAddLink, onNeighbourClick }: NeighboursRegionProps) {
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
                    {onNeighbourClick ? (
                      <button
                        type="button"
                        aria-label={`Open peek for node ${n.id}`}
                        onClick={() => onNeighbourClick(n.id)}
                        className="text-foreground hover:text-primary underline-offset-2 hover:underline transition-colors text-left"
                      >
                        {n.name ?? `Node ${n.id}`}
                      </button>
                    ) : (
                      <Link
                        to={ROUTES.NODE_DETAIL(n.id)}
                        className="text-foreground hover:text-primary underline-offset-2 hover:underline transition-colors"
                      >
                        {n.name ?? `Node ${n.id}`}
                      </Link>
                    )}
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

// ─── View ─────────────────────────────────────────────────────────────────────

export interface NodeDetailViewProps {
  /** The node to render. Must be > 0; caller validates route param / peek id. */
  nodeId: number;
  /**
   * Called when an action implies the view should dismiss (e.g. Delete success).
   * Undefined in route context (NodeDetailPage navigates away instead).
   * Supplied by modal context (WorkspaceNodePeekModal) to close the peek.
   */
  onClose?: () => void;
  /**
   * Called when the user clicks a neighbour row.
   * Undefined in route context (rows are <Link> elements navigating to /nodes/:id).
   * Supplied by modal context to swap the current peek to the neighbour's id.
   */
  onNeighbourClick?: (neighbourId: number) => void;
}

/**
 * NodeDetailView — shared node detail body.
 *
 * Renders metadata, content, neighbours, and write dialogs for any node.
 * Consumed by NodeDetailPage (route) and WorkspaceNodePeekModal (modal).
 * Container-specific behaviour (back button, chrome) belongs to the caller.
 *
 * See design doc §5.1 for the full component contract.
 */
export function NodeDetailView({ nodeId, onClose, onNeighbourClick }: NodeDetailViewProps) {
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

  if (isFetching) {
    return (
      <div className="space-y-4">
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
      <p className="text-sm text-muted-foreground" role="alert">
        Node {nodeId} not found.
      </p>
    );
  }

  if (error && !node) {
    return (
      <p className="text-sm text-destructive" role="alert">
        Failed to load node. Please try again.
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-6" data-testid="node-detail-view">
      {/* Metadata region */}
      <section aria-labelledby="ndv-metadata-heading">
        <div className="flex items-start justify-between gap-3 mb-4">
          <h1 id="ndv-metadata-heading" className="text-xl font-semibold">
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
          <MetadataRow
            label="Owner"
            value={
              <span className="tabular-nums">
                {node?.ownerId === undefined || node.ownerId === 0 ? '—' : node.ownerId}
              </span>
            }
          />
          <MetadataRow
            label="Access"
            value={
              node?.access ? (
                <span className="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-muted text-muted-foreground">
                  {node.access}
                </span>
              ) : (
                <span className="text-muted-foreground">—</span>
              )
            }
          />
        </div>
      </section>

      {/* Content region */}
      <section aria-labelledby="ndv-content-heading">
        <h2 id="ndv-content-heading" className="text-sm font-semibold mb-3">
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
        onNeighbourClick={onNeighbourClick}
      />

      {/* Dialogs — only mounted when node is loaded */}
      {node && (
        <>
          <EditNodeDialog open={editOpen} onOpenChange={setEditOpen} node={node} />
          <DeleteNodeDialog
            open={deleteOpen}
            onOpenChange={setDeleteOpen}
            node={node}
            onDeleted={() => {
              onClose?.();
            }}
          />
          <LinkNodeDialog open={linkOpen} onOpenChange={setLinkOpen} sourceId={nodeId} />
        </>
      )}
    </div>
  );
}
